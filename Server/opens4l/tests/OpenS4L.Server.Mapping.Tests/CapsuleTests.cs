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
            // Loose costume parts are dropped: set capsules give sets.
            Assert.Equal(new uint[] { 4050124, 4050123 }, group.Rewards.Select(x => x.ItemNumber.Id));
            // The permanent set (slot 1) is the rare 5% pick; the rest share 95%.
            Assert.Equal(500u, group.Rewards[0].Rate);
            Assert.Equal(9500u, group.Rewards[1].Rate);
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

        [Theory]
        [InlineData("Gentle Hearts Set(30Days)", ItemPeriodType.Days, 30)]
        [InlineData("Battle Maid Set(1day)", ItemPeriodType.Days, 1)]
        [InlineData("Netsphere Magician Set (7 days)", ItemPeriodType.Days, 7)]
        [InlineData("Male's beginner Set(3Hour)", ItemPeriodType.Hours, 3)]
        public void TryParseBoxPeriod_readsNames(string name, ItemPeriodType type, int period)
        {
            Assert.True(CapsuleDataParser.TryParseBoxPeriod(name, out var t, out var p));
            Assert.Equal(type, t);
            Assert.Equal(period, p);
        }

        [Fact]
        public void TryParseBoxPeriod_permanent_isNotTimed()
        {
            Assert.False(CapsuleDataParser.TryParseBoxPeriod("Western Girl Set(Permanent)", out _, out _));
        }

        [Fact]
        public void ApplyBoxPeriod_timedSetBox_givesTimedParts()
        {
            var doc = XDocument.Parse(@"<ItemReward><item Number=""4050099""><group>
  <reward Type=""2"" Data=""1021016"" PeriodType=""1"" Value=""0"" Color=""0"" Rate=""100"" />
</group></item></ItemReward>");
            var capsule = CapsuleDataParser.ParseRewardTable(doc).Single();

            CapsuleDataParser.ApplyBoxPeriod(capsule, "Gentle Hearts Set(30Days)");

            var reward = capsule.Groups[0].Rewards[0];
            Assert.Equal(ItemPeriodType.Days, reward.PeriodType);
            Assert.Equal(30, reward.Period);
        }

        [Fact]
        public void ApplyBoxPeriod_otherCapsules_areUnchanged()
        {
            var doc = XDocument.Parse(@"<ItemReward><item Number=""4030060""><group>
  <reward Type=""2"" Data=""4050100"" PeriodType=""1"" Value=""0"" Rate=""5"" />
</group></item></ItemReward>");
            var capsule = CapsuleDataParser.ParseRewardTable(doc).Single();

            CapsuleDataParser.ApplyBoxPeriod(capsule, "Gentle Hearts Set Capsule(30Days)");

            Assert.Equal(ItemPeriodType.None, capsule.Groups[0].Rewards[0].PeriodType);
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
        public async Task CreateFromShop_unknownRewardEffect_usesShopEffects()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9309, withPen: false);

            var item = plr.Inventory.CreateFromShop(Gun, effects: new uint[] { 99999 });

            Assert.Equal(new uint[] { 1 }, item.Effects.ToArray());
        }

        [Fact]
        public async Task CreateFromShop_effectGroupReward_isKept()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9311, withPen: false);
            typeof(GameDataService).GetProperty(nameof(GameDataService.EffectMatches))
                .SetValue(_ctx.GameData, ImmutableHashSet.Create<uint>(12002));

            var item = plr.Inventory.CreateFromShop(Gun, effects: new uint[] { 12002 });

            Assert.Equal(new uint[] { 12002 }, item.Effects.ToArray());
        }

        [Fact]
        public async Task CreateFromShop_timedRewardWithoutExactPeriod_isNeverPermanent()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9310, withPen: false);
            var gun = new ItemNumber(2000099u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, 31,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 7), (ItemPeriodType.Days, 30)));

            var exact = plr.Inventory.CreateFromShop(gun, ItemPeriodType.Days, 30);
            var nearest = plr.Inventory.CreateFromShop(gun, ItemPeriodType.Days, 15);

            Assert.Equal((ItemPeriodType.Days, (ushort)30), (exact.PeriodType, exact.Period));
            Assert.Equal((ItemPeriodType.Days, (ushort)7), (nearest.PeriodType, nearest.Period));
        }

        [Fact]
        public async Task CreateFromShop_hiddenExactPrice_isUsedForRewards()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9313, withPen: false);
            var gun = new ItemNumber(2000097u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, 33,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 7)));
            _ctx.GameData.GetShopItem(gun).ItemInfos.Single().PriceGroup.Prices
                .Single(p => p.PeriodType == ItemPeriodType.None).IsEnabled = false;

            var item = plr.Inventory.CreateFromShop(gun);

            Assert.Equal(ItemPeriodType.None, item.PeriodType);
        }

        [Fact]
        public async Task CreateFromShop_permanentRewardOfTimedOnlyItem_getsTheLongestPeriod()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9312, withPen: false);
            var gun = new ItemNumber(2000098u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, 32,
                (ItemPeriodType.Days, 1), (ItemPeriodType.Days, 30), (ItemPeriodType.Days, 7)));

            var item = plr.Inventory.CreateFromShop(gun);

            Assert.Equal((ItemPeriodType.Days, (ushort)30), (item.PeriodType, item.Period));
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

        // ---- tooltip text: periods and stats per reward ----

        [Theory]
        [InlineData("{N}HP +8 (1Day)", "3/1:hp+8")]
        [InlineData("{N}HP +8(add exp +5%) (Permanent)", "1/0:exp+5,hp+8")]
        [InlineData("{N}attack +10%(3, 10, 35Day)", "3/3:atk+10 3/10:atk+10 3/35:atk+10")]
        [InlineData("{N}SP +6 (1, 7, 30Day/Permanent)", "3/1:sp+6 3/7:sp+6 3/30:sp+6 1/0:sp+6")]
        [InlineData("{N}SP +6 (1Day, Permanent)", "3/1:sp+6 1/0:sp+6")]
        [InlineData("Force Pack (7Day) attack +11%, move speed +3%, HP +5, SP +5", "3/7:atk+11,hp+5,sp+5,speed+3")]
        [InlineData("{N}SP +6(1Day) or no stat(Permanent)", "3/1:sp+6 1/0:none")]
        [InlineData("attack +8%(Permanent){N}, attack +10%(30Day)", "1/0:atk+8 3/30:atk+10")]
        [InlineData("{N}no stat (1Day)", "3/1:none")]
        [InlineData("{N}(Jacket, Pants, Gloves, Shoes, Hair)-30DAY", "3/30:")]
        [InlineData("Smart Pack(1/7/30)Day SP +6, move speed +1%, all weapon depense +3%, EXP +20%",
            "3/1:def+3,exp+20,sp+6,speed+1 3/7:def+3,exp+20,sp+6,speed+1 3/30:def+3,exp+20,sp+6,speed+1")]
        [InlineData("{N}HP +8(EXP +5%) (10 hours)", "2/10:exp+5,hp+8")]
        [InlineData("{N}shooting weapon defense(head) +15% (30Day)", "3/30:gundef+15")]
        [InlineData("{N}Permanent SP +6, speed +1%", "1/0:sp+6,speed+1")]
        [InlineData("{N}2/3/5", "4/2: 4/3: 4/5:")]
        [InlineData("x15", "4/15:")]
        [InlineData("{N}None Option", "-/0:none")]
        [InlineData("{N}(Jacket, Pants, Gloves, Shoes, Acc)", "")]
        [InlineData("PEN{N}1,000~100,000", "")]
        public void TooltipText_readsPeriodsAndStats(string text, string expected)
        {
            var options = CapsuleTooltipText.Parse(text);

            var actual = string.Join(" ", options.Select(o =>
                $"{(o.PeriodType.HasValue ? ((int)o.PeriodType.Value).ToString() : "-")}/{o.Period}:" +
                (o.NoStats ? "none" : CapsuleTooltipText.Key(o.Stats))));
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void BuildOptions_favoursShorterPeriods()
        {
            var options = CapsuleDataParser.BuildOptions(
                CapsuleTooltipText.Parse("{N}SP +6 (30Day, 1Day, Permanent, 7Day)"), ItemPeriodType.None, 0);

            Assert.Equal(new[] { (ItemPeriodType.Days, (ushort)1, 4u), (ItemPeriodType.Days, (ushort)7, 3u),
                    (ItemPeriodType.Days, (ushort)30, 2u), (ItemPeriodType.None, (ushort)0, 1u) },
                options.Select(o => (o.PeriodType, o.Period, o.Weight)));
        }

        private static XDocument TooltipDoc(string id, params (uint item, string key)[] slots) => XDocument.Parse(
            $@"<x><item id=""{id}""><capsule_icon {string.Join(" ", slots.Select((s, i) => $@"ID_{i + 1}=""{s.item}"""))} />
<capsule_info {string.Join(" ", slots.Select((s, i) => $@"effect_key_{i + 1}=""{s.key}"""))} /></item></x>");

        [Fact]
        public void ParseTooltip_rewardGetsTheTooltipsPeriodsAndStats()
        {
            // Before the tooltip text was read every listed item was given permanent.
            var doc = TooltipDoc("4030027", (2010001, "WEAPON_1_7_30"), (4050100, "SET_30"));
            var strings = new Dictionary<string, string>
            {
                ["WEAPON_1_7_30"] = "{N}attack +10%(1, 7, 30Day)",
                ["SET_30"] = "{N}(Jacket, Pants, Gloves, Shoes, Hair)-30DAY"
            };

            var rewards = CapsuleDataParser.ParseTooltip(doc, strings).Single().Groups.SelectMany(g => g.Rewards).ToList();

            var weapon = rewards.Single(r => r.ItemNumber.Id == 2010001);
            Assert.Equal(new ushort[] { 1, 7, 30 }, weapon.Options.Select(o => o.Period));
            Assert.All(weapon.Options, o => Assert.Equal(ItemPeriodType.Days, o.PeriodType));
            Assert.All(weapon.Options, o => Assert.Equal("atk+10", CapsuleTooltipText.Key(o.Stats)));
            // a set box's text describes its parts: the box itself is unchanged
            Assert.Empty(rewards.Single(r => r.ItemNumber.Id == 4050100).Options);
        }

        [Fact]
        public void ApplyTooltip_keepsListedTablePeriodAndReplacesOthers()
        {
            var table = CapsuleDataParser.ParseRewardTable(XDocument.Parse(@"<ItemReward><item Number=""4030004""><group>
  <reward Type=""2"" Data=""2010001"" PeriodType=""3"" Value=""10"" Effects=""1201300005"" Rate=""60"" />
  <reward Type=""2"" Data=""2010002"" PeriodType=""1"" Value=""0"" Effects=""1201300005"" Rate=""40"" />
</group></item></ItemReward>")).Single();
            var strings = new Dictionary<string, string> { ["W"] = "{N}attack +10%(3, 10, 35Day)" };
            var tooltip = CapsuleDataParser.ParseTooltip(TooltipDoc("4030004", (2010001, "W"), (2010002, "W")), strings).Single();

            Assert.Equal(2, CapsuleDataParser.ApplyTooltip(table, tooltip));

            var rewards = table.Groups[0].Rewards;
            // the table's 10 days is one of the tooltip's periods: kept, with the tooltip's stat
            var kept = Assert.Single(rewards[0].Options);
            Assert.Equal((ItemPeriodType.Days, (ushort)10, "atk+10"), (kept.PeriodType, kept.Period, CapsuleTooltipText.Key(kept.Stats)));
            // permanent is not: the tooltip's 3/10/35 days are rolled instead
            Assert.Equal(new ushort[] { 3, 10, 35 }, rewards[1].Options.Select(o => o.Period));
            // the table's odds stay
            Assert.Equal(new uint[] { 60, 40 }, rewards.Select(r => r.Rate));
        }

        [Fact]
        public void ApplyBoxPeriod_appliesToTooltipOptions()
        {
            var strings = new Dictionary<string, string> { ["P"] = "{N}HP +8(add exp +5%) (Permanent)" };
            var capsule = CapsuleDataParser.ParseTooltip(TooltipDoc("4050099", (1041016, "P")), strings).Single();

            CapsuleDataParser.ApplyBoxPeriod(capsule, "Gentle Hearts Set(30Days)");

            var option = Assert.Single(capsule.Groups.Single().Rewards.Single().Options);
            Assert.Equal((ItemPeriodType.Days, (ushort)30), (option.PeriodType, option.Period));
        }

        private static CapsuleEffectResolver Resolver()
        {
            var effects = new Dictionary<uint, string>
            {
                [1299600006] = "Attack+10%", [1299600007] = "Attack+11%", [1201300005] = "HP+5", [1201301005] = "SP+5",
                [1299602002] = "Movement Speed+3%", [1104300008] = "HP+8", [1105300008] = "HP+8",
                [1102303008] = "Attack+12%", [1102800001] = "EXP+5%", [1102303003] = "Attack+5%"
            };
            var groups = new Dictionary<uint, string>
            {
                [12002] = "Attack+12%, EXP+5%", [14002] = "HP+8, EXP+5%", [15002] = "HP+8, EXP+5%"
            };
            return new CapsuleEffectResolver(effects, groups);
        }

        private static uint[] Resolve(CapsuleEffectResolver r, uint item, string text) =>
            r.Resolve(new ItemNumber(item), CapsuleTooltipText.ParseStats(text));

        [Fact]
        public void EffectResolver_mapsStatsToTheItemsCategory()
        {
            var r = Resolver();

            // one effect of the item's category (pants 1040xxx = 1 1 04 ...; weapons use 99 = any)
            Assert.Equal(new uint[] { 1104300008 }, Resolve(r, 1040015, "HP +8"));
            Assert.Equal(new uint[] { 1105300008 }, Resolve(r, 1050015, "HP +8"));
            Assert.Equal(new uint[] { 1299600006 }, Resolve(r, 2010001, "attack +10%"));
            // an effect group for the category beats composing (12002 = tops, 14002 = pants)
            Assert.Equal(new uint[] { 12002 }, Resolve(r, 1020015, "all weapon attack +12%(add exp +5%)"));
            Assert.Equal(new uint[] { 14002 }, Resolve(r, 1040015, "HP +8(add exp +5%)"));
            // a weapon Force Pack: one effect per stat
            Assert.Equal(new uint[] { 1201300005, 1201301005, 1299600007, 1299602002 },
                Resolve(r, 2010010, "attack +11%, move speed +3%, HP +5, SP +5"));
            // no matching effect for the category
            Assert.Null(Resolve(r, 1020015, "HP +8"));
        }

        [Fact]
        public void EffectResolver_prefersWhatTheEuShopGaveTheItem()
        {
            var r = Resolver();
            // the EU cash shop sold top 1020015 with Attack+12% + EXP+5% as plain effects
            r.AddShopEffects(1020015, new[] { new uint[] { 1102303008, 1102800001 }, new uint[] { 1102303003 } });

            Assert.Equal(new uint[] { 1102303008, 1102800001 }, Resolve(r, 1020015, "attack +12%(add exp +5%)"));
            Assert.Equal(new uint[] { 1102303003 }, Resolve(r, 1020015, "attack +5%"));
        }

        [Fact]
        public void ReadShopEffects_cashShopFirst()
        {
            var items = XDocument.Parse(@"<item_list><item key=""1020015"">
  <shopinfo shop_id=""1"" effect_match=""1102303003"" price_group=""P"" />
  <shopinfo shop_id=""3"" effect_match=""7012255"" price_group=""B"" />
  <shopinfo shop_id=""4"" effect_match=""1"" /></item></item_list>");
            var effects = XDocument.Parse(@"<effect_match_list><effect_match key=""7012255"">
  <effects effect_id=""1102303008"" /><effects effect_id=""1102800001"" /></effect_match></effect_match_list>");

            var sets = CapsuleEffectResolver.ReadShopEffects(items, effects)[1020015];

            Assert.Equal(new uint[] { 1102303008, 1102800001 }, sets[0]);
            Assert.Equal(new uint[] { 1102303003 }, sets.Last());
        }

        [Fact]
        public void Resolve_rollsTheTooltipOptions()
        {
            var reward = new CapsuleRewardInfo
            {
                Type = CapsuleRewardType.Item, ItemNumber = Gun, Effects = new uint[] { 5 },
                Options =
                {
                    new CapsuleRewardOption { PeriodType = ItemPeriodType.Days, Period = 1, Effects = new uint[] { 7 }, Weight = 2 },
                    new CapsuleRewardOption { PeriodType = ItemPeriodType.None, NoEffects = true, Weight = 1 },
                    new CapsuleRewardOption { PeriodType = ItemPeriodType.Days, Period = 30, Weight = 1 }
                }
            };
            var random = new Random(5);

            var rolls = Enumerable.Range(0, 400).Select(_ => CapsuleOpener.Resolve(reward, random)).ToList();

            var day = rolls.Where(x => x.Period == 1).ToList();
            Assert.All(day, x => Assert.Equal(new uint[] { 7 }, x.Effects));
            // "no stat": no effects at all, not the shop's
            Assert.All(rolls.Where(x => x.PeriodType == ItemPeriodType.None), x => Assert.True(x.NoEffects));
            // an option without ids keeps the reward's own effects
            Assert.All(rolls.Where(x => x.Period == 30), x => Assert.Equal(new uint[] { 5 }, x.Effects));
            // weight 2 of 4: the shortest period is the most common
            Assert.InRange(day.Count, 160, 240);
        }

        [Fact]
        public async Task CreateFromShop_noEffects_givesNone()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9314, withPen: false);

            var item = plr.Inventory.CreateFromShop(Gun, noEffects: true);

            Assert.Empty(item.Effects);
        }

        [Fact]
        public async Task CreateFromShop_timedRewardWithoutEffects_getsTheCashListingsStats()
        {
            // Timed prices tie between the PEN and AP listings and the PEN one won, so a 7-day reward
            // got the PEN shop's low stats (weapon Attack+1%) instead of the cash shop's (Attack+10%).
            var (plr, _) = await LoginWithCapsuleAsync(9315, withPen: false);
            var gun = new ItemNumber(2000096u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, 34,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 7)));
            var ap = GameFixtures.SeedPriceGroup(_ctx.GameData, 35, ItemPriceType.AP,
                new ShopPriceEntity { Id = 3501, PeriodType = (byte)ItemPeriodType.None, Durability = 2400 },
                new ShopPriceEntity { Id = 3502, PeriodType = (byte)ItemPeriodType.Days, Period = 7, Durability = 2400 });
            var shopItem = _ctx.GameData.GetShopItem(gun);
            var apInfo = new ShopItemInfo(shopItem, new ShopItemInfoEntity
            {
                Id = 99315, ShopItemId = gun.Id, PriceGroupId = ap.Id, EffectGroupId = 20, IsEnabled = true
            }, _ctx.GameData)
            {
                EffectGroup = new ShopEffectGroup(new ShopEffectGroupEntity
                {
                    Id = 21, Name = "cash", PreviewEffect = 77,
                    ShopEffects = new List<ShopEffectEntity> { new ShopEffectEntity { Id = 2, Effect = 77 } }
                })
            };
            shopItem.ItemInfos.Add(apInfo);

            var timed = plr.Inventory.CreateFromShop(gun, ItemPeriodType.Days, 7);
            var permanent = plr.Inventory.CreateFromShop(gun);

            Assert.Equal((ItemPeriodType.Days, (ushort)7), (timed.PeriodType, timed.Period));
            Assert.Equal(new uint[] { 77 }, timed.Effects.ToArray());
            Assert.Equal(new uint[] { 77 }, permanent.Effects.ToArray());
        }

        [Fact]
        public async Task Open_tooltipOption_grantsItsPeriodAndStats()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9316, withPen: false);
            var gun = new ItemNumber(2000095u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, 36,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 30)));
            typeof(GameDataService).GetProperty(nameof(GameDataService.EffectMatches))
                .SetValue(_ctx.GameData, ImmutableHashSet.Create<uint>(12002));
            var info = _ctx.GameData.Capsules[Capsule];
            info.Groups[0].Rewards[0] = new CapsuleRewardInfo
            {
                Type = CapsuleRewardType.Item, ItemNumber = gun, PeriodType = ItemPeriodType.None,
                Options = { new CapsuleRewardOption { PeriodType = ItemPeriodType.Days, Period = 30, Effects = new uint[] { 12002 } } }
            };
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            var reward = Assert.Single(CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(1)));

            Assert.Equal((ItemPeriodType.Days, (ushort)30, 12002u), (reward.PeriodType, reward.Period, reward.Effect));
            Assert.Equal(new uint[] { 12002 }, plr.Inventory.Single(x => x.ItemNumber == gun).Effects.ToArray());
        }

        private static CapsuleInfo SetBox(uint id, params uint[] parts) => new CapsuleInfo
        {
            ItemNumber = new ItemNumber(id),
            Groups = parts.Select(p => new CapsuleRewardGroup
            {
                Rewards = { new CapsuleRewardInfo { Type = CapsuleRewardType.Item, ItemNumber = new ItemNumber(p), Effects = new uint[] { 13002 } } }
            }).ToList()
        };

        [Fact]
        public void ApplySetGrade_permanentSet_givesEachPartItsForcePack()
        {
            var box = SetBox(4051921, 1020076, 1030073, 1040065, 1050066, 1060072, 1010001);

            CapsuleDataParser.ApplySetGrade(box, "Rider Set");

            var effects = box.Groups.Select(g => g.Rewards[0].Effects.SingleOrDefault()).ToArray();
            // top, pants, gloves, shoes, accessory; the face has no Force Pack and keeps the shop's.
            Assert.Equal(new uint[] { 12002, 13002, 14002, 15002, 16002, 0 }, effects);
        }

        [Fact]
        public void ApplySetGrade_timedSet_dropsForcePackForTheShopsStats()
        {
            var box = SetBox(4051920, 1030073);

            CapsuleDataParser.ApplySetGrade(box, "Rider Set(30Days)");

            Assert.Empty(box.Groups[0].Rewards[0].Effects);
        }

        private static readonly ItemNumber FullBooster = new ItemNumber(5040006u);

        private PlayerItem GiveBooster(Player plr, int id)
        {
            GameFixtures.SeedShopItem(_ctx.GameData, FullBooster, GameFixtures.SeedUnitPriceGroup(_ctx.GameData, id));
            return plr.Inventory.CreateFromShop(FullBooster, ItemPeriodType.Units, 1);
        }

        // A gun that comes as 1 day (weight 3) or permanent (weight 1).
        private ItemNumber SeedTimedOrPermanentGun(int priceGroupId)
        {
            var gun = new ItemNumber(2000096u);
            GameFixtures.SeedShopItem(_ctx.GameData, gun, GameFixtures.SeedPeriodPriceGroup(_ctx.GameData, priceGroupId,
                (ItemPeriodType.None, 0), (ItemPeriodType.Days, 1)));
            var info = _ctx.GameData.Capsules[Capsule];
            info.Groups[0].Rewards[0] = new CapsuleRewardInfo
            {
                Type = CapsuleRewardType.Item, ItemNumber = gun, PeriodType = ItemPeriodType.None,
                Options =
                {
                    new CapsuleRewardOption { PeriodType = ItemPeriodType.Days, Period = 1, Weight = 3 },
                    new CapsuleRewardOption { PeriodType = ItemPeriodType.None, Weight = 1 }
                }
            };
            return gun;
        }

        [Fact]
        public async Task Open_withFullUniqueBoosterOn_givesPermanentAndUsesTheBooster()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9317, withPen: false);
            var gun = SeedTimedOrPermanentGun(37);
            var booster = GiveBooster(plr, 38);
            plr.EquippedBoosts.Add(booster.Id);
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            var reward = Assert.Single(CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(1)));

            Assert.Equal(ItemPeriodType.None, reward.PeriodType);
            Assert.DoesNotContain(plr.Inventory, x => x.ItemNumber == FullBooster);
            Assert.Empty(plr.EquippedBoosts);
        }

        [Fact]
        public async Task Open_withBoosterNotSwitchedOn_rollsNormallyAndKeepsIt()
        {
            var (plr, _) = await LoginWithCapsuleAsync(9318, withPen: false);
            SeedTimedOrPermanentGun(39);
            GiveBooster(plr, 40);
            var capsule = plr.Inventory.First(x => x.ItemNumber == Capsule);

            CapsuleOpener.Open(plr, capsule, _ctx.GameData, new Random(1));

            Assert.Contains(plr.Inventory, x => x.ItemNumber == FullBooster);
        }

        [Fact]
        public async Task EquipBoost_switchesItOn_andAnswersWithTheBoostList()
        {
            var (plr, channel) = await LoginWithCapsuleAsync(9319, withPen: false);
            var booster = GiveBooster(plr, 41);

            await _ctx.Get<InventoryHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new ItemUseItemReqMessage { Action = UseItemAction.Equip, CharacterSlot = 0, EquipSlot = 0, ItemId = booster.Id });

            Assert.Contains(booster.Id, plr.EquippedBoosts);
            var ack = channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<ItemEquipBoostItemInfoAckMessage>().Last();
            Assert.Equal(new[] { booster.Id }, ack.Items);
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

        // Shaped like capsule 4035366: slot 1 = permanent set or coupons, slot 2 = one of the
        // 1/7/30-day sets, slot 3 = PEN. One reward comes from every slot.
        private static XDocument SlotCapsuleDoc() => XDocument.Parse(@"<x><item id=""4035366"">
    <capsule_icon ID_1=""4051921"" ID_2=""6000001"" ID_3=""4051918"" ID_4=""4051919"" ID_5=""4051920"" ID_6=""4040003"" />
    <capsule_slot slot_1=""1"" slot_2=""1"" slot_3=""2"" slot_4=""2"" slot_5=""2"" slot_6=""3"" />
    <capsule_info effect_key_6=""PEN_KEY"" />
  </item></x>");

        private static readonly Dictionary<string, string> SlotCapsuleStrings =
            new Dictionary<string, string> { ["PEN_KEY"] = "PEN{N}1,000~100,000" };

        private static string SetName(ItemNumber n) => n.Id switch
        {
            4051918 => "Rider Set(1 day)",
            4051919 => "Rider Set(7Days)",
            4051920 => "Rider Set(30Days)",
            _ => "Rider Set"
        };

        [Fact]
        public void ParseTooltip_numberedSlots_rollOncePerSlot()
        {
            var capsule = CapsuleDataParser.ParseTooltip(SlotCapsuleDoc(), SlotCapsuleStrings).Single();

            Assert.Equal(3, capsule.Groups.Count);
            Assert.Equal(new uint[] { 4051921, 6000001 }, capsule.Groups[0].Rewards.Select(r => r.ItemNumber.Id));
            Assert.Equal(new uint[] { 4051918, 4051919, 4051920 }, capsule.Groups[1].Rewards.Select(r => r.ItemNumber.Id));
            Assert.Equal(CapsuleRewardType.PEN, Assert.Single(capsule.Groups[2].Rewards).Type);
        }

        [Fact]
        public void WeightSetBoxes_permanentSetIsRare_shorterTimedSetIsLikelier()
        {
            var capsule = CapsuleDataParser.ParseTooltip(SlotCapsuleDoc(), SlotCapsuleStrings).Single();

            CapsuleDataParser.WeightSetBoxes(capsule, SetName);

            Assert.Equal(new uint[] { 500, 9500 }, capsule.Groups[0].Rewards.Select(r => r.Rate));
            // 1 / 7 / 30 days weigh 3 / 2 / 1.
            Assert.Equal(new uint[] { 5000, 3333, 1666 }, capsule.Groups[1].Rewards.Select(r => r.Rate));
        }

        [Fact]
        public void AddMissingSlots_tableWithoutTimedSetSlot_getsItAndTheTooltipPenRange()
        {
            var table = CapsuleDataParser.ParseRewardTable(XDocument.Parse(@"<x><item Number=""4035366"">
    <group>
      <reward Type=""2"" Data=""4051921"" PeriodType=""4"" Value=""1"" Rate=""5"" />
      <reward Type=""2"" Data=""6000001"" PeriodType=""4"" Value=""1"" Rate=""95"" />
    </group>
    <group>
      <reward Type=""1"" Data=""0"" Value=""300"" Rate=""40"" />
      <reward Type=""1"" Data=""0"" Value=""900"" Rate=""20"" />
    </group>
  </item></x>")).Single();
            var tooltip = CapsuleDataParser.ParseTooltip(SlotCapsuleDoc(), SlotCapsuleStrings).Single();

            Assert.Equal(2, CapsuleDataParser.AddMissingSlots(table, tooltip));

            Assert.Equal(3, table.Groups.Count);
            Assert.Equal(new uint[] { 5, 95 }, table.Groups[0].Rewards.Select(r => r.Rate)); // table odds kept
            var pen = Assert.Single(table.Groups[1].Rewards);
            Assert.Equal((1000u, 100000u), (pen.PEN, pen.PENMax));
            Assert.Equal(new uint[] { 4051918, 4051919, 4051920 }, table.Groups[2].Rewards.Select(r => r.ItemNumber.Id));
        }

        [Fact]
        public void ParseTooltip_doubleCapsule_unslottedRewardJoinsThePreviousSlot()
        {
            var doc = XDocument.Parse(@"<x><item id=""4110003"">
    <capsule_icon ID_1=""2010010"" ID_2=""7000000"" ID_3=""2010002"" ID_4=""2010003"" />
    <capsule_slot slot_1=""1"" slot_2=""2"" slot_3=""2"" slot_4="""" />
  </item></x>");

            var capsule = CapsuleDataParser.ParseTooltip(doc, new Dictionary<string, string>()).Single();

            Assert.Equal(2, capsule.Groups.Count);
            Assert.Equal(new uint[] { 7000000, 2010002, 2010003 }, capsule.Groups[1].Rewards.Select(r => r.ItemNumber.Id));
        }

        [Fact]
        public void RollPen_staysInRange_andFavoursTheLowEnd()
        {
            var reward = new CapsuleRewardInfo { Type = CapsuleRewardType.PEN, PEN = 1000, PENMax = 100000 };
            var random = new Random(7);

            var rolls = Enumerable.Range(0, 2000).Select(_ => CapsuleOpener.RollPen(reward, random)).ToList();

            Assert.All(rolls, x => Assert.InRange(x, 1000u, 100000u));
            Assert.InRange(rolls.Count(x => x <= 10000), 800, 1200); // half below 10,000 on a log scale
        }
    }
}
