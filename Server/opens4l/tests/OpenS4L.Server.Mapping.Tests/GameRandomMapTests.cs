using System;
using System.Linq;
using System.Threading.Tasks;
using OpenS4L.Database;
using OpenS4L.Database.Auth;
using OpenS4L.Database.Game;
using OpenS4L.Network.Data.GameRule;
using OpenS4L.Network.Message.Game;
using OpenS4L.Resource.Xml;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Data;
using OpenS4L.Server.Game.Handlers;
using OpenS4L.Server.Game.Services;
using ProudNet;
using Xunit;
using Constants = OpenS4L.Common.Constants;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Random map placeholders (map.x7 ids 993-999, bginfo-random.ini). The client sends the map id
    /// as a byte, so 999 (random DM) arrives as 231, which is also Wonderland (TD).
    /// </summary>
    public class GameRandomMapTests
    {
        private readonly GameTestContext _ctx = new GameTestContext();

        private static readonly MapInfo Wonderland = new MapInfo { Id = 231, Name = "Wonderland", GameRule = GameRule.Touchdown, PlayerLimit = 12, IsEnabled = true };
        private static readonly MapInfo RandomDM = new MapInfo { Id = 999, Name = "Random", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = true, IsRandom = true };
        private static readonly MapInfo RandomTD = new MapInfo { Id = 998, Name = "Random", GameRule = GameRule.Touchdown, PlayerLimit = 12, IsEnabled = true, IsRandom = true };
        private static readonly MapInfo Neden = new MapInfo { Id = 2, Name = "Neden", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = true };
        private static readonly MapInfo Galleon = new MapInfo { Id = 8, Name = "Galleon (off)", GameRule = GameRule.Deathmatch, PlayerLimit = 12, IsEnabled = false };
        private static readonly MapInfo Practice = new MapInfo { Id = 3, Name = "Station", GameRule = GameRule.Practice, IsEnabled = true };

        public GameRandomMapTests()
        {
            GameFixtures.SetMaps(_ctx.GameData, Neden, Galleon, Practice, Wonderland, RandomDM, RandomTD);
        }

        private static MapListMapDto MapDto(ushort id, string bgInfo)
        {
            return new MapListMapDto
            {
                id = id,
                @base = new MapListMapBaseDto(),
                resourse = new MapListMapResourseDto { bginfo_path = bgInfo },
                @switch = new MapListMapSwitchDto { eu = "on" }
            };
        }

        [Fact]
        public void Placeholder_detection_usesBgInfoAndId()
        {
            Assert.True(GameDataService.IsRandomMapPlaceholder(MapDto(999, "Resources/MapInfo/bginfo-random.ini")));
            Assert.True(GameDataService.IsRandomMapPlaceholder(MapDto(993, "Resources/MapInfo/BGINFO-RANDOM.INI")));
            Assert.True(GameDataService.IsRandomMapPlaceholder(MapDto(300, "Resources/MapInfo/bginfo-x.ini")));
            Assert.False(GameDataService.IsRandomMapPlaceholder(MapDto(231, "Resources/MapInfo/bginfo-wonderland.ini")));
            Assert.False(GameDataService.IsRandomMapPlaceholder(MapDto(2, null)));
        }

        [Fact]
        public void GetMap_disambiguatesTruncatedIdByGameRule()
        {
            var gds = _ctx.GameData;
            Assert.Same(RandomDM, gds.GetMap(231, GameRule.Deathmatch));
            Assert.Same(Wonderland, gds.GetMap(231, GameRule.Touchdown));
            Assert.Same(RandomTD, gds.GetMap(230, GameRule.Touchdown));
            Assert.Same(Neden, gds.GetMap(2, GameRule.Deathmatch));
            // Unknown game rule for an existing id still returns a map, so the caller reports
            // InvalidGameRule like before
            Assert.Same(Neden, gds.GetMap(2, GameRule.Touchdown));
            Assert.Null(gds.GetMap(99, GameRule.Deathmatch));
        }

        [Fact]
        public void GetRandomMap_onlyPicksEnabledRealMaps()
        {
            var gds = _ctx.GameData;
            for (var i = 0; i < 50; ++i)
                Assert.Same(Neden, gds.GetRandomMap(GameRule.Deathmatch, 12));

            for (var i = 0; i < 50; ++i)
                Assert.Same(Wonderland, gds.GetRandomMap(GameRule.Touchdown, 12));

            Assert.Null(gds.GetRandomMap(GameRule.Captain));
        }

        [Fact]
        public void GetRandomMap_prefersMapsThatFitThePlayerLimit()
        {
            var small = new MapInfo { Id = 20, Name = "small", GameRule = GameRule.Deathmatch, PlayerLimit = 4, IsEnabled = true };
            var big = new MapInfo { Id = 21, Name = "big", GameRule = GameRule.Deathmatch, PlayerLimit = 16, IsEnabled = true };
            GameFixtures.SetMaps(_ctx.GameData, small, big, RandomDM);

            for (var i = 0; i < 50; ++i)
                Assert.Same(big, _ctx.GameData.GetRandomMap(GameRule.Deathmatch, 12));

            // Nothing fits -> any enabled map
            Assert.Contains(_ctx.GameData.GetRandomMap(GameRule.Deathmatch, 32), new[] { small, big });
        }

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
                db.Players.Add(new PlayerEntity { Id = (int)accountId, TotalExperience = 1000 });
                await db.SaveChangesAsync();
            }

            var (session, channel) = _ctx.CreateSession(accountId);
            await _ctx.Get<AuthenticationHandler>().OnHandle(new MessageContext { Session = session }, new LoginRequestReqMessage
            {
                AccountId = accountId, SessionId = "sid-" + accountId, Version = new Version(1, 0, 0, 0)
            });
            return (session.Player, channel);
        }

        private Channel BuildChannel()
        {
            return new Channel(new ChannelEntity
            {
                Id = 1, Name = "Test", PlayerLimit = 32, Color = "FF0000", MinLevel = 0, MaxLevel = 99
            }, _ctx.Get<RoomManager>());
        }

        private static RoomCreationOptions Options(GameRule gameRule, byte map)
        {
            var options = GameFixtures.CreateRoomCreationOptions();
            options.GameRule = gameRule;
            options.Map = map;
            options.PlayerLimit = 12;
            options.SpectatorLimit = 0;
            options.Password = "";
            return options;
        }

        [Fact]
        public async Task Create_withRandomDeathmatch_resolvesToRealMap()
        {
            var (plr, socket) = await LoginAsync(9101);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, err) = channel.RoomManager.Create(Options(GameRule.Deathmatch, 231));
            Assert.Equal(RoomCreateError.OK, err);
            Assert.Same(Neden, room.Map);
            Assert.Equal(2, room.Options.Map);
            Assert.True(room.Options.IsRandomMap);

            // The client gets the real map in the enter info (this is what it loads at game start)
            Assert.Equal(RoomJoinError.OK, room.Join(plr));
            var enterInfo = socket.Outbound
                .Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<RoomEnterRoomInfoAck2Message>()
                .Last();
            Assert.Equal(2, enterInfo.Room.Map);
        }

        [Fact]
        public async Task Create_withWonderlandTouchdown_isNotTreatedAsRandom()
        {
            var (plr, _) = await LoginAsync(9102);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, err) = channel.RoomManager.Create(Options(GameRule.Touchdown, 231));
            Assert.Equal(RoomCreateError.OK, err);
            Assert.Same(Wonderland, room.Map);
            Assert.False(room.Options.IsRandomMap);
        }

        [Fact]
        public async Task Create_manualMaps_stillWork_evenWhenDisabled()
        {
            var (plr, _) = await LoginAsync(9103);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, err) = channel.RoomManager.Create(Options(GameRule.Deathmatch, 2));
            Assert.Equal(RoomCreateError.OK, err);
            Assert.Same(Neden, room.Map);

            // IsEnabled is only used for the random pick; a map the client lets you choose keeps working
            var (room2, err2) = channel.RoomManager.Create(Options(GameRule.Deathmatch, 8));
            Assert.Equal(RoomCreateError.OK, err2);
            Assert.Same(Galleon, room2.Map);
        }

        [Fact]
        public async Task Create_wrongGameRuleForMap_isInvalidGameRule()
        {
            var (plr, _) = await LoginAsync(9104);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, err) = channel.RoomManager.Create(Options(GameRule.Touchdown, 2));
            Assert.Null(room);
            Assert.Equal(RoomCreateError.InvalidGameRule, err);
        }

        [Fact]
        public async Task Create_randomWithoutAnyEnabledMap_isInvalidMap()
        {
            GameFixtures.SetMaps(_ctx.GameData, Galleon, RandomDM);
            var (plr, _) = await LoginAsync(9105);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, err) = channel.RoomManager.Create(Options(GameRule.Deathmatch, 231));
            Assert.Null(room);
            Assert.Equal(RoomCreateError.InvalidMap, err);
        }

        [Fact]
        public async Task ChangeRules_toRandomTouchdown_resolvesAndBroadcastsRealMap()
        {
            var (plr, socket) = await LoginAsync(9106);
            var channel = BuildChannel();
            channel.Join(plr);

            var (room, _) = channel.RoomManager.Create(Options(GameRule.Deathmatch, 2));
            room.Join(plr);

            var error = room.ChangeRules(new ChangeRule2Dto
            {
                Name = "r", GameRule = GameRule.Touchdown, Map = 230, PlayerLimit = 12, SpectatorLimit = 0,
                TimeLimit = TimeSpan.FromMinutes(10), ScoreLimit = 10, Password = "", ItemLimit = 0
            });

            Assert.Equal(RoomChangeRulesError.OK, error);
            Assert.Same(Wonderland, room.Map);
            Assert.Equal(231, room.Options.Map);
            Assert.True(room.Options.IsRandomMap);

            var notify = socket.Outbound
                .Select(o => o.GetType().GetProperty("Message")?.GetValue(o))
                .OfType<OpenS4L.Network.Message.GameRule.RoomChangeRuleNotifyAck2Message>()
                .Last();
            Assert.Equal(231, notify.Settings.Map);
        }
    }
}
