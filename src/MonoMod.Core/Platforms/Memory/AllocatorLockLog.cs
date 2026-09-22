using System;
using System.Diagnostics;

namespace MonoMod.Core.Platforms.Memory
{
    /// <summary>
    /// Temporary diagnostics: records how long the allocator lock is waited for and held, so a stall can be
    /// attributed to a specific operation instead of inferred from stacks. Behaviour-neutral: it only reads
    /// timestamps around the existing acquire/release and writes a line when a threshold is exceeded.
    /// </summary>
    internal static class AllocatorLockLog
    {
        private static readonly long ThresholdTicks = Stopwatch.Frequency / 4; // 250 ms
        private static readonly string? logPath = Environment.GetEnvironmentVariable("MONOMOD_ALLOC_LOG");
        private static readonly long startTicks = Stopwatch.GetTimestamp();

        internal static IDisposable Measure(string operation, long waitStartTicks)
        {
            var entered = Stopwatch.GetTimestamp();
            var waited = entered - waitStartTicks;
            if (waited >= ThresholdTicks)
                Write($"waited {Ms(waited):F0}ms for the allocator lock ({operation})");
            return new Hold(operation, entered);
        }

        private sealed class Hold : IDisposable
        {
            private readonly string operation;
            private readonly long enteredTicks;
            private bool disposed;

            internal Hold(string operation, long enteredTicks)
            {
                this.operation = operation;
                this.enteredTicks = enteredTicks;
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                var held = Stopwatch.GetTimestamp() - enteredTicks;
                if (held >= ThresholdTicks)
                    Write($"held the allocator lock {Ms(held):F0}ms ({operation})");
            }
        }

        private static double Ms(long ticks) => (double)ticks / Stopwatch.Frequency * 1000;
        private static double Elapsed() => (double)(Stopwatch.GetTimestamp() - startTicks) / Stopwatch.Frequency;

        private static void Write(string line)
        {
            try
            {
                var text = $"{Elapsed():F1}s {line}";
                if (logPath is { Length: > 0 })
                    System.IO.File.AppendAllText(logPath, text + Environment.NewLine);
                else
                    Console.Error.WriteLine(text);
            }
            catch
            {
                // diagnostics must never affect the measured process
            }
        }
    }
}
