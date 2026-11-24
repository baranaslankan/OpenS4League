using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using OpenS4L;
using OpenS4L.Common;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
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
    /// Capsule data parsing (reward table + client tooltip), rolling, and opening over the harness.
    /// All XML here is synthetic test data shaped like the real files.
    /// </summary>
    public class CapsuleTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        private static readonly ItemNumber Sword = new ItemNumber(ItemCategory.Weapon, 1, 1);
        private static readonly ItemNumber Gun = new ItemNumber(ItemCategory.Weapon, 1, 2);
        private static readonly ItemNumber Capsule = new ItemNumber(ItemCategory.OneTimeUse, 2, 1);

        [Fact]
        public void ParseRewardTable_readsGroupsRatesAndTypes()
        {
            var doc = XDocument.Parse(@"<ItemReward>
  <item Number=""4020001"">
    <group>
      <reward Type=""1"" Data=""0"" Value=""300"" Rate=""40"" />
      <reward Type=""1"" Data=""0"" Value=""900"" Rate=""20"" />
    </group>
    <group>
      <reward Type=""2"" Data=""2000001"" PeriodType=""3"" Value=""7"" Color=""2"" Rate=""100"" />
    </group>
  </item>
  <item Number=""4020002""><group /></item>
</ItemReward>");

            var capsules = CapsuleDataParser.ParseRewardTable(doc).ToList();

            var capsule = Assert.Single(capsules); // the empty one is skipped
            Assert.Equal(4020001u, capsule.ItemNumber.Id);
            Assert.Equal(CapsuleSource.RewardTable, capsule.Source);
            Assert.Equal(2, capsule.Groups.Count);
            Assert.Equal(new uint[] { 300, 900 }, capsule.Groups[0].Rewards.Select(x => x.PEN));
            Assert.Equal(new uint[] { 40, 20 }, capsule.Groups[0].Rewards.Select(x => x.Rate));
            var item = Assert.Single(capsule.Groups[1].Rewards);
            Assert.Equal(CapsuleRewardType.Item, item.Type);
            Assert.Equal(2000001u, item.ItemNumber.Id);
            Assert.Equal(ItemPeriodType.Days, item.PeriodType);
            Assert.Equal(7, item.Period);
            Assert.Equal(2, item.Color);
        }

        [Fact]
        public void ParseTooltip_groupsBySlotAndReadsPenRange()
        {
            var doc = XDocument.Parse(@"<Item_tooltip_addcapsule>
  <item id=""4020003"">
    <capsule_icon ID_1=""1060012"" ID_2=""1060013"" ID_3=""4040003"" ID_4="""" />
    <capsule_slot slot_1=""1"" slot_2=""1"" slot_3=""2"" slot_4="""" />
    <capsule_info effect_key_1=""ACC"" effect_key_2=""ACC"" effect_key_3=""PEN_KEY"" effect_key_4="""" />
    <color_index color_1=""3"" color_2="""" color_3="""" />
  </item>
  <item id=""4020004"">
    <capsule_icon ID_1="""" />
  </item>
</Item_tooltip_addcapsule>");
            var strings = new Dictionary<string, string> { ["PEN_KEY"] = "PEN{N}1,000~100,000" };

            var capsule = Assert.Single(CapsuleDataParser.ParseTooltip(doc, strings));

            Assert.Equal(CapsuleSource.Tooltip, capsule.Source);
            Assert.Equal(2, capsule.Groups.Count);
            Assert.Equal(new uint[] { 1060012, 1060013 }, capsule.Groups[0].Rewards.Select(x => x.ItemNumber.Id));
            Assert.Equal(3, capsule.Groups[0].Rewards[0].Color);
            Assert.All(capsule.Groups[0].Rewards, r => Assert.Equal(0u, r.Rate));
            var pen = Assert.Single(capsule.Groups[1].Rewards);
            Assert.Equal(CapsuleRewardType.PEN, pen.Type);
            Assert.Equal(1000u, pen.PEN);
            Assert.Equal(100000u, pen.PENMax);
        }

        [Fact]
        public void ParseTooltip_setCapsule_rollsOneItemFromWholeList()
        {
            // Shaped like a set capsule: the permanent set alone in slot 1, parts in slot 2,
            // and a 30-day set with no slot at all.
            var doc = XDocument.Parse(@"<x><item id=""4110069"">
    <capsule_icon ID_1=""4050124"" ID_2=""1021018"" ID_3=""1031018"" ID_9=""4050123"" ID_16=""1"" />
    <capsule_slot slot_1=""1"" slot_2=""2"" slot_3=""2"" slot_16=""CAPSULE_RANDOME_ESPERCHIP_CAPSULE"" />
  </item></x>");

            var capsule = Assert.Single(CapsuleDataParser.ParseTooltip(doc, new Dictionary<string, string>()));

            var group = Assert.Single(capsule.Groups);
            Assert.Equal(new uint[] { 4050124, 1021018, 1031018, 4050123 }, group.Rewards.Select(x => x.ItemNumber.Id));
        }

        [Fact]
        public void ParseTooltip_setBox_givesEveryPart()
        {
            var doc = XDocument.Parse(@"<x>
  <item id=""4050160""><capsule_icon ID_1=""1021031"" ID_2=""1031031"" ID_3=""1041031"" ID_4="""" />
    <capsule_slot slot_1="""" slot_2="""" slot_3="""" /></item>
  <item id=""4160121""><capsule_icon ID_1=""4051954"" ID_2=""6000001"" />
    <capsule_slot slot_1="""" slot_2="""" /></item>
</x>");

            var capsules = CapsuleDataParser.ParseTooltip(doc, new Dictionary<string, string>()).ToList();

            var set = capsules.Single(c => c.ItemNumber.Id == 4050160);
            Assert.Equal(3, set.Groups.Count);
            Assert.All(set.Groups, g => Assert.Single(g.Rewards));
            Assert.Single(capsules.Single(c => c.ItemNumber.Id == 4160121).Groups); // not a set: one draw
        }

        [Fact]
        public void ParseRewardTable_readsEffects()
        {
            var doc = XDocument.Parse(@"<ItemReward><item Number=""4050123""><group>
  <reward Type=""2"" Data=""1021018"" PeriodType=""3"" Value=""30"" Color=""1"" Effects=""01101301006"" Rate=""100"" />
</group></item></ItemReward>");

            var reward = CapsuleDataParser.ParseRewardTable(doc).Single().Groups[0].Rewards[0];

            Assert.Equal(new uint[] { 1101301006 }, reward.Effects);
        }

        [Fact]
        public void ParseTooltip_penWithoutText_isSkipped()
        {
            var doc = XDocument.Parse(@"<x><item id=""1""><capsule_icon ID_1=""4040003"" /><capsule_info effect_key_1=""MISSING"" /></item></x>");
            Assert.Empty(CapsuleDataParser.ParseTooltip(doc, new Dictionary<string, string>()));
        }

        [Theory]
        [InlineData("PEN{N}164~100,000", 164u, 100000u)]
        [InlineData("PEN 300", 300u, 300u)]
        public void TryParseRange_readsBounds(string text, uint min, uint max)
        {
            Assert.True(CapsuleDataParser.TryParseRange(text, out var a, out var b));
            Assert.Equal(min, a);
            Assert.Equal(max, b);
        }

        [Fact]
        public void Roll_respectsRates()
        {
            var group = new CapsuleRewardGroup
            {
                Rewards = { new CapsuleRewardInfo { PEN = 1, Rate = 0 }, new CapsuleRewardInfo { PEN = 2, Rate = 5 } }
            };
            var random = new Random(1);
            for (var i = 0; i < 200; ++i)
                Assert.Equal(2u, CapsuleOpener.Roll(group, random).PEN);
        }

        [Fact]
        public void Roll_equalOddsReachesEveryReward()
        {
            var group = new CapsuleRewardGroup
            {
                Rewards = { new CapsuleRewardInfo { PEN = 1 }, new CapsuleRewardInfo { PEN = 2 }, new CapsuleRewardInfo { PEN = 3 } }
            };
            var random = new Random(7);
            var seen = Enumerable.Range(0, 300).Select(_ => CapsuleOpener.Roll(group, random).PEN).ToHashSet();
            Assert.Equal(new HashSet<uint> { 1, 2, 3 }, seen);
        }

        [Fact]
        public async Task Open_grantsPenAndItemAndRemovesCapsule()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9301);
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);
            var penBefore = plr.PEN;

            var rewards = CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(3));

            Assert.NotNull(rewards);
            Assert.Equal(2, rewards.Length);
            var pen = rewards.Single(r => r.RewardType == CapsuleRewardType.PEN);
            Assert.InRange(pen.PEN, 100u, 200u);
            Assert.Equal(penBefore + pen.PEN, plr.PEN);
            Assert.Contains(plr.Inventory, x => x.ItemNumber == Gun);
            Assert.DoesNotContain(plr.Inventory, x => x.Id == capsule.Id);
        }

        [Fact]
        public async Task Open_rewardHiddenFromShop_isStillGranted()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9308, withPen: false);
            foreach (var info in _ctx.GameData.GetShopItem(Gun).ItemInfos)
                info.IsEnabled = false;
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            Assert.NotNull(CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(1)));
            Assert.Contains(plr.Inventory, x => x.ItemNumber == Gun);
        }

        [Fact]
        public async Task Open_rewardNotInShop_keepsCapsule()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9302, rewardInShop: false, withPen: false);
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            Assert.Null(CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(1)));
            Assert.Contains(plr.Inventory, x => x.Id == capsule.Id);
        }

        [Fact]
        public async Task Handler_sendsRewardsWithSuccessResult()
        {
            var (plr, channel) = await LoginWithCapsuleAsync(9303);
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            await _ctx.Get<InventoryHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseCapsuleReqMessage { ItemId = capsule.Id });

            var ack = channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<ItemUseCapsuleAckMessage>().Last();
            Assert.Equal(CapsuleOpener.ResultSuccess, ack.Result);
            Assert.Equal(2, ack.Rewards.Length);
        }

        [Fact]
        public async Task Handler_unknownItem_sendsFailure()
        {
            var (plr, channel) = await LoginWithCapsuleAsync(9304);

            await _ctx.Get<InventoryHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseCapsuleReqMessage { ItemId = 123456 });

            var ack = channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<ItemUseCapsuleAckMessage>().Last();
            Assert.Equal(CapsuleOpener.ResultFailed, ack.Result);
        }

        [Fact]
        public async Task PerUnitItems_stackInOneEntry()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9305);

            var again = plr.Inventory.CreateFromShop(Capsule);

            var stack = Assert.Single(plr.Inventory.Where(x => x.ItemNumber == Capsule));
            Assert.Same(stack, again);
            Assert.Equal(ItemPeriodType.Units, stack.PeriodType);
            Assert.Equal(2, stack.Durability);
        }

        [Fact]
        public async Task PerUnitItems_buyingFiveUnitsAddsFive()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9306);

            plr.Inventory.CreateFromShop(Capsule, ItemPeriodType.Units, 5);

            Assert.Equal(6, plr.Inventory.Single(x => x.ItemNumber == Capsule).Durability);
        }

        [Fact]
        public async Task Open_fromStack_usesOneAndKeepsTheRest()
        {
            var (plr, channel) = await LoginWithCapsuleAsync(9307);
            var capsule = plr.Inventory.CreateFromShop(Capsule);
            Assert.Equal(2, capsule.Durability);

            Assert.NotNull(CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(3)));

            Assert.Contains(plr.Inventory, x => x.Id == capsule.Id);
            Assert.Equal(1, capsule.Durability);
            var update = channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<ItemUpdateInventoryAckMessage>().Last(m => m.Item.Id == capsule.Id);
            Assert.Equal(InventoryAction.Update, update.Action);
        }

        private async Task<(Player, FakeSocketChannel)> LoginWithCapsuleAsync(uint accountId, bool rewardInShop = true,
            bool withPen = true)
        {
            var gds = _ctx.GameData;
            GameFixtures.SeedShopItem(gds, Capsule, GameFixtures.SeedUnitPriceGroup(gds));
            if (rewardInShop)
                GameFixtures.SeedShopItem(gds, Gun);

            var info = new CapsuleInfo { ItemNumber = Capsule, Source = CapsuleSource.Tooltip };
            info.Groups.Add(new CapsuleRewardGroup { Rewards = { new CapsuleRewardInfo { Type = CapsuleRewardType.Item, ItemNumber = Gun } } });
            if (withPen)
                info.Groups.Add(new CapsuleRewardGroup { Rewards = { new CapsuleRewardInfo { Type = CapsuleRewardType.PEN, PEN = 100, PENMax = 200 } } });
            typeof(GameDataService).GetProperty(nameof(GameDataService.Capsules))
                .SetValue(gds, ImmutableDictionary<ItemNumber, CapsuleInfo>.Empty.Add(Capsule, info));

            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "c" + accountId, Nickname = "cap" + accountId });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            var plr = session.Player;
            Assert.NotNull(plr.Inventory.CreateFromShop(Capsule));
            return (plr, channel);
        }
    }
}
