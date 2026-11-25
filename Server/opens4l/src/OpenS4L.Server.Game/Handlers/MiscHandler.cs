using System;
using System.Threading.Tasks;
using Logging;
using OpenS4L.Blub;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game.Rules;
using OpenS4L.Server.Game.Services;
using ProudNet;

namespace OpenS4L.Server.Game.Handlers
{
    internal class MiscHandler
        : IHandle<TimeSyncReqMessage>, IHandle<AdminShowWindowReqMessage>, IHandle<AdminActionReqMessage>
    {
        private readonly CommandService _commandService;
        private readonly ILogger _logger;

        public MiscHandler(CommandService commandService, ILogger<MiscHandler> logger)
        {
            _commandService = commandService;
            _logger = logger;
        }

        [Inline]
        public async Task<bool> OnHandle(MessageContext context, TimeSyncReqMessage message)
        {
            context.Session.Send(new TimeSyncAckMessage
            {
                ClientTime = message.Time,
                ServerTime = (uint)Environment.TickCount
            });

            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        [Inline]
        public async Task<bool> OnHandle(MessageContext context, AdminShowWindowReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            _logger.Debug("AdminShowWindowReq from {Nickname} level={SecurityLevel}",
                plr.Account.Nickname, plr.Account.SecurityLevel);
            session.Send(new AdminShowWindowAckMessage(plr.Account.SecurityLevel <= SecurityLevel.User));
            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        [Inline]
        public async Task<bool> OnHandle(MessageContext context, AdminActionReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            _logger.Debug("AdminActionReq from {Nickname} command={Command}", plr.Account.Nickname, message.Command);
            await _commandService.Execute(plr, message.Command.GetArgs());
            return true;
        }
    }
}
