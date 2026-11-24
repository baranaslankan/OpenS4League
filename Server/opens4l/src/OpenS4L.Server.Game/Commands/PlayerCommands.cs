using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Common;
using OpenS4L.Server.Game.Services;

namespace OpenS4L.Server.Game.Commands
{
    /// <summary>
    /// Administrator commands that change a player's money, level and inventory.
    /// Every command takes an optional target (account id or nickname) and defaults to the caller.
    /// </summary>
    internal class PlayerCommands : ICommandHandler
    {
        private readonly PlayerManager _playerManager;
        private readonly GameDataService _gameDataService;

        public PlayerCommands(PlayerManager playerManager, GameDataService gameDataService)
        {
            _playerManager = playerManager;
            _gameDataService = gameDataService;
        }

        [Command(
            CommandUsage.Player | CommandUsage.Console,
            SecurityLevel.Administrator,
            "Usage: pen <amount> [accountId or nickname]"
        )]
        public async Task<bool> Pen(Player plr, string[] args)
        {
            if (!TryParseAmountAndTarget(plr, args, out var amount, out var target))
                return false;
            if (target == null)
                return false;

            target.PEN = amount;
            target.SendMoneyUpdate();
            this.Reply(plr, $"{target.Account.Nickname}: PEN={target.PEN}");
            return true;
        }

        [Command(
            CommandUsage.Player | CommandUsage.Console,
            SecurityLevel.Administrator,
            "Usage: ap <amount> [accountId or nickname]"
        )]
        public async Task<bool> Ap(Player plr, string[] args)
        {
            if (!TryParseAmountAndTarget(plr, args, out var amount, out var target))
                return false;
            if (target == null)
                return false;

            target.AP = amount;
            target.SendMoneyUpdate();
            this.Reply(plr, $"{target.Account.Nickname}: AP={target.AP}");
            return true;
        }

        [Command(
            CommandUsage.Player | CommandUsage.Console,
            SecurityLevel.Administrator,
            "Usage: exp <amount> [accountId or nickname]  (adds experience, levels up normally)"
        )]
        public async Task<bool> Exp(Player plr, string[] args)
        {
            if (!TryParseAmountAndTarget(plr, args, out var amount, out var target))
                return false;
            if (target == null)
                return false;

            target.GainExperience(amount);
            this.Reply(plr,
                $"{target.Account.Nickname}: level={target.Level} exp={target.TotalExperience} (relog to refresh the bar)");
            return true;
        }

        [Command(
            CommandUsage.Player | CommandUsage.Console,
            SecurityLevel.Administrator,
            "Usage: level <level> [accountId or nickname]"
        )]
        public async Task<bool> Level(Player plr, string[] args)
        {
            if (!TryParseAmountAndTarget(plr, args, out var level, out var target))
                return false;
            if (target == null)
                return false;

            var levelInfo = _gameDataService.Levels.GetValueOrDefault((int)level);
            if (levelInfo == null)
            {
                this.ReplyError(plr, $"Level {level} not found (0-{_gameDataService.Levels.Keys.Max()})");
                return true;
            }

            target.TotalExperience = levelInfo.TotalExperience;
            this.Reply(plr,
                $"{target.Account.Nickname}: level={target.Level} exp={target.TotalExperience} (relog to refresh the bar)");
            return true;
        }

        [Command(
            CommandUsage.Player | CommandUsage.Console,
            SecurityLevel.Administrator,
            "Usage: item <itemNumber> [accountId or nickname]  (gives a permanent copy of a shop item)"
        )]
        public async Task<bool> Item(Player plr, string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || !uint.TryParse(args[0], out var number))
                return false;

            var target = ResolveTarget(plr, args.Length == 2 ? args[1] : null);
            if (target == null)
                return false;

            var shopItem = _gameDataService.GetShopItem(new ItemNumber(number));
            if (shopItem == null)
            {
                this.ReplyError(plr, $"Item {number} is not in the shop");
                return true;
            }

            // Prefer an enabled permanent price, then any enabled price.
            var choice = shopItem.ItemInfos
                .Where(info => info.IsEnabled && info.PriceGroup != null)
                .SelectMany(info => info.PriceGroup.Prices.Where(p => p.IsEnabled).Select(price => (info, price)))
                .OrderBy(x => x.price.PeriodType == ItemPeriodType.None ? 0 : 1)
                .FirstOrDefault();

            if (choice.info == null)
            {
                this.ReplyError(plr, $"Item {number} has no enabled price");
                return true;
            }

            // Same effect handling as a shop purchase (ShopHandler).
            var effects = choice.info.EffectGroup?.Effects.Select(x => x.Effect).Where(x => x != 0).ToArray()
                          ?? System.Array.Empty<uint>();
            var item = target.Inventory.Create(choice.info, choice.price, 0, effects);
            this.Reply(plr, $"{target.Account.Nickname}: got item {number} (id {item.Id})");
            return true;
        }

        private bool TryParseAmountAndTarget(Player plr, string[] args, out uint amount, out Player target)
        {
            target = null;
            amount = 0;
            if (args.Length < 1 || args.Length > 2 || !uint.TryParse(args[0], out amount))
                return false;

            target = ResolveTarget(plr, args.Length == 2 ? args[1] : null);
            return true;
        }

        private Player ResolveTarget(Player plr, string arg)
        {
            Player target;
            if (arg == null)
                target = plr;
            else
                target = ulong.TryParse(arg, out var id) ? _playerManager[id] : _playerManager.GetByNickname(arg);

            if (target == null)
                this.ReplyError(plr, arg == null ? "Give a target from the console" : "Player not found");

            return target;
        }
    }
}
