using System;
using System.Diagnostics;
using System.Threading;
using MonoMod.Logs;

namespace MonoMod.Core
{
    /// <summary>
    /// Temporary instrumentation: counts how often detour computation allocates near-range memory and how often
    /// recompilation rebuilds a detour, to attribute the macOS test-host stalls to allocation volume rather than
    /// to the allocator itself. Reports at most once per 5 seconds.
    /// </summary>
    internal static class NearAllocationCounters
    {
        private static long recompileCallbacks;
        private static long recompiledDetours;
        private static long detourInfos;
        private static long nearAllocations;
        private static long nearAllocationFailures;
        private static long nearAllocationTicks;
        private static long maxNearAllocationTicks;
        private static long lastReport;

        internal static void RecompileCallback()
        {
            Interlocked.Increment(ref recompileCallbacks);
            Report();
        }
        internal static void RecompiledDetour() => Interlocked.Increment(ref recompiledDetours);
        internal static void DetourInfo() => Interlocked.Increment(ref detourInfos);

        internal static void NearAllocation(long ticks, bool success)
        {
            Interlocked.Increment(ref nearAllocations);
            if (!success)
                Interlocked.Increment(ref nearAllocationFailures);
            Interlocked.Add(ref nearAllocationTicks, ticks);

            long previousMax;
            while ((previousMax = Volatile.Read(ref maxNearAllocationTicks)) < ticks
                && Interlocked.CompareExchange(ref maxNearAllocationTicks, ticks, previousMax) != previousMax)
            {
            }

            Report();
        }

        private static void Report()
        {
            var now = Stopwatch.GetTimestamp();
            var last = Volatile.Read(ref lastReport);
            if (now - last < Stopwatch.Frequency * 5)
                return;
            if (Interlocked.CompareExchange(ref lastReport, now, last) != last)
                return;

            var msPerTick = 1000.0 / Stopwatch.Frequency;
            var line =
                $"near-alloc counters: recompileCallbacks={Interlocked.Read(ref recompileCallbacks)} " +
                $"recompiledDetours={Interlocked.Read(ref recompiledDetours)} " +
                $"detourInfos={Interlocked.Read(ref detourInfos)} " +
                $"nearAllocations={Interlocked.Read(ref nearAllocations)} " +
                $"failures={Interlocked.Read(ref nearAllocationFailures)} " +
                $"totalMs={Interlocked.Read(ref nearAllocationTicks) * msPerTick:F0} " +
                $"maxMs={Volatile.Read(ref maxNearAllocationTicks) * msPerTick:F0}";

            var path = Environment.GetEnvironmentVariable("MONOMOD_NEAR_ALLOC_LOG");
            if (path is { Length: > 0 })
            {
                try { System.IO.File.AppendAllText(path, line + Environment.NewLine); }
                catch { /* instrumentation must never affect the measured process */ }
            }
            else
            {
                Console.Error.WriteLine(line);
            }
        }
    }
}
