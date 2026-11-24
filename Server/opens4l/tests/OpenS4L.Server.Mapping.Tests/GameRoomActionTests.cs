using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network;
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
    /// Room actions: master hand over, master team moves, vote kick, quick join/start, master kick.
    /// </summary>
    public class GameRoomActionTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();
        private readonly Channel _channel;

        public GameRoomActionTests()
        {
            GameFixtures.SetMaps(_ctx.GameData,
                new MapInfo { Id = 2, Name = "Neden", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = true },
                new MapInfo { Id = 62, Name = "Neden TD", GameRule = GameRule.Touchdown, PlayerLimit = 12, IsEnabled = true },
                new MapInfo { Id = 3, Name = "Station", GameRule = GameRule.Practice, IsEnabled = true });
            _channel = new Channel(new ChannelEntity
            {
                Id = 1, Name = "Test", PlayerLimit = 32, Color = "FF0000", MinLevel = 0, MaxLevel = 99
            }, _ctx.Get<RoomManager>());
        }

        private RoomHandler Handler => _ctx.Get<RoomHandler>();

        private static MessageContext Ctx(Player plr) => new MessageContext { Session = plr.Session };

        private async Task<(Player plr, FakeSocketChannel socket)> LoginAsync(uint accountId)
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
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 1000 });
                await db.SaveChangesAsync();
            }

            var (session, socket) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            Assert.Equal(ChannelJoinError.OK, _channel.Join(session.Player));
            return (session.Player, socket);
        }

        private Room CreateRoom(GameRule gameRule = GameRule.Deathmatch, string password = "")
        {
            var options = GameFixtures.CreateRoomCreationOptions();
            options.GameRule = gameRule;
            options.Map = gameRule == GameRule.Touchdown ? (byte)62 : (byte)2;
            options.PlayerLimit = 12;
            options.SpectatorLimit = 0;
            options.Password = password;
            var (room, err) = _channel.RoomManager.Create(options);
            Assert.Equal(RoomCreateError.OK, err);
            return room;
        }

        private async Task<(Room room, Player[] players, FakeSocketChannel[] sockets)> RoomWithPlayersAsync(
            int count, uint firstId)
        {
            var room = CreateRoom();
            var players = new List<Player>();
            var sockets = new List<FakeSocketChannel>();
            for (var i = 0; i < count; ++i)
            {
                var (plr, socket) = await LoginAsync(firstId + (uint)i);
                Assert.Equal(RoomJoinError.OK, room.Join(plr));
                players.Add(plr);
                sockets.Add(socket);
            }

            return (room, players.ToArray(), sockets.ToArray());
        }

        private static List<object> Sent(FakeSocketChannel socket)
        {
            return socket.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o)).ToList();
        }

        #region Master change
        [Fact]
        public async Task ChoiceMasterChange_givesMasterAndHost()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(2, 9301);
            Assert.Same(p[0], room.Master);

            await Handler.OnHandle(Ctx(p[0]), new RoomChoiceMasterChangeReqMessage { AccountId = p[1].Account.Id });

            Assert.Same(p[1], room.Master);
            Assert.Same(p[1], room.Host);
            Assert.Contains(Sent(sockets[0]), x => x is RoomChangeMasterAckMessage m && m.AccountId == p[1].Account.Id);
        }

        [Fact]
        public async Task ChoiceMasterChange_unknownPlayer_isIgnored()
        {
            var (room, p, _) = await RoomWithPlayersAsync(1, 9311);

            await Handler.OnHandle(Ctx(p[0]), new RoomChoiceMasterChangeReqMessage { AccountId = 424242 });

            Assert.Same(p[0], room.Master);
        }
        #endregion

        #region Team change by master
        [Fact]
        public async Task ChoiceTeamChange_movesPlayer()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(2, 9321);
            var from = p[1].Team.Id;
            var to = from == TeamId.Alpha ? TeamId.Beta : TeamId.Alpha;

            await Handler.OnHandle(Ctx(p[0]), new RoomChoiceTeamChangeReqMessage
            {
                PlayerToMove = p[1].Account.Id, PlayerToReplace = 0, FromTeam = from, ToTeam = to
            });

            Assert.Equal(to, p[1].Team.Id);
            var ack = Sent(sockets[1]).OfType<RoomChoiceTeamChangeAckMessage>().Single();
            Assert.Equal(p[1].Account.Id, ack.PlayerToMove);
            Assert.Equal(to, ack.ToTeam);
            Assert.Equal(from, ack.FromTeam);
        }

        [Fact]
        public async Task ChoiceTeamChange_swapsPlayers()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(2, 9331);
            var teamA = p[0].Team.Id;
            var teamB = p[1].Team.Id;
            Assert.NotEqual(teamA, teamB);

            await Handler.OnHandle(Ctx(p[0]), new RoomChoiceTeamChangeReqMessage
            {
                PlayerToMove = p[0].Account.Id, PlayerToReplace = p[1].Account.Id, FromTeam = teamA, ToTeam = teamB
            });

            Assert.Equal(teamB, p[0].Team.Id);
            Assert.Equal(teamA, p[1].Team.Id);
            var sent = Sent(sockets[1]);
            Assert.Contains(sent, x => x is RoomChangeTeamAckMessage m && m.AccountId == p[0].Account.Id && m.Team == teamB);
            Assert.Contains(sent, x => x is RoomChangeTeamAckMessage m && m.AccountId == p[1].Account.Id && m.Team == teamA);
            Assert.Single(sent.OfType<RoomChoiceTeamChangeAckMessage>());
        }

        [Fact]
        public async Task ChoiceTeamChange_readyPlayer_fails()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(2, 9341);
            var from = p[1].Team.Id;
            var to = from == TeamId.Alpha ? TeamId.Beta : TeamId.Alpha;
            p[1].IsReady = true;

            await Handler.OnHandle(Ctx(p[0]), new RoomChoiceTeamChangeReqMessage
            {
                PlayerToMove = p[1].Account.Id, FromTeam = from, ToTeam = to
            });

            Assert.Equal(from, p[1].Team.Id);
            var fail = Sent(sockets[0]).OfType<RoomChoiceTeamChangeFailAckMessage>().Single();
            Assert.Equal((byte)ChangeTeamResult.AlreadyReady, fail.Result);
        }
        #endregion

        #region Vote kick
        [Fact]
        public async Task VoteKick_majority_kicksTarget()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(4, 9351);
            var target = p[3];

            await Handler.OnHandle(Ctx(p[0]), new GameKickOutRequestReqMessage
            {
                Sender = p[0].Account.Id, Target = target.Account.Id, Reason = VoteKickReason.AFK
            });

            // 3 voters -> 2 needed; the sender voted yes already
            Assert.NotNull(room.CurrentVoteKick);
            var state = Sent(sockets[1]).OfType<GameKickOutStateAckMessage>().Last();
            Assert.Equal(1u, state.YesCount);
            Assert.Equal(2u, state.NeededCount);
            Assert.Equal(target.Account.Id, state.Target);
            Assert.Equal(VoteKickReason.AFK, state.Reason);

            // The target can't vote
            await Handler.OnHandle(Ctx(target), new GameKickOutVoteResultReqMessage { IsYes = false });
            Assert.Equal(0u, room.CurrentVoteKick.NoCount);

            await Handler.OnHandle(Ctx(p[1]), new GameKickOutVoteResultReqMessage { IsYes = true });

            Assert.Null(room.CurrentVoteKick);
            Assert.Null(target.Room);
            Assert.Contains(Sent(sockets[0]), x => x is RoomLeavePlayerAckMessage m &&
                                                    m.AccountId == target.Account.Id &&
                                                    m.Reason == RoomLeaveReason.VoteKick);
            // Kicked players can't come back
            Assert.Equal(RoomJoinError.KickedPreviously, room.Join(target));
        }

        [Fact]
        public async Task VoteKick_majorityNo_endsWithoutKick()
        {
            var (room, p, _) = await RoomWithPlayersAsync(4, 9361);
            var target = p[3];

            await Handler.OnHandle(Ctx(p[0]), new GameKickOutRequestReqMessage
            {
                Sender = p[0].Account.Id, Target = target.Account.Id, Reason = VoteKickReason.Etc
            });
            await Handler.OnHandle(Ctx(p[1]), new GameKickOutVoteResultReqMessage { IsYes = false });
            await Handler.OnHandle(Ctx(p[2]), new GameKickOutVoteResultReqMessage { IsYes = false });

            Assert.Null(room.CurrentVoteKick);
            Assert.Same(room, target.Room);
        }

        [Fact]
        public async Task VoteKick_timeout_endsWithoutKick()
        {
            var (room, p, _) = await RoomWithPlayersAsync(4, 9371);
            var target = p[3];

            await Handler.OnHandle(Ctx(p[0]), new GameKickOutRequestReqMessage
            {
                Sender = p[0].Account.Id, Target = target.Account.Id, Reason = VoteKickReason.Hacking
            });
            Assert.NotNull(room.CurrentVoteKick);

            _ctx.Get<ManualSchedulerService>().RunAllScheduled();

            Assert.Null(room.CurrentVoteKick);
            Assert.Same(room, target.Room);
        }

        [Fact]
        public async Task VoteKick_twoPlayers_notEnoughVoters()
        {
            var (room, p, _) = await RoomWithPlayersAsync(2, 9381);

            Assert.Equal(VoteKickError.NotEnoughPlayers, room.StartVoteKick(p[0], p[1], VoteKickReason.Etc));
            Assert.Null(room.CurrentVoteKick);
            Assert.Same(room, p[1].Room);
        }

        [Fact]
        public async Task VoteKick_onlyOneAtATime_andCancelledWhenTargetLeaves()
        {
            var (room, p, _) = await RoomWithPlayersAsync(4, 9391);

            Assert.Equal(VoteKickError.OK, room.StartVoteKick(p[0], p[3], VoteKickReason.Etc));
            Assert.Equal(VoteKickError.AlreadyVoting, room.StartVoteKick(p[1], p[2], VoteKickReason.Etc));
            Assert.Equal(VoteKickError.InvalidTarget, room.StartVoteKick(p[1], p[1], VoteKickReason.Etc));

            room.Leave(p[3]);
            Assert.Null(room.CurrentVoteKick);
        }
        #endregion

        #region Quick join / quick start
        [Fact]
        public async Task QuickJoin_joinsOpenRoom_skipsPasswordRooms()
        {
            var locked = CreateRoom(password: "pw");
            var (owner, _) = await LoginAsync(9401);
            locked.Join(owner);
            var open = CreateRoom();
            var (owner2, _) = await LoginAsync(9402);
            open.Join(owner2);

            var (plr, _) = await LoginAsync(9403);
            await Handler.OnHandle(Ctx(plr), new RoomQuickJoinReqMessage());

            Assert.Same(open, plr.Room);
        }

        [Fact]
        public async Task QuickJoin_noRoom_sendsQuickJoinFailed()
        {
            var (plr, socket) = await LoginAsync(9411);
            await Handler.OnHandle(Ctx(plr), new RoomQuickJoinReqMessage());

            Assert.Null(plr.Room);
            Assert.Contains(Sent(socket), x => x is ServerResultAckMessage m && m.Result == ServerResult.QuickJoinFailed);
        }

        [Fact]
        public async Task QuickStart_filtersByGameRule()
        {
            var dm = CreateRoom(GameRule.Deathmatch);
            var (a, _) = await LoginAsync(9421);
            dm.Join(a);
            var td = CreateRoom(GameRule.Touchdown);
            var (b, _) = await LoginAsync(9422);
            td.Join(b);

            var (plr, _) = await LoginAsync(9423);
            await Handler.OnHandle(Ctx(plr), new RoomQuickStartReqMessage { GameRule = (byte)GameRule.Touchdown });

            Assert.Same(td, plr.Room);
        }
        #endregion

        #region Master kick (RoomLeaveRequestReq)
        [Fact]
        public async Task Kick_byNonMaster_isIgnored()
        {
            var (room, p, _) = await RoomWithPlayersAsync(3, 9431);

            await Handler.OnHandle(Ctx(p[1]), new RoomLeaveRequestReqMessage
            {
                AccountId = p[2].Account.Id, Reason = RoomLeaveReason.Kicked
            });

            Assert.Same(room, p[2].Room);
        }

        [Fact]
        public async Task Kick_byMasterInLobby_kicks()
        {
            var (room, p, _) = await RoomWithPlayersAsync(2, 9441);

            await Handler.OnHandle(Ctx(p[0]), new RoomLeaveRequestReqMessage
            {
                AccountId = p[1].Account.Id, Reason = RoomLeaveReason.Kicked
            });

            Assert.Null(p[1].Room);
        }
        #endregion
    }
}
