using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenS4L.LoadBot
{
    /// <summary>
    /// Latency samples per named step, shared by every bot of a run. Steps are timed on the bot
    /// side from just before a request is sent to when its reply arrives, so they include the
    /// network round trip. The report gives count, failures and p50/p95/p99/max per step.
    /// </summary>
    public static class Metrics
    {
        // Report order: the login path first, then rooms, then chat, then the keep-alive ping.
        public static readonly string[] StepOrder =
        {
            "auth.connect", "auth.login", "auth.serverlist",
            "game.connect", "game.login", "game.character", "game.channel",
            "room.make", "room.enter", "room.ready", "room.loading", "room.start",
            "chat.connect", "chat.login",
            "ping.auth", "ping.game", "ping.chat"
        };

        private static readonly ConcurrentDictionary<string, ConcurrentBag<double>> s_samples =
            new ConcurrentDictionary<string, ConcurrentBag<double>>();

        private static readonly ConcurrentDictionary<string, int> s_failures =
            new ConcurrentDictionary<string, int>();

        public static void Record(string step, TimeSpan elapsed)
        {
            s_samples.GetOrAdd(step, _ => new ConcurrentBag<double>()).Add(elapsed.TotalMilliseconds);
        }

        public static void Fail(string step)
        {
            s_failures.AddOrUpdate(step, 1, (_, n) => n + 1);
        }

        /// <summary>Time <paramref name="step"/> from now until the returned scope is completed.</summary>
        public static StepTimer Start(string step) => new StepTimer(step, Stopwatch.GetTimestamp());

        public readonly struct StepTimer
        {
            private readonly long _start;

            public string Step { get; }

            public StepTimer(string step, long start)
            {
                Step = step;
                _start = start;
            }

            public TimeSpan Done()
            {
                var elapsed = Stopwatch.GetElapsedTime(_start);
                Record(Step, elapsed);
                return elapsed;
            }

            public void Failed() => Fail(Step);
        }

        public class StepStats
        {
            public string Step { get; set; }
            public int Count { get; set; }
            public int Failures { get; set; }
            public double P50 { get; set; }
            public double P95 { get; set; }
            public double P99 { get; set; }
            public double Max { get; set; }
        }

        public static List<StepStats> Snapshot()
        {
            var names = s_samples.Keys.Concat(s_failures.Keys).Distinct()
                .OrderBy(n => Array.IndexOf(StepOrder, n) is var i && i >= 0 ? i : int.MaxValue)
                .ThenBy(n => n, StringComparer.Ordinal);

            var result = new List<StepStats>();
            foreach (var name in names)
            {
                var values = s_samples.TryGetValue(name, out var bag) ? bag.ToArray() : Array.Empty<double>();
                Array.Sort(values);
                result.Add(new StepStats
                {
                    Step = name,
                    Count = values.Length,
                    Failures = s_failures.TryGetValue(name, out var f) ? f : 0,
                    P50 = Percentile(values, 0.50),
                    P95 = Percentile(values, 0.95),
                    P99 = Percentile(values, 0.99),
                    Max = values.Length > 0 ? values[^1] : 0
                });
            }

            return result;
        }

        /// <summary>Nearest-rank percentile of an ascending array (0 when empty).</summary>
        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0)
                return 0;

            var rank = (int)Math.Ceiling(p * sorted.Length);
            return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
        }

        public static string FormatTable(IReadOnlyList<StepStats> stats)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{"step",-16} {"count",7} {"fail",5} {"p50 ms",9} {"p95 ms",9} {"p99 ms",9} {"max ms",9}");
            foreach (var s in stats)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-16} {1,7} {2,5} {3,9:0.0} {4,9:0.0} {5,9:0.0} {6,9:0.0}",
                    s.Step, s.Count, s.Failures, s.P50, s.P95, s.P99, s.Max));
            }

            return sb.ToString();
        }

        public static void WriteJson(string path, object run, IReadOnlyList<StepStats> stats)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new { Run = run, Steps = stats },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
