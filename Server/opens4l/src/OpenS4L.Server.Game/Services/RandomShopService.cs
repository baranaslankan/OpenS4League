using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Logging;
using OpenS4L.Network;
using OpenS4L.Network.Data.Game;
using OpenS4L.Server.Game.Data;
using ProudNet;

namespace OpenS4L.Server.Game.Services
{
    /// <summary>
    /// Fumbi random shop (lobby "random shop" machines).
    ///
    /// Flow (taken from the 0.8.32 client by static analysis, see the remarks on each part):
    /// <list type="number">
    /// <item>The client sends RandomShopUpdateCheckReq with the version of its cached shop data.
    /// The server answers RandomShopUpdateCheckAck(version); when the versions differ it also sends
    /// RandomShopUpdateInfoAck carrying the LZO-compressed package/lineup data (<see cref="BuildClientData"/>).</item>
    /// <item>RandomShopRollingStartReq carries a package id. The server charges PEN, grants the item
    /// (the inventory add goes out first) and answers RandomShopRollingStartAck(result, items) where
    /// items[0] is the prize and the optional items[1] the bonus item.</item>
    /// </list>
    /// Only PEN prices are used; there is no AP or cash path.
    /// </summary>
    public class RandomShopService
    {
        // RandomShopRollingStartAck.Unk1, read by the client's GameServerClient handler:
        // -3 LOBBY_RANDOMSHOP_NOT_EXIST_PACKAGE, -2 LOBBY_RANDOMSHOP_NOT_ENOUGH_MONEY,
        // 0 LOBBY_RANDOMSHOP_MAKE_ITEM_FAIL, 1 success (anything else is ignored silently)
        public const int ResultSuccess = 1;
        public const int ResultMakeItemFail = 0;
        public const int ResultNotEnoughMoney = -2;
        public const int ResultNotExistPackage = -3;

        // RandomShopUpdateInfoAck.Unk1: bit set of the data sections in the message. The client only
        // applies the data when all five (package, effect, color, lineup, period) arrive together.
        public const byte AllSections = 0x1F;

        public const string PeriodGroup = "default";

        private readonly GameDataService _gameDataService;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<ulong, int> _gauges = new ConcurrentDictionary<ulong, int>();
        private readonly object _cacheSync = new object();
        private ImmutableDictionary<ItemNumber, ShopItem> _cachedShopItems;
        private RandomShopInfo _cachedInfo;
        private IReadOnlyList<RandomShopPackage> _packages;
        private ClientData _clientData;

        public RandomShopService(GameDataService gameDataService, ILogger<RandomShopService> logger)
        {
            _gameDataService = gameDataService;
            _logger = logger;
        }

        public RandomShopInfo Info => _gameDataService.RandomShop;

        public IReadOnlyList<RandomShopPackage> Packages
        {
            get
            {
                EnsureCache();
                return _packages;
            }
        }

        /// <summary>
        /// Data for RandomShopUpdateInfoAck.
        /// </summary>
        public ClientData GetClientData()
        {
            EnsureCache();
            return _clientData;
        }

        public RandomShopPackage GetPackage(int id)
        {
            return Packages.FirstOrDefault(x => x.Id == id);
        }

        /// <summary>
        /// Luck gauge of the account. Kept in memory only (lost on server restart), see <see cref="Roll"/>.
        /// </summary>
        public int GetGauge(ulong accountId)
        {
            return _gauges.TryGetValue(accountId, out var value) ? value : 0;
        }

        public void SetGauge(ulong accountId, int value)
        {
            _gauges[accountId] = Math.Max(0, value);
        }

        /// <summary>
        /// One roll of <paramref name="packageId"/>: charges the package price in PEN, grants the prize
        /// and, when the luck gauge is full, a bonus item. Nothing changes unless the result is success.
        ///
        /// Luck gauge (assumption - the client only shows it): every roll adds costume_acc/weapon_acc
        /// (capped at gauge_max); when it reaches costume_req/weapon_req the roll also grants a bonus
        /// item from the same package with a permanent period and the gauge drops by that amount.
        /// </summary>
        public RollResult Roll(Player plr, int packageId, Random random)
        {
            var package = GetPackage(packageId);
            if (package == null || package.Items.Count == 0)
                return new RollResult(ResultNotExistPackage);

            var character = plr.CharacterManager.CurrentCharacter;
            if (character == null || !package.CanUse(character.Gender))
                return new RollResult(ResultMakeItemFail);

            var price = (uint)Math.Max(0, package.Price);
            if (plr.PEN < price)
                return new RollResult(ResultNotEnoughMoney);

            var info = Info;
            var prizeItem = package.Items[random.Next(package.Items.Count)];
            var prizePeriod = RollPeriod(info, package.Kind, random);
            var prizeChoice = SelectPrice(prizeItem, prizePeriod?.PeriodType ?? ItemPeriodType.None, prizePeriod?.Period ?? 0);
            if (prizeChoice.info == null)
                return new RollResult(ResultMakeItemFail);

            var gauge = Math.Min(GetGauge(plr.Account.Id) + info.GetGaugeGain(package.Kind), Math.Max(0, info.GaugeMax));
            var required = info.GetGaugeRequired(package.Kind);
            (ShopItemInfo info, ShopPrice price) bonusChoice = default;
            if (required > 0 && gauge >= required)
            {
                var bonusItem = package.Items[random.Next(package.Items.Count)];
                bonusChoice = SelectPrice(bonusItem, ItemPeriodType.None, 0);
                if (bonusChoice.info != null)
                    gauge -= required;
            }

            plr.PEN -= price;
            SetGauge(plr.Account.Id, gauge);

            var items = new List<RandomShopRollingDto> { Grant(plr, prizeChoice) };
            if (bonusChoice.info != null)
                items.Add(Grant(plr, bonusChoice));

            _logger.Debug("Random shop roll package={PackageId} items={Items} gauge={Gauge}",
                package.Id, string.Join(",", items.Select(x => x.ItemId.Id)), gauge);
            return new RollResult(ResultSuccess, items.ToArray());
        }

        /// <summary>Weighted pick of a period; null when no period has a weight.</summary>
        public static RandomShopPeriod RollPeriod(RandomShopInfo info, RandomShopKind kind, Random random)
        {
            var total = info.Periods.Sum(x => (long)Math.Max(0, x.GetWeight(kind)));
            if (total <= 0)
                return null;

            var pick = random.NextInt64(total);
            foreach (var period in info.Periods)
            {
                pick -= Math.Max(0, period.GetWeight(kind));
                if (pick < 0)
                    return period;
            }

            return info.Periods[info.Periods.Count - 1];
        }

        /// <summary>
        /// Picks the PEN shop price for the granted item: the rolled period when the shop has that
        /// price, otherwise the permanent price, otherwise the shortest enabled price. Items keep
        /// referencing a real shop price this way (the provisioned shop has no hour prices, so hour
        /// rolls currently end up permanent). Returns default when the item has no usable PEN price.
        /// </summary>
        public static (ShopItemInfo info, ShopPrice price) SelectPrice(ShopItem shopItem, ItemPeriodType periodType, ushort period)
        {
            return shopItem.ItemInfos
                .Where(IsUsable)
                .SelectMany(info => info.PriceGroup.Prices.Where(x => x.IsEnabled).Select(price => (info, price)))
                .OrderBy(x => x.price.PeriodType == periodType && x.price.Period == period ? 0
                    : x.price.PeriodType == ItemPeriodType.None ? 1 : 2)
                .ThenBy(x => x.price.Period)
                .FirstOrDefault();
        }

        private static bool IsUsable(ShopItemInfo info)
        {
            return info.IsEnabled && info.PriceGroup != null && info.PriceGroup.PriceType == ItemPriceType.PEN &&
                   info.PriceGroup.Prices.Any(x => x.IsEnabled);
        }

        private static RandomShopRollingDto Grant(Player plr, (ShopItemInfo info, ShopPrice price) choice)
        {
            var effects = choice.info.EffectGroup?.Effects.Select(x => x.Effect).Where(x => x != 0).ToArray()
                          ?? Array.Empty<uint>();

            // Sends ItemUpdateInventoryAck(Add) - the client looks the prize up in its inventory by id.
            // ShopItemId is only 32 bits on the wire (and in the client's 28-byte struct) while item ids
            // from IdGeneratorService are 64-bit, so the client gets the low 32 bits. Known limitation.
            var item = plr.Inventory.Create(choice.info, choice.price, 0, effects);
            return new RandomShopRollingDto
            {
                ItemId = item.ItemNumber,
                PeriodType = item.PeriodType,
                Period = item.Period,
                Color = item.Color,
                EffectGroupId = effects.FirstOrDefault(),
                Unk1 = 0,
                ShopItemId = (uint)item.Id
            };
        }

        #region Packages
        // Package ids exist in the client's randomshop_package.x7 / randomshopinfo_string_table.x7
        // (e.g. 1001001 "Melee Weapon 1", 2104001 "Male Shirt 1").
        private static readonly (int id, string name, byte[] subCategories)[] s_weaponPackages =
        {
            (1001001, "Melee Weapon 1", new byte[] { 0 }),
            (1002001, "Shooting Weapon 1", new byte[] { 1, 2, 3 }),
            (1003001, "Sub Weapon 1", new byte[] { 4, 5, 6 })
        };

        private static readonly (int id, string name, CostumeSlot slot)[] s_costumePackages =
        {
            (04001, "Shirt 1", CostumeSlot.Shirt),
            (05001, "Pants 1", CostumeSlot.Pants),
            (06001, "Glove 1", CostumeSlot.Gloves),
            (07001, "Shoes 1", CostumeSlot.Shoes),
            (08001, "Hair 1", CostumeSlot.Hair),
            (09001, "Accessory 1", CostumeSlot.Accessory)
        };

        /// <summary>
        /// Builds the packages from the shop: weapons by sub category, costumes by gender and slot.
        /// Only items with an enabled PEN price are included; empty packages are left out.
        /// </summary>
        public static IReadOnlyList<RandomShopPackage> BuildPackages(IEnumerable<ShopItem> shopItems, RandomShopInfo info)
        {
            var items = shopItems
                .Where(x => x.ItemInfos.Any(IsUsable))
                .OrderBy(x => x.ItemNumber.Id)
                .ToList();
            var packages = new List<RandomShopPackage>();

            foreach (var (id, name, subCategories) in s_weaponPackages)
            {
                packages.Add(new RandomShopPackage
                {
                    Id = id,
                    Name = name,
                    Kind = RandomShopKind.Weapon,
                    Gender = Gender.None,
                    Price = info.WeaponPrice,
                    Items = items.Where(x => x.ItemNumber.Category == ItemCategory.Weapon &&
                                             subCategories.Contains(x.ItemNumber.SubCategory)).ToList()
                });
            }

            foreach (var gender in new[] { Gender.Male, Gender.Female })
            {
                var prefix = gender == Gender.Male ? 2100000 : 2200000;
                var genderName = gender == Gender.Male ? "Male " : "Female ";
                foreach (var (id, name, slot) in s_costumePackages)
                {
                    packages.Add(new RandomShopPackage
                    {
                        Id = prefix + id,
                        Name = genderName + name,
                        Kind = RandomShopKind.Costume,
                        Gender = gender,
                        Price = info.CostumePrice,
                        Items = items.Where(x => x.ItemNumber.Category == ItemCategory.Costume &&
                                                 x.ItemNumber.SubCategory == (byte)slot &&
                                                 x.Gender == gender).ToList()
                    });
                }
            }

            return packages.Where(x => x.Items.Count > 0).ToList();
        }
        #endregion

        #region Client data
        /// <summary>
        /// Serializes the shop data in the layout CRandomShop reads from RandomShopUpdateInfoAck
        /// (all sections in one stream, each an int count followed by the entries):
        /// <code>
        /// package: int id, str name, str name_key, str desc_key, str open, str price_type, int price, str gender
        /// effect:  int 0, byte 0, str group, int effect
        /// color:   int 0, byte 0, str group, byte color
        /// lineup:  int 0, byte 0, int package_id, str reward_type, int reward_value, int item_key,
        ///          str color_group, str effect_group, str period_group, int default_color
        /// period:  int 0, byte 0, str group, int period_type, int period_value
        /// </code>
        /// Strings are ProudNet strings. open is "on"/"off", price_type "pen", gender "man"/"woman"/
        /// anything else for both. The leading int/byte of the group entries are not part of the
        /// client's xml cache and are left 0 like the client does when it loads that cache.
        /// reward_type "item" and reward_value 1 are guesses (the client never reads them back in the
        /// code that was looked at).
        /// </summary>
        public static byte[] BuildClientData(IReadOnlyList<RandomShopPackage> packages, RandomShopInfo info)
        {
            using (var w = new BinaryWriter(new MemoryStream()))
            {
                w.Write(packages.Count);
                foreach (var package in packages)
                {
                    w.Write(package.Id);
                    w.WriteProudString(package.Name ?? "");
                    w.WriteProudString("N" + package.Id);
                    w.WriteProudString("D" + package.Id);
                    w.WriteProudString("on");
                    w.WriteProudString("pen");
                    w.Write(package.Price);
                    w.WriteProudString(package.Gender == Gender.Male ? "man" : package.Gender == Gender.Female ? "woman" : "all");
                }

                var lineup = packages
                    .SelectMany(package => package.Items.Select(item => (package, item, info: item.ItemInfos.First(IsUsable))))
                    .ToList();

                var effectGroups = lineup
                    .Select(x => x.info.EffectGroup)
                    .GroupBy(x => GetEffectGroupName(x))
                    .Select(x => (name: x.Key, effects: x.First()?.Effects.Select(e => e.Effect).Where(e => e != 0).ToArray() ?? Array.Empty<uint>()))
                    .ToList();
                var effectCount = effectGroups.Sum(x => Math.Max(1, x.effects.Length));
                w.Write(effectCount);
                foreach (var (name, effects) in effectGroups)
                {
                    foreach (var effect in effects.Length == 0 ? new uint[] { 0 } : effects)
                    {
                        w.Write(0);
                        w.Write((byte)0);
                        w.WriteProudString(name);
                        w.Write(effect);
                    }
                }

                var colorGroups = lineup.Select(x => Math.Max(0, x.item.ColorGroup)).Distinct().OrderBy(x => x).ToList();
                w.Write(colorGroups.Sum(x => x + 1));
                foreach (var colors in colorGroups)
                {
                    for (var color = 0; color <= colors; ++color)
                    {
                        w.Write(0);
                        w.Write((byte)0);
                        w.WriteProudString(GetColorGroupName(colors));
                        w.Write((byte)color);
                    }
                }

                w.Write(lineup.Count);
                foreach (var (package, item, shopInfo) in lineup)
                {
                    w.Write(0);
                    w.Write((byte)0);
                    w.Write(package.Id);
                    w.WriteProudString("item");
                    w.Write(1);
                    w.Write((int)item.ItemNumber.Id);
                    w.WriteProudString(GetColorGroupName(Math.Max(0, item.ColorGroup)));
                    w.WriteProudString(GetEffectGroupName(shopInfo.EffectGroup));
                    w.WriteProudString(PeriodGroup);
                    w.Write(0);
                }

                w.Write(info.Periods.Count);
                foreach (var period in info.Periods)
                {
                    w.Write(0);
                    w.Write((byte)0);
                    w.WriteProudString(PeriodGroup);
                    w.Write((int)period.PeriodType);
                    w.Write((int)period.Period);
                }

                return ((MemoryStream)w.BaseStream).ToArray();
            }
        }

        private static string GetEffectGroupName(ShopEffectGroup group)
        {
            return "e" + (group?.Id ?? 0);
        }

        private static string GetColorGroupName(int colors)
        {
            return "c" + colors;
        }

        private void EnsureCache()
        {
            var shopItems = _gameDataService.ShopItems ?? ImmutableDictionary<ItemNumber, ShopItem>.Empty;
            var info = _gameDataService.RandomShop;
            lock (_cacheSync)
            {
                if (_packages != null && ReferenceEquals(_cachedShopItems, shopItems) && ReferenceEquals(_cachedInfo, info))
                    return;

                var packages = BuildPackages(shopItems.Values, info);
                var data = BuildClientData(packages, info);
                string version;
                using (var sha = SHA256.Create())
                    version = "rs-" + Convert.ToHexString(sha.ComputeHash(data), 0, 8).ToLowerInvariant();

                _packages = packages;
                _clientData = Compress(data, version);
                _cachedShopItems = shopItems;
                _cachedInfo = info;
                _logger.Information("Random shop: {Count} packages, data version {Version}", packages.Count, version);
            }
        }

        // The client only runs LZO when both lengths in UpdateInfoAck are nonzero; otherwise it parses
        // the byte array as-is. So when the native LZO library can't be loaded (e.g. macOS arm64 dev
        // machines) the data goes out uncompressed instead of failing.
        private ClientData Compress(byte[] data, string version)
        {
            try
            {
                var compressed = data.CompressLZO();
                return new ClientData(compressed, compressed.Length, data.Length, version);
            }
            catch (Exception ex) when (ex is TypeInitializationException || ex is DllNotFoundException)
            {
                _logger.Warning(ex, "LZO unavailable - sending random shop data uncompressed");
                return new ClientData(data, 0, 0, version);
            }
        }
        #endregion

        public class ClientData
        {
            /// <summary>LZO compressed data, or the raw data when both lengths are 0</summary>
            public byte[] Data { get; }
            public int CompressedLength { get; }
            public int DecompressedLength { get; }
            public string Version { get; }

            public ClientData(byte[] data, int compressedLength, int decompressedLength, string version)
            {
                Data = data;
                CompressedLength = compressedLength;
                DecompressedLength = decompressedLength;
                Version = version;
            }
        }

        public class RollResult
        {
            public int Result { get; }
            public RandomShopRollingDto[] Items { get; }

            public RollResult(int result, RandomShopRollingDto[] items = null)
            {
                Result = result;
                Items = items ?? Array.Empty<RandomShopRollingDto>();
            }
        }
    }
}
