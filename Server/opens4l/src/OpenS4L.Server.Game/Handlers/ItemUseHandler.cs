using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Logging;
using Microsoft.Extensions.Options;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Network;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Rules;
using OpenS4L.Server.Game.Services;
using ProudNet;

namespace OpenS4L.Server.Game.Handlers
{
    /// <summary>
    /// Utility items used from the inventory: nickname change, nickname change cancel and the
    /// Deathmatch record reset.
    /// </summary>
    internal class ItemUseHandler
        : IHandle<ItemUseChangeNickReqMessage>, IHandle<ItemUseChangeNickCancelReqMessage>,
          IHandle<ItemUseRecordResetReqMessage>
    {
        // Result codes of the client's ack handlers (S4ClientLocal.exe 0.8.32.26995):
        // ItemUseChangeNickAck 0 success, -1 already used, -4 bad characters, -6 too short,
        // anything else "failed". ItemUseChangeNickCancelAck and ItemUseRecordResetAck: 0 success,
        // anything else failed.
        public const uint ResultOK = 0;
        public const uint ResultAlreadyUsed = unchecked((uint)-1);
        public const uint ResultFailed = unchecked((uint)-2);
        public const uint ResultInvalidCharacters = unchecked((uint)-4);
        public const uint ResultTooShort = unchecked((uint)-6);

        /// <summary>Nickname change items (item.x7) and how many days the new nickname lasts (null = permanent).</summary>
        public static readonly IReadOnlyDictionary<ItemNumber, int?> ChangeNickItems = new Dictionary<ItemNumber, int?>
        {
            [4000001u] = null,
            [4000003u] = 1,
            [4000006u] = 3,
            [4000004u] = 7,
            [4000005u] = 30
        };

        /// <summary>"Remove nickname change" items (item.x7).</summary>
        public static readonly ItemNumber[] ChangeNickCancelItems = { 4000002u, 4000007u };

        /// <summary>Deathmatch kill/death record reset (item.x7).</summary>
        public static readonly ItemNumber RecordResetItem = 4010001u;

        private readonly ILogger _logger;
        private readonly DatabaseService _databaseService;
        private readonly GameOptions _gameOptions;
        private readonly NicknameLookupService _nicknameLookupService;

        public ItemUseHandler(ILogger<ItemUseHandler> logger, DatabaseService databaseService,
            IOptions<GameOptions> gameOptions, NicknameLookupService nicknameLookupService)
        {
            _logger = logger;
            _databaseService = databaseService;
            _gameOptions = gameOptions.Value;
            _nicknameLookupService = nicknameLookupService;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        public async Task<bool> OnHandle(MessageContext context, ItemUseChangeNickReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var logger = plr.AddContextToLogger(_logger);
            var item = plr.Inventory[message.ItemId];

            if (item == null || item.IsExpiredOrUsedUp() || !ChangeNickItems.TryGetValue(item.ItemNumber, out var days))
            {
                logger.Warning("Item={ItemId} is not a nickname change item", message.ItemId);
                session.Send(ChangeNickAck(ResultFailed, message.ItemId, message.Nickname));
                return true;
            }

            var nickname = message.Nickname?.Trim();
            var check = Nicknames.CheckFormat(nickname, _gameOptions.NickRestrictions);
            if (check != NicknameCheckResult.OK)
            {
                session.Send(ChangeNickAck(
                    check == NicknameCheckResult.TooShort ? ResultTooShort : ResultInvalidCharacters,
                    item.Id, message.Nickname));
                return true;
            }

            // One change at a time: a temporary nickname has to run out or be cancelled first
            if (await Nicknames.HasPendingChangeAsync(_databaseService, plr.Account.Id))
            {
                session.Send(ChangeNickAck(ResultAlreadyUsed, item.Id, nickname));
                return true;
            }

            if (await Nicknames.IsTakenAsync(_databaseService, nickname) ||
                !await Nicknames.ChangeAsync(_databaseService, plr.Account.Id, nickname, days))
            {
                session.Send(ChangeNickAck(ResultFailed, item.Id, nickname));
                return true;
            }

            logger.Information("Nickname changed from {OldNickname} to {Nickname} days={Days}",
                plr.Account.Nickname, nickname, days);
            plr.Account.Nickname = nickname;
            _nicknameLookupService.SetNickname(plr.Account.Id, nickname);
            plr.Inventory.UseOne(item);

            session.Send(ChangeNickAck(ResultOK, item.Id, nickname));
            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        public async Task<bool> OnHandle(MessageContext context, ItemUseChangeNickCancelReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var logger = plr.AddContextToLogger(_logger);

            // The request carries no item id, use the first cancel item in the inventory
            var item = plr.Inventory.FirstOrDefault(x => ChangeNickCancelItems.Contains(x.ItemNumber) && !x.IsExpiredOrUsedUp());
            if (item == null)
            {
                logger.Warning("No nickname change cancel item");
                session.Send(new ItemUseChangeNickCancelAckMessage { Unk = ResultAlreadyUsed });
                return true;
            }

            var oldNickname = plr.Account.Nickname;
            var nickname = await Nicknames.RestoreAsync(_databaseService, plr.Account.Id, false);
            if (nickname == null)
            {
                session.Send(new ItemUseChangeNickCancelAckMessage { Unk = ResultAlreadyUsed });
                return true;
            }

            logger.Information("Nickname change cancelled {OldNickname} -> {Nickname}", oldNickname, nickname);
            plr.Account.Nickname = nickname;
            _nicknameLookupService.SetNickname(plr.Account.Id, nickname);
            plr.Inventory.UseOne(item);

            session.Send(new ItemUseChangeNickCancelAckMessage { Unk = ResultOK });
            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        public async Task<bool> OnHandle(MessageContext context, ItemUseRecordResetReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;
            var logger = plr.AddContextToLogger(_logger);
            var item = plr.Inventory[message.ItemId];

            if (item == null || item.IsExpiredOrUsedUp() || item.ItemNumber != RecordResetItem)
            {
                logger.Warning("Item={ItemId} is not a record reset item", message.ItemId);
                session.Send(new ItemUseRecordResetAckMessage { Result = ResultFailed, Unk2 = message.ItemId });
                return true;
            }

            // The server keeps no Deathmatch kill/death record (the account info stats are sent
            // empty), so there is nothing to clear; the item is still used up.
            logger.Information("Record reset with item={ItemId}", item.Id);
            plr.Inventory.UseOne(item);

            session.Send(new ItemUseRecordResetAckMessage { Result = ResultOK, Unk2 = item.Id });
            return true;
        }

        private static ItemUseChangeNickAckMessage ChangeNickAck(uint result, ulong itemId, string nickname)
        {
            return new ItemUseChangeNickAckMessage { Result = result, Unk2 = itemId, Unk3 = nickname ?? "" };
        }
    }
}
