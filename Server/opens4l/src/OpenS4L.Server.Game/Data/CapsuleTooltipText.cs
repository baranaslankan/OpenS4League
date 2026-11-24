using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenS4L.Server.Game.Data
{
    /// <summary>One stat as the tooltip (or an effect name) writes it, e.g. "attack +10%" = (atk, 10).</summary>
    public readonly record struct CapsuleStat(string Name, int Value)
    {
        public override string ToString() => $"{Name}+{Value}";
    }

    /// <summary>One period the tooltip lists for a reward, with the stats it has at that period.</summary>
    public class CapsuleTooltipOption
    {
        /// <summary>Null when the text gives stats but no period (the reward's own period is kept).</summary>
        public ItemPeriodType? PeriodType { get; set; }

        public ushort Period { get; set; }

        /// <summary>The stats the text lists. Empty = the text says nothing about stats.</summary>
        public IReadOnlyCollection<CapsuleStat> Stats { get; set; } = Array.Empty<CapsuleStat>();

        /// <summary>The text says the reward has no stat ("no stat", "None Option").</summary>
        public bool NoStats { get; set; }

        /// <summary>The part of the text the stats were read from (for logs).</summary>
        public string StatText { get; set; } = "";
    }

    /// <summary>
    /// Reads the client's capsule tooltip texts (tooltipinfo_string_table.x7, one per reward slot)
    /// into period choices and stats. The texts are free-form English, e.g.
    /// "{N}attack +10%(3, 10, 35Day)", "{N}SP +6 (1, 7, 30Day/Permanent)",
    /// "Force Pack (7Day) attack +11%, move speed +3%, HP +5, SP +5", "{N}no stat (1Day)",
    /// "attack +8%(Permanent){N}, attack +10%(30Day)", "{N}(Jacket, Pants, Gloves, Shoes, Hair)-30DAY",
    /// "x3" / "{N}2/3/5" (unit counts).
    /// </summary>
    public static class CapsuleTooltipText
    {
        private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "(1/7/30)Day"
        private static readonly Regex SlashDays = new Regex(@"\((\d+(?:\s*/\s*\d+)+)\)\s*days?", Ci);

        // "(1Day)", "(3, 10, 35Day)", "(1Day, Permanent)", "(10 hours)", "(1, 7, 30Day/Permanent)"
        private static readonly Regex ParenPeriod = new Regex(@"\(([^()]*?(?:day|hour|permanent)[^()]*)\)", Ci);

        // "10Days ...", "-30DAY", " 10 Hours", "Permanent SP +6 ..."
        private static readonly Regex BarePeriod = new Regex(@"(?<![\w])-?\s*(\d+)\s*(days?|hours?)\b|\bpermanent\b", Ci);

        private static readonly Regex PeriodToken = new Regex(@"(\d+)\s*(days?|hours?)?|(permanent)", Ci);

        // "x3", "2/3/5", "1"
        private static readonly Regex UnitCounts = new Regex(@"^\s*(?:x\s*(\d+)|(\d+(?:\s*/\s*\d+)*))\s*$", Ci);

        private static readonly Regex Term = new Regex(@"([a-z][a-z .()]*?)\s*\+\s*(\d+)\s*%?", RegexOptions.CultureInvariant);

        private static readonly Regex NoStat = new Regex(@"\bno stat\b|\bnone option\b", Ci);

        /// <summary>Stat names as one canonical word, matched against the end of the name.</summary>
        private static readonly (Regex Pattern, string Name)[] Canonical =
        {
            // the tooltip writes the head-gun defence of hats both ways
            (Tail(@"(?:shooting weapon defen[cs]e|guns def\.?)\s*\(head\)"), "gundef"),
            (Tail(@"shooting weapon defen[cs]e"), "gundef"),
            (Tail(@"(?:sentry ?hp|stationary weapon hp)"), "sentryhp"),
            (Tail(@"attack\s*\(td only\)"), "atktd"),
            // weapon-specific defences keep their own name
            (Tail(@"(?:melee weapon|rifle|guns?|heavy gun|snipe|sniper|stationary weapon|mental weapon|throwing weapon) defen[cs]e"), null),
            (Tail(@"(?:defen[cs]e|depense|def\.?)"), "def"),
            (Tail(@"(?:attack|atk)"), "atk"),
            // a healing weapon's "attack" effect is its healing: the client has no healing effect
            (Tail(@"healing"), "atk"),
            (Tail(@"(?:move(?:ment)? ?speed|speed)"), "speed"),
            (Tail(@"exp"), "exp"),
            (Tail(@"hp"), "hp"),
            (Tail(@"sp"), "sp"),
            (Tail(@"magazine"), "mag"),
        };

        private static Regex Tail(string pattern) =>
            new Regex(@"(?:^|[^a-z])(" + pattern + @")\s*$", RegexOptions.CultureInvariant);

        /// <summary>Every option the text lists, in text order. Empty when it lists no period, count or stat.</summary>
        public static IList<CapsuleTooltipOption> Parse(string raw)
        {
            var text = Clean(raw);
            var result = new List<CapsuleTooltipOption>();
            if (text.Length == 0)
                return result;

            var units = UnitCounts.Match(text);
            if (units.Success)
            {
                var counts = units.Groups[1].Success
                    ? new[] { units.Groups[1].Value }
                    : Regex.Split(units.Groups[2].Value, @"\s*/\s*");
                foreach (var count in counts)
                {
                    if (ushort.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                        result.Add(new CapsuleTooltipOption { PeriodType = ItemPeriodType.Units, Period = n });
                }

                return result;
            }

            var specs = FindPeriods(text);
            if (specs.Count == 0)
            {
                var stats = ParseStats(text);
                var none = NoStat.IsMatch(text) && stats.Count == 0;
                if (stats.Count > 0 || none)
                    result.Add(new CapsuleTooltipOption { Stats = stats, NoStats = none, StatText = text });
                return result;
            }

            // One period list: the stats are the rest of the text, before or after it. Several:
            // each period list belongs to the stats written in front of it.
            var parts = new List<(IList<(ItemPeriodType, ushort)> Periods, string StatText)>();
            if (specs.Count == 1)
            {
                var s = specs[0];
                parts.Add((s.Periods, text.Substring(0, s.Start) + " " + text.Substring(s.End)));
            }
            else
            {
                var prev = 0;
                foreach (var s in specs)
                {
                    parts.Add((s.Periods, text.Substring(prev, s.Start - prev)));
                    prev = s.End;
                }

                var rest = text.Substring(prev);
                if (ParseStats(rest).Count > 0)
                    parts[parts.Count - 1] = (parts[parts.Count - 1].Periods, parts[parts.Count - 1].StatText + " " + rest);
            }

            foreach (var (periods, statText) in parts)
            {
                var stats = ParseStats(statText);
                var none = NoStat.IsMatch(statText) && stats.Count == 0;
                foreach (var (type, period) in periods)
                {
                    result.Add(new CapsuleTooltipOption
                    {
                        PeriodType = type, Period = period, Stats = stats, NoStats = none, StatText = Clean(statText)
                    });
                }
            }

            return result;
        }

        /// <summary>Reads "+N" stats, e.g. "all weapon attack +12%(add exp +5%)" = {atk+12, exp+5}.</summary>
        public static IReadOnlyCollection<CapsuleStat> ParseStats(string text)
        {
            var set = new HashSet<CapsuleStat>();
            foreach (Match m in Term.Matches((text ?? "").ToLowerInvariant()))
            {
                if (int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    set.Add(new CapsuleStat(CanonicalName(m.Groups[1].Value), value));
            }

            return set.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Value).ToArray();
        }

        /// <summary>A stable key for a stat set ("atk+10,exp+5"), "" for none.</summary>
        public static string Key(IEnumerable<CapsuleStat> stats) =>
            string.Join(",", stats.Distinct().OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Value));

        private static string CanonicalName(string name)
        {
            name = name.Trim(' ', '.', '(').Trim();
            foreach (var (pattern, canonical) in Canonical)
            {
                var m = pattern.Match(" " + name);
                if (m.Success)
                    return canonical ?? Regex.Replace(m.Groups[1].Value, @"\s+", "");
            }

            return Regex.Replace(name, @"\s+", "");
        }

        private static string Clean(string raw) =>
            Regex.Replace((raw ?? "").Replace("{N}", " ").Replace("{n}", " "), @"\s+", " ").Trim();

        private sealed class PeriodSpec
        {
            public int Start;
            public int End;
            public IList<(ItemPeriodType, ushort)> Periods;
        }

        private static List<PeriodSpec> FindPeriods(string text)
        {
            var specs = new List<PeriodSpec>();
            bool Free(int start, int end) => specs.All(s => end <= s.Start || start >= s.End);

            foreach (Match m in SlashDays.Matches(text))
            {
                var periods = Regex.Split(m.Groups[1].Value, @"\s*/\s*")
                    .Select(x => ushort.TryParse(x, out var p) ? p : (ushort)0)
                    .Where(p => p > 0)
                    .Select(p => (ItemPeriodType.Days, p))
                    .ToList();
                if (periods.Count > 0)
                    specs.Add(new PeriodSpec { Start = m.Index, End = m.Index + m.Length, Periods = periods });
            }

            foreach (Match m in ParenPeriod.Matches(text))
            {
                if (!Free(m.Index, m.Index + m.Length))
                    continue;
                var content = m.Groups[1].Value;
                var unit = content.IndexOf("day", StringComparison.OrdinalIgnoreCase) >= 0
                    ? ItemPeriodType.Days
                    : ItemPeriodType.Hours;
                var periods = ParsePeriodList(content, unit);
                if (periods.Count > 0)
                    specs.Add(new PeriodSpec { Start = m.Index, End = m.Index + m.Length, Periods = periods });
            }

            foreach (Match m in BarePeriod.Matches(text))
            {
                if (!Free(m.Index, m.Index + m.Length))
                    continue;
                IList<(ItemPeriodType, ushort)> periods;
                if (m.Groups[1].Success)
                {
                    if (!ushort.TryParse(m.Groups[1].Value, out var p) || p == 0)
                        continue;
                    periods = new[] { (UnitOf(m.Groups[2].Value), p) };
                }
                else
                {
                    periods = new[] { (ItemPeriodType.None, (ushort)0) };
                }

                specs.Add(new PeriodSpec { Start = m.Index, End = m.Index + m.Length, Periods = periods });
            }

            return specs.OrderBy(s => s.Start).ToList();
        }

        /// <summary>"3, 10, 35Day" / "1Day, Permanent" / "1, 7, 30Day/Permanent": bare numbers take the next unit.</summary>
        private static IList<(ItemPeriodType, ushort)> ParsePeriodList(string content, ItemPeriodType defaultUnit)
        {
            var result = new List<(ItemPeriodType, ushort)>();
            var pending = new List<ushort>();
            foreach (Match m in PeriodToken.Matches(content))
            {
                if (m.Groups[3].Success)
                {
                    result.AddRange(pending.Select(p => (defaultUnit, p)));
                    pending.Clear();
                    result.Add((ItemPeriodType.None, 0));
                    continue;
                }

                if (!ushort.TryParse(m.Groups[1].Value, out var n))
                    continue;
                if (m.Groups[2].Success)
                {
                    var unit = UnitOf(m.Groups[2].Value);
                    result.AddRange(pending.Select(p => (unit, p)));
                    pending.Clear();
                    result.Add((unit, n));
                }
                else
                {
                    pending.Add(n);
                }
            }

            result.AddRange(pending.Select(p => (defaultUnit, p)));
            return result.Where(x => x.Item1 == ItemPeriodType.None || x.Item2 > 0).Distinct().ToList();
        }

        private static ItemPeriodType UnitOf(string unit) =>
            unit.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? ItemPeriodType.Hours : ItemPeriodType.Days;
    }
}
