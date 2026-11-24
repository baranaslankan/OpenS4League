using OpenS4L.Network.Data.Club;
using OpenS4L.Network.Data.Game;
using OpenS4L.Network.Data.GameRule;
using OpenS4L.Server.Game;
using OpenS4L.Server.Game.Mappers;
using Xunit;

namespace OpenS4L.Server.Mapping.Tests
{
    /// <summary>
    /// Differential tests for the Game server's object mappings, for the sources that can be
    /// constructed without a live DB/Redis/data-files stack. Each test registers the legacy
    /// ExpressMapper config (<see cref="Legacy.LegacyGameConfig"/>) from a clean slate and
    /// compares its output against the Mapperly <see cref="GameMapper"/> on identical objects.
    /// </summary>
    [Collection("Serial")]
    public class GameMappingDifferentialTests
    {
        private static void Register()
        {
            Legacy.LegacyGameConfig.Register();
        }

        private readonly GameMapper _mapper = GameFixtures.CreateMapper();

        [Fact]
        public void RoomCreationOptions_ToChangeRuleDto_Matches()
        {
            Register();
            var options = GameFixtures.CreateRoomCreationOptions();
            MappingAssert.Equal<ChangeRuleDto>(
                options,
                src => ExpressMapper.Mapper.Map<RoomCreationOptions, ChangeRuleDto>((RoomCreationOptions)src),
                src => _mapper.ToChangeRuleDto((RoomCreationOptions)src));
        }

        [Fact]
        public void RoomCreationOptions_ToChangeRule2Dto_Matches()
        {
            Register();
            var options = GameFixtures.CreateRoomCreationOptions();
            MappingAssert.Equal<ChangeRule2Dto>(
                options,
                src => ExpressMapper.Mapper.Map<RoomCreationOptions, ChangeRule2Dto>((RoomCreationOptions)src),
                src => _mapper.ToChangeRule2Dto((RoomCreationOptions)src));
        }

        /// <summary>
        /// Matches the legacy mapping except ExpireTime, which was latent bug #1 (README.md) and is now
        /// fixed deliberately: the legacy result is patched to the value LegacyGameConfig meant to send
        /// (the expiry as unix seconds), so every other member is still pinned to the legacy output.
        /// </summary>
        [Fact]
        public void PlayerItem_ToItemDto_MatchesLegacyWithFixedExpireTime()
        {
            Register();
            var gds = GameFixtures.CreateGameDataService();
            var item = GameFixtures.CreatePlayerItem(gds); // 30-day item bought at unix 1700000000

            var legacy = ExpressMapper.Mapper.Map<PlayerItem, ItemDto>(item);
            Assert.Equal(System.DateTimeOffset.MinValue, legacy.ExpireTime); // the bug that was fixed

            MappingAssert.Equal<ItemDto>(
                item,
                src =>
                {
                    var dto = ExpressMapper.Mapper.Map<PlayerItem, ItemDto>((PlayerItem)src);
                    dto.ExpireTime = ((PlayerItem)src).ExpireDate;
                    return dto;
                },
                src => _mapper.ToItemDto((PlayerItem)src));

            Assert.Equal(1700000000L + 30 * 86400, _mapper.ToItemDto(item).ExpireTime.ToUnixTimeSeconds());
        }
    }
}
