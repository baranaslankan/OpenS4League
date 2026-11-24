using System.Threading.Tasks;
using OpenS4L.Network.Data.Chat;
using OpenS4L.Network.Message.Chat;
using OpenS4L.Server.Chat.Mappers;
using OpenS4L.Server.Chat.Rules;
using ProudNet;

namespace OpenS4L.Server.Chat.Handlers
{
    internal class UserDataHandler : IHandle<UserDataOneReqMessage>, IHandle<RoomInvitationPlayerReqMessage>
    {
        private readonly PlayerManager _playerManager;
        private readonly ChatMapper _mapper;

        public UserDataHandler(PlayerManager playerManager, ChatMapper mapper)
        {
            _playerManager = playerManager;
            _mapper = mapper;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        [Inline]
        public async Task<bool> OnHandle(MessageContext context, UserDataOneReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            if (plr.Account.Id == message.AccountId)
            {
                session.Send(new UserDataFourAckMessage(25, _mapper.ToUserDataDto(plr)));
                return true;
            }

            var target = _playerManager[message.AccountId];
            // The target may be offline
            if (target == null || plr.Channel != target.Channel)
                return true;

            session.Send(new UserDataFourAckMessage(25, _mapper.ToUserDataDto(target)));
            return true;
        }

        [Firewall(typeof(MustBeLoggedIn))]
        [Inline]
        public async Task<bool> OnHandle(MessageContext context, RoomInvitationPlayerReqMessage message)
        {
            var session = context.GetSession<Session>();
            var plr = session.Player;

            // Can only invite to the room we are in and only players in the same channel
            if (plr.Channel == null || plr.RoomId == 0 || plr.Account.Id == message.AccountId)
                return true;

            var target = _playerManager[message.AccountId];
            if (target == null || target.Channel != plr.Channel || target.RoomId != 0 ||
                target.Ignore.Contains(plr.Account.Id))
            {
                return true;
            }

            // Field meaning is a guess (taken from the message's constructor): inviter id, inviter
            // nickname and the inviter's location (channel + room) so the client can join the room
            target.Session.Send(new RoomInvitationPlayerAckMessage(
                plr.Account.Id, plr.Account.Nickname, _mapper.ToPlayerLocationDto(plr)
            ));
            return true;
        }
    }
}
