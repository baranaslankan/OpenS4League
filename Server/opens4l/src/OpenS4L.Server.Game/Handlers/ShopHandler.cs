using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Logging;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Rules;
using OpenS4L.Server.Game.Services;
using ProudNet;

namespace OpenS4L.Server.Game.Handlers
{
    internal class ShopHandler : IHandle<ItemBuyItemReqMessage>, IHandle<RandomShopUpdateCheckReqMessage>,
        IHandle<RandomShopRollingStartReqMessage>
    {
        private readonly GameDataService _gameDataService;
        private readonly RandomShopService _randomShopService;
        private readonly ILogger _logger;

        public ShopHandler(GameDataService gameDataService, RandomShopService randomShopService,
            ILogger<ShopHandler> logger)
        {
            _gameDataService = gameDataService;
            _randomShopService = randomShopService;
            _logger = logger;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemBuyItemReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var plrLogger = plr.AddContextToLogger(_logger);
            var logger = plrLogger;

            var itemsTobuy = message.Items.GroupBy(x => x);
            var newItems = new List<PlayerItem>();

            foreach (var group in itemsTobuy)
            {
                logger = plrLogger.ForContext("@ItemToBuy", group.Key);
                var itemToBuy = group.Key;
                var count = group.Count();
                logger.Debug("Trying to buy item");

                var shopItem = _gameDataService.GetShopItem(itemToBuy.ItemNumber);
                if (shopItem == null)
                {
                    logger.Warning("Trying to buy non-existant item");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                // TODO master level

                if (shopItem.MinLevel > plr.Level || shopItem.MaxLevel != 0 && shopItem.MaxLevel < plr.Level)
                {
                    logger.Warning(
                        "Trying to buy item without meeting level requirements MinLevel={Minlevel} MaxLevel={MaxLevel} PlayerLevel={PlayerLevel}",
                        shopItem.MinLevel,
                        shopItem.MaxLevel,
                        plr.Level
                    );
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                if (itemToBuy.Color > shopItem.ColorGroup)
                {
                    logger.Warning("Trying to buy item with invalid color");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                var shopItemInfo = shopItem.GetItemInfo(itemToBuy.PriceType);
                if (shopItemInfo == null)
                {
                    logger.Warning("Trying to buy non-existant item");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                if (!shopItemInfo.IsEnabled)
                {
                    logger.Warning("Trying to buy disabled item");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                var priceInfo = shopItemInfo.PriceGroup.GetPrice(itemToBuy.PeriodType, itemToBuy.Period);
                if (priceInfo == null)
                {
                    logger.Warning("Trying to buy item with invalid price info");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                if (!priceInfo.IsEnabled)
                {
                    logger.Warning("Trying to buy item with disabled price info");
                    session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.UnkownItem));
                    continue;
                }

                var cost = (uint)(priceInfo.Price * count);
                switch (itemToBuy.PriceType)
                {
                    case ItemPriceType.PEN:
                        if (plr.PEN < cost)
                        {
                            logger.Warning("Trying to buy item without enough PEN currentPEN={CurrentPEN}", plr.PEN);
                            session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.NotEnoughMoney));
                            return true;
                        }

                        plr.PEN -= cost;

                        break;

                    case ItemPriceType.AP:
                    case ItemPriceType.Premium:
                        if (plr.AP < cost)
                        {
                            logger.Warning("Trying to buy item without enough AP currentAP={CurrentAP}", plr.AP);
                            session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.NotEnoughMoney));
                            return true;
                        }

                        plr.AP -= cost;

                        break;

                    // TODO Implement other price types

                    default:
                        logger.Warning("Trying to buy item with invalid price type");
                        session.Send(new ItemBuyItemAckMessage(itemToBuy, ItemBuyResult.DBError));
                        return true;
                }

                for (var i = 0; i < count; ++i)
                {
                    var effects = Array.Empty<uint>();
                    if (shopItemInfo.EffectGroup.Effects.Count > 0)
                        effects = shopItemInfo.EffectGroup.Effects.Select(x => x.Effect).Where(x => x != 0).ToArray();

                    var result = plr.Inventory.TryCreate(shopItemInfo, priceInfo, itemToBuy.Color, effects,
                        out var newItem);
                    if (result != PlayerInventory.CreateError.None)
                    {
                        logger.Error("Unable to create item {ItemNumber} error={Error}",
                            itemToBuy.ItemNumber, result);
                        break;
                    }

                    newItems.Add(newItem);
                }

                var newItemIds = newItems.Select(x => x.Id).ToArray();
                session.Send(new ItemBuyItemAckMessage(newItemIds, itemToBuy));
                newItems.Clear();

                plr.SendMoneyUpdate();
            }

            return true;
        }

        // Fumbi random shop data check. Unk1 is the version of the client's cached shop data
        // (./shop/_rc_new_random_shop). The client compares the ack's version with its cache: equal
        // means "cache is current", otherwise it waits for RandomShopUpdateInfoAck with all five data
        // sections (Unk1 = 0x1F), LZO-compressed like the NewShop data. Unk2 is not used.
        [Firewall(typeof(MustBeLoggedIn))]
        public Task<bool> OnHandle(MessageContext context, RandomShopUpdateCheckReqMessage message)
        {
            var session = context.GetSession<Session>();
            var data = _randomShopService.GetClientData();
            session.Send(new RandomShopUpdateCheckAckMessage { Unk = data.Version });

            if (message.Unk1 != data.Version)
            {
                session.Send(new RandomShopUpdateInfoAckMessage
                {
                    Unk1 = RandomShopService.AllSections,
                    Unk2 = data.Data,
                    Unk3 = data.CompressedLength,
                    Unk4 = data.DecompressedLength,
                    Unk5 = data.Version
                });
            }

            return Task.FromResult(true);
        }

        // Fumbi random shop roll. Unk is the package id (a "machine" from the update data).
        [Inline]
        [Firewall(typeof(MustBeLoggedIn))]
        public Task<bool> OnHandle(MessageContext context, RandomShopRollingStartReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            var result = _randomShopService.Roll(plr, message.Unk, Random.Shared);
            if (result.Result != RandomShopService.ResultSuccess)
            {
                plr.AddContextToLogger(_logger).Warning("Random shop roll failed package={PackageId} result={Result}",
                    message.Unk, result.Result);
            }
            else
            {
                plr.SendMoneyUpdate();
            }

            session.Send(new RandomShopRollingStartAckMessage { Unk1 = result.Result, Items = result.Items });
            return Task.FromResult(true);
        }
    }
}
