using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Common.Configuration;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network;
using OpenS4L.Network.Message.Game;
using OpenS4L.Network.Message.GameRule;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.GameRules;
using OpenS4L.Server.Game.Handlers;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Chaser (Slaughter, GameRule 8): rounds, chaser selection, scoring and the slaughter messages.
    /// </summary>
    public class GameChaserTests
    {
        private const byte ChaserMapId = 40;

        private readonly GameTestContext _ctx = new GameTestContext();
        private readonly Channel _channel;

        public GameChaserTests()
        {
            GameFixtures.SetMaps(_ctx.GameData,
                new MapInfo { Id = ChaserMapId, Name = "Chaser map", GameRule = GameRule.Chaser, PlayerLimit = 12, IsEnabled = true },
                new MapInfo { Id = 997, Name = "Random", GameRule = GameRule.Chaser, PlayerLimit = 12, IsEnabled = true, IsRandom = true },
                new MapInfo { Id = 2, Name = "Neden", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = true });
            _channel = new Channel(new ChannelEntity
            {
                Id = 1, Name = "Test", PlayerLimit = 32, Color = "FF0000", MinLevel = 0, MaxLevel = 99
            }, _ctx.Get<RoomManager>());
        }

        private ManualSchedulerService Scheduler => _ctx.Get<ManualSchedulerService>();
        private RoomHandler Handler => _ctx.Get<RoomHandler>();

        private static MessageContext Ctx(Player plr) => new MessageContext { Session = plr.Session };

        private async Task<(Player plr, FakeSocketChannel socket)> LoginAsync(uint accountId)
        {
            var cache = (Foundatio.Caching.InMemoryCacheClient)_ctx.Get<Foundatio.Caching.ICacheClient>();
            await cache.SetAsync<string>(Constants.Cache.SessionKey(accountId), "sid-" + accountId);
            using (var auth = _ctx.Get<AuthContext>())
            {
                auth.Accounts.Add(new AccountEntity { Id = (int)accountId, Username = "c" + accountId, Nickname = "chaser" + accountId });
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

        private Room CreateRoom(byte map = ChaserMapId)
        {
            var options = GameFixtures.CreateRoomCreationOptions();
            options.GameRule = GameRule.Chaser;
            options.Map = map;
            options.PlayerLimit = 12;
            options.SpectatorLimit = 0;
            options.Password = "";
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
                plr.IsConnectingToRoom = false;
                players.Add(plr);
                sockets.Add(socket);
            }

            return (room, players.ToArray(), sockets.ToArray());
        }

        /// <summary>Waiting -> Loading -> Starting -> Playing (first round started)</summary>
        private Chaser StartPlaying(Room room, Player[] players)
        {
            foreach (var plr in players)
                plr.IsReady = true;

            var sm = room.GameRule.StateMachine;
            Assert.True(sm.StartGame());
            Assert.True(sm.StartGame());
            Assert.True(Scheduler.RunNextScheduledFor(sm));
            Assert.Equal(GameState.Playing, sm.GameState);
            return (Chaser)room.GameRule;
        }

        private static List<object> Sent(FakeSocketChannel socket)
        {
            return socket.Outbound.Select(o => o.GetType().GetProperty("Message")?.GetValue(o)).ToList();
        }

        private static void ClearSent(FakeSocketChannel[] sockets)
        {
            foreach (var socket in sockets)
                socket.ClearOutbound();
        }

        private static ScoreContext Score(Player plr) => new ScoreContext(plr, null);

        private static void Kill(Chaser rule, Player killer, Player target)
        {
            rule.OnScoreKill(Score(killer), null, Score(target), AttackAttribute.SubmachineGun);
        }

        private static int SocketOf(Player[] players, Player plr) => Array.IndexOf(players, plr);

        [Fact]
        public void Resolver_createsChaser()
        {
            var options = GameFixtures.CreateRoomCreationOptions();
            options.GameRule = GameRule.Chaser;
            var resolver = _ctx.Get<GameRuleResolver>();
            Assert.True(resolver.HasGameRule(options));
            Assert.IsType<Chaser>(resolver.CreateGameRule(options));
        }

        [Fact]
        public async Task StartGame_needsTwoPlayers()
        {
            var (room, p, _) = await RoomWithPlayersAsync(1, 9601);
            p[0].IsReady = true;

            Assert.IsType<Chaser>(room.GameRule);
            Assert.False(room.GameRule.StateMachine.StartGame());
            Assert.Equal(GameState.Waiting, room.GameRule.StateMachine.GameState);
        }

        [Fact]
        public async Task FirstRound_broadcastsChaserAndSurvivors()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9611);
            var rule = StartPlaying(room, p);

            Assert.Equal(1, rule.CurrentRound);
            Assert.NotNull(rule.CurrentChaser);
            Assert.Equal(2, rule.Survivors.Count);
            Assert.DoesNotContain(rule.CurrentChaser, rule.Survivors);

            // 3 players: 60s + 7s first round intro
            Assert.Equal(TimeSpan.FromSeconds(60), rule.CurrentRoundLimit);
            Assert.Equal(TimeSpan.FromSeconds(67), rule.CurrentRoundTime);

            foreach (var socket in sockets)
            {
                var msg = Sent(socket).OfType<SlaughterChangeSlaughterAckMessage>().Single();
                Assert.Equal(rule.CurrentChaser.Account.Id, msg.AccountId);
                Assert.Equal(rule.Survivors.Select(x => x.Account.Id).OrderBy(x => x),
                    msg.Unk.OrderBy(x => x));
            }

            // No ResetRound for the first round
            Assert.DoesNotContain(Sent(sockets[0]), x =>
                x is GameEventMessageAckMessage m && m.Event == GameEventMessage.ResetRound);
        }

        [Fact]
        public void RoundLimit_addsTenSecondsPerPlayerAboveSix()
        {
            var options = new ChaserOptions();
            Assert.Equal(TimeSpan.FromSeconds(60), Chaser.GetRoundLimit(options, 2));
            Assert.Equal(TimeSpan.FromSeconds(60), Chaser.GetRoundLimit(options, 6));
            Assert.Equal(TimeSpan.FromSeconds(70), Chaser.GetRoundLimit(options, 7));
            Assert.Equal(TimeSpan.FromSeconds(120), Chaser.GetRoundLimit(options, 12));
        }

        [Fact]
        public async Task ChaserKillsEveryone_chaserWinsAndNextRoundHasAnotherChaser()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9621);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            var survivors = rule.Survivors.ToArray();
            ClearSent(sockets);

            Kill(rule, chaser, survivors[0]);
            Assert.True(rule.IsRoundRunning);
            Kill(rule, chaser, survivors[1]);
            Assert.False(rule.IsRoundRunning);

            Assert.Contains(Sent(sockets[0]), x => x is SlaughterSLRoundWinAckMessage);
            Assert.DoesNotContain(Sent(sockets[0]), x => x is SlaughterRoundWinAckMessage);
            Assert.Contains(Sent(sockets[0]), x =>
                x is GameEventMessageAckMessage m && m.Event == GameEventMessage.ChaserIn && m.AccountId == 5000);

            var chaserScore = (ChaserPlayerScore)chaser.Score;
            Assert.Equal(2u, chaserScore.ChaserKills);
            Assert.Equal(1u, chaserScore.ChaserRoundWins);
            Assert.Equal(4u, chaser.Score.GetTotalScore());
            Assert.Equal(0u, survivors[0].Score.GetTotalScore());

            // The round end timer of the first round fires after the chaser already won: no second win
            Assert.True(Scheduler.RunNextScheduledFor(rule));
            Assert.Equal(1, rule.CurrentRound);
            Assert.DoesNotContain(Sent(sockets[0]), x => x is SlaughterRoundWinAckMessage);

            // Next round
            ClearSent(sockets);
            Assert.True(Scheduler.RunNextScheduledFor(rule));
            Assert.Equal(2, rule.CurrentRound);
            Assert.True(rule.IsRoundRunning);
            Assert.NotSame(chaser, rule.CurrentChaser);
            Assert.Equal(TimeSpan.FromSeconds(64), rule.CurrentRoundTime);
            Assert.Contains(Sent(sockets[0]), x =>
                x is GameEventMessageAckMessage m && m.Event == GameEventMessage.ResetRound);
            Assert.Contains(Sent(sockets[0]), x =>
                x is SlaughterChangeSlaughterAckMessage m && m.AccountId == rule.CurrentChaser.Account.Id);

            // Round two times out (survivors win), then round three starts
            Assert.True(Scheduler.RunNextScheduledFor(rule));
            Assert.False(rule.IsRoundRunning);
            Assert.Contains(Sent(sockets[0]), x => x is SlaughterRoundWinAckMessage);
            Assert.True(Scheduler.RunNextScheduledFor(rule));
            Assert.Equal(3, rule.CurrentRound);
        }

        [Fact]
        public async Task RoundTimeout_survivorsWin()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9631);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            var survivors = rule.Survivors.ToArray();
            Kill(rule, chaser, survivors[0]);
            ClearSent(sockets);

            // The round end timer
            Assert.True(Scheduler.RunNextScheduledFor(rule));
            Assert.False(rule.IsRoundRunning);

            var win = Sent(sockets[0]).OfType<SlaughterRoundWinAckMessage>().Single();
            Assert.Equal(Chaser.RoundWinReasonTimeout, win.Unk);

            // Dead survivor: round win (+5), alive survivor: round win and survived (+5 +10)
            Assert.Equal(5u, survivors[0].Score.GetTotalScore());
            Assert.Equal(15u, survivors[1].Score.GetTotalScore());
            Assert.Equal(2u, chaser.Score.GetTotalScore());
        }

        [Fact]
        public async Task SurvivorKillsChaser_survivorsWin()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9641);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            var survivors = rule.Survivors.ToArray();
            ClearSent(sockets);

            Kill(rule, survivors[0], chaser);

            Assert.False(rule.IsRoundRunning);
            var win = Sent(sockets[1]).OfType<SlaughterRoundWinAckMessage>().Single();
            Assert.Equal(Chaser.RoundWinReasonChaserKilled, win.Unk);
            Assert.Equal(1u, survivors[0].Score.Kills);
            Assert.Equal(1u, ((ChaserPlayerScore)chaser.Score).ChaserDeaths);
            Assert.Equal(15u, survivors[0].Score.GetTotalScore());
            Assert.Equal(15u, survivors[1].Score.GetTotalScore());
        }

        [Fact]
        public async Task SurvivorSuicide_creditsTheChaser()
        {
            var (room, p, _) = await RoomWithPlayersAsync(3, 9651);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            var survivors = rule.Survivors.ToArray();

            rule.OnScoreSuicide(survivors[0]);

            Assert.Equal(1u, survivors[0].Score.Suicides);
            Assert.Equal(2u, chaser.Score.GetTotalScore());
            Assert.True(rule.IsRoundRunning);
            Assert.DoesNotContain(survivors[0], rule.AliveSurvivors);
        }

        [Fact]
        public async Task AttackPoint_fromChaser_isRelayedAndPicksTheBonusTarget()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9661);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            var survivors = rule.Survivors.ToArray();
            ClearSent(sockets);

            await Handler.OnHandle(Ctx(chaser), new SlaughterAttackPointReqMessage
            {
                AccountId = survivors[0].Account.Id, Unk1 = 12.5f, Unk2 = 3f
            });

            var chaserSocket = sockets[SocketOf(p, chaser)];
            Assert.DoesNotContain(Sent(chaserSocket), x => x is SlaughterAttackPointAckMessage);
            foreach (var survivor in survivors)
            {
                var ack = Sent(sockets[SocketOf(p, survivor)]).OfType<SlaughterAttackPointAckMessage>().Single();
                Assert.Equal(survivors[0].Account.Id, ack.AccountId);
                Assert.Equal(12.5f, ack.Unk1);
                Assert.Equal(3f, ack.Unk2);
            }

            var score = (ChaserPlayerScore)survivors[0].Score;
            Assert.Equal(12.5f, score.AttackPoints);
            Assert.Equal(3f, score.BonusAttackPoints);
            Assert.Equal(12.5f, ((ChaserPlayerScore)chaser.Score).ChaserAttackPoints);

            Assert.Same(survivors[0], rule.BonusTarget);
            Assert.Contains(Sent(chaserSocket), x =>
                x is SlaughterChangeBonusTargetAckMessage m && m.AccountId == survivors[0].Account.Id);

            // Killing the bonus target is worth more
            Kill(rule, chaser, survivors[0]);
            Assert.Equal(1u, ((ChaserPlayerScore)chaser.Score).BonusKills);
            Assert.Equal(4u, chaser.Score.GetTotalScore());
        }

        [Fact]
        public async Task AttackPoint_fromSurvivor_isIgnored()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9671);
            var rule = StartPlaying(room, p);
            var survivors = rule.Survivors.ToArray();
            ClearSent(sockets);

            await Handler.OnHandle(Ctx(survivors[0]), new SlaughterAttackPointReqMessage
            {
                AccountId = survivors[1].Account.Id, Unk1 = 50f, Unk2 = 0f
            });

            Assert.All(sockets, s => Assert.DoesNotContain(Sent(s), x => x is SlaughterAttackPointAckMessage));
            Assert.Equal(0f, ((ChaserPlayerScore)survivors[1].Score).AttackPoints);
            Assert.Null(rule.BonusTarget);
        }

        [Fact]
        public async Task HealPoint_isRelayedToTheOthers()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9681);
            var rule = StartPlaying(room, p);
            var healer = rule.Survivors[0];
            ClearSent(sockets);

            await Handler.OnHandle(Ctx(healer), new SlaughterHealPointReqMessage { Unk = 40f });

            Assert.DoesNotContain(Sent(sockets[SocketOf(p, healer)]), x => x is SlaughterHealPointAckMessage);
            foreach (var plr in p.Where(x => x != healer))
            {
                var ack = Sent(sockets[SocketOf(p, plr)]).OfType<SlaughterHealPointAckMessage>().Single();
                Assert.Equal(healer.Account.Id, ack.AccountId);
                Assert.Equal(40f, ack.Unk);
            }

            Assert.Equal(40f, ((ChaserPlayerScore)healer.Score).HealPoints);
        }

        [Fact]
        public async Task ChaserLeaves_startsNextRoundWithoutWinner()
        {
            var (room, p, sockets) = await RoomWithPlayersAsync(3, 9691);
            var rule = StartPlaying(room, p);
            var chaser = rule.CurrentChaser;
            ClearSent(sockets);

            room.Leave(chaser);

            Assert.Equal(GameState.Playing, rule.StateMachine.GameState);
            Assert.False(rule.IsRoundRunning);
            var socket = sockets[SocketOf(p, rule.Survivors[0])];
            Assert.DoesNotContain(Sent(socket), x => x is SlaughterRoundWinAckMessage || x is SlaughterSLRoundWinAckMessage);
            Assert.Contains(Sent(socket), x => x is GameEventMessageAckMessage m && m.Event == GameEventMessage.ChaserIn);
        }

        [Fact]
        public async Task Intrude_sendsTheCurrentChaser()
        {
            var (room, p, _) = await RoomWithPlayersAsync(2, 9701);
            var rule = StartPlaying(room, p);
            var (late, lateSocket) = await LoginAsync(9703);
            Assert.Equal(RoomJoinError.OK, room.Join(late));

            rule.OnPlayerIntrude(late);

            var msg = Sent(lateSocket).OfType<SlaughterChangeSlaughterAckMessage>().Single();
            Assert.Equal(rule.CurrentChaser.Account.Id, msg.AccountId);
            Assert.DoesNotContain(late, rule.Survivors);
        }

        [Fact]
        public async Task BriefingPlayer_appends101Bytes()
        {
            var (room, p, _) = await RoomWithPlayersAsync(2, 9711);
            var plr = p[0];

            byte[] Serialize(BriefingPlayer briefingPlayer)
            {
                using (var ms = new System.IO.MemoryStream())
                using (var w = new System.IO.BinaryWriter(ms))
                {
                    briefingPlayer.Serialize(w);
                    w.Flush();
                    return ms.ToArray();
                }
            }

            var baseLength = Serialize(new BriefingPlayer { AccountId = plr.Account.Id }).Length;
            var chaserRecord = Serialize(new BriefingPlayerChaser(plr));
            Assert.Equal(baseLength + 21 * 4 + 4 * 4 + 1, chaserRecord.Length);
        }

        [Fact]
        public async Task Briefing_writesTheSlaughterTrailer()
        {
            var (room, p, _) = await RoomWithPlayersAsync(3, 9721);
            var rule = StartPlaying(room, p);

            var data = room.GetBriefing().GetData();
            var briefing = (BriefingChaser)room.GetBriefing();
            Assert.Equal(rule.CurrentChaser.Account.Id, briefing.Chaser);
            Assert.Equal(67000, briefing.RoundTime);
            Assert.Equal(60000, briefing.RoundLimit);

            // chaser, bonus target, 4 ints, empty int list, 2 survivors, 2 alive survivors
            var trailer = 8 + 8 + 4 * 4 + 4 + (4 + 2 * 8) * 2;
            var withoutTrailer = new Briefing
            {
                Teams = briefing.Teams, Players = briefing.Players, Spectators = briefing.Spectators
            }.GetData();
            Assert.Equal(withoutTrailer.Length + trailer, data.Length);
        }

        [Fact]
        public async Task RandomChaserMap_resolvesToRealMap()
        {
            var room = CreateRoom(unchecked((byte)997));

            Assert.True(room.Options.IsRandomMap);
            Assert.Equal(ChaserMapId, room.Options.Map);
            Assert.IsType<Chaser>(room.GameRule);
        }
    }
}
