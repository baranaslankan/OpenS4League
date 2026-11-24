using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using OpenS4L.Database;
using OpenS4L.Database.Game;
using OpenS4L.Resource.xml;
using OpenS4L.Resource.Xml;
using OpenS4L.Server.Game.Data;

namespace OpenS4L.Server.Game.Services
{
    public partial class GameDataService
    {
        public void LoadLevels()
        {
            _logger.Information("Loading levels...");
            var dto = Deserialize<ExperienceDto>("xml/experience.x7");
            Levels = Transform().ToImmutableDictionary(x => x.Level, x => x);
            _logger.Information("Loaded {Count} levels", Levels.Count);

            IEnumerable<LevelInfo> Transform()
            {
                for (var i = 0; i < dto.exp.Length; ++i)
                {
                    yield return new LevelInfo
                    {
                        Level = i,
                        ExperienceToNextLevel = dto.exp[i].require,
                        TotalExperience = dto.exp[i].accumulate
                    };
                }
            }
        }

        public void LoadMaps()
        {
            _logger.Information("Loading maps...");
            var dto = Deserialize<MapListDto>("xml/map.x7");
            var stringTable = Deserialize<StringTableDto>("language/xml/gameinfo_string_table.x7");
            Maps = Transform().ToImmutableArray();
            _logger.Information("Loaded {Count} maps", Maps.Length);

            IEnumerable<MapInfo> Transform()
            {
                foreach (var mapDto in dto.map)
                {
                    // Keep the full id: the random placeholders (993-999) would collide with real
                    // maps once truncated to a byte (999 -> 231 = Wonderland TD)
                    var map = new MapInfo
                    {
                        Id = mapDto.id,
                        GameRule = (GameRule)mapDto.@base.mode_number,
                        PlayerLimit = mapDto.@base.limit_player,
                        IsRandom = IsRandomMapPlaceholder(mapDto)
                    };

                    if (!string.IsNullOrWhiteSpace(mapDto.@switch.eu) &&
                        mapDto.@switch.eu != "off" &&
                        mapDto.@switch.eu != "dev")
                    {
                        map.IsEnabled = true;
                    }

                    var name = stringTable.@string.FirstOrDefault(s => s.key.Equals(
                        mapDto.@base.map_name_key,
                        StringComparison.InvariantCultureIgnoreCase
                    ));
                    if (string.IsNullOrWhiteSpace(name?.eng))
                    {
                        _logger.Warning("Missing english translation for {MapKey}", mapDto.@base.map_name_key);
                        map.Name = mapDto.@base.map_name_key;
                    }
                    else
                    {
                        map.Name = name.eng;
                    }

                    yield return map;
                }
            }
        }

        /// <summary>
        /// The client's map.x7 has "random" entries (999=DM, 998=TD, 997=Chaser, ...) that point to
        /// bginfo-random.ini instead of a real map
        /// </summary>
        internal static bool IsRandomMapPlaceholder(MapListMapDto mapDto)
        {
            var bgInfo = mapDto.resourse?.bginfo_path;
            if (!string.IsNullOrWhiteSpace(bgInfo) &&
                bgInfo.EndsWith("bginfo-random.ini", StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            // A map id that does not fit in a byte can't be sent to the client as a real map
            return mapDto.id > byte.MaxValue;
        }

        private void LoadEffectMatches(StringTableDto names)
        {
            var path = Path.Combine(_resourcePath, "xml", "effect_match_list.x7");
            if (!File.Exists(path))
                return;

            var nameOf = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var s in names?.@string ?? Array.Empty<StringTableStringDto>())
            {
                if (s.key != null)
                    nameOf[s.key] = s.eng ?? "";
            }

            var groups = XDocument.Load(path).Descendants("match_key")
                .Select(x => (Id: uint.TryParse((string)x.Attribute("id"), out var id) ? id : 0,
                    Name: nameOf.GetValueOrDefault((string)x.Attribute("name_key") ?? "", "")))
                .Where(x => x.Id != 0)
                .GroupBy(x => x.Id)
                .ToImmutableDictionary(x => x.Key, x => x.First().Name);
            EffectMatches = groups.Keys.ToImmutableHashSet();
            EffectMatchNames = groups;
            _logger.Information("Loaded {Count} effect groups", EffectMatches.Count);
        }

        public void LoadEffects()
        {
            _logger.Information("Loading effects...");
            var dto = Deserialize<EffectListDto>("xml/effect_list.x7");
            var stringTable = Deserialize<StringTableDto>("language/xml/item_effect_string_table.x7");
            Effects = Transform().ToImmutableDictionary(x => x.Id, x => x);
            _logger.Information("Loaded {Count} effects", Effects.Count);
            LoadEffectMatches(stringTable);

            IEnumerable<ItemEffect> Transform()
            {
                foreach (var itemEffectDto in dto.item_effect.Where(itemEffect => itemEffect.effect_id != 0))
                {
                    var itemEffect = new ItemEffect
                    {
                        Id = itemEffectDto.effect_id
                    };

                    var nameDto = stringTable.@string.FirstOrDefault(x =>
                        x.key.Equals(itemEffectDto.name_key, StringComparison.InvariantCultureIgnoreCase)
                    );
                    var name = nameDto?.eng;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        _logger.Warning("Missing english translation for item effect {Key}", itemEffectDto.name_key);
                        name = itemEffectDto.name_key;
                    }

                    itemEffect.Name = name;

                    if (Enum.IsDefined(typeof(EffectType), itemEffectDto.effect_type))
                    {
                        itemEffect.EffectType = (EffectType)itemEffectDto.effect_type;
                        itemEffect.Value = itemEffectDto.value_min;
                        itemEffect.Rate = itemEffectDto.rate_min / 10f;
                    }
                    else
                    {
                        _logger.Warning("Unknown effect type {EffectType} for Effect {EffectName}",
                            itemEffectDto.effect_type, itemEffect.Name);
                    }

                    yield return itemEffect;
                }
            }
        }

        public void LoadItems()
        {
            _logger.Information("Loading items...");
            var costumesDto = Deserialize<ItemListDto>("xml/item.x7");
            var weaponsDto = Deserialize<WeaponListDto>("xml/_eu_weapon.x7");
            var actionsDto = Deserialize<ActionListDto>("xml/action.x7");
            var stringTable = Deserialize<StringTableDto>("language/xml/iteminfo_string_table.x7");

            // Ugly hack because S4 League devs are shit. Sometimes they add ids multiple times
            var items = Transform().ToArray();
            var dict = new Dictionary<ItemNumber, ItemInfo>();
            foreach (var item in items)
                dict[item.ItemNumber] = item;

            Items = dict.ToImmutableDictionary();
            _logger.Information("Loaded {Count} items", Items.Count);

            IEnumerable<ItemInfo> Transform()
            {
                foreach (var itemDto in costumesDto.item)
                {
                    var id = new ItemNumber(itemDto.item_key);
                    ItemInfo item = null;

                    switch (id.Category)
                    {
                        case ItemCategory.Weapon:
                            item = LoadWeapon(id, itemDto.etc);
                            break;

                        case ItemCategory.Skill:
                            item = LoadAction(id);
                            break;

                        default:
                            item = new ItemInfo();
                            break;
                    }

                    item.ItemNumber = id;
                    item.Gender = ParseGender(itemDto.@base.sex);
                    item.Image = itemDto.graphic.icon_image;

                    var name = stringTable.@string.FirstOrDefault(s =>
                        s.key.Equals(itemDto.@base.name_key, StringComparison.InvariantCultureIgnoreCase)
                    );
                    if (string.IsNullOrWhiteSpace(name?.eng))
                    {
                        _logger.Warning(
                            "Missing english translation for item {id}",
                            name != null ? itemDto.@base.name_key : id.ToString()
                        );
                        item.Name = name != null ? name.key : itemDto.@base.name;
                    }
                    else
                    {
                        item.Name = name.eng;
                    }

                    yield return item;
                }
            }

            Gender ParseGender(string gender)
            {
                bool Equals(string str)
                {
                    return gender.Equals(str, StringComparison.InvariantCultureIgnoreCase);
                }

                if (string.IsNullOrWhiteSpace(gender))
                    return Gender.None;

                if (Equals("unisex"))
                    return Gender.None;

                if (Equals("woman"))
                    return Gender.Female;

                if (Equals("man"))
                    return Gender.Male;

                throw new Exception("Invalid gender " + gender);
            }

            ItemInfo LoadAction(ItemNumber id)
            {
                var actionDto = actionsDto.Action.FirstOrDefault(x => x.name == id);
                if (actionDto == null)
                {
                    _logger.Warning("Missing action for item {id}", id);
                    return new ItemInfoAction();
                }

                var item = new ItemInfoAction
                {
                    RequiredMP = float.Parse(actionDto.ability.required_mp, CultureInfo.InvariantCulture),
                    DecrementMP = float.Parse(actionDto.ability.decrement_mp, CultureInfo.InvariantCulture),
                    DecrementMPDelay = float.Parse(actionDto.ability.decrement_mp_delay, CultureInfo.InvariantCulture)
                };

                if (actionDto.Float != null)
                {
                    item.ValuesF = new List<float>();

                    var props = actionDto.Float.GetType().GetProperties();
                    foreach (var prop in props)
                    {
                        var value = (string)prop.GetValue(actionDto.Float);
                        if (!string.IsNullOrWhiteSpace(value))
                            item.ValuesF.Add(float.Parse(value, CultureInfo.InvariantCulture));
                    }
                }

                if (actionDto.Integer != null)
                {
                    item.Values = new List<int>();

                    var props = actionDto.Integer.GetType().GetProperties();
                    foreach (var prop in props)
                    {
                        var value = (int)prop.GetValue(actionDto.Integer);
                        item.Values.Add(value);
                    }
                }

                return item;
            }

            ItemInfo LoadWeapon(ItemNumber id, ItemListItemEtcDto etc)
            {
                var weaponDto = weaponsDto.weapon.FirstOrDefault(x => x.item_key == id);
                // Weapon skins (e.g. Virtual Shield) have no entry: they use their parent weapon's.
                if (weaponDto == null && etc?.parent_numberSpecified == true)
                {
                    var parent = new ItemNumber(id.Category, id.SubCategory, (ushort)etc.parent_number);
                    weaponDto = weaponsDto.weapon.FirstOrDefault(x => x.item_key == parent);
                }

                if (weaponDto == null)
                {
                    _logger.Warning("Missing weapon for item {id}", id);
                    return new ItemInfoWeapon();
                }

                var ability = weaponDto.ability;
                var item = new ItemInfoWeapon
                {
                    Type = (byte)ability.type,
                    RateOfFire = float.Parse(ability.rate_of_fire, CultureInfo.InvariantCulture),
                    Power = float.Parse(ability.power, CultureInfo.InvariantCulture),
                    MoveSpeedRate = float.Parse(ability.move_speed_rate, CultureInfo.InvariantCulture),
                    AttackMoveSpeedRate = float.Parse(ability.attack_move_speed_rate, CultureInfo.InvariantCulture),
                    MagazineCapacity = ability.magazine_capacity,
                    CrackedMagazineCapacity = ability.cracked_magazine_capacity,
                    MaxAmmo = ability.max_ammo,
                    Accuracy = float.Parse(ability.accuracy, CultureInfo.InvariantCulture),
                    Range = string.IsNullOrWhiteSpace(ability.range)
                        ? 0
                        : float.Parse(ability.range, CultureInfo.InvariantCulture),
                    SupportSniperMode = ability.support_sniper_mode > 0,
                    SniperModeFov = ability.sniper_mode_fov > 0,
                    AutoTargetDistance = string.IsNullOrWhiteSpace(ability.auto_target_distance)
                        ? 0
                        : float.Parse(ability.auto_target_distance, CultureInfo.InvariantCulture)
                };

                return item;
            }
        }

        public void LoadDefaultItems()
        {
            _logger.Information("Loading default items...");
            var dto = Deserialize<DefaultItemDto>("xml/default_item.x7");
            DefaultItems = Transform().ToImmutableArray();
            _logger.Information("Loaded {Count} default items", DefaultItems.Length);

            IEnumerable<DefaultItem> Transform()
            {
                foreach (var itemDto in dto.male.item)
                {
                    var item = new DefaultItem
                    {
                        ItemNumber = new ItemNumber(itemDto.category, itemDto.sub_category, itemDto.number),
                        Gender = CharacterGender.Male,
                        Variation = itemDto.variation
                    };
                    yield return item;
                }

                foreach (var itemDto in dto.female.item)
                {
                    var item = new DefaultItem
                    {
                        ItemNumber = new ItemNumber(itemDto.category, itemDto.sub_category, itemDto.number),
                        Gender = CharacterGender.Female,
                        Variation = itemDto.variation
                    };
                    yield return item;
                }
            }
        }

        public async Task LoadShop()
        {
            using (var db = _databaseService.Open<GameContext>())
            {
                _logger.Information("Loading effect groups...");
                var effects = await db.EffectGroups.Include(x => x.ShopEffects).ToArrayAsync();
                ShopEffects = effects.ToImmutableDictionary(x => x.Id, x => new ShopEffectGroup(x));
                _logger.Information("Loaded {Count} effect groups", ShopEffects.Count);

                _logger.Information("Loading price groups...");
                var prices = await db.PriceGroups.Include(x => x.ShopPrices).ToArrayAsync();
                ShopPrices = prices.ToImmutableDictionary(x => x.Id, x => new ShopPriceGroup(x));
                _logger.Information("Loaded {Count} price groups", ShopPrices.Count);

                _logger.Information("Loading shop items...");
                var items = await db.Items.Include(x => x.ItemInfos).ToArrayAsync();
                ShopItems = items.ToImmutableDictionary(x => (ItemNumber)x.Id, x => new ShopItem(x, this));
                _logger.Information("Loaded {Count} shop items", ShopItems.Count);

                var version = await db.ShopVersion.FirstOrDefaultAsync();
                if (version == null)
                {
                    _logger.Warning("No shop version found in database! Using current timestamp");
                    version = new ShopVersionEntity { Version = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };
                }

                ShopVersion = version.Version;
                _logger.Information("Loaded shop version {Version}", ShopVersion);
            }
        }

        public void LoadGameTempos()
        {
            _logger.Information("Loading game tempos...");
            var dto = Deserialize<ConstantInfoDto>("xml/constant_info.x7");
            GameTempos = Transform().ToImmutableDictionary(x => x.Name, x => x);
            _logger.Information("Loaded {Count} game tempos", GameTempos.Count);

            IEnumerable<GameTempo> Transform()
            {
                foreach (var gameTempoDto in dto.GAMEINFOLIST)
                {
                    var gameTempo = new GameTempo
                    {
                        Name = gameTempoDto.TEMPVALUE.value
                    };

                    var values = gameTempoDto.GAMETEPMO_COMMON_TOTAL_VALUE;
                    gameTempo.ActorDefaultHPMax =
                        float.Parse(values.GAMETEMPO_actor_default_hp_max, CultureInfo.InvariantCulture);
                    gameTempo.ActorDefaultMPMax =
                        float.Parse(values.GAMETEMPO_actor_default_mp_max, CultureInfo.InvariantCulture);
                    gameTempo.ActorDefaultMoveSpeed = values.GAMETEMPO_fastrun_required_mp;

                    yield return gameTempo;
                }
            }
        }

        public void LoadEquipLimits()
        {
            _logger.Information("Loading equip limits...");
            var dto = Deserialize<EquipLimitDto>("xml/equip_limit.x7");
            EquipLimits = Transform().ToImmutableDictionary(x => x.Id, x => x);
            _logger.Information("Loaded {Count} equip limits", EquipLimits.Count);

            IEnumerable<EquipLimitInfo> Transform()
            {
                foreach (var limitDto in dto.preset)
                {
                    yield return new EquipLimitInfo
                    {
                        Id = limitDto.id,
                        Blacklist = limitDto.require_Item?.Select(x => new ItemNumber(x.Item_Id)).ToArray()
                                    ?? Array.Empty<ItemNumber>()
                    };
                }
            }
        }

        /// <summary>
        /// Capsule rewards: the optional server-side table <c>server/ItemBag.xml</c> (with odds) wins;
        /// every other capsule in the client's tooltip file gives one of its listed rewards per slot,
        /// all equally likely. Either way the tooltip's per-reward text (periods and stats) is applied,
        /// its stats mapped to effect ids preferring the ones the official EU shop
        /// (<c>server/eu2019</c>) gave the item.
        /// </summary>
        public void LoadCapsules()
        {
            _logger.Information("Loading capsules...");
            var capsules = new Dictionary<ItemNumber, CapsuleInfo>();
            var tooltips = new Dictionary<ItemNumber, CapsuleInfo>();

            var tooltipPath = Path.Combine(_resourcePath, "xml", "_eu_item_tooltip_addcapsule.x7");
            var stringsPath = Path.Combine(_resourcePath, "language", "xml", "tooltipinfo_string_table.x7");
            if (File.Exists(tooltipPath))
            {
                var strings = new Dictionary<string, string>();
                if (File.Exists(stringsPath))
                {
                    foreach (var e in XDocument.Load(stringsPath).Root?.Elements("string") ?? Enumerable.Empty<XElement>())
                    {
                        var key = (string)e.Attribute("key");
                        if (key != null)
                            strings[key] = (string)e.Attribute("eng") ?? "";
                    }
                }

                foreach (var capsule in CapsuleDataParser.ParseTooltip(XDocument.Load(tooltipPath), strings))
                    capsules[capsule.ItemNumber] = tooltips[capsule.ItemNumber] = capsule;
            }
            else
            {
                _logger.Warning("Capsule tooltip file not found at {Path}", tooltipPath);
            }

            var tablePath = Path.Combine(_resourcePath, "server", "ItemBag.xml");
            var fromTable = 0;
            var corrected = 0;
            var slotsAdded = 0;
            if (File.Exists(tablePath))
            {
                foreach (var capsule in CapsuleDataParser.ParseRewardTable(XDocument.Load(tablePath)))
                {
                    var tooltip = tooltips.GetValueOrDefault(capsule.ItemNumber);
                    corrected += CapsuleDataParser.ApplyTooltip(capsule, tooltip);
                    slotsAdded += CapsuleDataParser.AddMissingSlots(capsule, tooltip);
                    capsules[capsule.ItemNumber] = capsule;
                    ++fromTable;
                }
            }
            else
            {
                _logger.Information("No capsule reward table at {Path}; all capsules use equal odds", tablePath);
            }

            var resolver = CreateCapsuleEffectResolver(capsules.Values);
            var unmapped = new Dictionary<string, int>();
            foreach (var capsule in capsules.Values)
            {
                CapsuleDataParser.ResolveEffects(capsule, resolver, (reward, option) =>
                {
                    var key = $"{reward.ItemNumber.Category}/{reward.ItemNumber.SubCategory} \"{option.StatText}\"";
                    unmapped[key] = unmapped.GetValueOrDefault(key) + 1;
                });
                CapsuleDataParser.ApplyBoxPeriod(capsule, Items.GetValueOrDefault(capsule.ItemNumber)?.Name);
                CapsuleDataParser.ApplySetGrade(capsule, Items.GetValueOrDefault(capsule.ItemNumber)?.Name);
                CapsuleDataParser.ApplyBoosterRewards(capsule);
                CapsuleDataParser.WeightSetBoxes(capsule, n => Items.GetValueOrDefault(n)?.Name);
            }

            if (unmapped.Count > 0)
            {
                _logger.Warning(
                    "Capsules: {Count} tooltip stat texts ({Rewards} rewards) match no effect and keep the reward table's or shop's stats: {Texts}",
                    unmapped.Count, unmapped.Values.Sum(),
                    string.Join("; ", unmapped.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}")));
            }

            Capsules = capsules.ToImmutableDictionary();
            _logger.Information(
                "Loaded {Count} capsules ({FromTable} with odds from the reward table, {Corrected} table rewards corrected by the tooltip, {SlotsAdded} tooltip rolls the table lacked)",
                Capsules.Count, fromTable, corrected, slotsAdded);
        }

        private CapsuleEffectResolver CreateCapsuleEffectResolver(IEnumerable<CapsuleInfo> capsules)
        {
            var resolver = new CapsuleEffectResolver(
                (Effects ?? ImmutableDictionary<uint, ItemEffect>.Empty).ToDictionary(x => x.Key, x => x.Value.Name ?? ""),
                EffectMatchNames);

            var itemsPath = Path.Combine(_resourcePath, "server", "eu2019", "_eu_new_shop_item.xml");
            var effectsPath = Path.Combine(_resourcePath, "server", "eu2019", "_eu_new_shop_effect.xml");
            if (File.Exists(itemsPath) && File.Exists(effectsPath))
            {
                try
                {
                    foreach (var (item, sets) in CapsuleEffectResolver.ReadShopEffects(XDocument.Load(itemsPath),
                                 XDocument.Load(effectsPath)))
                        resolver.AddShopEffects(item, sets);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException || ex is IOException)
                {
                    _logger.Warning(ex, "Capsules: EU shop effects at {Path} unusable", itemsPath);
                }
            }

            foreach (var reward in capsules.Where(c => c.Source == CapsuleSource.RewardTable)
                         .SelectMany(c => c.Groups).SelectMany(g => g.Rewards)
                         .Where(r => r.Type == CapsuleRewardType.Item && r.Effects.Length > 0))
                resolver.AddTableEffects(reward.ItemNumber.Id, reward.Effects);
            return resolver;
        }

        public async Task LoadLevelRewards()
        {
            using (var db = _databaseService.Open<GameContext>())
            {
                _logger.Information("Loading level rewards...");
                var rewards = await db.LevelRewards.ToArrayAsync();
                LevelRewards = rewards.ToImmutableDictionary(x => x.Level, x => new LevelReward(x));
                _logger.Information("Loaded {Count} level rewards", LevelRewards.Count);
            }
        }
    }
}
