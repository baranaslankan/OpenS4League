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
