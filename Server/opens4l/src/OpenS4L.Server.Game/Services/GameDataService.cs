using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Logging;
using Microsoft.Extensions.Hosting;
using OpenS4L.Database;
using OpenS4L.Server.Game.Data;

namespace OpenS4L.Server.Game.Services
{
    public partial class GameDataService : IHostedService
    {
        private readonly ILogger _logger;
        private readonly DatabaseService _databaseService;
        private readonly string _resourcePath;

        public ImmutableDictionary<int, LevelInfo> Levels { get; private set; }
        public ImmutableArray<MapInfo> Maps { get; private set; }
        public ImmutableDictionary<uint, ItemEffect> Effects { get; private set; }
        public ImmutableDictionary<ItemNumber, ItemInfo> Items { get; private set; }
        public ImmutableArray<DefaultItem> DefaultItems { get; private set; }
        public ImmutableDictionary<string, GameTempo> GameTempos { get; private set; }
        public ImmutableDictionary<int, EquipLimitInfo> EquipLimits { get; private set; }
        public ImmutableDictionary<ItemNumber, CapsuleInfo> Capsules { get; private set; } =
            ImmutableDictionary<ItemNumber, CapsuleInfo>.Empty;

        // Shop data
        public ImmutableDictionary<ItemNumber, ShopItem> ShopItems { get; private set; }
        public ImmutableDictionary<int, ShopEffectGroup> ShopEffects { get; private set; }
        public ImmutableDictionary<int, ShopPriceGroup> ShopPrices { get; private set; }
        public ImmutableDictionary<int, LevelReward> LevelRewards { get; private set; }
        public string ShopVersion { get; private set; }

        public GameDataService(ILogger<GameDataService> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
            _resourcePath = Path.Combine(Program.BaseDirectory, "data");
        }

        public DefaultItem GetDefaultItem(CharacterGender gender, CostumeSlot slot, byte variation)
        {
            return DefaultItems.FirstOrDefault(item =>
                item.Gender == gender && item.Variation == variation &&
                item.ItemNumber.SubCategory == (byte)slot);
        }

        public ShopItem GetShopItem(ItemNumber itemNumber)
        {
            ShopItems.TryGetValue(itemNumber, out var item);
            return item;
        }

        public ShopItemInfo GetShopItemInfo(ItemNumber itemNumber, ItemPriceType priceType)
        {
            var item = GetShopItem(itemNumber);
            return item?.GetItemInfo(priceType);
        }

        public LevelInfo GetLevelFromExperience(uint experience)
        {
            return Levels.Values
                       .OrderBy(levelInfo => levelInfo.Level)
                       .FirstOrDefault(levelInfo => experience >= levelInfo.TotalExperience &&
                                                    experience < levelInfo.TotalExperience + levelInfo.ExperienceToNextLevel)
                   ?? Levels.Last().Value;
        }

        /// <summary>
        /// Finds a map by the id the client sends (a byte) and the game rule.
        /// The game rule is needed because the random placeholders collide with real maps once
        /// truncated (999 -> 231). If no map matches the game rule, any map with the id is returned
        /// so callers can still tell "invalid game rule" apart from "invalid map".
        /// </summary>
        public MapInfo GetMap(byte mapId, GameRule gameRule)
        {
            var maps = Maps.Where(x => x.WireId == mapId).ToArray();
            return maps.Where(x => x.GameRule == gameRule).OrderBy(x => x.IsRandom).FirstOrDefault()
                   ?? maps.OrderBy(x => x.IsRandom).FirstOrDefault();
        }

        /// <summary>
        /// Picks a random enabled real map for the given game rule.
        /// Maps that can hold <paramref name="playerLimit"/> players are preferred.
        /// </summary>
        /// <returns>null when there is no enabled map for the game rule</returns>
        public MapInfo GetRandomMap(GameRule gameRule, int playerLimit = 0)
        {
            var maps = Maps
                .Where(x => !x.IsRandom && x.IsEnabled && x.GameRule == gameRule && x.Id <= byte.MaxValue)
                .ToArray();
            var fitting = maps.Where(x => x.PlayerLimit >= playerLimit).ToArray();
            if (fitting.Length > 0)
                maps = fitting;

            return maps.Length == 0 ? null : maps[Random.Shared.Next(maps.Length)];
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            LoadLevels();
            LoadMaps();
            LoadEffects();
            LoadItems();
            LoadDefaultItems();
            LoadGameTempos();
            LoadEquipLimits();
            LoadCapsules();
            LoadRandomShop();
            await LoadShop();
            await LoadLevelRewards();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private T Deserialize<T>(string fileName)
        {
            var serializer = new XmlSerializer(typeof(T));

            var path = Path.Combine(_resourcePath, fileName.Replace('/', Path.DirectorySeparatorChar));
            using (var r = new StreamReader(path))
                return (T)serializer.Deserialize(r);
        }

        private byte[] GetBytes(string fileName)
        {
            var path = Path.Combine(_resourcePath, fileName.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
