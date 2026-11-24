using System;
using System.Collections.Immutable;
using System.Linq;
using Logging;
using OpenS4L.Database.Game;
using OpenS4L.Database.Helpers;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Services;
using Newtonsoft.Json;

namespace OpenS4L.Server.Game
{
    public class PlayerItem : DatabaseObject
    {
        private readonly GameDataService _gameDataService;
        private int _durability;
        private uint _enchantMP;
        private uint _enchantLevel;

        public PlayerInventory Inventory { get; }

        public ulong Id { get; }
        public ItemNumber ItemNumber { get; }
        public ItemPriceType PriceType { get; }
        public ItemPeriodType PeriodType { get; }
        public ushort Period { get; }
        public byte Color { get; }
        public PlayerItemEffectCollection Effects { get; }
        public DateTimeOffset PurchaseDate { get; }
        public int Durability
        {
            get => _durability;
            set => SetIfChanged(ref _durability, value);
        }
        /// <summary>
        /// When a timed (hour/day period) item runs out; MinValue for items without an expiry.
        /// </summary>
        public DateTimeOffset ExpireDate =>
            PeriodType == ItemPeriodType.Days ? PurchaseDate.AddDays(Period)
            : PeriodType == ItemPeriodType.Hours ? PurchaseDate.AddHours(Period)
            : DateTimeOffset.MinValue;

        /// <summary>True for hour/day period items, which expire at <see cref="ExpireDate"/>.</summary>
        public bool HasExpireDate => PeriodType == ItemPeriodType.Days || PeriodType == ItemPeriodType.Hours;

        public bool IsExpired => IsExpiredAt(DateTimeOffset.Now);

        public bool IsExpiredAt(DateTimeOffset now) => HasExpireDate && now >= ExpireDate;
        public uint EnchantMP
        {
            get => _enchantMP;
            set => SetIfChanged(ref _enchantMP, value);
        }
        public uint EnchantLevel
        {
            get => _enchantLevel;
            set => SetIfChanged(ref _enchantLevel, value);
        }

        public CharacterInventory CharacterInventory { get; internal set; }

        internal PlayerItem(ILogger logger, GameDataService gameDataService, PlayerInventory inventory, PlayerItemEntity entity)
        {
            _gameDataService = gameDataService;
            Inventory = inventory;
            Id = (ulong)entity.Id;

            var itemInfo = _gameDataService.ShopItems.Values.First(group => group.GetItemInfo(entity.ShopItemInfoId) != null);
            ItemNumber = itemInfo.ItemNumber;

            var priceGroup = _gameDataService.ShopPrices.Values.First(group => group.GetPrice(entity.ShopPriceId) != null);
            var price = priceGroup.GetPrice(entity.ShopPriceId);

            PriceType = priceGroup.PriceType;
            PeriodType = price.PeriodType;
            Period = price.Period;
            Color = entity.Color;

            var effects = Array.Empty<uint>();
            if (!string.IsNullOrWhiteSpace(entity.Effects))
            {
                try
                {
                    effects = JsonConvert.DeserializeObject<uint[]>(entity.Effects);
                }
                catch (Exception ex)
                {
                    logger.Warning(
                        ex,
                        "Unable to load effects from item={ItemId} effects={Effects}",
                        entity.Id,
                        entity.Effects
                    );
                }
            }

            Effects = new PlayerItemEffectCollection(this, effects);
            PurchaseDate = DateTimeOffset.FromUnixTimeSeconds(entity.PurchaseDate);
            _durability = entity.Durability;
            _enchantMP = (uint)entity.MP;
            _enchantLevel = (uint)entity.MPLevel;

            SetExistsState(true);
        }

        internal PlayerItem(GameDataService gameDataService, PlayerInventory inventory, long id,
            ShopItemInfo itemInfo, ShopPrice price,
            byte color, uint[] effects, DateTimeOffset purchaseDate)
        {
            _gameDataService = gameDataService;
            Inventory = inventory;
            Id = (ulong)id;
            ItemNumber = itemInfo.ShopItem.ItemNumber;
            PriceType = itemInfo.PriceGroup.PriceType;
            PeriodType = price.PeriodType;
            Period = price.Period;
            Color = color;
            Effects = new PlayerItemEffectCollection(this, effects);
            PurchaseDate = purchaseDate;
            _durability = price.Durability;
        }

        public ItemEffect[] GetItemEffects()
        {
            if (Effects.Count == 0)
                return null;

            return Effects.Select(x => _gameDataService.Effects.GetValueOrDefault(x)).ToArray();
        }

        /// <summary>
        /// Sum of the Rate (percent) of this item's effects of the given type.
        /// </summary>
        public float GetEffectRate(EffectType effectType)
        {
            return Effects
                .Select(x => _gameDataService.Effects.GetValueOrDefault(x))
                .Where(x => x != null && x.EffectType == effectType)
                .Sum(x => x.Rate);
        }

        /// <summary>
        /// The PEN bonus (percent) of a PEN Plus boost item, 0 for anything else.
        /// Uses the item's effects; items without effects (the provisioned shop has a single empty
        /// effect group) fall back to effect_list.x7, where PEN Plus 500000N carries effect
        /// 1999801000+N (5000001 = 50%, 5000002 = 100%, 5000003..5000010 = 10..90%).
        /// </summary>
        public float GetPENBoostRate()
        {
            if (ItemNumber.Category != ItemCategory.Boost || ItemNumber.SubCategory != 0)
                return 0;

            var rate = GetEffectRate(EffectType.PENGain);
            if (rate > 0 || Effects.Count > 0)
                return rate;

            var effect = _gameDataService.Effects.GetValueOrDefault(1999801000u + ItemNumber.Number);
            return effect != null && effect.EffectType == EffectType.PENGain ? effect.Rate : 0;
        }

        /// <summary>Expired by date, or a per-unit item with no units left.</summary>
        public bool IsExpiredOrUsedUp()
        {
            return IsExpired || (PeriodType == ItemPeriodType.Units && Durability == 0);
        }

        public ShopItem GetShopItem()
        {
            return _gameDataService.GetShopItem(ItemNumber);
        }

        public ShopItemInfo GetShopItemInfo()
        {
            return _gameDataService.GetShopItemInfo(ItemNumber, PriceType);
        }

        public ShopPrice GetShopPrice()
        {
            return GetShopItemInfo().PriceGroup.GetPrice(PeriodType, Period);
        }

        public int LoseDurability(int loss)
        {
            if (loss < 0)
                throw new ArgumentOutOfRangeException(nameof(loss));

            if (Inventory.Player.Room == null)
                throw new InvalidOperationException("Player is not inside a room");

            if (Durability == -1 || loss == 0)
                return 0;

            loss = Math.Min(Durability, loss);
            Durability -= loss;
            return loss;
        }

        // Refund and repair mirror the client's own calculation (S4ClientLocal.exe 0.8.32.26995,
        // item class around 0x1079b00/0x107a150) so the PEN the client shows in the resell/repair
        // dialogs is what the server charges or pays.

        /// <summary>
        /// Resell value in PEN, as the client computes it:
        /// - Days/Hours items: trunc(price * 0.2 * remaining / total) (remaining days from the
        ///   expire date; Hours use Durability as remaining hours and Period as total)
        /// - permanent costumes/weapons/skills: trunc(price * 0.15 + price * 0.1 * durability / maxDurability)
        /// - permanent esper chips with durability: price
        /// - per-unit items: price * units left / bundle size (the client shows price * units left;
        ///   the division is server-side only so bundles can't refund more than they cost)
        /// - anything else: 0
        /// price is the shop price of this item's period with the item info's discount applied
        /// (price * (100 + discount) / 100, the client skips it for price type 4).
        /// </summary>
        public uint CalculateRefund()
        {
            var shopItemInfo = GetShopItemInfo();
            var shopPrice = shopItemInfo?.PriceGroup.GetPrice(PeriodType, Period);
            if (shopPrice == null)
                return 0;

            var price = (long)shopPrice.Price;
            if (PriceType != ItemPriceType.None)
                price = ((short)shopItemInfo.Discount + 100) * price / 100;

            if (price <= 0)
                return 0;

            var total = 0.0;
            if (PeriodType == ItemPeriodType.Hours || PeriodType == ItemPeriodType.Days)
                total = Period;

            if (total != 0)
            {
                var remaining = PeriodType == ItemPeriodType.Hours
                    ? Durability
                    : (ExpireDate - DateTimeOffset.Now).TotalDays;
                if (remaining < 0)
                    remaining = total;

                return ToUInt(price * 0.2 * (remaining / total));
            }

            var maxDurability = PeriodType == ItemPeriodType.None ? shopPrice.Durability : -1;
            var category = ItemNumber.Category;
            if (category == ItemCategory.Costume || category == ItemCategory.Weapon || category == ItemCategory.Skill ||
                category == ItemCategory.EsperChip && maxDurability > 0)
            {
                if (category == ItemCategory.EsperChip)
                    return ToUInt(price);

                return ToUInt(price * 0.15 + price * 0.1 * ((double)Durability / maxDurability));
            }

            // The client shows price * units left. A unit price is the price of the whole bundle
            // (Period units), so pay per unit left to keep bundles from refunding more than they
            // cost. Same as the client for single-unit prices.
            if (PeriodType == ItemPeriodType.Units)
                return ToUInt(price * (double)Math.Min(Durability, Math.Max(1, (int)Period)) / Math.Max(1, (int)Period));

            return 0;
        }

        /// <summary>
        /// Repair cost in PEN for the lost durability, as the client computes it (EU branch):
        /// - only permanent items; lost = maxDurability - durability
        /// - costumes: ceil(repairCost * 0.75 * lost / maxDurability), where repairCost is the shop
        ///   list's RepairCost field (ShopItem.RepairCost, currently always 0, so costumes repair for free)
        /// - everything else: trunc(lost * 1.904762)
        /// - the result is halved (integer shift)
        /// </summary>
        public uint CalculateRepair()
        {
            if (PeriodType != ItemPeriodType.None || Durability == -1)
                return 0;

            var shopPrice = GetShopPrice();
            if (shopPrice == null || shopPrice.Durability <= 0)
                return 0;

            var maxDurability = shopPrice.Durability;
            var lost = maxDurability - Durability;
            if (lost <= 0)
                return 0;

            uint cost;
            if (ItemNumber.Category == ItemCategory.Costume)
            {
                var repairCost = (uint)Math.Max(0, GetShopItem()?.RepairCost ?? 0);
                var ratio = (float)lost / maxDurability;
                cost = ToUInt(Math.Ceiling((double)((float)repairCost * 0.75f * ratio)));
            }
            else
            {
                cost = (uint)(int)(lost * 1.904762f);
            }

            return cost >> 1;
        }

        private static uint ToUInt(double value)
        {
            if (value <= 0 || double.IsNaN(value))
                return 0;

            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }
    }
}
