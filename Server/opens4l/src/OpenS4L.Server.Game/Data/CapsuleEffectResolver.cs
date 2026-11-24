using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>
    /// Finds the effect ids for the stats a capsule tooltip lists for a reward ("attack +10%" on a
    /// weapon = 1299600006). In order of preference:
    /// <list type="number">
    /// <item>an effect set the official EU shop gave this very item (cash shop first);</item>
    /// <item>an effect set the reward table gives this item elsewhere;</item>
    /// <item>one effect or effect group (effect_match_list.x7) with exactly these stats, for the
    /// item's category (effect ids carry it: 1 1 04 300 008 = costume pants HP+8; group 14002 = pants);</item>
    /// <item>one effect of the item's category per stat (e.g. a weapon Force Pack = HP+5, SP+5,
    /// Attack+11%, Move speed+3%).</item>
    /// </list>
    /// Effects and groups the EU shop used win ties. Null when the stats can't be matched.
    /// </summary>
    public class CapsuleEffectResolver
    {
        private readonly Dictionary<uint, string> _keyOf = new Dictionary<uint, string>();
        private readonly Dictionary<uint, CapsuleStat[]> _statsOf = new Dictionary<uint, CapsuleStat[]>();
        private readonly HashSet<uint> _groups = new HashSet<uint>();
        private readonly HashSet<uint> _usedByShop = new HashSet<uint>();
        private readonly Dictionary<uint, List<uint[]>> _shopSets = new Dictionary<uint, List<uint[]>>();
        private readonly Dictionary<uint, List<uint[]>> _tableSets = new Dictionary<uint, List<uint[]>>();

        /// <param name="effects">effect_list.x7: id -> English name.</param>
        /// <param name="groups">effect_match_list.x7: group id -> English name.</param>
        public CapsuleEffectResolver(IReadOnlyDictionary<uint, string> effects, IReadOnlyDictionary<uint, string> groups)
        {
            foreach (var (id, name) in effects)
                Add(id, name, false);
            foreach (var (id, name) in groups)
                Add(id, name, true);
        }

        private void Add(uint id, string name, bool group)
        {
            if (id == 0)
                return;
            var stats = CapsuleTooltipText.ParseStats(name).ToArray();
            if (group)
                _groups.Add(id);
            if (stats.Length == 0)
                return;
            _statsOf[id] = stats;
            _keyOf[id] = CapsuleTooltipText.Key(stats);
        }

        public bool IsKnown(uint id) => _statsOf.ContainsKey(id) || _groups.Contains(id);

        /// <summary>Adds the effect sets the official shop gave an item, cash shop (the one capsules mirror) first.</summary>
        public void AddShopEffects(uint item, IEnumerable<uint[]> sets)
        {
            foreach (var set in sets)
            {
                var known = set.Where(IsKnown).Distinct().OrderBy(x => x).ToArray();
                if (known.Length == 0)
                    continue;
                if (!_shopSets.TryGetValue(item, out var list))
                    _shopSets[item] = list = new List<uint[]>();
                list.Add(known);
                _usedByShop.UnionWith(known);
            }
        }

        /// <summary>Adds an effect set the reward table gives an item.</summary>
        public void AddTableEffects(uint item, uint[] set)
        {
            var known = set.Where(IsKnown).Distinct().OrderBy(x => x).ToArray();
            if (known.Length == 0)
                return;
            if (!_tableSets.TryGetValue(item, out var list))
                _tableSets[item] = list = new List<uint[]>();
            if (!list.Any(x => x.SequenceEqual(known)))
                list.Add(known);
        }

        public uint[] Resolve(ItemNumber item, IReadOnlyCollection<CapsuleStat> stats)
        {
            if (stats == null || stats.Count == 0)
                return null;
            var key = CapsuleTooltipText.Key(stats);

            foreach (var source in new[] { _shopSets, _tableSets })
            {
                if (!source.TryGetValue(item.Id, out var sets))
                    continue;
                var hit = sets.FirstOrDefault(set => KeyOf(set) == key);
                if (hit != null)
                    return hit;
            }

            var single = _keyOf.Where(x => x.Value == key && FitsCategory(x.Key, item))
                .Select(x => x.Key)
                .OrderBy(id => _usedByShop.Contains(id) ? 0 : 1)
                .ThenBy(id => _groups.Contains(id) ? 0 : 1)
                .ThenBy(id => id)
                .ToArray();
            if (single.Length > 0)
                return new[] { single[0] };

            var composed = new List<uint>();
            foreach (var stat in stats.Distinct())
            {
                var statKey = CapsuleTooltipText.Key(new[] { stat });
                var pick = _keyOf.Where(x => x.Value == statKey && !_groups.Contains(x.Key) && FitsCategory(x.Key, item))
                    .Select(x => x.Key)
                    .OrderBy(id => _usedByShop.Contains(id) ? 0 : 1)
                    .ThenBy(id => id)
                    .ToArray();
                if (pick.Length == 0)
                    return null;
                composed.Add(pick[0]);
            }

            return composed.OrderBy(x => x).ToArray();
        }

        private string KeyOf(uint[] set) =>
            CapsuleTooltipText.Key(set.SelectMany(id => _statsOf.TryGetValue(id, out var s) ? s : Array.Empty<CapsuleStat>()));

        /// <summary>
        /// Effect ids carry the item category they are for: 10-digit effects as
        /// [type][category][subcategory 2][effect 3][level 3] (subcategory 99 = any), 5-digit groups as
        /// [category][subcategory]xxx, 7-digit groups as 70[category][subcategory]xxx.
        /// </summary>
        public static bool FitsCategory(uint id, ItemNumber item)
        {
            var d = id.ToString(CultureInfo.InvariantCulture);
            var category = (int)item.Category;
            var sub = item.SubCategory;
            switch (d.Length)
            {
                case 10:
                    var lower = int.Parse(d.Substring(2, 2), CultureInfo.InvariantCulture);
                    return d[1] - '0' == category && (lower == sub || lower == 99);
                case 5:
                    return d[0] - '0' == category && d[1] - '0' == sub;
                case 7 when d.StartsWith("70", StringComparison.Ordinal):
                    return d[2] - '0' == category && d[3] - '0' == sub;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Effect sets per item from the decoded EU shop (<c>_eu_new_shop_item.xml</c> +
        /// <c>_eu_new_shop_effect.xml</c>): shop 3 then 2 (cash), then 1 (PEN).
        /// </summary>
        public static Dictionary<uint, List<uint[]>> ReadShopEffects(XDocument items, XDocument effects)
        {
            var matches = new Dictionary<string, uint[]>();
            foreach (var m in effects.Root?.Elements("effect_match") ?? Enumerable.Empty<XElement>())
            {
                var key = (string)m.Attribute("key");
                if (key == null)
                    continue;
                matches[key] = m.Elements("effects")
                    .Select(e => uint.TryParse((string)e.Attribute("effect_id"), out var v) ? v : 0)
                    .Where(v => v != 0)
                    .ToArray();
            }

            var result = new Dictionary<uint, List<uint[]>>();
            foreach (var item in items.Root?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                if (!uint.TryParse((string)item.Attribute("key"), out var number))
                    continue;
                var sets = new List<uint[]>();
                foreach (var info in item.Elements("shopinfo")
                             .Where(x => (string)x.Attribute("shop_id") is "1" or "2" or "3")
                             .OrderBy(x => (string)x.Attribute("shop_id") == "1" ? 1 : 0)
                             .ThenByDescending(x => (string)x.Attribute("shop_id")))
                {
                    var key = (string)info.Attribute("effect_match");
                    if (!uint.TryParse(key, out var id) || id == 0)
                        continue;
                    // A key with members is the members; otherwise the id itself (an effect or a group).
                    sets.Add(matches.TryGetValue(key, out var members) && members.Length > 0 ? members : new[] { id });
                    if (matches.ContainsKey(key))
                        sets.Add(new[] { id });
                }

                if (sets.Count > 0)
                    result[number] = sets;
            }

            return result;
        }
    }
}
