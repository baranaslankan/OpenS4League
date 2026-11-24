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
        public DateTimeOffset ExpireDate =>
            PeriodType == ItemPeriodType.Days ? PurchaseDate.AddDays(Period) : DateTimeOffset.MinValue;
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

        public bool IsExpired()
        {
            switch (PeriodType)
            {
                case ItemPeriodType.Days:
                    return ExpireDate < DateTimeOffset.Now;

                case ItemPeriodType.Units:
                    return Durability == 0;

                default:
                    return false;
            }
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

        // TODO Calculate refund/repair cost

        public uint CalculateRefund()
        {
            return 0;
        }

        public uint CalculateRepair()
        {
            return 0;
        }
    }
}
