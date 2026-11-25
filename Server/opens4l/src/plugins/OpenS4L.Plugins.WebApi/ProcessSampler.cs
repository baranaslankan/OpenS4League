using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenS4L.Plugins.WebApi.Models;

namespace OpenS4L.Plugins.WebApi
{
    /// <summary>
    /// Process figures for /metrics. CPU is the share of all cores the process used between two
    /// calls (the first call measures from process start), so a dashboard polling every few
    /// seconds gets a current value rather than a lifetime average.
    /// </summary>
    public static class ProcessSampler
    {
        private static readonly object s_lock = new object();
        private static long s_lastWall;
        private static TimeSpan s_lastCpu;

        public static MetricsProcessDto Sample()
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();

            double cpuPercent;
            lock (s_lock)
            {
                TimeSpan wall;
                TimeSpan used;
                if (s_lastWall == 0)
                {
                    wall = DateTime.Now - process.StartTime;
                    used = cpu;
                }
                else
                {
                    wall = Stopwatch.GetElapsedTime(s_lastWall, now);
                    used = cpu - s_lastCpu;
                }

                s_lastWall = now;
                s_lastCpu = cpu;
                cpuPercent = wall.TotalMilliseconds > 0
                    ? used.TotalMilliseconds / (wall.TotalMilliseconds * Environment.ProcessorCount) * 100
                    : 0;
            }

            return new MetricsProcessDto
            {
                CpuPercent = Math.Round(Math.Clamp(cpuPercent, 0, 100), 1),
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetMb = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                ManagedHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                Threads = process.Threads.Count,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString()
            };
        }
    }
}
