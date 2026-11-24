using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>
    /// Builds <see cref="CapsuleInfo"/> from the two data sources we have:
    /// <list type="bullet">
    /// <item>A server-side reward table (<c>ItemBag.xml</c> layout: item/group/reward with a Rate).
    /// It is not part of the client and not in this repository; operators supply it.</item>
    /// <item>The client's capsule tooltip (<c>_eu_item_tooltip_addcapsule.x7</c>), which lists
    /// what a capsule can give but has no odds. Every listed reward is then equally likely.</item>
    /// </list>
    /// </summary>
    public static class CapsuleDataParser
    {
        /// <summary>Item number the client uses as the "PEN" icon in capsule tooltips.</summary>
        public const uint PenIconItem = 4040003;

        public static IEnumerable<CapsuleInfo> ParseRewardTable(XDocument doc)
        {
            foreach (var item in doc.Root?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                if (!uint.TryParse((string)item.Attribute("Number"), out var number))
                    continue;

                var capsule = new CapsuleInfo { ItemNumber = new ItemNumber(number), Source = CapsuleSource.RewardTable };
                foreach (var group in item.Elements("group"))
                {
                    var rewardGroup = new CapsuleRewardGroup();
                    foreach (var reward in group.Elements("reward"))
                    {
                        var type = UInt(reward, "Type");
                        var value = UInt(reward, "Value");
                        if (type == (uint)CapsuleRewardType.PEN)
                        {
                            rewardGroup.Rewards.Add(new CapsuleRewardInfo
                            {
                                Type = CapsuleRewardType.PEN, PEN = value, Rate = UInt(reward, "Rate")
                            });
                        }
                        else if (type == (uint)CapsuleRewardType.Item && UInt(reward, "Data") != 0)
                        {
                            rewardGroup.Rewards.Add(new CapsuleRewardInfo
                            {
                                Type = CapsuleRewardType.Item,
                                ItemNumber = new ItemNumber(UInt(reward, "Data")),
                                PeriodType = (ItemPeriodType)UInt(reward, "PeriodType"),
                                Period = (ushort)Math.Min(value, ushort.MaxValue),
                                Color = (byte)Math.Min(UInt(reward, "Color"), byte.MaxValue),
                                Effects = ParseEffects((string)reward.Attribute("Effects")),
                                Rate = UInt(reward, "Rate")
                            });
                        }
                    }

                    if (rewardGroup.Rewards.Count > 0)
                        capsule.Groups.Add(rewardGroup);
                }

                if (capsule.Groups.Count > 0)
                    yield return capsule;
            }
        }

        /// <param name="tooltip">The client's <c>_eu_item_tooltip_addcapsule.x7</c>.</param>
        /// <param name="strings">Tooltip text by key (English), used to read PEN ranges such as "PEN 1,000~100,000".</param>
        public static IEnumerable<CapsuleInfo> ParseTooltip(XDocument tooltip, IReadOnlyDictionary<string, string> strings)
        {
            foreach (var item in tooltip.Root?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                if (!uint.TryParse((string)item.Attribute("id"), out var number))
                    continue;

                var icons = item.Element("capsule_icon");
                var infos = item.Element("capsule_info");
                var colors = item.Element("color_index");
                if (icons == null)
                    continue;

                // Set boxes (item category 4, subcategory 5 = "Set") list their parts without slot
                // numbers and give every part. Blank slots elsewhere are ordinary random capsules.
                var slots = item.Element("capsule_slot");
                var isSetBox = number / 10000 == 405 && (slots == null || !slots.Attributes()
                    .Any(a => a.Name.LocalName.StartsWith("slot_") && uint.TryParse(a.Value.Trim(), out _)));

                var entries = new List<(CapsuleRewardInfo Reward, string Slot)>();
                for (var i = 1; i <= 10; ++i)
                {
                    if (!uint.TryParse((string)icons.Attribute("ID_" + i), out var rewardItem) || rewardItem == 0)
                        continue;

                    CapsuleRewardInfo reward;
                    if (rewardItem == PenIconItem)
                    {
                        var key = (string)infos?.Attribute("effect_key_" + i) ?? "";
                        if (!strings.TryGetValue(key, out var text) || !TryParseRange(text, out var min, out var max))
                            continue;

                        reward = new CapsuleRewardInfo { Type = CapsuleRewardType.PEN, PEN = min, PENMax = max };
                    }
                    else
                    {
                        byte.TryParse((string)colors?.Attribute("color_" + i), out var color);
                        reward = new CapsuleRewardInfo
                        {
                            Type = CapsuleRewardType.Item,
                            ItemNumber = new ItemNumber(rewardItem),
                            PeriodType = ItemPeriodType.None,
                            Color = color
                        };

                        // The slot text says which periods and stats the reward came with. A set
                        // box's text ("(Jacket, Pants, ...)-30DAY") describes its parts, not the box.
                        var key = (string)infos?.Attribute("effect_key_" + i) ?? "";
                        if (!IsSetBox(reward.ItemNumber) && strings.TryGetValue(key, out var text))
                            reward.Options = BuildOptions(CapsuleTooltipText.Parse(text), ItemPeriodType.None, 0);
                    }

                    entries.Add((reward, ((string)slots?.Attribute("slot_" + i))?.Trim() ?? ""));
                }

                List<CapsuleRewardGroup> groups;
                if (isSetBox)
                {
                    groups = entries.Select(e => new CapsuleRewardGroup { Rewards = { e.Reward } }).ToList();
                }
                else
                {
                    // Costume capsules that list set boxes gave sets, not loose parts: their
                    // tooltips also show the set's parts (tops, gloves, ...), which are dropped.
                    if (entries.Any(e => IsSetBox(e.Reward.ItemNumber)))
                        entries.RemoveAll(e => e.Reward.Type == CapsuleRewardType.Item && e.Reward.ItemNumber.Id / 1000000 == 1);

                    var numbered = entries.Where(e => uint.TryParse(e.Slot, out _)).Select(e => e.Slot).Distinct().ToList();
                    groups = numbered.Count >= 2 ? GroupBySlot(entries) : GroupAsOne(entries);
                }

                if (groups.Count > 0)
                    yield return new CapsuleInfo
                    {
                        ItemNumber = new ItemNumber(number), Source = CapsuleSource.Tooltip, Groups = groups
                    };
            }
        }

        private static bool IsSetBox(ItemNumber item) => item.Id / 10000 == 405;

        /// <summary>
        /// Tooltips that number two or more slots give one reward per slot, e.g. "permanent set or
        /// coupons" + "1/7/30-day set" + PEN, and double capsules give two items. A reward without a
        /// slot joins the slot listed before it; PEN without a slot is its own roll.
        /// </summary>
        private static List<CapsuleRewardGroup> GroupBySlot(List<(CapsuleRewardInfo Reward, string Slot)> entries)
        {
            var bySlot = new List<(string Slot, CapsuleRewardGroup Group)>();
            var loosePen = new CapsuleRewardGroup();
            string last = null;
            foreach (var (reward, slot) in entries)
            {
                var key = uint.TryParse(slot, out _) ? slot : null;
                if (key == null && reward.Type == CapsuleRewardType.PEN)
                {
                    loosePen.Rewards.Add(reward);
                    continue;
                }

                key ??= last ?? entries.Select(e => e.Slot).First(x => uint.TryParse(x, out _));
                last = key;
                var group = bySlot.FirstOrDefault(x => x.Slot == key).Group;
                if (group == null)
                {
                    group = new CapsuleRewardGroup();
                    bySlot.Add((key, group));
                }

                group.Rewards.Add(reward);
            }

            return bySlot.OrderBy(x => uint.Parse(x.Slot)).Select(x => x.Group).Append(loosePen)
                .Where(g => g.Rewards.Count > 0).ToList();
        }

        /// <summary>
        /// Other tooltips: one item rolled from everything listed (the slot-1 top prize at
        /// <see cref="TopPrizePercent"/>%), and PEN, when listed, rolled separately.
        /// </summary>
        private static List<CapsuleRewardGroup> GroupAsOne(List<(CapsuleRewardInfo Reward, string Slot)> entries)
        {
            var items = new CapsuleRewardGroup();
            var pens = new CapsuleRewardGroup();
            var topPrizes = new List<CapsuleRewardInfo>();
            foreach (var (reward, slot) in entries)
            {
                (reward.Type == CapsuleRewardType.PEN ? pens : items).Rewards.Add(reward);
                if (reward.Type == CapsuleRewardType.Item && slot == "1")
                    topPrizes.Add(reward);
            }

            WeightTopPrize(items, topPrizes);
            return new[] { items, pens }.Where(g => g.Rewards.Count > 0).ToList();
        }

        /// <summary>
        /// Odds inside per-slot groups that have none (no reward table): a permanent set box next to
        /// other rewards is the rare <see cref="TopPrizePercent"/>% pick, and among timed set boxes the
        /// shorter period is likelier (1/7/30 days weigh 3/2/1). Everything else is equally likely.
        /// <paramref name="nameOf"/> gives an item's name, which holds a set box's period.
        /// </summary>
        public static void WeightSetBoxes(CapsuleInfo capsule, Func<ItemNumber, string> nameOf)
        {
            foreach (var group in capsule.Groups)
            {
                if (group.Rewards.Count < 2 || group.Rewards.Any(r => r.Rate > 0))
                    continue;

                var hours = new Dictionary<CapsuleRewardInfo, long>();
                var permanent = new List<CapsuleRewardInfo>();
                foreach (var r in group.Rewards.Where(r => r.Type == CapsuleRewardType.Item && IsSetBox(r.ItemNumber)))
                {
                    if (TryParseBoxPeriod(nameOf(r.ItemNumber), out var type, out var period))
                        hours[r] = Length(type, period);
                    else
                        permanent.Add(r);
                }

                if (hours.Count == 0 && (permanent.Count == 0 || permanent.Count == group.Rewards.Count))
                    continue;

                // Weight per reward: timed set boxes by shortness rank, the rest 1 (the rank's top).
                var ranks = hours.Values.Distinct().OrderByDescending(x => x).ToList();
                var weight = group.Rewards.Where(r => !permanent.Contains(r)).ToDictionary(r => r,
                    r => hours.TryGetValue(r, out var h) ? (uint)(ranks.IndexOf(h) + 1) : (uint)Math.Max(1, ranks.Count));
                const uint total = 10000;
                var pool = permanent.Count > 0 && weight.Count > 0 ? total - total * TopPrizePercent / 100 : total;
                foreach (var r in permanent)
                    r.Rate = Math.Max(1, (total - pool) / (uint)permanent.Count);
                var sum = weight.Values.Sum(x => (long)x);
                foreach (var (r, w) in weight)
                    r.Rate = (uint)Math.Max(1, pool * w / sum);
            }
        }

        /// <summary>
        /// Puts back what a reward table leaves out of a tooltip that gives one reward per slot: a
        /// slot none of whose items the table lists (e.g. the 1/7/30-day set slot) is added as its
        /// own roll, and a PEN roll whose amounts fall outside the tooltip's range takes the range.
        /// Returns how many groups were added or replaced.
        /// </summary>
        public static int AddMissingSlots(CapsuleInfo table, CapsuleInfo tooltip)
        {
            if (tooltip == null || tooltip.Groups.Count(g => g.Rewards.Any(r => r.Type == CapsuleRewardType.Item)) < 2)
                return 0;

            var changed = 0;
            var listed = table.Groups.SelectMany(g => g.Rewards).Where(r => r.Type == CapsuleRewardType.Item)
                .Select(r => r.ItemNumber).ToHashSet();
            foreach (var group in tooltip.Groups)
            {
                if (group.Rewards.All(r => r.Type == CapsuleRewardType.Item) && !group.Rewards.Any(r => listed.Contains(r.ItemNumber)))
                {
                    table.Groups.Add(group);
                    ++changed;
                }
            }

            var pen = tooltip.Groups.SelectMany(g => g.Rewards).FirstOrDefault(r => r.Type == CapsuleRewardType.PEN && r.PENMax > r.PEN);
            if (pen == null)
                return changed;
            if (!table.Groups.Any(g => g.Rewards.Count > 0 && g.Rewards.All(r => r.Type == CapsuleRewardType.PEN)))
            {
                table.Groups.Add(new CapsuleRewardGroup { Rewards = { pen } });
                return changed + 1;
            }

            for (var i = 0; i < table.Groups.Count; ++i)
            {
                var rewards = table.Groups[i].Rewards;
                if (rewards.Count > 0 && rewards.All(r => r.Type == CapsuleRewardType.PEN) &&
                    rewards.Any(r => r.PEN < pen.PEN || Math.Max(r.PEN, r.PENMax) > pen.PENMax))
                {
                    table.Groups[i] = new CapsuleRewardGroup { Rewards = { pen } };
                    ++changed;
                }
            }

            return changed;
        }

        /// <summary>
        /// Turns the tooltip's options for one reward into weighted reward options. Options without
        /// a period keep <paramref name="periodType"/>/<paramref name="period"/>. The tooltip gives no
        /// odds between periods, so shorter periods are favoured: with n periods, the shortest
        /// weighs n, the next n - 1, ..., the longest (permanent last) 1. Same for unit counts.
        /// </summary>
        public static IList<CapsuleRewardOption> BuildOptions(IEnumerable<CapsuleTooltipOption> options,
            ItemPeriodType periodType, ushort period)
        {
            var distinct = new List<CapsuleRewardOption>();
            foreach (var o in options)
            {
                var type = o.PeriodType ?? periodType;
                var p = o.PeriodType.HasValue ? o.Period : period;
                if (type == ItemPeriodType.None || type == 0)
                    p = 0;
                if (distinct.Any(x => x.PeriodType == type && x.Period == p))
                    continue;
                distinct.Add(new CapsuleRewardOption
                {
                    PeriodType = type, Period = p, Stats = o.Stats, StatText = o.StatText, NoEffects = o.NoStats,
                    KeepsPeriod = !o.PeriodType.HasValue
                });
            }

            var ordered = distinct.OrderBy(x => Length(x.PeriodType, x.Period)).ToList();
            for (var i = 0; i < ordered.Count; ++i)
                ordered[i].Weight = (uint)(ordered.Count - i);
            return ordered;
        }

        private static long Length(ItemPeriodType type, ushort period) => type switch
        {
            ItemPeriodType.Hours => period,
            ItemPeriodType.Days => period * 24L,
            ItemPeriodType.Units => period,
            _ => long.MaxValue
        };

        /// <summary>
        /// Corrects a reward-table capsule with the client's tooltip for it. The reward table (no known
        /// official source) keeps its rewards and odds, but where the tooltip lists periods or stats
        /// for the same item it wins: a period the tooltip also lists is kept with the tooltip's stats
        /// for it; a period the tooltip doesn't list is replaced by a roll over the tooltip's periods.
        /// </summary>
        /// <returns>How many rewards changed.</returns>
        public static int ApplyTooltip(CapsuleInfo table, CapsuleInfo tooltip)
        {
            if (tooltip == null)
                return 0;

            var byItem = tooltip.Groups.SelectMany(g => g.Rewards)
                .Where(r => r.Type == CapsuleRewardType.Item && r.Options.Count > 0)
                .GroupBy(r => r.ItemNumber)
                .ToDictionary(g => g.Key, g => g.First());

            var changed = 0;
            foreach (var reward in table.Groups.SelectMany(g => g.Rewards))
            {
                if (reward.Type != CapsuleRewardType.Item || !byItem.TryGetValue(reward.ItemNumber, out var tip))
                    continue;

                var type = reward.PeriodType == 0 ? ItemPeriodType.None : reward.PeriodType;
                var period = type == ItemPeriodType.None ? (ushort)0 : reward.Period;
                // Options without a period (stats only) take the table's period.
                var options = tip.Options.Select(o => Copy(o, o.KeepsPeriod ? type : o.PeriodType,
                    o.KeepsPeriod ? period : o.Period)).ToList();
                var same = options.Where(o => o.PeriodType == type && o.Period == period).ToList();
                reward.Options = same.Count > 0 ? new List<CapsuleRewardOption> { same[0] } : options;
                ++changed;
            }

            return changed;
        }

        private static CapsuleRewardOption Copy(CapsuleRewardOption o, ItemPeriodType type, ushort period) =>
            new CapsuleRewardOption
            {
                PeriodType = type, Period = type == ItemPeriodType.None ? (ushort)0 : period, Stats = o.Stats,
                StatText = o.StatText, Effects = o.Effects, NoEffects = o.NoEffects, Weight = o.Weight,
                KeepsPeriod = o.KeepsPeriod
            };

        /// <summary>
        /// Fills every option's effect ids from its stats. Options whose stats can't be matched keep
        /// no ids (the reward's own effects, else the shop's) and are passed to <paramref name="unmapped"/>.
        /// </summary>
        public static void ResolveEffects(CapsuleInfo capsule, CapsuleEffectResolver resolver,
            Action<CapsuleRewardInfo, CapsuleRewardOption> unmapped)
        {
            foreach (var reward in capsule.Groups.SelectMany(g => g.Rewards))
            {
                foreach (var option in reward.Options)
                {
                    if (option.NoEffects || option.Stats.Count == 0)
                        continue;
                    var ids = resolver.Resolve(reward.ItemNumber, option.Stats);
                    if (ids == null)
                        unmapped?.Invoke(reward, option);
                    option.Effects = ids ?? Array.Empty<uint>();
                }
            }
        }

        /// <summary>Chance of the slot-1 prize when the tooltip gives no odds (reward-table norm).</summary>
        public const uint TopPrizePercent = 5;

        /// <summary>
        /// Gives the slot-1 items <see cref="TopPrizePercent"/>% together and the rest of the group
        /// the remainder, split evenly. Leaves equal odds when slot 1 is empty or holds everything.
        /// </summary>
        public static void WeightTopPrize(CapsuleRewardGroup group, IList<CapsuleRewardInfo> topPrizes)
        {
            var others = group.Rewards.Where(r => !topPrizes.Contains(r)).ToList();
            if (topPrizes.Count == 0 || others.Count == 0)
                return;

            const uint total = 10000;
            var top = total * TopPrizePercent / 100;
            foreach (var r in topPrizes)
                r.Rate = Math.Max(1, top / (uint)topPrizes.Count);
            foreach (var r in others)
                r.Rate = Math.Max(1, (total - top) / (uint)others.Count);
        }

        /// <summary>Reads "PEN{N}1,000~100,000" style text. A single number gives min == max.</summary>
        public static bool TryParseRange(string text, out uint min, out uint max)
        {
            min = max = 0;
            var numbers = Regex.Matches(text ?? "", @"\d[\d,]*")
                .Select(m => uint.TryParse(m.Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
                .Where(v => v > 0)
                .ToArray();
            if (numbers.Length == 0)
                return false;

            min = numbers.Min();
            max = numbers.Max();
            return true;
        }

        /// <summary>Reads an Effects attribute such as "12002" or "1104300008 01101301006" (0 = none).</summary>
        public static uint[] ParseEffects(string text)
        {
            return Regex.Matches(text ?? "", @"\d+")
                .Select(m => uint.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
                .Where(v => v != 0)
                .ToArray();
        }

        private static readonly Regex BoxPeriodRegex =
            new Regex(@"\(\s*(\d+)\s*(days?|hours?)\s*\)\s*$", RegexOptions.IgnoreCase);

        /// <summary>Reads the period from a set box name such as "Gentle Hearts Set(30Days)" or "... (1 day)".</summary>
        public static bool TryParseBoxPeriod(string name, out ItemPeriodType periodType, out ushort period)
        {
            periodType = ItemPeriodType.None;
            period = 0;
            var match = BoxPeriodRegex.Match(name ?? "");
            if (!match.Success || !ushort.TryParse(match.Groups[1].Value, out period) || period == 0)
                return false;

            periodType = match.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase)
                ? ItemPeriodType.Hours
                : ItemPeriodType.Days;
            return true;
        }

        /// <summary>
        /// The Force Pack stat group per costume slot. These are ITEM_GRADE_UNIQUE in the client's
        /// item_grade.x7, which shows the item with the orange tag; the cash shop's stats are RARE.
        /// </summary>
        public static readonly IReadOnlyDictionary<CostumeSlot, uint> CostumeForcePack = new Dictionary<CostumeSlot, uint>
        {
            [CostumeSlot.Hair] = 10002,     // Guns Def. (head)+15%, EXP+5%
            [CostumeSlot.Shirt] = 12002,    // Attack+12%, EXP+5%
            [CostumeSlot.Pants] = 13002,    // Defense+14%, EXP+5%
            [CostumeSlot.Gloves] = 14002,   // HP+8, EXP+5%
            [CostumeSlot.Shoes] = 15002,    // HP+8, EXP+5%
            [CostumeSlot.Accessory] = 16002 // SP+8, EXP+5%
        };

        /// <summary>
        /// Stats of a set box's costume parts: a permanent set (no period in its name) gives the
        /// Force Pack group of each part's slot (orange tag), a timed set the cash shop's stats
        /// (whatever the reward table says). Slots without a Force Pack group keep the shop's stats.
        /// </summary>
        public static void ApplyBoosterRewards(CapsuleInfo capsule)
        {
            foreach (var group in capsule.Groups)
            {
                var boosters = group.Rewards.Where(r => r.Type == CapsuleRewardType.Item && r.ItemNumber.Id / 1000 == 5040).ToList();
                foreach (var r in boosters.Where(r => r.Options.Count == 0 && (r.PeriodType == ItemPeriodType.None || r.PeriodType == 0)))
                {
                    // The EU shop sold x1.5..x5 by time (10 hours, hidden 1 day) and 100% by uses.
                    (r.PeriodType, r.Period) = r.ItemNumber.Id == 5040006 ? (ItemPeriodType.Units, (ushort)1) : (ItemPeriodType.Days, (ushort)1);
                }

                // No odds in the data; the stronger the booster the rarer, 100% rarest.
                if (boosters.Count == group.Rewards.Count && boosters.Count > 1 && boosters.All(r => r.Rate == 0))
                {
                    foreach (var r in boosters)
                        r.Rate = (r.ItemNumber.Id % 1000) switch { 1 => 3000u, 2 => 2500u, 3 => 2000u, 4 => 1200u, 5 => 800u, _ => 500u };
                }
            }
        }

        public static void ApplySetGrade(CapsuleInfo capsule, string boxName)
        {
            if (capsule.ItemNumber.Id / 10000 != 405 || string.IsNullOrEmpty(boxName))
                return;

            var permanent = !TryParseBoxPeriod(boxName, out _, out _);
            foreach (var reward in capsule.Groups.SelectMany(g => g.Rewards))
            {
                if (reward.Type != CapsuleRewardType.Item || reward.ItemNumber.Category != ItemCategory.Costume)
                    continue;

                var effects = permanent && CostumeForcePack.TryGetValue((CostumeSlot)reward.ItemNumber.SubCategory, out var fp)
                    ? new[] { fp }
                    : Array.Empty<uint>();
                reward.Effects = effects;
                foreach (var option in reward.Options)
                {
                    option.Effects = effects;
                    option.NoEffects = false;
                }
            }
        }

        /// <summary>
        /// Timed set boxes (405xxxx named "(30Days)" etc.) give the same part numbers as the permanent
        /// box; only the box says how long they last. The reward table lists those parts as permanent,
        /// so the box name's period is applied to every permanent item reward.
        /// </summary>
        public static void ApplyBoxPeriod(CapsuleInfo capsule, string boxName)
        {
            if (capsule.ItemNumber.Id / 10000 != 405 || !TryParseBoxPeriod(boxName, out var periodType, out var period))
                return;

            foreach (var reward in capsule.Groups.SelectMany(g => g.Rewards))
            {
                if (reward.Type != CapsuleRewardType.Item)
                    continue;

                // the tooltip's options for a part: the box decides how long it lasts
                foreach (var option in reward.Options.Where(o => o.PeriodType == ItemPeriodType.None || o.PeriodType == 0))
                {
                    option.PeriodType = periodType;
                    option.Period = period;
                }

                if (reward.PeriodType != ItemPeriodType.None && reward.PeriodType != 0)
                    continue;

                reward.PeriodType = periodType;
                reward.Period = period;
            }
        }

        private static uint UInt(XElement e, string name)
        {
            return uint.TryParse((string)e.Attribute(name), out var v) ? v : 0;
        }
    }
}
