using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>A value with a relative weight (the "prob" columns of the 2019 random shop file).</summary>
    public class RandomShopWeighted<T>
    {
        public T Value { get; set; }
        public int Weight { get; set; }

        public RandomShopWeighted()
        {
        }

        public RandomShopWeighted(T value, int weight)
        {
            Value = value;
            Weight = weight;
        }
    }

    /// <summary>&lt;package&gt; of the 2019 file. Parsed for the lineup filter only; packages stay synthetic.</summary>
    public class RandomShopOddsPackage
    {
        public int Id { get; set; }

        /// <summary>on / off / new</summary>
        public string Open { get; set; }

        /// <summary>pen / coupon / item</summary>
        public string PriceType { get; set; }

        public int Price { get; set; }

        /// <summary>man / woman / all</summary>
        public string Gender { get; set; }

        public bool IsPen => string.Equals(PriceType, "pen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>&lt;lineup&gt; of the 2019 file: one prize of a package. Prob is out of 10000 per package.</summary>
    public class RandomShopOddsLineup
    {
        public int PackageId { get; set; }
        public string RewardType { get; set; }
        public uint ItemKey { get; set; }
        public string ColorGroup { get; set; }
        public string EffectGroup { get; set; }
        public string PeriodGroup { get; set; }
        public int DefaultColor { get; set; }
        public int Prob { get; set; }
        public int Grade { get; set; }

        public bool IsItem => string.Equals(RewardType, "ITEM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The 2019 groups and prize weight that apply to one item, see <see cref="RandomShopOdds.GetSlot"/>.</summary>
    public class RandomShopOddsSlot
    {
        public string ColorGroup { get; set; }
        public string EffectGroup { get; set; }
        public string PeriodGroup { get; set; }
        public int Weight { get; set; }

        /// <summary>true when the item itself is in the 2019 lineup (otherwise the values are derived)</summary>
        public bool InLineup { get; set; }
    }

    /// <summary>
    /// The official 2019 EU random shop (_eu_new_random_shop.xml, operator data in the server data
    /// folder, not in the repo). The 2013 client has almost none of its lineup items, so the server
    /// keeps its synthetic packages and only takes the odds from here: period, effect and colour
    /// groups, and the prize weight. The grade columns are parsed but not used (their meaning in the
    /// roll is unknown).
    /// </summary>
    public class RandomShopOdds
    {
        /// <summary>Path below the server data folder.</summary>
        public const string FileName = "server/eu2019/_eu_new_random_shop.xml";

        public const string DefaultWeaponPeriodGroup = "Def_WP_Days";
        public const string DefaultCostumePeriodGroup = "Def_CT_Days";

        // Weapon sub categories the 2019 lineup has no entry for, by the effect group names of the file
        // (4 sentry gun, 5 grenade/mine gun, 6 mind energy). With the 2013 client's effect_list all
        // Def_Weapon_* groups end up as the same two effects, so the exact pick barely matters.
        private static readonly Dictionary<byte, string> s_weaponEffectFallback = new Dictionary<byte, string>
        {
            { 0, "Def_Weapon_Melee" },
            { 1, "Def_Weapon_Rifle" },
            { 2, "Def_Weapon_Heavy" },
            { 3, "Def_Weapon_Snipe" },
            { 4, "Def_Weapon_Sentry" },
            { 5, "Def_Weapon_Gun" },
            { 6, "Def_Weapon_Mind" }
        };

        public IList<RandomShopOddsPackage> Packages { get; set; }
        public IList<RandomShopOddsLineup> Lineup { get; set; }
        public IDictionary<string, IList<RandomShopWeighted<RandomShopPeriod>>> PeriodGroups { get; set; }
        public IDictionary<string, IList<RandomShopWeighted<uint>>> EffectGroups { get; set; }
        public IDictionary<string, IList<RandomShopWeighted<byte>>> ColorGroups { get; set; }

        public RandomShopOdds()
        {
            Packages = new List<RandomShopOddsPackage>();
            Lineup = new List<RandomShopOddsLineup>();
            PeriodGroups = new Dictionary<string, IList<RandomShopWeighted<RandomShopPeriod>>>(StringComparer.Ordinal);
            EffectGroups = new Dictionary<string, IList<RandomShopWeighted<uint>>>(StringComparer.Ordinal);
            ColorGroups = new Dictionary<string, IList<RandomShopWeighted<byte>>>(StringComparer.Ordinal);
        }

        /// <summary>Parses the file. Rows with missing or unparseable values are skipped.</summary>
        public static RandomShopOdds Parse(XDocument doc)
        {
            var odds = new RandomShopOdds();
            var root = doc.Root;
            if (root == null)
                return odds;

            foreach (var x in root.Element("package_list")?.Elements("package") ?? Enumerable.Empty<XElement>())
            {
                if (!TryGetInt(x, "id", out var id))
                    continue;

                odds.Packages.Add(new RandomShopOddsPackage
                {
                    Id = id,
                    Open = (string)x.Attribute("open"),
                    PriceType = (string)x.Attribute("price_type"),
                    Price = GetInt(x, "price"),
                    Gender = (string)x.Attribute("gender")
                });
            }

            foreach (var x in root.Element("lineup_list")?.Elements("lineup") ?? Enumerable.Empty<XElement>())
            {
                if (!TryGetInt(x, "pid", out var pid) || !TryGetUInt(x, "item_key", out var itemKey))
                    continue;

                odds.Lineup.Add(new RandomShopOddsLineup
                {
                    PackageId = pid,
                    RewardType = (string)x.Attribute("reward_type"),
                    ItemKey = itemKey,
                    ColorGroup = (string)x.Attribute("color_group"),
                    EffectGroup = (string)x.Attribute("effect_group"),
                    PeriodGroup = (string)x.Attribute("period_group"),
                    DefaultColor = GetInt(x, "default_color"),
                    Prob = GetInt(x, "prob"),
                    Grade = GetInt(x, "grade")
                });
            }

            foreach (var x in root.Element("period_list")?.Elements("period") ?? Enumerable.Empty<XElement>())
            {
                var group = (string)x.Attribute("group");
                if (string.IsNullOrEmpty(group) ||
                    !RandomShopInfo.TryParsePeriodType((string)x.Attribute("type"), out var periodType) ||
                    !TryGetInt(x, "value", out var value) || value < 0 || value > ushort.MaxValue)
                    continue;

                Add(odds.PeriodGroups, group, new RandomShopPeriod { PeriodType = periodType, Period = (ushort)value }, GetInt(x, "prob"));
            }

            foreach (var x in root.Element("effect_list")?.Elements("effect") ?? Enumerable.Empty<XElement>())
            {
                var group = (string)x.Attribute("group");
                if (string.IsNullOrEmpty(group) || !TryGetUInt(x, "id", out var effect))
                    continue;

                Add(odds.EffectGroups, group, effect, GetInt(x, "prob"));
            }

            foreach (var x in root.Element("color_list")?.Elements("color") ?? Enumerable.Empty<XElement>())
            {
                var group = (string)x.Attribute("group");
                if (string.IsNullOrEmpty(group) || !TryGetInt(x, "color", out var color) || color < 0 || color > byte.MaxValue)
                    continue;

                Add(odds.ColorGroups, group, (byte)color, GetInt(x, "prob"));
            }

            return odds;
        }

        /// <summary>
        /// The groups and prize weight for an item of a synthetic package. Only ITEM prizes of PEN
        /// packages count (the coupon packages hold capsules/coupons the synthetic packages never have).
        /// <list type="bullet">
        /// <item>Item in the lineup: its groups (first entry) and its prob (average over its entries).</item>
        /// <item>Otherwise the groups most lineup entries of the same category and sub category use
        /// (e.g. melee -> Def_Weapon_Melee), and as weight the mean prob of the lineup entries of the
        /// same category (weapon/costume). The mean is what an item would get if the package's 10000
        /// were split evenly, so known common items (e.g. 681) stay above it and known rare ones
        /// (e.g. 192) below it.</item>
        /// <item>Effect group for weapon sub categories the lineup lacks: by name (sentry, gun, mind).
        /// Period group the category has no entry for: Def_WP_Days / Def_CT_Days.</item>
        /// </list>
        /// </summary>
        public RandomShopOddsSlot GetSlot(ItemNumber itemNumber, RandomShopKind kind)
        {
            // a lineup pid missing from package_list counts as PEN
            var notPen = new HashSet<int>(Packages.Where(x => !x.IsPen).Select(x => x.Id));
            var entries = Lineup.Where(x => x.IsItem && !notPen.Contains(x.PackageId)).ToList();

            var own = entries.Where(x => x.ItemKey == itemNumber.Id).ToList();
            var category = entries.Where(x => ((ItemNumber)x.ItemKey).Category == itemNumber.Category).ToList();
            var subCategory = category.Where(x => ((ItemNumber)x.ItemKey).SubCategory == itemNumber.SubCategory).ToList();

            var slot = new RandomShopOddsSlot { InLineup = own.Count > 0 };
            if (own.Count > 0)
            {
                var first = own[0];
                slot.ColorGroup = first.ColorGroup;
                slot.EffectGroup = first.EffectGroup;
                slot.PeriodGroup = first.PeriodGroup;
                slot.Weight = Mean(own);
                return slot;
            }

            slot.Weight = category.Count > 0 ? Mean(category) : 1;
            slot.EffectGroup = MostCommon(subCategory, x => x.EffectGroup);
            if (slot.EffectGroup == null && itemNumber.Category == ItemCategory.Weapon &&
                s_weaponEffectFallback.TryGetValue(itemNumber.SubCategory, out var name))
                slot.EffectGroup = name;

            slot.PeriodGroup = MostCommon(subCategory, x => x.PeriodGroup) ?? MostCommon(category, x => x.PeriodGroup) ??
                               (kind == RandomShopKind.Weapon ? DefaultWeaponPeriodGroup : DefaultCostumePeriodGroup);
            slot.ColorGroup = MostCommon(subCategory, x => x.ColorGroup) ?? MostCommon(category, x => x.ColorGroup);
            return slot;
        }

        /// <summary>
        /// Weighted pick; entries with a weight &lt;= 0 are never picked. Returns null when nothing has
        /// a weight.
        /// </summary>
        public static RandomShopWeighted<T> Pick<T>(IList<RandomShopWeighted<T>> values, Random random)
        {
            return Pick(values, x => x.Weight, random);
        }

        /// <summary>Weighted pick by <paramref name="weight"/>; default when nothing has a weight &gt; 0.</summary>
        public static T Pick<T>(IList<T> values, Func<T, int> weight, Random random)
        {
            if (values == null)
                return default;

            var total = values.Sum(x => (long)Math.Max(0, weight(x)));
            if (total <= 0)
                return default;

            var pick = random.NextInt64(total);
            foreach (var value in values)
            {
                pick -= Math.Max(0, weight(value));
                if (pick < 0)
                    return value;
            }

            return values.Last(x => weight(x) > 0);
        }

        private static int Mean(IList<RandomShopOddsLineup> entries)
        {
            return Math.Max(1, (int)Math.Round(entries.Average(x => (double)Math.Max(0, x.Prob)), MidpointRounding.AwayFromZero));
        }

        // GroupBy keeps first-seen order and OrderByDescending is stable, so ties go to the first one
        private static string MostCommon(IEnumerable<RandomShopOddsLineup> entries, Func<RandomShopOddsLineup, string> selector)
        {
            return entries
                .Select(selector)
                .Where(x => !string.IsNullOrEmpty(x))
                .GroupBy(x => x, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .FirstOrDefault();
        }

        private static void Add<T>(IDictionary<string, IList<RandomShopWeighted<T>>> groups, string group, T value, int weight)
        {
            if (!groups.TryGetValue(group, out var list))
            {
                list = new List<RandomShopWeighted<T>>();
                groups.Add(group, list);
            }

            list.Add(new RandomShopWeighted<T>(value, weight));
        }

        private static int GetInt(XElement element, string attribute)
        {
            return TryGetInt(element, attribute, out var value) ? value : 0;
        }

        private static bool TryGetInt(XElement element, string attribute, out int value)
        {
            return int.TryParse((string)element.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetUInt(XElement element, string attribute, out uint value)
        {
            return uint.TryParse((string)element.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
