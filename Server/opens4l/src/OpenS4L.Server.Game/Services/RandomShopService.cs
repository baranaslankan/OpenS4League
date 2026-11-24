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
    ///
    /// Odds: the packages are always built from the shop (<see cref="BuildPackages"/>). When the
    /// 2019 EU file (<see cref="RandomShopOdds.FileName"/>) is in the data folder, prize weight,
    /// period, effect and colour come from it (<see cref="BuildEntry"/>); otherwise the prize is
    /// uniform and the period weighted by new_random_shop.x7.
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
        private ImmutableDictionary<uint, ItemEffect> _cachedEffects;
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
            if (package == null || package.Entries.Count == 0)
                return new RollResult(ResultNotExistPackage);

            var character = plr.CharacterManager.CurrentCharacter;
            if (character == null || !package.CanUse(character.Gender))
                return new RollResult(ResultMakeItemFail);

            var price = (uint)Math.Max(0, package.Price);
            if (plr.PEN < price)
                return new RollResult(ResultNotEnoughMoney);

            var info = Info;
            var prize = RollPrize(package, random);
            var prizeChoice = SelectPrice(prize.Item, prize.PeriodType, prize.Period);
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

            var items = new List<RandomShopRollingDto> { Grant(plr, prizeChoice, prize.Color, prize.Effects) };
            if (bonusChoice.info != null)
                items.Add(Grant(plr, bonusChoice, 0, null));

            _logger.Debug("Random shop roll package={PackageId} items={Items} gauge={Gauge}",
                package.Id, string.Join(",", items.Select(x => x.ItemId.Id)), gauge);
            return new RollResult(ResultSuccess, items.ToArray());
        }

        /// <summary>Weighted pick of a new_random_shop.x7 period; null when no period has a weight.</summary>
        public static RandomShopPeriod RollPeriod(RandomShopInfo info, RandomShopKind kind, Random random)
        {
            return RandomShopOdds.Pick(info.Periods, x => x.GetWeight(kind), random);
        }

        /// <summary>
        /// Rolls the prize of a package: the entry by weight, then its period, colour and effect, each
        /// by weight from the entry's groups (see <see cref="BuildEntry"/>). Effects is null when the
        /// prize gets its shop effects. No period weight means permanent.
        /// </summary>
        public static Prize RollPrize(RandomShopPackage package, Random random)
        {
            var entry = RandomShopOdds.Pick(package.Entries, x => x.Weight, random)
                        ?? package.Entries[random.Next(package.Entries.Count)];
            var period = RandomShopOdds.Pick(entry.Periods, random)?.Value;
            var color = RandomShopOdds.Pick(entry.Colors, random)?.Value ?? 0;
            uint[] effects = null;
            if (!entry.UseShopEffects)
            {
                var effect = RandomShopOdds.Pick(entry.Effects, random);
                effects = effect == null ? Array.Empty<uint>() : new[] { effect.Value };
            }

            return new Prize(entry.Item, period?.PeriodType ?? ItemPeriodType.None, period?.Period ?? 0, color, effects);
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

        /// <param name="effects">null: all effects of the shop item's effect group</param>
        private static RandomShopRollingDto Grant(Player plr, (ShopItemInfo info, ShopPrice price) choice, byte color, uint[] effects)
        {
            effects = effects
                      ?? choice.info.EffectGroup?.Effects.Select(x => x.Effect).Where(x => x != 0).ToArray()
                      ?? Array.Empty<uint>();

            // Sends ItemUpdateInventoryAck(Add) - the client looks the prize up in its inventory by id.
            // ShopItemId is only 32 bits on the wire (and in the client's 28-byte struct) while item ids
            // from IdGeneratorService are 64-bit, so the client gets the low 32 bits. Known limitation.
            var item = plr.Inventory.Create(choice.info, choice.price, color, effects);
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
        /// <paramref name="knownEffects"/> (effect_list.x7) limits the 2019 effects to the ones the
        /// server knows; null accepts every effect.
        /// </summary>
        public static IReadOnlyList<RandomShopPackage> BuildPackages(IEnumerable<ShopItem> shopItems, RandomShopInfo info,
            IReadOnlyDictionary<uint, ItemEffect> knownEffects = null)
        {
            var packages = BuildPackageItems(shopItems, info);
            foreach (var package in packages)
                package.Entries = package.Items.Select(x => BuildEntry(x, package.Kind, info, knownEffects)).ToList();
            return packages;
        }

        /// <summary>
        /// What a prize can roll. Without 2019 odds (<see cref="RandomShopInfo.Odds"/> null) this is
        /// the behaviour from before the 2019 file: weight 1 (uniform), the new_random_shop.x7 period
        /// weights of the package kind, all effects of the shop effect group and colour 0.
        /// With odds, each part takes the 2019 group from <see cref="RandomShopOdds.GetSlot"/> and
        /// falls back to that behaviour on its own when the group is missing or nothing of it is left:
        /// <list type="bullet">
        /// <item>period: the group's entries with prob &gt; 0</item>
        /// <item>effect: one effect by prob, limited to effects in <paramref name="knownEffects"/></item>
        /// <item>colour: by prob, limited to the item's colours (0..ShopItem.ColorGroup); a group cut
        /// down that way gets its own name ("Dist_4@2") so the client data stays exact</item>
        /// <item>weight: the 2019 prob of the item, or the derived one</item>
        /// </list>
        /// </summary>
        public static RandomShopEntry BuildEntry(ShopItem item, RandomShopKind kind, RandomShopInfo info,
            IReadOnlyDictionary<uint, ItemEffect> knownEffects = null)
        {
            var shopInfo = item.ItemInfos.First(IsUsable);
            var colors = Math.Max(0, item.ColorGroup);
            var entry = new RandomShopEntry
            {
                Item = item,
                Weight = 1,
                PeriodGroup = PeriodGroup,
                Periods = info.Periods.Select(x => new RandomShopWeighted<RandomShopPeriod>(x, x.GetWeight(kind))).ToList(),
                EffectGroup = GetEffectGroupName(shopInfo.EffectGroup),
                Effects = (shopInfo.EffectGroup?.Effects.Select(x => x.Effect).Where(x => x != 0) ?? Enumerable.Empty<uint>())
                    .Select(x => new RandomShopWeighted<uint>(x, 1)).ToList(),
                UseShopEffects = true,
                ColorGroup = GetColorGroupName(colors),
                Colors = Enumerable.Range(0, colors + 1).Select(x => new RandomShopWeighted<byte>((byte)x, x == 0 ? 1 : 0)).ToList()
            };

            var odds = info.Odds;
            if (odds == null)
                return entry;

            var slot = odds.GetSlot(item.ItemNumber, kind);
            entry.Weight = Math.Max(1, slot.Weight);

            if (slot.PeriodGroup != null && odds.PeriodGroups.TryGetValue(slot.PeriodGroup, out var periods))
            {
                var usable = periods.Where(x => x.Weight > 0).ToList();
                if (usable.Count > 0)
                {
                    entry.PeriodGroup = slot.PeriodGroup;
                    entry.Periods = usable;
                }
            }

            if (slot.EffectGroup != null && odds.EffectGroups.TryGetValue(slot.EffectGroup, out var effects))
            {
                var usable = effects
                    .Where(x => x.Weight > 0 && x.Value != 0 && (knownEffects == null || knownEffects.ContainsKey(x.Value)))
                    .ToList();
                if (usable.Count > 0)
                {
                    entry.EffectGroup = slot.EffectGroup;
                    entry.Effects = usable;
                    entry.UseShopEffects = false;
                }
            }

            if (slot.ColorGroup != null && odds.ColorGroups.TryGetValue(slot.ColorGroup, out var groupColors))
            {
                var usable = groupColors.Where(x => x.Weight > 0 && x.Value <= colors).ToList();
                if (usable.Count > 0)
                {
                    entry.ColorGroup = usable.Count == groupColors.Count ? slot.ColorGroup : slot.ColorGroup + "@" + colors;
                    entry.Colors = usable;
                }
            }

            return entry;
        }

        private static List<RandomShopPackage> BuildPackageItems(IEnumerable<ShopItem> shopItems, RandomShopInfo info)
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

                // Every group a lineup entry names, once, in first-use order. Entries sharing a group
                // name share its content (see BuildEntry), so the first one stands for all.
                var lineup = packages.SelectMany(package => package.Entries.Select(entry => (package, entry))).ToList();
                var entries = lineup.Select(x => x.entry).ToList();

                var effectGroups = Distinct(entries, x => x.EffectGroup);
                w.Write(effectGroups.Sum(x => Math.Max(1, x.Effects.Count)));
                foreach (var entry in effectGroups)
                {
                    var effects = entry.Effects.Count == 0 ? new uint[] { 0 } : entry.Effects.Select(x => x.Value);
                    foreach (var effect in effects)
                    {
                        w.Write(0);
                        w.Write((byte)0);
                        w.WriteProudString(entry.EffectGroup);
                        w.Write(effect);
                    }
                }

                var colorGroups = Distinct(entries, x => x.ColorGroup);
                w.Write(colorGroups.Sum(x => x.Colors.Count));
                foreach (var entry in colorGroups)
                {
                    foreach (var color in entry.Colors)
                    {
                        w.Write(0);
                        w.Write((byte)0);
                        w.WriteProudString(entry.ColorGroup);
                        w.Write(color.Value);
                    }
                }

                w.Write(lineup.Count);
                foreach (var (package, entry) in lineup)
                {
                    w.Write(0);
                    w.Write((byte)0);
                    w.Write(package.Id);
                    w.WriteProudString("item");
                    w.Write(1);
                    w.Write((int)entry.Item.ItemNumber.Id);
                    w.WriteProudString(entry.ColorGroup);
                    w.WriteProudString(entry.EffectGroup);
                    w.WriteProudString(entry.PeriodGroup);
                    w.Write(0);
                }

                var periodGroups = Distinct(entries, x => x.PeriodGroup);
                w.Write(periodGroups.Sum(x => x.Periods.Count));
                foreach (var entry in periodGroups)
                {
                    foreach (var period in entry.Periods)
                    {
                        w.Write(0);
                        w.Write((byte)0);
                        w.WriteProudString(entry.PeriodGroup);
                        w.Write((int)period.Value.PeriodType);
                        w.Write((int)period.Value.Period);
                    }
                }

                return ((MemoryStream)w.BaseStream).ToArray();
            }
        }

        private static List<RandomShopEntry> Distinct(IEnumerable<RandomShopEntry> entries, Func<RandomShopEntry, string> group)
        {
            return entries.GroupBy(group, StringComparer.Ordinal).Select(x => x.First()).ToList();
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
            var effects = _gameDataService.Effects;
            lock (_cacheSync)
            {
                if (_packages != null && ReferenceEquals(_cachedShopItems, shopItems) && ReferenceEquals(_cachedInfo, info) &&
                    ReferenceEquals(_cachedEffects, effects))
                    return;

                var packages = BuildPackages(shopItems.Values, info, effects);
                var data = BuildClientData(packages, info);
                string version;
                using (var sha = SHA256.Create())
                    version = "rs-" + Convert.ToHexString(sha.ComputeHash(data), 0, 8).ToLowerInvariant();

                _packages = packages;
                _clientData = Compress(data, version);
                _cachedShopItems = shopItems;
                _cachedInfo = info;
                _cachedEffects = effects;
                _logger.Information("Random shop: {Count} packages, {Odds}, data version {Version}", packages.Count,
                    info.Odds == null ? "new_random_shop.x7 odds" : "2019 odds", version);
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

        public class Prize
        {
            public ShopItem Item { get; }
            public ItemPeriodType PeriodType { get; }
            public ushort Period { get; }
            public byte Color { get; }

            /// <summary>null: the shop item's own effects</summary>
            public uint[] Effects { get; }

            public Prize(ShopItem item, ItemPeriodType periodType, ushort period, byte color, uint[] effects)
            {
                Item = item;
                PeriodType = periodType;
                Period = period;
                Color = color;
                Effects = effects;
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
