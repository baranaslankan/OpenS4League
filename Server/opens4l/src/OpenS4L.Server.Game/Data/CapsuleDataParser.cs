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
                var slots = item.Element("capsule_slot");
                var infos = item.Element("capsule_info");
                var colors = item.Element("color_index");
                if (icons == null)
                    continue;

                // Entries sharing a slot are alternatives; every slot is rolled once.
                var groups = new SortedDictionary<string, CapsuleRewardGroup>(StringComparer.Ordinal);
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
                    }

                    var slot = ((string)slots?.Attribute("slot_" + i) ?? "").Trim();
                    if (!groups.TryGetValue(slot, out var group))
                        groups[slot] = group = new CapsuleRewardGroup();
                    group.Rewards.Add(reward);
                }

                if (groups.Count > 0)
                    yield return new CapsuleInfo
                    {
                        ItemNumber = new ItemNumber(number), Source = CapsuleSource.Tooltip, Groups = groups.Values.ToList()
                    };
            }
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

        private static uint UInt(XElement e, string name)
        {
            return uint.TryParse((string)e.Attribute(name), out var v) ? v : 0;
        }
    }
}
