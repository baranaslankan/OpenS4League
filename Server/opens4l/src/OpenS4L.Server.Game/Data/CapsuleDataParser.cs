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

                // Otherwise the tooltip's slot numbers can't be trusted as groups: set
                // capsules list the top prize alone in slot 1 (in the reward tables it is a rare pick
                // against a coupon) and often leave rewards without a slot. So one item is rolled from
                // everything listed, and PEN, when listed, is rolled separately.
                var items = new CapsuleRewardGroup();
                var pens = new CapsuleRewardGroup();
                var topPrizes = new List<CapsuleRewardInfo>();
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

                    (reward.Type == CapsuleRewardType.PEN ? pens : items).Rewards.Add(reward);
                    if (reward.Type == CapsuleRewardType.Item && ((string)slots?.Attribute("slot_" + i))?.Trim() == "1")
                        topPrizes.Add(reward);
                }

                // The top prize (alone in slot 1, e.g. the permanent set) is rare: in the reward
                // tables a set inside a capsule is almost always a 5% pick against the rest.
                if (!isSetBox)
                {
                    // Costume capsules that list set boxes gave sets, not loose parts: their
                    // tooltips also show the set's parts (tops, gloves, ...), which are dropped.
                    if (items.Rewards.Any(r => r.ItemNumber.Id / 10000 == 405))
                    {
                        foreach (var part in items.Rewards.Where(r => r.ItemNumber.Id / 1000000 == 1).ToList())
                        {
                            items.Rewards.Remove(part);
                            topPrizes.Remove(part);
                        }
                    }

                    WeightTopPrize(items, topPrizes);
                }

                var groups = isSetBox
                    ? items.Rewards.Concat(pens.Rewards)
                        .Select(r => new CapsuleRewardGroup { Rewards = { r } }).ToList()
                    : new[] { items, pens }.Where(g => g.Rewards.Count > 0).ToList();
                if (groups.Count > 0)
                    yield return new CapsuleInfo
                    {
                        ItemNumber = new ItemNumber(number), Source = CapsuleSource.Tooltip, Groups = groups
                    };
            }
        }

        private static bool IsSetBox(ItemNumber item) => item.Id / 10000 == 405;

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
