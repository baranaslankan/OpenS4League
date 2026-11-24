using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>
    /// Which half of the Fumbi random shop a package belongs to. Prices, gauge gain and period
    /// weights in new_random_shop.x7 come in costume/weapon pairs.
    /// </summary>
    public enum RandomShopKind
    {
        Costume,
        Weapon
    }

    /// <summary>
    /// One rollable period with its relative weight (new_random_shop.x7 &lt;period&gt;).
    /// </summary>
    public class RandomShopPeriod
    {
        public ItemPeriodType PeriodType { get; set; }
        public ushort Period { get; set; }
        public int CostumeWeight { get; set; }
        public int WeaponWeight { get; set; }

        public int GetWeight(RandomShopKind kind)
        {
            return kind == RandomShopKind.Costume ? CostumeWeight : WeaponWeight;
        }
    }

    /// <summary>
    /// Settings of the Fumbi random shop, read from the client's xml/new_random_shop.x7.
    /// <see cref="Default"/> holds the values of the 2013/2014 EU client file so the shop works
    /// without that file in the server data folder.
    /// </summary>
    public class RandomShopInfo
    {
        public int CostumePrice { get; set; }
        public int WeaponPrice { get; set; }

        // <chance costume_probability weapon_probability gauge_max costume_acc costume_req weapon_acc weapon_req>
        // *_probability is parsed but not used: its meaning is unknown.
        public int CostumeProbability { get; set; }
        public int WeaponProbability { get; set; }
        public int GaugeMax { get; set; }
        public int CostumeGaugeGain { get; set; }
        public int CostumeGaugeRequired { get; set; }
        public int WeaponGaugeGain { get; set; }
        public int WeaponGaugeRequired { get; set; }

        // <probability_balance count_max> - meaning unknown, not used
        public int BalanceCountMax { get; set; }

        // <level limit> - not enforced by the server, see RandomShopService
        public int LevelLimit { get; set; }

        public IList<RandomShopPeriod> Periods { get; set; }

        /// <summary>
        /// The 2019 EU odds (<see cref="RandomShopOdds.FileName"/>), or null when that file is not in
        /// the server data folder. When set they replace <see cref="Periods"/> and the uniform prize pick.
        /// </summary>
        public RandomShopOdds Odds { get; set; }

        public RandomShopInfo()
        {
            Periods = new List<RandomShopPeriod>();
        }

        public int GetPrice(RandomShopKind kind)
        {
            return kind == RandomShopKind.Costume ? CostumePrice : WeaponPrice;
        }

        public int GetGaugeGain(RandomShopKind kind)
        {
            return kind == RandomShopKind.Costume ? CostumeGaugeGain : WeaponGaugeGain;
        }

        public int GetGaugeRequired(RandomShopKind kind)
        {
            return kind == RandomShopKind.Costume ? CostumeGaugeRequired : WeaponGaugeRequired;
        }

        public static RandomShopInfo Default
        {
            get
            {
                return new RandomShopInfo
                {
                    CostumePrice = 800,
                    WeaponPrice = 3000,
                    CostumeProbability = 72,
                    WeaponProbability = 72,
                    GaugeMax = 10000,
                    CostumeGaugeGain = 800,
                    CostumeGaugeRequired = 10000,
                    WeaponGaugeGain = 3000,
                    WeaponGaugeRequired = 10000,
                    BalanceCountMax = 200,
                    LevelLimit = 4,
                    Periods = new List<RandomShopPeriod>
                    {
                        new RandomShopPeriod { PeriodType = ItemPeriodType.None, Period = 0, CostumeWeight = 4, WeaponWeight = 4 },
                        new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 10, CostumeWeight = 3, WeaponWeight = 3 },
                        new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 7, CostumeWeight = 7, WeaponWeight = 7 },
                        new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 5, CostumeWeight = 15, WeaponWeight = 15 },
                        new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 3, CostumeWeight = 170, WeaponWeight = 170 },
                        new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 1, CostumeWeight = 800, WeaponWeight = 800 }
                    }
                };
            }
        }

        /// <summary>
        /// Parses new_random_shop.x7. Missing elements/attributes keep the <see cref="Default"/> value.
        /// </summary>
        public static RandomShopInfo Parse(XDocument doc)
        {
            var info = Default;
            var root = doc.Root;
            if (root == null)
                return info;

            var price = root.Element("price");
            info.CostumePrice = GetInt(price, "costume", info.CostumePrice);
            info.WeaponPrice = GetInt(price, "weapon", info.WeaponPrice);

            var chance = root.Element("chance");
            info.CostumeProbability = GetInt(chance, "costume_probability", info.CostumeProbability);
            info.WeaponProbability = GetInt(chance, "weapon_probability", info.WeaponProbability);
            info.GaugeMax = GetInt(chance, "gauge_max", info.GaugeMax);
            info.CostumeGaugeGain = GetInt(chance, "costume_acc", info.CostumeGaugeGain);
            info.CostumeGaugeRequired = GetInt(chance, "costume_req", info.CostumeGaugeRequired);
            info.WeaponGaugeGain = GetInt(chance, "weapon_acc", info.WeaponGaugeGain);
            info.WeaponGaugeRequired = GetInt(chance, "weapon_req", info.WeaponGaugeRequired);

            info.BalanceCountMax = GetInt(root.Element("probability_balance"), "count_max", info.BalanceCountMax);
            info.LevelLimit = GetInt(root.Element("level"), "limit", info.LevelLimit);

            var periods = root.Element("periods")?.Elements("period")
                .Select(x => new
                {
                    Ok = TryParsePeriodType((string)x.Attribute("period_type"), out var periodType),
                    Period = new RandomShopPeriod
                    {
                        PeriodType = periodType,
                        Period = (ushort)GetInt(x, "period_value", 0),
                        CostumeWeight = GetInt(x, "costume_prob", 0),
                        WeaponWeight = GetInt(x, "weapon_prob", 0)
                    }
                })
                .Where(x => x.Ok)
                .Select(x => x.Period)
                .ToList();

            if (periods != null && periods.Count > 0)
                info.Periods = periods;

            return info;
        }

        /// <summary>
        /// Period type names as the client spells them (NONE, HOURS, DAYS, USECNT).
        /// </summary>
        public static bool TryParsePeriodType(string value, out ItemPeriodType periodType)
        {
            switch (value?.Trim().ToUpperInvariant())
            {
                case "NONE":
                    periodType = ItemPeriodType.None;
                    return true;

                case "HOURS":
                    periodType = ItemPeriodType.Hours;
                    return true;

                case "DAYS":
                    periodType = ItemPeriodType.Days;
                    return true;

                case "USECNT":
                    periodType = ItemPeriodType.Units;
                    return true;

                default:
                    periodType = ItemPeriodType.None;
                    return false;
            }
        }

        private static int GetInt(XElement element, string attribute, int defaultValue)
        {
            var value = (string)element?.Attribute(attribute);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : defaultValue;
        }
    }

    /// <summary>
    /// A Fumbi shop "machine". The client takes name and description from its own
    /// randomshop_package.x7 by <see cref="Id"/>, so ids must come from that file.
    /// </summary>
    public class RandomShopPackage
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public RandomShopKind Kind { get; set; }

        /// <summary>None means both genders.</summary>
        public Gender Gender { get; set; }

        public int Price { get; set; }
        public IList<ShopItem> Items { get; set; }

        /// <summary>What can be rolled for each of <see cref="Items"/> (same order).</summary>
        public IList<RandomShopEntry> Entries { get; set; }

        public RandomShopPackage()
        {
            Items = new List<ShopItem>();
            Entries = new List<RandomShopEntry>();
        }

        public bool CanUse(CharacterGender gender)
        {
            switch (Gender)
            {
                case Gender.Male:
                    return gender == CharacterGender.Male;

                case Gender.Female:
                    return gender == CharacterGender.Female;

                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// One prize of a package with everything the roll picks from. The group names go to the client
    /// in RandomShopUpdateInfoAck, so the client lists what the server rolls.
    /// </summary>
    public class RandomShopEntry
    {
        public ShopItem Item { get; set; }

        /// <summary>Relative weight of the item within its package.</summary>
        public int Weight { get; set; }

        public string PeriodGroup { get; set; }
        public IList<RandomShopWeighted<RandomShopPeriod>> Periods { get; set; }

        public string EffectGroup { get; set; }
        public IList<RandomShopWeighted<uint>> Effects { get; set; }

        /// <summary>
        /// true: the prize gets all effects of its shop effect group (no 2019 odds for it);
        /// false: one effect is picked from <see cref="Effects"/>.
        /// </summary>
        public bool UseShopEffects { get; set; }

        public string ColorGroup { get; set; }

        /// <summary>The colours the client is told about; the roll picks by weight (weight 0 = shown only).</summary>
        public IList<RandomShopWeighted<byte>> Colors { get; set; }

        public RandomShopEntry()
        {
            Weight = 1;
            Periods = new List<RandomShopWeighted<RandomShopPeriod>>();
            Effects = new List<RandomShopWeighted<uint>>();
            Colors = new List<RandomShopWeighted<byte>>();
        }
    }
}
