using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenS4L;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Handlers;
using OpenS4L.Server.Game.Services;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Fumbi random shop: settings parsing, package building, the client data layout, rolling and
    /// the ShopHandler messages over the harness. XML here is synthetic, shaped like new_random_shop.x7.
    /// </summary>
    public class RandomShopTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        // Seeded by GameTestContext: 2000006 (weapon sub 0 -> melee), 2010001 (weapon sub 1 -> shooting)
        private static readonly ItemNumber MaleShirt = new ItemNumber(ItemCategory.Costume, (byte)CostumeSlot.Shirt, 7);
        private static readonly ItemNumber FemaleShirt = new ItemNumber(ItemCategory.Costume, (byte)CostumeSlot.Shirt, 8);
        private const int MeleePackage = 1001001;
        private const int ShootingPackage = 1002001;
        private const int MaleShirtPackage = 2104001;
        private const int FemaleShirtPackage = 2204001;

        public RandomShopTests()
        {
            GameFixtures.SeedShopItem(_ctx.GameData, MaleShirt, gender: Gender.Male);
            GameFixtures.SeedShopItem(_ctx.GameData, FemaleShirt, gender: Gender.Female);
        }

        [Fact]
        public void Parse_readsPricesGaugeAndPeriods()
        {
            var doc = XDocument.Parse(@"<random_shop string_table=""x"">
  <price costume=""900"" weapon=""3100"" />
  <chance costume_probability=""70"" weapon_probability=""71"" gauge_max=""9000"" costume_acc=""500"" costume_req=""8000"" weapon_acc=""2000"" weapon_req=""7000"" />
  <probability_balance count_max=""150"" />
  <periods>
    <period period_type=""NONE"" period_value=""0"" costume_prob=""2"" weapon_prob=""5"" />
    <period period_type=""HOURS"" period_value=""3"" costume_prob=""100"" weapon_prob=""90"" />
    <period period_type=""BOGUS"" period_value=""1"" costume_prob=""1"" weapon_prob=""1"" />
  </periods>
  <level limit=""6"" />
</random_shop>");

            var info = RandomShopInfo.Parse(doc);

            Assert.Equal(900, info.GetPrice(RandomShopKind.Costume));
            Assert.Equal(3100, info.GetPrice(RandomShopKind.Weapon));
            Assert.Equal(9000, info.GaugeMax);
            Assert.Equal(500, info.GetGaugeGain(RandomShopKind.Costume));
            Assert.Equal(8000, info.GetGaugeRequired(RandomShopKind.Costume));
            Assert.Equal(2000, info.GetGaugeGain(RandomShopKind.Weapon));
            Assert.Equal(7000, info.GetGaugeRequired(RandomShopKind.Weapon));
            Assert.Equal(70, info.CostumeProbability);
            Assert.Equal(150, info.BalanceCountMax);
            Assert.Equal(6, info.LevelLimit);
            Assert.Equal(2, info.Periods.Count); // unknown period type skipped
            Assert.Equal(ItemPeriodType.Hours, info.Periods[1].PeriodType);
            Assert.Equal(3, info.Periods[1].Period);
            Assert.Equal(90, info.Periods[1].GetWeight(RandomShopKind.Weapon));
        }

        [Fact]
        public void Parse_missingValues_keepDefaults()
        {
            var info = RandomShopInfo.Parse(XDocument.Parse("<random_shop><price costume=\"x\" /></random_shop>"));
            var expected = RandomShopInfo.Default;

            Assert.Equal(expected.CostumePrice, info.CostumePrice);
            Assert.Equal(expected.WeaponPrice, info.WeaponPrice);
            Assert.Equal(expected.GaugeMax, info.GaugeMax);
            Assert.Equal(expected.Periods.Count, info.Periods.Count);
        }

        [Fact]
        public void Default_matchesClientFileValues()
        {
            var info = RandomShopInfo.Default;
            Assert.Equal(800, info.CostumePrice);
            Assert.Equal(3000, info.WeaponPrice);
            Assert.Equal(10000, info.GaugeMax);
            Assert.Equal(new[] { 4, 3, 7, 15, 170, 800 }, info.Periods.Select(x => x.CostumeWeight));
            Assert.Equal(new ushort[] { 0, 10, 7, 5, 3, 1 }, info.Periods.Select(x => x.Period));
            Assert.Equal(4, info.LevelLimit);
        }

        [Theory]
        [InlineData("NONE", ItemPeriodType.None)]
        [InlineData("hours", ItemPeriodType.Hours)]
        [InlineData("DAYS", ItemPeriodType.Days)]
        [InlineData("USECNT", ItemPeriodType.Units)]
        public void TryParsePeriodType_matchesClientNames(string value, ItemPeriodType expected)
        {
            Assert.True(RandomShopInfo.TryParsePeriodType(value, out var periodType));
            Assert.Equal(expected, periodType);
        }

        [Fact]
        public void RollPeriod_respectsWeights()
        {
            var info = new RandomShopInfo
            {
                Periods = new List<RandomShopPeriod>
                {
                    new RandomShopPeriod { PeriodType = ItemPeriodType.None, CostumeWeight = 0, WeaponWeight = 1 },
                    new RandomShopPeriod { PeriodType = ItemPeriodType.Hours, Period = 1, CostumeWeight = 5, WeaponWeight = 0 }
                }
            };
            var random = new Random(1);
            for (var i = 0; i < 200; ++i)
            {
                Assert.Equal(ItemPeriodType.Hours, RandomShopService.RollPeriod(info, RandomShopKind.Costume, random).PeriodType);
                Assert.Equal(ItemPeriodType.None, RandomShopService.RollPeriod(info, RandomShopKind.Weapon, random).PeriodType);
            }
        }

        [Fact]
        public void RollPeriod_noWeights_returnsNull()
        {
            var info = new RandomShopInfo { Periods = new List<RandomShopPeriod> { new RandomShopPeriod() } };
            Assert.Null(RandomShopService.RollPeriod(info, RandomShopKind.Costume, new Random(1)));
        }

        [Fact]
        public void SelectPrice_prefersRolledPeriodThenPermanent()
        {
            var shopItem = _ctx.GameData.GetShopItem((ItemNumber)2010001u);
            var info = shopItem.ItemInfos[0];
            var original = info.PriceGroup;
            try
            {
                info.PriceGroup = new ShopPriceGroup(new ShopPriceGroupEntity
                {
                    Id = 77, PriceType = (byte)ItemPriceType.PEN, Name = "pen",
                    ShopPrices = new List<ShopPriceEntity>
                    {
                        new ShopPriceEntity { Id = 1, PeriodType = (byte)ItemPeriodType.Hours, Period = 3, Price = 1, IsEnabled = true },
                        new ShopPriceEntity { Id = 2, PeriodType = (byte)ItemPeriodType.None, Period = 0, Price = 1, IsEnabled = true },
                        new ShopPriceEntity { Id = 3, PeriodType = (byte)ItemPeriodType.Hours, Period = 1, Price = 1, IsEnabled = false }
                    }
                });

                Assert.Equal(1, RandomShopService.SelectPrice(shopItem, ItemPeriodType.Hours, 3).price.Id);
                // disabled exact match -> permanent
                Assert.Equal(2, RandomShopService.SelectPrice(shopItem, ItemPeriodType.Hours, 1).price.Id);
                Assert.Equal(2, RandomShopService.SelectPrice(shopItem, ItemPeriodType.Hours, 10).price.Id);
            }
            finally
            {
                info.PriceGroup = original;
            }
        }

        [Fact]
        public void BuildPackages_groupsByWeaponTypeAndCostumeGenderSlot()
        {
            var packages = RandomShopService.BuildPackages(_ctx.GameData.ShopItems.Values, RandomShopInfo.Default);

            var melee = Assert.Single(packages, x => x.Id == MeleePackage);
            Assert.Equal(new[] { 2000006u }, melee.Items.Select(x => x.ItemNumber.Id));
            Assert.Equal(RandomShopKind.Weapon, melee.Kind);
            Assert.Equal(3000, melee.Price);
            Assert.Equal(Gender.None, melee.Gender);

            var shooting = Assert.Single(packages, x => x.Id == ShootingPackage);
            Assert.Equal(new[] { 2010001u }, shooting.Items.Select(x => x.ItemNumber.Id));

            var male = Assert.Single(packages, x => x.Id == MaleShirtPackage);
            Assert.Equal(new[] { MaleShirt.Id }, male.Items.Select(x => x.ItemNumber.Id));
            Assert.Equal(800, male.Price);
            Assert.Equal(Gender.Male, male.Gender);
            var female = Assert.Single(packages, x => x.Id == FemaleShirtPackage);
            Assert.Equal(new[] { FemaleShirt.Id }, female.Items.Select(x => x.ItemNumber.Id));

            // no sub weapons / pants etc. seeded -> empty packages are left out; skills/coupons never included
            Assert.DoesNotContain(packages, x => x.Id == 1003001 || x.Id == 2105001);
            Assert.DoesNotContain(packages.SelectMany(x => x.Items), x => x.ItemNumber.Id == 3050001u || x.ItemNumber.Id == 6000015u);
        }

        [Fact]
        public void BuildClientData_writesSectionsInClientOrder()
        {
            var info = RandomShopInfo.Default;
            var packages = RandomShopService.BuildPackages(_ctx.GameData.ShopItems.Values, info);

            var data = RandomShopService.BuildClientData(packages, info);

            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                // packages
                Assert.Equal(packages.Count, r.ReadInt32());
                var genders = new Dictionary<int, string>();
                foreach (var package in packages)
                {
                    Assert.Equal(package.Id, r.ReadInt32());
                    Assert.Equal(package.Name, r.ReadProudString());
                    Assert.Equal("N" + package.Id, r.ReadProudString());
                    Assert.Equal("D" + package.Id, r.ReadProudString());
                    Assert.Equal("on", r.ReadProudString());
                    Assert.Equal("pen", r.ReadProudString());
                    Assert.Equal(package.Price, r.ReadInt32());
                    genders[package.Id] = r.ReadProudString();
                }
                Assert.Equal("all", genders[MeleePackage]);
                Assert.Equal("man", genders[MaleShirtPackage]);
                Assert.Equal("woman", genders[FemaleShirtPackage]);

                // effects (fixture effect group 20 has effect 1)
                var effectCount = r.ReadInt32();
                var effects = new List<(string group, uint effect)>();
                for (var i = 0; i < effectCount; ++i)
                {
                    Assert.Equal(0, r.ReadInt32());
                    Assert.Equal(0, r.ReadByte());
                    effects.Add((r.ReadProudString(), r.ReadUInt32()));
                }
                Assert.Contains(("e20", 1u), effects);

                // colors
                var colorCount = r.ReadInt32();
                var colorGroups = new HashSet<string>();
                for (var i = 0; i < colorCount; ++i)
                {
                    Assert.Equal(0, r.ReadInt32());
                    Assert.Equal(0, r.ReadByte());
                    colorGroups.Add(r.ReadProudString());
                    r.ReadByte();
                }
                Assert.Contains("c0", colorGroups);

                // lineup
                var lineupCount = r.ReadInt32();
                Assert.Equal(packages.Sum(x => x.Items.Count), lineupCount);
                var lineup = new List<(int pid, int item)>();
                for (var i = 0; i < lineupCount; ++i)
                {
                    Assert.Equal(0, r.ReadInt32());
                    Assert.Equal(0, r.ReadByte());
                    var pid = r.ReadInt32();
                    Assert.Equal("item", r.ReadProudString());
                    Assert.Equal(1, r.ReadInt32());
                    var item = r.ReadInt32();
                    Assert.Contains(r.ReadProudString(), colorGroups);
                    Assert.Contains(r.ReadProudString(), effects.Select(x => x.group));
                    Assert.Equal(RandomShopService.PeriodGroup, r.ReadProudString());
                    Assert.Equal(0, r.ReadInt32());
                    lineup.Add((pid, item));
                }
                Assert.Contains((MaleShirtPackage, (int)MaleShirt.Id), lineup);

                // periods
                Assert.Equal(info.Periods.Count, r.ReadInt32());
                foreach (var period in info.Periods)
                {
                    Assert.Equal(0, r.ReadInt32());
                    Assert.Equal(0, r.ReadByte());
                    Assert.Equal(RandomShopService.PeriodGroup, r.ReadProudString());
                    Assert.Equal((int)period.PeriodType, r.ReadInt32());
                    Assert.Equal(period.Period, r.ReadInt32());
                }

                Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
            }
        }

        [Fact]
        public async Task UpdateCheck_outdatedVersion_sendsAckAndCompressedData()
        {
            var (plr, channel) = await LoginAsync(9401);
            var service = _ctx.Get<RandomShopService>();

            await _ctx.Get<ShopHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new RandomShopUpdateCheckReqMessage { Unk1 = "", Unk2 = 0 });

            var messages = Messages(channel);
            var check = messages.OfType<RandomShopUpdateCheckAckMessage>().Last();
            var update = messages.OfType<RandomShopUpdateInfoAckMessage>().Last();
            Assert.True(messages.IndexOf(check) < messages.IndexOf(update));

            var expected = RandomShopService.BuildClientData(service.Packages, service.Info);
            Assert.Equal(service.GetClientData().Version, check.Unk);
            Assert.Equal(check.Unk, update.Unk5);
            Assert.Equal(RandomShopService.AllSections, update.Unk1);
            if (update.Unk3 == 0)
            {
                // native LZO not loadable on this host -> raw data, client skips decompression
                Assert.Equal(0, update.Unk4);
                Assert.Equal(expected, update.Unk2);
            }
            else
            {
                Assert.Equal(update.Unk2.Length, update.Unk3);
                Assert.Equal(expected.Length, update.Unk4);
                Assert.Equal(expected, update.Unk2.DecompressLZO(update.Unk4));
            }
        }

        [Fact]
        public async Task UpdateCheck_currentVersion_sendsOnlyAck()
        {
            var (plr, channel) = await LoginAsync(9402);
            var version = _ctx.Get<RandomShopService>().GetClientData().Version;

            await _ctx.Get<ShopHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new RandomShopUpdateCheckReqMessage { Unk1 = version });

            var messages = Messages(channel);
            Assert.Equal(version, messages.OfType<RandomShopUpdateCheckAckMessage>().Last().Unk);
            Assert.Empty(messages.OfType<RandomShopUpdateInfoAckMessage>());
        }

        [Fact]
        public async Task Roll_chargesPenAndGrantsItemBeforeAck()
        {
            var (plr, channel) = await LoginAsync(9403);
            plr.PEN = 5000;

            await Roll(plr, ShootingPackage);

            var messages = Messages(channel);
            var ack = messages.OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Equal(RandomShopService.ResultSuccess, ack.Unk1);
            var prize = Assert.Single(ack.Items);
            Assert.Equal(2010001u, prize.ItemId.Id);
            Assert.Equal(2000u, plr.PEN);

            var item = FindByShopItemId(plr, prize.ShopItemId);
            Assert.NotNull(item);
            Assert.Equal(prize.ItemId, item.ItemNumber);
            // fixture shop only has a permanent price -> rolled hour periods fall back to it
            Assert.Equal(ItemPeriodType.None, prize.PeriodType);
            Assert.Equal(1u, prize.EffectGroupId);

            var add = messages.OfType<ItemUpdateInventoryAckMessage>().Last();
            Assert.True(messages.IndexOf(add) < messages.IndexOf(ack));
            Assert.Contains(messages, x => x is MoneyRefreshCashInfoAckMessage);
            Assert.Equal(RandomShopInfo.Default.WeaponGaugeGain, _ctx.Get<RandomShopService>().GetGauge(plr.Account.Id));
        }

        [Fact]
        public async Task Roll_notEnoughPen_changesNothing()
        {
            var (plr, channel) = await LoginAsync(9404);
            plr.PEN = 2999;
            var itemCount = plr.Inventory.Count;

            await Roll(plr, ShootingPackage);

            var ack = Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Equal(RandomShopService.ResultNotEnoughMoney, ack.Unk1);
            Assert.Empty(ack.Items);
            Assert.Equal(2999u, plr.PEN);
            Assert.Equal(itemCount, plr.Inventory.Count);
            Assert.Equal(0, _ctx.Get<RandomShopService>().GetGauge(plr.Account.Id));
        }

        [Fact]
        public async Task Roll_unknownPackage_returnsNotExist()
        {
            var (plr, channel) = await LoginAsync(9405);
            plr.PEN = 100000;

            await Roll(plr, 1234567);

            Assert.Equal(RandomShopService.ResultNotExistPackage,
                Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last().Unk1);
            Assert.Equal(100000u, plr.PEN);
        }

        [Fact]
        public async Task Roll_otherGenderCostume_fails()
        {
            var (plr, channel) = await LoginAsync(9406); // male character
            plr.PEN = 100000;

            await Roll(plr, FemaleShirtPackage);

            Assert.Equal(RandomShopService.ResultMakeItemFail,
                Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last().Unk1);
            Assert.Equal(100000u, plr.PEN);

            await Roll(plr, MaleShirtPackage);

            var ack = Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Equal(RandomShopService.ResultSuccess, ack.Unk1);
            Assert.Equal(MaleShirt, ack.Items[0].ItemId);
            Assert.Equal(100000u - 800u, plr.PEN);
        }

        [Fact]
        public async Task Roll_fullGauge_grantsPermanentBonusItem()
        {
            var (plr, channel) = await LoginAsync(9407);
            plr.PEN = 3000;
            var service = _ctx.Get<RandomShopService>();
            service.SetGauge(plr.Account.Id, 9000);

            await Roll(plr, MeleePackage);

            var ack = Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Equal(RandomShopService.ResultSuccess, ack.Unk1);
            Assert.Equal(2, ack.Items.Length);
            Assert.Equal(ItemPeriodType.None, ack.Items[1].PeriodType);
            Assert.NotNull(FindByShopItemId(plr, ack.Items[1].ShopItemId));
            Assert.NotEqual(ack.Items[0].ShopItemId, ack.Items[1].ShopItemId);
            // 9000 + 3000 capped at 10000, minus the 10000 needed
            Assert.Equal(0, service.GetGauge(plr.Account.Id));
            Assert.Equal(0u, plr.PEN);
        }

        [Fact]
        public async Task Roll_gaugeBelowRequired_noBonus()
        {
            var (plr, channel) = await LoginAsync(9408);
            plr.PEN = 3000;
            _ctx.Get<RandomShopService>().SetGauge(plr.Account.Id, 6000);

            await Roll(plr, MeleePackage);

            var ack = Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Single(ack.Items);
            Assert.Equal(9000, _ctx.Get<RandomShopService>().GetGauge(plr.Account.Id));
        }

        #region 2019 odds
        // Synthetic, shaped like _eu_new_random_shop.xml (2019 EU). 2000006 is in a PEN package and a
        // coupon package; 2010001 (seeded shooting weapon) and the seeded shirts are not in the lineup.
        private const string OddsXml = @"<random_shop_info version="""">
  <package_list>
    <package id=""1004001"" open=""new"" name_key=""N1004001"" desc_key=""D1004001"" price_type=""pen"" price=""7000"" gender=""all""/>
    <package id=""2001006"" open=""new"" name_key=""N2001006"" desc_key=""D2001006"" price_type=""pen"" price=""3000"" gender=""man""/>
    <package id=""4020001"" open=""new"" name_key=""N4020001"" desc_key=""D4020001"" price_type=""coupon"" price=""50"" gender=""man""/>
    <package id=""bad"" open=""off"" price_type=""pen"" price=""1"" gender=""all""/>
  </package_list>
  <period_list>
    <period group=""Def_WP_Days"" value=""1"" type=""DAYS"" prob=""4000"" grade=""0""/>
    <period group=""Def_WP_Days"" value=""0"" type=""NONE"" prob=""200"" grade=""15""/>
    <period group=""Def_CT_Days"" value=""3"" type=""DAYS"" prob=""2500"" grade=""0""/>
    <period group=""Def_CT_Days"" value=""7"" type=""BOGUS"" prob=""1500"" grade=""0""/>
  </period_list>
  <lineup_list>
    <lineup pid=""1004001"" reward_type=""ITEM"" reward_value=""0"" item_key=""2000006"" color_group=""Dist_1"" effect_group=""Def_Weapon_Melee"" period_group=""Def_WP_Days"" default_color=""0"" prob=""681"" grade=""0""/>
    <lineup pid=""1004001"" reward_type=""ITEM"" reward_value=""0"" item_key=""2000074"" color_group=""Dist_1"" effect_group=""Def_Weapon_Melee"" period_group=""Def_WP_Days"" default_color=""0"" prob=""192"" grade=""20""/>
    <lineup pid=""1004001"" reward_type=""ITEM"" reward_value=""0"" item_key=""2010050"" color_group=""Dist_1"" effect_group=""Def_Weapon_Rifle"" period_group=""Def_WP_Days"" default_color=""0"" prob=""205"" grade=""20""/>
    <lineup pid=""4020001"" reward_type=""ITEM"" reward_value=""0"" item_key=""2000006"" color_group=""Dist_1"" effect_group=""Coupon_None"" period_group=""CP_CAPSULE"" default_color=""0"" prob=""4000"" grade=""0""/>
    <lineup pid=""2001006"" reward_type=""ITEM"" reward_value=""0"" item_key=""1020001"" color_group=""Unique_2"" effect_group=""Def_Top"" period_group=""Def_CT_Days"" default_color=""0"" prob=""750"" grade=""0""/>
    <lineup pid=""2001006"" reward_type=""ITEM"" reward_value=""0"" item_key=""1020002"" color_group=""Dist_4"" effect_group=""Def_Top"" period_group=""Def_CT_Days"" default_color=""0"" prob=""208"" grade=""0""/>
    <lineup pid=""2001006"" reward_type=""ITEM"" reward_value=""0"" item_key=""1020003"" color_group=""Dist_4"" effect_group=""Def_Top"" period_group=""Def_CT_Days"" default_color=""0"" prob=""212"" grade=""0""/>
    <lineup pid=""2001006"" reward_type=""ITEM"" reward_value=""0"" item_key=""x"" color_group=""Dist_4"" effect_group=""Def_Top"" period_group=""Def_CT_Days"" default_color=""0"" prob=""5"" grade=""0""/>
  </lineup_list>
  <effect_list>
    <effect group=""Def_Weapon_Melee"" id=""1299600001"" prob=""6500"" grade=""0""/>
    <effect group=""Def_Weapon_Melee"" id=""1299600006"" prob=""2500"" grade=""10""/>
    <effect group=""Def_Weapon_Melee"" id=""21001"" prob=""1000"" grade=""30""/>
    <effect group=""Def_Weapon_Rifle"" id=""1299600001"" prob=""6500"" grade=""0""/>
    <effect group=""Def_Weapon_Rifle"" id=""22001"" prob=""1000"" grade=""30""/>
    <effect group=""Def_Top"" id=""12002"" prob=""1000"" grade=""30""/>
    <effect group=""Coupon_None"" id=""0"" prob=""10000"" grade=""0""/>
    <effect group=""Def_Weapon_Sentry"" id=""nope"" prob=""1"" grade=""0""/>
  </effect_list>
  <color_list>
    <color group=""Dist_1"" color=""0"" prob=""10000"" grade=""0""/>
    <color group=""Dist_4"" color=""0"" prob=""2500"" grade=""0""/>
    <color group=""Dist_4"" color=""1"" prob=""2500"" grade=""0""/>
    <color group=""Dist_4"" color=""2"" prob=""2500"" grade=""0""/>
    <color group=""Dist_4"" color=""3"" prob=""2500"" grade=""0""/>
    <color group=""Unique_2"" color=""2"" prob=""10000"" grade=""0""/>
  </color_list>
</random_shop_info>";

        private const uint KnownEffectA = 1299600001;
        private const uint KnownEffectB = 1299600006;

        private static RandomShopOdds ParseOdds()
        {
            return RandomShopOdds.Parse(XDocument.Parse(OddsXml));
        }

        private RandomShopInfo OddsInfo()
        {
            GameFixtures.AddEffects(_ctx.GameData, new ItemEffect { Id = KnownEffectA }, new ItemEffect { Id = KnownEffectB });
            var info = RandomShopInfo.Default;
            info.Odds = ParseOdds();
            return info;
        }

        [Fact]
        public void Odds_Parse_readsAllSections_skipsBadRows()
        {
            var odds = ParseOdds();

            Assert.Equal(3, odds.Packages.Count); // id "bad" skipped
            Assert.True(odds.Packages[0].IsPen);
            Assert.False(odds.Packages[2].IsPen);
            Assert.Equal(7000, odds.Packages[0].Price);
            Assert.Equal("man", odds.Packages[1].Gender);

            Assert.Equal(7, odds.Lineup.Count); // item_key "x" skipped
            var first = odds.Lineup[0];
            Assert.Equal(1004001, first.PackageId);
            Assert.Equal(2000006u, first.ItemKey);
            Assert.Equal(("Dist_1", "Def_Weapon_Melee", "Def_WP_Days"), (first.ColorGroup, first.EffectGroup, first.PeriodGroup));
            Assert.Equal(681, first.Prob);
            Assert.True(first.IsItem);

            var wp = odds.PeriodGroups["Def_WP_Days"];
            Assert.Equal(new[] { (ItemPeriodType.Days, (ushort)1, 4000), (ItemPeriodType.None, (ushort)0, 200) },
                wp.Select(x => (x.Value.PeriodType, x.Value.Period, x.Weight)));
            Assert.Single(odds.PeriodGroups["Def_CT_Days"]); // BOGUS type skipped

            Assert.Equal(new[] { (KnownEffectA, 6500), (KnownEffectB, 2500), (21001u, 1000) },
                odds.EffectGroups["Def_Weapon_Melee"].Select(x => (x.Value, x.Weight)));
            Assert.False(odds.EffectGroups.ContainsKey("Def_Weapon_Sentry")); // id "nope" skipped

            Assert.Equal(new byte[] { 0, 1, 2, 3 }, odds.ColorGroups["Dist_4"].Select(x => x.Value));
            Assert.Equal(10000, odds.ColorGroups["Unique_2"].Single().Weight);
        }

        [Fact]
        public void Odds_GetSlot_lineupItem_usesItsGroupsAndProb_notCouponPackages()
        {
            var slot = ParseOdds().GetSlot((ItemNumber)2000006u, RandomShopKind.Weapon);

            Assert.True(slot.InLineup);
            Assert.Equal(681, slot.Weight); // the coupon package entry (4000, Coupon_None) is ignored
            Assert.Equal("Def_Weapon_Melee", slot.EffectGroup);
            Assert.Equal("Def_WP_Days", slot.PeriodGroup);
            Assert.Equal("Dist_1", slot.ColorGroup);
        }

        [Fact]
        public void Odds_GetSlot_unknownItem_derivesGroupsFromSubCategory_andMeanWeight()
        {
            var odds = ParseOdds();

            var rifle = odds.GetSlot((ItemNumber)2010001u, RandomShopKind.Weapon);
            Assert.False(rifle.InLineup);
            Assert.Equal("Def_Weapon_Rifle", rifle.EffectGroup);
            Assert.Equal("Def_WP_Days", rifle.PeriodGroup);
            Assert.Equal("Dist_1", rifle.ColorGroup);
            Assert.Equal(359, rifle.Weight); // (681 + 192 + 205) / 3, PEN weapon entries only

            var shirt = odds.GetSlot(MaleShirt, RandomShopKind.Costume);
            Assert.Equal("Def_Top", shirt.EffectGroup);
            Assert.Equal("Def_CT_Days", shirt.PeriodGroup);
            Assert.Equal("Dist_4", shirt.ColorGroup); // 2 of 3 shirts
            Assert.Equal(390, shirt.Weight); // (750 + 208 + 212) / 3
        }

        [Fact]
        public void Odds_GetSlot_subCategoryWithoutLineup_fallsBack()
        {
            var odds = ParseOdds();

            var sentry = odds.GetSlot((ItemNumber)2040001u, RandomShopKind.Weapon);
            Assert.Equal("Def_Weapon_Sentry", sentry.EffectGroup);
            Assert.Equal("Def_WP_Days", sentry.PeriodGroup);
            Assert.Equal("Dist_1", sentry.ColorGroup);

            var hair = odds.GetSlot(new ItemNumber(ItemCategory.Costume, (byte)CostumeSlot.Hair, 1), RandomShopKind.Costume);
            Assert.Null(hair.EffectGroup);
            Assert.Equal("Def_CT_Days", hair.PeriodGroup);

            var empty = new RandomShopOdds().GetSlot((ItemNumber)2000006u, RandomShopKind.Weapon);
            Assert.Equal(1, empty.Weight);
            Assert.Equal(RandomShopOdds.DefaultWeaponPeriodGroup, empty.PeriodGroup);
            Assert.Equal(RandomShopOdds.DefaultCostumePeriodGroup,
                new RandomShopOdds().GetSlot(MaleShirt, RandomShopKind.Costume).PeriodGroup);
        }

        [Fact]
        public void Pick_followsWeights_seeded()
        {
            var values = new List<RandomShopWeighted<string>>
            {
                new RandomShopWeighted<string>("never", 0),
                new RandomShopWeighted<string>("three", 3),
                new RandomShopWeighted<string>("one", 1),
                new RandomShopWeighted<string>("negative", -5)
            };
            var random = new Random(42);
            var counts = new Dictionary<string, int>();
            for (var i = 0; i < 4000; ++i)
            {
                var value = RandomShopOdds.Pick(values, random).Value;
                counts[value] = counts.GetValueOrDefault(value) + 1;
            }

            Assert.Equal(new[] { "one", "three" }, counts.Keys.OrderBy(x => x));
            Assert.InRange(counts["three"], 2850, 3150); // expected 3000

            Assert.Null(RandomShopOdds.Pick(new List<RandomShopWeighted<string>> { new RandomShopWeighted<string>("x", 0) }, random));
            Assert.Null(RandomShopOdds.Pick((IList<RandomShopWeighted<string>>)null, random));
        }

        [Fact]
        public void BuildEntry_withOdds_usesKnownEffectsItemColorsAndPeriodGroup()
        {
            var info = OddsInfo();
            var packages = RandomShopService.BuildPackages(_ctx.GameData.ShopItems.Values, info, _ctx.GameData.Effects);

            var melee = Assert.Single(Assert.Single(packages, x => x.Id == MeleePackage).Entries);
            Assert.Equal(681, melee.Weight);
            Assert.Equal("Def_Weapon_Melee", melee.EffectGroup);
            Assert.False(melee.UseShopEffects);
            // 21001 is not in effect_list -> dropped
            Assert.Equal(new[] { (KnownEffectA, 6500), (KnownEffectB, 2500) }, melee.Effects.Select(x => (x.Value, x.Weight)));
            Assert.Equal("Def_WP_Days", melee.PeriodGroup);
            Assert.Equal(2, melee.Periods.Count);
            Assert.Equal("Dist_1", melee.ColorGroup);

            var shirt = Assert.Single(Assert.Single(packages, x => x.Id == MaleShirtPackage).Entries);
            Assert.Equal(390, shirt.Weight);
            Assert.Equal("Def_CT_Days", shirt.PeriodGroup);
            // Def_Top has no known effect -> the shop item's own effects
            Assert.True(shirt.UseShopEffects);
            Assert.Equal("e20", shirt.EffectGroup);
            // shirt has colour 0 only -> Dist_4 cut down to colour 0
            Assert.Equal("Dist_4@0", shirt.ColorGroup);
            Assert.Equal(new byte[] { 0 }, shirt.Colors.Select(x => x.Value));
        }

        [Fact]
        public void BuildEntry_withoutOdds_keepsUniformX7Behaviour()
        {
            var info = RandomShopInfo.Default;
            var entry = RandomShopService.BuildEntry(_ctx.GameData.GetShopItem((ItemNumber)2000006u), RandomShopKind.Weapon, info);

            Assert.Equal(1, entry.Weight);
            Assert.True(entry.UseShopEffects);
            Assert.Equal(RandomShopService.PeriodGroup, entry.PeriodGroup);
            Assert.Equal(info.Periods.Select(x => x.WeaponWeight), entry.Periods.Select(x => x.Weight));
            Assert.Equal("c0", entry.ColorGroup);
            Assert.Equal(new[] { ((byte)0, 1) }, entry.Colors.Select(x => (x.Value, x.Weight)));
        }

        [Fact]
        public void RollPrize_withOdds_followsPeriodColorAndEffectWeights_seeded()
        {
            var info = OddsInfo();
            var shirt = _ctx.GameData.GetShopItem(MaleShirt);
            shirt.ColorGroup = 3;
            try
            {
                var packages = RandomShopService.BuildPackages(_ctx.GameData.ShopItems.Values, info, _ctx.GameData.Effects);
                var melee = Assert.Single(packages, x => x.Id == MeleePackage);
                var shirts = Assert.Single(packages, x => x.Id == MaleShirtPackage);
                Assert.Equal("Dist_4", shirts.Entries[0].ColorGroup); // all four colours fit

                var random = new Random(7);
                var days = 0;
                var effectA = 0;
                const int rolls = 4000;
                for (var i = 0; i < rolls; ++i)
                {
                    var prize = RandomShopService.RollPrize(melee, random);
                    Assert.Equal(2000006u, prize.Item.ItemNumber.Id);
                    Assert.Equal(0, prize.Color);
                    var effect = Assert.Single(prize.Effects);
                    Assert.Contains(effect, new[] { KnownEffectA, KnownEffectB });
                    if (effect == KnownEffectA)
                        ++effectA;
                    if (prize.PeriodType == ItemPeriodType.Days)
                    {
                        Assert.Equal(1, prize.Period);
                        ++days;
                    }
                    else
                    {
                        Assert.Equal(ItemPeriodType.None, prize.PeriodType);
                    }
                }

                // 4000 / 4200 days (3810 expected), 6500 / 9000 effect A (2889 expected)
                Assert.InRange(days, 3730, 3890);
                Assert.InRange(effectA, 2730, 3050);

                var colors = new HashSet<byte>();
                for (var i = 0; i < 200; ++i)
                {
                    var prize = RandomShopService.RollPrize(shirts, random);
                    colors.Add(prize.Color);
                    Assert.Null(prize.Effects); // shop effects
                    Assert.Equal(ItemPeriodType.Days, prize.PeriodType);
                    Assert.Equal(3, prize.Period);
                }
                Assert.Equal(new byte[] { 0, 1, 2, 3 }, colors.OrderBy(x => x));
            }
            finally
            {
                shirt.ColorGroup = 0;
            }
        }

        [Fact]
        public void RollPrize_followsItemWeights_seeded()
        {
            var package = new RandomShopPackage
            {
                Entries = new List<RandomShopEntry>
                {
                    new RandomShopEntry { Item = _ctx.GameData.GetShopItem((ItemNumber)2000006u), Weight = 681 },
                    new RandomShopEntry { Item = _ctx.GameData.GetShopItem((ItemNumber)2010001u), Weight = 192 }
                }
            };
            var random = new Random(3);
            var common = 0;
            for (var i = 0; i < 8730; ++i)
            {
                if (RandomShopService.RollPrize(package, random).Item.ItemNumber.Id == 2000006u)
                    ++common;
            }

            Assert.InRange(common, 6650, 6970); // expected 6810
        }

        [Fact]
        public void BuildClientData_withOdds_describesTheRolledGroups()
        {
            var info = OddsInfo();
            var packages = RandomShopService.BuildPackages(_ctx.GameData.ShopItems.Values, info, _ctx.GameData.Effects);

            var data = RandomShopService.BuildClientData(packages, info);

            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                var packageCount = r.ReadInt32();
                for (var i = 0; i < packageCount; ++i)
                {
                    r.ReadInt32();
                    for (var j = 0; j < 5; ++j)
                        r.ReadProudString();
                    r.ReadInt32();
                    r.ReadProudString();
                }

                var effects = ReadGroupSection(r, () => r.ReadUInt32());
                Assert.Equal(new[] { KnownEffectA, KnownEffectB }, effects.Where(x => x.group == "Def_Weapon_Melee").Select(x => x.value));
                Assert.Contains(("e20", 1u), effects); // shirt keeps its shop effect group

                var colors = ReadGroupSection(r, () => r.ReadByte());
                Assert.Contains(("Dist_1", (byte)0), colors);
                Assert.Contains(("Dist_4@0", (byte)0), colors);
                Assert.Equal(colors.Count, colors.Distinct().Count());

                var lineupCount = r.ReadInt32();
                var lineup = new List<(int pid, int item, string color, string effect, string period)>();
                for (var i = 0; i < lineupCount; ++i)
                {
                    Assert.Equal(0, r.ReadInt32());
                    Assert.Equal(0, r.ReadByte());
                    var pid = r.ReadInt32();
                    Assert.Equal("item", r.ReadProudString());
                    Assert.Equal(1, r.ReadInt32());
                    var item = r.ReadInt32();
                    var color = r.ReadProudString();
                    var effect = r.ReadProudString();
                    var period = r.ReadProudString();
                    lineup.Add((pid, item, color, effect, period));
                    Assert.Equal(0, r.ReadInt32());
                }
                Assert.Contains((MeleePackage, 2000006, "Dist_1", "Def_Weapon_Melee", "Def_WP_Days"), lineup);
                Assert.Contains((MaleShirtPackage, (int)MaleShirt.Id, "Dist_4@0", "e20", "Def_CT_Days"), lineup);

                var periods = ReadGroupSection(r, () => (type: r.ReadInt32(), value: r.ReadInt32()));
                Assert.Equal(new[] { ((int)ItemPeriodType.Days, 1), ((int)ItemPeriodType.None, 0) },
                    periods.Where(x => x.group == "Def_WP_Days").Select(x => (x.value.type, x.value.value)));
                Assert.Equal(new[] { ((int)ItemPeriodType.Days, 3) },
                    periods.Where(x => x.group == "Def_CT_Days").Select(x => (x.value.type, x.value.value)));
                Assert.DoesNotContain(periods, x => x.group == RandomShopService.PeriodGroup);

                // every group the lineup names is in its section
                Assert.All(lineup, x =>
                {
                    Assert.Contains(colors, c => c.group == x.color);
                    Assert.Contains(effects, e => e.group == x.effect);
                    Assert.Contains(periods, p => p.group == x.period);
                });
                Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
            }
        }

        [Fact]
        public async Task Roll_withOdds_grantsOnePickedEffect()
        {
            _ctx.GameData.RandomShop.Odds = OddsInfo().Odds;
            var (plr, channel) = await LoginAsync(9409);
            plr.PEN = 3000;

            await Roll(plr, MeleePackage);

            var ack = Messages(channel).OfType<RandomShopRollingStartAckMessage>().Last();
            Assert.Equal(RandomShopService.ResultSuccess, ack.Unk1);
            var prize = Assert.Single(ack.Items);
            Assert.Contains(prize.EffectGroupId, new[] { KnownEffectA, KnownEffectB });
            var item = FindByShopItemId(plr, prize.ShopItemId);
            Assert.Equal(new[] { prize.EffectGroupId }, item.Effects.ToArray());
            // fixture shop only has a permanent price -> rolled day periods fall back to it
            Assert.Equal(ItemPeriodType.None, prize.PeriodType);
            Assert.Equal(0u, plr.PEN);
        }

        private static List<(string group, T value)> ReadGroupSection<T>(BinaryReader r, Func<T> readValue)
        {
            var count = r.ReadInt32();
            var result = new List<(string group, T value)>();
            for (var i = 0; i < count; ++i)
            {
                Assert.Equal(0, r.ReadInt32());
                Assert.Equal(0, r.ReadByte());
                var group = r.ReadProudString();
                result.Add((group, readValue()));
            }

            return result;
        }
        #endregion

        // ShopItemId is 32-bit on the wire; IdGen item ids are 64-bit, the DTO carries the low half
        private static PlayerItem FindByShopItemId(Player plr, uint shopItemId)
        {
            return plr.Inventory.SingleOrDefault(x => (uint)x.Id == shopItemId);
        }

        private Task Roll(Player plr, int packageId)
        {
            return _ctx.Get<ShopHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new RandomShopRollingStartReqMessage { Unk = packageId });
        }

        private static List<object> Messages(FakeSocketChannel channel)
        {
            return channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o)).Where(x => x != null).ToList();
        }

        private async Task<(Player plr, FakeSocketChannel channel)> LoginAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "r" + accountId, Nickname = "nick" + accountId });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 1000 });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });

            var plr = session.Player;
            var (character, _) = plr.CharacterManager.Create(0, CharacterGender.Male, 0, 0, 0, 0, 0, 0);
            Assert.NotNull(character);
            plr.CharacterManager.Select(0);
            return (plr, channel);
        }
    }
}
