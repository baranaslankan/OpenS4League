using System;
using System.Linq;
using System.Threading.Tasks;
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
    /// <summary>LicenseGainReq/LicenseExerciseReq with every license force-granted.</summary>
    public class GameLicenseTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        private async Task<(Player plr, FakeSocketChannel channel)> LoginAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId, Nickname = "lic" + accountId });
                await auth.SaveChangesAsync();
            }
            using (var db = _ctx.Get<GameContext>())
            {
                db.Players.Add(new PlayerEntity { Id = (int)accountId });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            return (session.Player, channel);
        }

        private static LicenseMyInfoAckMessage[] LicenseAcks(FakeSocketChannel channel)
        {
            return channel.Outbound
                .Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<LicenseMyInfoAckMessage>()
                .ToArray();
        }

        [Fact]
        public async Task LicenseGain_resendsAllLicenses()
        {
            var (plr, channel) = await LoginAsync(9901);
            var atLogin = LicenseAcks(channel);
            Assert.Single(atLogin);
            Assert.Contains((uint)ItemLicense.PlasmaSword, atLogin[0].Licenses);
            Assert.DoesNotContain((uint)ItemLicense.None, atLogin[0].Licenses);

            await _ctx.Get<LicenseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new LicenseGainReqMessage { License = ItemLicense.PlasmaSword });
            var acks = LicenseAcks(channel);
            Assert.Equal(2, acks.Length);
            Assert.Equal(atLogin[0].Licenses, acks[1].Licenses);

            // Exercise has no answer
            var sent = channel.Outbound.Count;
            await _ctx.Get<LicenseHandler>().OnHandle(new MessageContext { Session = plr.Session },
                new LicenseExerciseReqMessage { License = ItemLicense.PlasmaSword });
            Assert.Equal(sent, channel.Outbound.Count);
        }
    }
}
