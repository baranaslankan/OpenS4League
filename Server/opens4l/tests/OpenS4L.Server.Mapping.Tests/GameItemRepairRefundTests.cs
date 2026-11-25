using System;
using OpenS4L.Database.Game;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Services;
using Xunit;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// PlayerItem.CalculateRepair/CalculateRefund, which mirror the client's resell/repair dialogs.
    /// </summary>
    public class GameItemRepairRefundTests
    {
        private readonly GameDataService _gds = GameFixtures.CreateGameDataService();

        private PlayerItem Create(ItemNumber itemNumber, ShopPriceEntity priceEntity, DateTimeOffset? purchaseDate = null,
            int repairCost = 0)
        {
            var group = GameFixtures.SeedPriceGroup(_gds, 30, ItemPriceType.PEN, priceEntity);
            GameFixtures.SeedShopItem(_gds, itemNumber, 30);
            _gds.ShopItems[itemNumber].RepairCost = repairCost;
            var info = _gds.GetShopItemInfo(itemNumber, ItemPriceType.PEN);
            return GameFixtures.CreatePlayerItem(_gds, info, group.GetPrice(priceEntity.Id),
                purchaseDate ?? DateTimeOffset.Now);
        }

        private static ShopPriceEntity Permanent(uint price, int durability) => new ShopPriceEntity
        {
            Id = 301, PeriodType = (byte)ItemPeriodType.None, Period = 0, Price = (int)price,
            IsRefundable = true, Durability = durability
        };

        [Fact]
        public void Weapon_repair_andRefund()
        {
            var item = Create(new ItemNumber(ItemCategory.Weapon, 2, 1), Permanent(5000, 100));
            Assert.Equal(0u, item.CalculateRepair());
            Assert.Equal(1250u, item.CalculateRefund()); // 5000 * 0.15 + 5000 * 0.1 * 1

            item.Durability = 40;
            // lost 60 * 1.904762 = 114 -> halved
            Assert.Equal(57u, item.CalculateRepair());
            // 5000 * 0.15 + 5000 * 0.1 * 0.4
            Assert.Equal(950u, item.CalculateRefund());
        }

        [Fact]
        public void Costume_repair_usesShopRepairCost()
        {
            var item = Create(new ItemNumber(ItemCategory.Costume, 2, 1), Permanent(5000, 100));
            item.Durability = 40;
            Assert.Equal(0u, item.CalculateRepair()); // RepairCost is sent as 0

            item = Create(new ItemNumber(ItemCategory.Costume, 2, 2), Permanent(5000, 100), repairCost: 1000);
            item.Durability = 40;
            // ceil(1000 * 0.75 * 0.6) = 450 -> halved
            Assert.Equal(225u, item.CalculateRepair());
        }

        [Fact]
        public void PeriodItem_refund_byRemainingDays_andNoRepair()
        {
            var days = new ShopPriceEntity
            {
                Id = 302, PeriodType = (byte)ItemPeriodType.Days, Period = 30, Price = 5000,
                IsRefundable = true, Durability = 100
            };
            var item = Create(new ItemNumber(ItemCategory.Weapon, 2, 3), days, DateTimeOffset.Now.AddDays(-15));
            // 5000 * 0.2 * 15 / 30 (a few ms less than 15 days remain)
            Assert.InRange(item.CalculateRefund(), 499u, 500u);
            item.Durability = 10;
            Assert.Equal(0u, item.CalculateRepair());

            // Expired items count as the full period, like the client
            item = Create(new ItemNumber(ItemCategory.Weapon, 2, 4), days, DateTimeOffset.Now.AddDays(-40));
            Assert.Equal(1000u, item.CalculateRefund());
        }

        [Fact]
        public void UnitItem_refund_perUnitLeft()
        {
            var units = new ShopPriceEntity
            {
                Id = 303, PeriodType = (byte)ItemPeriodType.Units, Period = 5, Price = 1000,
                IsRefundable = true, Durability = 5
            };
            var item = Create(new ItemNumber(ItemCategory.OneTimeUse, 1, 1), units);
            Assert.Equal(1000u, item.CalculateRefund());
            item.Durability = 2;
            Assert.Equal(400u, item.CalculateRefund());
            Assert.Equal(0u, item.CalculateRepair());
        }

        [Fact]
        public void EsperChip_refund_fullPrice()
        {
            var item = Create(new ItemNumber(ItemCategory.EsperChip, 1, 1), Permanent(3000, 10));
            Assert.Equal(3000u, item.CalculateRefund());
        }

        [Fact]
        public void IndestructibleItem_noRepair()
        {
            var item = Create(new ItemNumber(ItemCategory.Weapon, 2, 5), Permanent(5000, -1));
            Assert.Equal(0u, item.CalculateRepair());
        }
    }
}
