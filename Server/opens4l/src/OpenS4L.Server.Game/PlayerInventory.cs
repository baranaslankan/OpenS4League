using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Blub.Collections.Concurrent;
using Logging;
using Microsoft.EntityFrameworkCore;
using OpenS4L.Common;
using OpenS4L.Database;
using OpenS4L.Database.Game;
using OpenS4L.Network.Data.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Mappers;
using OpenS4L.Server.Game.Services;
using Newtonsoft.Json;

namespace OpenS4L.Server.Game
{
    public class PlayerInventory : IReadOnlyCollection<PlayerItem>
    {
        private readonly GameDataService _gameDataService;
        private readonly IdGeneratorService _idGeneratorService;
        private readonly GameMapper _mapper;
        private readonly ConcurrentDictionary<ulong, PlayerItem> _items;
        private readonly ConcurrentStack<PlayerItem> _itemsToRemove;
        private ILogger _logger;

        public Player Player { get; private set; }
        public int Count => _items.Count;

        /// <summary>
        /// True when any item is dirty or queued for removal (i.e. this inventory has unsaved
        /// changes that need flushing on the next save cycle).
        /// </summary>
        public bool HasPendingChanges => !_itemsToRemove.IsEmpty || _items.Values.Any(x => x.IsDirty);

        /// <summary>
        /// Returns the item with the given id or null if not found
        /// </summary>
        public PlayerItem this[ulong id] => GetItem(id);

        public PlayerInventory(ILogger<PlayerInventory> logger, GameDataService gameDataService,
            IdGeneratorService idGeneratorService, GameMapper mapper)
        {
            _logger = logger;
            _gameDataService = gameDataService;
            _idGeneratorService = idGeneratorService;
            _mapper = mapper;
            _items = new ConcurrentDictionary<ulong, PlayerItem>();
            _itemsToRemove = new ConcurrentStack<PlayerItem>();
        }

        internal void Initialize(Player plr, PlayerEntity entity)
        {
            Player = plr;
            _logger = plr.AddContextToLogger(_logger);

            foreach (var entityItem in entity.Items)
            {
                PlayerItem item;
                try
                {
                    item = new PlayerItem(_logger, _gameDataService, this, entityItem);
                }
                catch (InvalidOperationException)
                {
                    // Shop data has moved on since this item was saved (e.g. a shop
                    // reprovision regenerated shop_iteminfos.Id) - drop the orphaned item
                    // instead of crashing the whole login.
                    _logger.Warning(
                        "Dropping player item Id={ItemId} - no ShopItemInfo/PriceGroup " +
                        "matches ShopItemInfoId={ShopItemInfoId} ShopPriceId={ShopPriceId} " +
                        "(stale shop data?)",
                        entityItem.Id, entityItem.ShopItemInfoId, entityItem.ShopPriceId);
                    continue;
                }
                // Already expired at login: deleted, never listed. Characters loaded after this don't
                // find it and drop it from their slots; the save clears their references before deleting.
                if (item.IsExpired)
                {
                    _logger.Information("Deleting expired item Id={ItemId} ItemNumber={ItemNumber} ExpireDate={ExpireDate}",
                        item.Id, item.ItemNumber, item.ExpireDate);
                    _itemsToRemove.Push(item);
                    continue;
                }

                _items.TryAdd(item.Id, item);
            }
        }

        /// <summary>
        /// Handles period items whose time ran out: unequips them from their character (the client gets
        /// the usual UnEquip ack), then deletes them from the inventory (the client gets the delete ack).
        /// Returns the deleted items.
        /// </summary>
        public IReadOnlyList<PlayerItem> UpdateExpiredItems(DateTimeOffset now)
        {
            var expired = _items.Values.Where(x => x.IsExpiredAt(now)).ToList();
            foreach (var item in expired)
            {
                item.CharacterInventory?.Remove(item);
                Remove(item);
            }

            return expired;
        }

        /// <summary>
        /// Returns the item with the given id or null if not found
        /// </summary>
        public PlayerItem GetItem(ulong id)
        {
            _items.TryGetValue(id, out var item);
            return item;
        }

        /// <summary>
        /// Error codes for item creation failures.
        /// </summary>
        public enum CreateError
        {
            /// <summary>Item was created successfully.</summary>
            None = 0,
            /// <summary>No shop item info exists for the given item number / price type.</summary>
            ItemNotFound = 1,
            /// <summary>No price exists for the given period type / period.</summary>
            PriceNotFound = 2
        }

        /// <summary>
        /// Creates a new item, returning an error code instead of throwing.
        /// </summary>
        public CreateError TryCreate(ItemNumber itemNumber, ItemPriceType priceType, ItemPeriodType periodType,
            ushort period, byte color, uint[] effects, uint count, out PlayerItem item)
        {
            item = null;

            var shopItemInfo = _gameDataService.GetShopItemInfo(itemNumber, priceType);
            if (shopItemInfo == null)
                return CreateError.ItemNotFound;

            var price = shopItemInfo.PriceGroup.GetPrice(periodType, period);
            if (price == null)
                return CreateError.PriceNotFound;

            item = Create(shopItemInfo, price, color, effects);
            return CreateError.None;
        }

        /// <summary>
        /// Creates a new item, returning an error code instead of throwing.
        /// </summary>
        public CreateError TryCreate(ShopItemInfo shopItemInfo, ShopPrice price, byte color, uint[] effects,
            out PlayerItem item)
        {
            item = null;

            if (shopItemInfo == null)
                return CreateError.ItemNotFound;

            if (price == null)
                return CreateError.PriceNotFound;

            item = Create(shopItemInfo, price, color, effects);
            return CreateError.None;
        }

        /// <summary>
        /// Creates a new item
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        public PlayerItem Create(ItemNumber itemNumber, ItemPriceType priceType, ItemPeriodType periodType, ushort period,
            byte color, uint[] effects, uint count, bool sendUpdate = true)
        {
            var result = TryCreate(itemNumber, priceType, periodType, period, color, effects, count, out var item);
            if (result != CreateError.None)
                throw new ArgumentException(result.ToString());

            return item;
        }

        /// <summary>
        /// Creates a new item
        /// </summary>
        public PlayerItem Create(ShopItemInfo shopItemInfo, ShopPrice price, byte color, uint[] effects, bool sendUpdate = true)
        {
            // Per-unit items stack: the client shows an item's durability as its count.
            if (price.PeriodType == ItemPeriodType.Units)
            {
                var units = Math.Max(1, (int)price.Period);
                var stack = _items.Values.FirstOrDefault(x => x.ItemNumber == shopItemInfo.ShopItem.ItemNumber &&
                                                              x.PeriodType == ItemPeriodType.Units && x.Color == color);
                if (stack != null)
                {
                    stack.Durability += units;
                    if (sendUpdate)
                        SendStackUpdate(stack);
                    return stack;
                }

                var first = CreateNew(shopItemInfo, price, color, effects, false);
                first.Durability = units;
                if (sendUpdate)
                    Player.Session.Send(new ItemUpdateInventoryAckMessage(InventoryAction.Add, _mapper.ToItemDto(first)));
                return first;
            }

            return CreateNew(shopItemInfo, price, color, effects, sendUpdate);
        }

        /// <summary>
        /// Uses up one of a per-unit item: lowers the stack, or removes the item when it was the last one.
        /// </summary>
        public bool UseOne(PlayerItem item)
        {
            if (GetItem(item.Id) == null)
                return false;

            if (item.PeriodType == ItemPeriodType.Units && item.Durability > 1)
            {
                item.Durability -= 1;
                SendStackUpdate(item);
                return true;
            }

            return Remove(item);
        }

        private void SendStackUpdate(PlayerItem item)
        {
            Player.Session.Send(new ItemUpdateInventoryAckMessage(InventoryAction.Update, _mapper.ToItemDto(item)));
        }

        private PlayerItem CreateNew(ShopItemInfo shopItemInfo, ShopPrice price, byte color, uint[] effects, bool sendUpdate)
        {
            var itemId = _idGeneratorService.GetNextId(IdKind.Item);
            var item = new PlayerItem(_gameDataService, this,
                itemId, shopItemInfo, price, color, effects, DateTimeOffset.Now);
            _items.TryAdd(item.Id, item);

            if (sendUpdate)
                Player.Session.Send(new ItemUpdateInventoryAckMessage(InventoryAction.Add, _mapper.ToItemDto(item)));

            return item;
        }

        /// <summary>
        /// Gives a shop item without charging for it (admin commands, capsule rewards).
        /// Uses the shop price matching <paramref name="periodType"/>/<paramref name="period"/> when one
        /// exists, otherwise a permanent price, otherwise any enabled price. Effects match a purchase.
        /// Returns null when the item is not in the shop or has no enabled price.
        /// </summary>
        public PlayerItem CreateFromShop(ItemNumber itemNumber, ItemPeriodType periodType = ItemPeriodType.None,
            ushort period = 0, byte color = 0, uint[] effects = null, bool noEffects = false)
        {
            var shopItem = _gameDataService.GetShopItem(itemNumber);
            if (shopItem == null)
                return null;

            // Listings hidden from the shop still count: capsules can give items that aren't sold.
            // The visible listing wins over a hidden one even when only the hidden one has the exact
            // period: PlayerItem resolves its listing by item number + price type (the first one), so
            // an item created from another listing could not be saved. The provisioned shop has one
            // listing per item, whose price group carries permanent + the day periods.
            var choice = shopItem.ItemInfos
                .Where(info => info.PriceGroup != null)
                // Prices the shop doesn't offer (IsEnabled false, e.g. the hidden permanent price of
                // an item sold for days only) still count when they are the exact period asked for.
                .SelectMany(info => info.PriceGroup.Prices.Select(price => (info, price)))
                .Where(x => x.price.IsEnabled || (x.price.PeriodType == periodType && x.price.Period == period))
                .OrderBy(x => x.info.IsEnabled ? 0 : 1)
                .ThenBy(x => x.price.PeriodType == periodType && x.price.Period == period ? 0
                    // a timed reward without its exact period: the nearest timed price, never permanent
                    : IsTimed(periodType) && IsTimed(x.price.PeriodType) ? 1
                    : x.price.PeriodType == ItemPeriodType.None ? 2 : 3)
                .ThenBy(x => x.price.IsEnabled ? 0 : 1)
                .ThenBy(x => IsTimed(periodType) && IsTimed(x.price.PeriodType)
                    ? Math.Abs(ToHours(x.price.PeriodType, x.price.Period) - ToHours(periodType, period))
                    // a permanent reward of an item sold only for a time: the longest time
                    : periodType == ItemPeriodType.None && IsTimed(x.price.PeriodType)
                        ? -ToHours(x.price.PeriodType, x.price.Period)
                        : 0)
                .ThenBy(x => x.price.Period) // per-unit items: one unit
                .FirstOrDefault();

            if (choice.info == null)
                return null;

            // Ids the client doesn't know (neither an effect nor an effect group) fall back to the shop's.
            // Those come from the cash (AP) listing whatever price was picked: the PEN listing has the
            // low PEN-shop stats (weapon Attack+1%), and rewards (capsules, set boxes) are the cash
            // shop's items. A timed reward would otherwise land on the PEN listing's stats.
            effects = noEffects ? Array.Empty<uint>() : effects?.Where(x => _gameDataService.IsKnownEffect(x)).ToArray();
            if (!noEffects && (effects == null || effects.Length == 0))
                effects = ShopEffects(shopItem.ItemInfos.FirstOrDefault(x => x.PriceGroup?.PriceType == ItemPriceType.AP))
                          ?? ShopEffects(choice.info)
                          ?? Array.Empty<uint>();
            return Create(choice.info, choice.price, color, effects);
        }

        private static uint[] ShopEffects(ShopItemInfo info)
        {
            var effects = info?.EffectGroup?.Effects.Select(x => x.Effect).Where(x => x != 0).ToArray();
            return effects == null || effects.Length == 0 ? null : effects;
        }

        private static bool IsTimed(ItemPeriodType type) => type == ItemPeriodType.Days || type == ItemPeriodType.Hours;

        private static long ToHours(ItemPeriodType type, ushort period) =>
            type == ItemPeriodType.Days ? period * 24L : period;

        /// <summary>
        /// Removes the item from the inventory
        /// </summary>
        public bool Remove(PlayerItem item)
        {
            return Remove(item.Id);
        }

        /// <summary>
        /// Removes the item from the inventory
        /// </summary>
        public bool Remove(ulong id)
        {
            var item = GetItem(id);
            if (item == null)
                return false;

            _items.Remove(item.Id);
            if (item.Exists)
                _itemsToRemove.Push(item);

            Player.Session.Send(new ItemInventroyDeleteAckMessage(item.Id));
            return true;
        }

        /// <summary>
        /// Captures the inventory's unsaved state into a snapshot (pure read — does not mutate
        /// the live inventory). New and dirty-existing items are included so the writer can
        /// reproduce the old <c>Save</c> exactly; unchanged existing items are skipped.
        /// </summary>
        public void BuildSnapshot(PlayerSaveSnapshot s)
        {
            foreach (var itemToRemove in _itemsToRemove)
                s.ItemIdsToRemove.Add((long)itemToRemove.Id);

            foreach (var item in _items.Values)
            {
                if (!item.Exists)
                {
                    s.Items.Add(new SnapshotItem
                    {
                        Id = (long)item.Id,
                        ShopItemInfoId = item.GetShopItemInfo().Id,
                        ShopPriceId = item.GetShopItemInfo().PriceGroup.GetPrice(item.PeriodType, item.Period).Id,
                        Effects = JsonConvert.SerializeObject(item.Effects.ToArray()),
                        Color = item.Color,
                        PurchaseDate = item.PurchaseDate.ToUnixTimeSeconds(),
                        Durability = item.Durability,
                        MP = (int)item.EnchantMP,
                        MPLevel = (int)item.EnchantLevel,
                        Exists = false
                    });
                }
                else if (item.IsDirty)
                {
                    s.Items.Add(new SnapshotItem
                    {
                        Id = (long)item.Id,
                        ShopItemInfoId = item.GetShopItemInfo().Id,
                        ShopPriceId = item.GetShopPrice().Id,
                        Effects = JsonConvert.SerializeObject(item.Effects.ToArray()),
                        Color = item.Color,
                        PurchaseDate = item.PurchaseDate.ToUnixTimeSeconds(),
                        Durability = item.Durability,
                        MP = (int)item.EnchantMP,
                        MPLevel = (int)item.EnchantLevel,
                        Exists = true
                    });
                }
            }
        }

        /// <summary>
        /// Marks the captured snapshot as persisted: drains the remove-stack, flags new items as
        /// existing, and clears item dirty flags. Called only after the snapshot was durably
        /// enqueued (or written directly), so a failed publish leaves state dirty for retry.
        /// </summary>
        public void ClearPendingChanges()
        {
            while (_itemsToRemove.TryPop(out _)) { }

            foreach (var item in _items.Values)
            {
                if (!item.Exists)
                    item.SetExistsState(true);
                else
                    item.SetDirtyState(false);
            }
        }

        public bool Contains(ulong id)
        {
            return _items.ContainsKey(id);
        }

        public IEnumerator<PlayerItem> GetEnumerator()
        {
            return _items.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
