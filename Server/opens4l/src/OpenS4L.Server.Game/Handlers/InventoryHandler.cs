using System;
using System.Linq;
using System.Threading.Tasks;
using Logging;
using OpenS4L.Network;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Services;
using ProudNet;

namespace OpenS4L.Server.Game.Handlers
{
    internal class InventoryHandler
        : IHandle<ItemUseItemReqMessage>, IHandle<ItemRepairItemReqMessage>, IHandle<ItemRefundItemReqMessage>,
          IHandle<ItemDiscardItemReqMessage>, IHandle<ItemUseCapsuleReqMessage>
    {
        private readonly ILogger _logger;
        private readonly GameDataService _gameDataService;

        public InventoryHandler(ILogger<InventoryHandler> logger, GameDataService gameDataService)
        {
            _logger = logger;
            _gameDataService = gameDataService;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemUseCapsuleReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var capsule = plr?.Inventory[message.ItemId];
            if (capsule == null)
            {
                session.Send(new ItemUseCapsuleAckMessage(CapsuleOpener.ResultFailed));
                return true;
            }

            var rewards = CapsuleOpener.Open(plr, capsule, _gameDataService, Random.Shared);
            if (rewards == null)
            {
                _logger.Warning("Unable to open capsule {ItemNumber} (no known rewards in the shop)",
                    capsule.ItemNumber);
                session.Send(new ItemUseCapsuleAckMessage(CapsuleOpener.ResultFailed));
                return true;
            }

            _logger.Information("Opened capsule {ItemNumber}: {Rewards}", capsule.ItemNumber,
                string.Join(", ", rewards.Select(r => r.RewardType == CapsuleRewardType.PEN
                    ? r.PEN + " PEN"
                    : r.ItemNumber.ToString())));
            session.Send(new ItemUseCapsuleAckMessage(rewards, CapsuleOpener.ResultSuccess));
            return true;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemUseItemReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var character = plr.CharacterManager[message.CharacterSlot];
            var item = plr.Inventory[message.ItemId];

            // This is a weird thing since newer seasons
            // The client sends a request with itemid 0 on login
            // and requires an answer to it for equipment to work properly
            if (message.Action == UseItemAction.UnEquip && message.ItemId == 0)
            {
                session.Send(new ItemUseItemAckMessage(
                    message.CharacterSlot,
                    message.EquipSlot,
                    message.ItemId,
                    message.Action
                ));
                return true;
            }

            if (character == null || item == null || plr.Room != null && plr.State != PlayerState.Lobby)
            {
                _logger.Warning("Item use refused Action={Action} Item={ItemNumber} CharacterSlot={CharacterSlot} EquipSlot={EquipSlot} InRoom={InRoom}",
                    message.Action, item?.ItemNumber, message.CharacterSlot, message.EquipSlot, plr.Room != null);
                session.Send(new ServerResultAckMessage(ServerResult.FailedToRequestTask));
                return true;
            }

            // On success, Character.Equip/UnEquip raise ItemAdded/ItemRemoved, which send the
            // ItemUseItemAckMessage the client is waiting for. On failure nothing else responds,
            // so always send a result here too - otherwise the client hangs indefinitely on a
            // request the server dropped (see Character.Equip for the equip-slot-swap case this
            // used to hit on almost every weapon change).
            switch (message.Action)
            {
                case UseItemAction.Equip:
                {
                    var error = character.Equip(item, message.EquipSlot);
                    if (error != CharacterInventoryError.OK)
                    {
                        _logger.Warning("Equip refused Item={ItemNumber} EquipSlot={EquipSlot} Error={Error}",
                            item.ItemNumber, message.EquipSlot, error);
                        session.Send(new ServerResultAckMessage(ServerResult.WearingUnusableItem));
                    }
                    break;
                }

                case UseItemAction.UnEquip:
                    if (character.UnEquip(item.ItemNumber.Category, message.EquipSlot) != CharacterInventoryError.OK)
                        session.Send(new ServerResultAckMessage(ServerResult.WearingUnusableItem));
                    break;

                default:
                    // Not known yet (using a Unique Booster may send this): logged so the real client's
                    // request can be identified. No answer, as before: the client may send it routinely.
                    _logger.Warning("Unknown item use Action={Action} Item={ItemNumber} CharacterSlot={CharacterSlot} EquipSlot={EquipSlot}",
                        message.Action, item.ItemNumber, message.CharacterSlot, message.EquipSlot);
                    break;
            }

            return true;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemRepairItemReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var logger = plr.AddContextToLogger(_logger);

            foreach (var id in message.Items)
            {
                var item = session.Player.Inventory[id];
                if (item == null)
                {
                    logger.Warning("Item={ItemId} not found", id);
                    session.Send(new ItemRepairItemAckMessage(ItemRepairResult.Error0, 0));
                    return true;
                }

                if (item.Durability == -1)
                {
                    logger.Warning("Item={ItemId} can not be repaired", id);
                    session.Send(new ItemRepairItemAckMessage(ItemRepairResult.Error1, 0));
                    return true;
                }

                var cost = item.CalculateRepair();
                if (plr.PEN < cost)
                {
                    session.Send(new ItemRepairItemAckMessage(ItemRepairResult.NotEnoughMoney, 0));
                    return true;
                }

                var price = item.GetShopPrice();
                if (price == null)
                {
                    logger.Warning("No shop entry found item={ItemId}", id);
                    session.Send(new ItemRepairItemAckMessage(ItemRepairResult.Error2, 0));
                    return true;
                }

                if (item.Durability >= price.Durability)
                {
                    session.Send(new ItemRepairItemAckMessage(ItemRepairResult.OK, item.Id));
                    continue;
                }

                item.Durability = price.Durability;
                plr.PEN -= cost;

                session.Send(new ItemRepairItemAckMessage(ItemRepairResult.OK, item.Id));
                plr.SendMoneyUpdate();
            }

            return true;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemRefundItemReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var item = plr.Inventory[message.ItemId];
            var logger = plr.AddContextToLogger(_logger);

            if (item == null)
            {
                logger.Warning("Item={ItemId} not found", message.ItemId);
                session.Send(new ItemRefundItemAckMessage(ItemRefundResult.Failed, 0));
                return true;
            }

            var price = item.GetShopPrice();
            if (price == null)
            {
                logger.Warning("No shop entry found item={ItemId}", message.ItemId);
                session.Send(new ItemRefundItemAckMessage(ItemRefundResult.Failed, 0));
                return true;
            }

            if (!price.CanRefund)
            {
                logger.Warning("Cannot refund item={ItemId}", message.ItemId);
                session.Send(new ItemRefundItemAckMessage(ItemRefundResult.Failed, 0));
                return true;
            }

            plr.PEN += item.CalculateRefund();
            plr.Inventory.Remove(item);

            session.Send(new ItemRefundItemAckMessage(ItemRefundResult.OK, item.Id));
            plr.SendMoneyUpdate();

            return true;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, ItemDiscardItemReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var item = plr.Inventory[message.ItemId];
            var logger = plr.AddContextToLogger(_logger);

            if (item == null)
            {
                logger.Warning("Item={ItemId} not found", message.ItemId);
                session.Send(new ItemDiscardItemAckMessage(2, 0));
                return true;
            }

            var shopItem = item.GetShopItem();
            if (shopItem == null)
            {
                logger.Warning("No shop entry found item={ItemId}", message.ItemId);
                session.Send(new ItemDiscardItemAckMessage(2, 0));
                return true;
            }

            // An expired period item can always be thrown away.
            if (!shopItem.IsDestroyable && !item.IsExpired)
            {
                logger.Warning("Cannot discard item={ItemId}", message.ItemId);
                session.Send(new ItemDiscardItemAckMessage(2, 0));
                return true;
            }

            plr.Inventory.Remove(item);
            session.Send(new ItemDiscardItemAckMessage(0, item.Id));

            return true;
        }
    }
}
