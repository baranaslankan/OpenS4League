using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Message.Game;
using OpenS4L.Network.Message.GameRule;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Handlers;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// The lobby EXP bar is refreshed after experience changes (ExpRefreshInfoAck, or
    /// PlayerAccountInfoAck when GameOptions.ExpRefreshUsesAccountInfo is set).
    /// </summary>
    public class GameExperienceRefreshTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        private async Task<(Player plr, FakeSocketChannel channel)> LoginAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "g" + accountId, Nickname = "nick" + accountId });
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
            return (session.Player, channel);
        }

        private static List<object> Sent(FakeSocketChannel channel)
        {
            return channel.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o)).ToList();
        }

        [Fact]
        public async Task SendExperienceUpdate_sendsExpRefreshInfoWithTotalExperience()
        {
            var (plr, channel) = await LoginAsync(9201);
            plr.GainExperience(150);

            plr.SendExperienceUpdate();

            var ack = Sent(channel).OfType<ExpRefreshInfoAckMessage>().Single();
            Assert.Equal(150, ack.Unk);
        }

        [Fact]
        public async Task SendExperienceUpdate_fallback_resendsAccountInfo()
        {
            var (plr, channel) = await LoginAsync(9202);
            _ctx.Get<IOptions<GameOptions>>().Value.ExpRefreshUsesAccountInfo = true;
            plr.GainExperience(150);
            var before = Sent(channel).OfType<PlayerAccountInfoAckMessage>().Count();

            plr.SendExperienceUpdate();

            var sent = Sent(channel);
            Assert.Empty(sent.OfType<ExpRefreshInfoAckMessage>());
            var info = sent.OfType<PlayerAccountInfoAckMessage>().ToArray();
            Assert.Equal(before + 1, info.Length);
            Assert.Equal(150u, info.Last().Info.TotalExperience);
            Assert.Equal(1, info.Last().Info.Level);
            Assert.Equal("nick9202", info.Last().Info.Nickname);
        }

        [Fact]
        public async Task MatchResult_refreshesExpBar_afterResultBriefing()
        {
            var deathmatch = _ctx.Get<IOptions<DeathmatchOptions>>().Value;
            deathmatch.ExperienceRates = new ExperienceRateOptions { FirstPlaceBonus = 50 };
            GameFixtures.SetMaps(_ctx.GameData,
                new MapInfo { Id = 2, Name = "Neden", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = true });

            var (plr, channel) = await LoginAsync(9203);
            var gameChannel = new Channel(new ChannelEntity
            {
                Id = 1, Name = "T", PlayerLimit = 32, Color = "FF0000", MinLevel = 0, MaxLevel = 99
            }, _ctx.Get<RoomManager>());
            gameChannel.Join(plr);
            var options = GameFixtures.CreateRoomCreationOptions();
            options.GameRule = GameRule.Deathmatch;
            options.Map = 2;
            options.PlayerLimit = 12;
            options.SpectatorLimit = 0;
            var (room, err) = gameChannel.RoomManager.Create(options);
            Assert.Equal(RoomCreateError.OK, err);
            Assert.Equal(RoomJoinError.OK, room.Join(plr));

            // Pretend the player played the match
            plr.State = PlayerState.Waiting;
            plr.StartPlayTime = DateTimeOffset.Now;

            room.GameRule.OnResult();

            Assert.Equal(50u, plr.TotalExperience);
            var sent = Sent(channel);
            var briefingIndex = sent.FindLastIndex(x => x is GameBriefingInfoAckMessage);
            var refreshIndex = sent.FindLastIndex(x => x is ExpRefreshInfoAckMessage);
            Assert.True(briefingIndex >= 0);
            Assert.True(refreshIndex > briefingIndex);
            Assert.Equal(50, ((ExpRefreshInfoAckMessage)sent[refreshIndex]).Unk);
        }
    }
}
