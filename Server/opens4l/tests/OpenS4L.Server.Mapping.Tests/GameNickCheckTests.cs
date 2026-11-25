using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Handlers;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// NickCheckReq (nickname dialog of the first character creation) answers NickCheckAck(IsTaken)
    /// with the same rules CharacterFirstCreateReq uses.
    /// </summary>
    public class GameNickCheckTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        public GameNickCheckTests()
        {
            _ctx.Get<IOptions<GameOptions>>().Value.NickRestrictions = new NickRestrictionOptions
            {
                MinLength = 4, MaxLength = 16, MaxRepeat = 3, WhitespaceAllowed = false, AsciiOnly = true
            };
        }

        private async Task<(Game.Session session, FakeSocketChannel channel)> LoginWithoutNicknameAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId });
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId + 1, Username = "other", Nickname = "TakenNick" });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 0 });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            Assert.NotNull(session.Player);
            return (session, channel);
        }

        private async Task<NickCheckAckMessage> CheckAsync(Game.Session session, FakeSocketChannel channel, string nickname)
        {
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session },
                new NickCheckReqMessage { Nickname = nickname });
            return channel.Outbound
                .Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<NickCheckAckMessage>()
                .LastOrDefault();
        }

        [Fact]
        public async Task NickCheck_reportsTakenAndInvalidNames()
        {
            var (session, channel) = await LoginWithoutNicknameAsync(9501);

            Assert.False((await CheckAsync(session, channel, "FreshNick")).IsTaken);
            Assert.True((await CheckAsync(session, channel, "TakenNick")).IsTaken);
            Assert.True((await CheckAsync(session, channel, "abc")).IsTaken); // too short
            Assert.True((await CheckAsync(session, channel, "has space")).IsTaken);
            Assert.True((await CheckAsync(session, channel, "aaaab")).IsTaken); // repeats
        }

        [Fact]
        public async Task NickCheck_withoutLogin_sendsNothing()
        {
            var (session, channel) = _ctx.CreateSession(9511);

            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session },
                new NickCheckReqMessage { Nickname = "FreshNick" });

            Assert.Empty(channel.Outbound);
        }
    }
}
