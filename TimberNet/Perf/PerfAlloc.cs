using System;
using System.Diagnostics;
using System.Reflection;

namespace TimberNet.Perf
{
    /// <summary>
    /// How many bytes this program has allocated, as cheaply as the runtime allows. The frame rate log takes differences of it to
    /// say which sections allocate. Unity's Mono may or may not have a per-thread counter, so it is tried, checked against a known
    /// allocation, and otherwise the size of the managed heap is used (which only moves when the heap takes new blocks, so
    /// individual readings are coarse and only sums over many readings mean anything).
    /// </summary>
    public static class PerfAlloc
    {
        public const int ModeNone = 0, ModeThread = 1, ModeHeap = 2;

        /// <summary>Which counter <see cref="Read"/> uses.</summary>
        public static int Mode { get; private set; }

        public static string ModeName => Mode == ModeThread ? "GC.GetAllocatedBytesForCurrentThread" :
                                         Mode == ModeHeap ? "GC.GetTotalMemory(false)" : "none";

        static Func<long>? threadBytes;

        /// <summary>True when allocation can be measured at all.</summary>
        public static bool Enabled => Mode != ModeNone;

        /// <summary>Chooses the counter. Safe to call more than once. <paramref name="preferHeap"/> is for tests.</summary>
        public static void Init(bool preferHeap = false)
        {
            threadBytes = null;
            Mode = ModeNone;
            try
            {
                if (!preferHeap)
                {
                    MethodInfo? method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        var candidate = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                        long before = candidate();
                        // Allocate something that cannot be optimized away and see whether the counter noticed.
                        byte[] probe = new byte[64 * 1024];
                        probe[0] = 1;
                        long after = candidate();
                        GC.KeepAlive(probe);
                        if (after - before >= 60 * 1024) { threadBytes = candidate; Mode = ModeThread; return; }
                    }
                }
                GC.GetTotalMemory(false);
                Mode = ModeHeap;
            }
            catch (Exception)
            {
                threadBytes = null;
                Mode = ModeNone;
            }
        }

        /// <summary>Replaces the counter with one the caller moves by hand. For tests only. Null goes back to <see cref="Init"/>.</summary>
        public static void UseTestSource(Func<long>? source)
        {
            if (source == null) { Init(); return; }
            threadBytes = source;
            Mode = ModeThread;
        }

        /// <summary>The counter now, in bytes. Differences between two readings are what matters.</summary>
        public static long Read()
        {
            switch (Mode)
            {
                case ModeThread: return threadBytes!();
                case ModeHeap: return GC.GetTotalMemory(false);
                default: return 0;
            }
        }

        /// <summary>What one <see cref="Read"/> costs, in Stopwatch ticks, averaged over many.</summary>
        public static double MeasureReadTicks(int repeats = 2000)
        {
            if (Mode == ModeNone) return 0;
            long total = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < repeats; i++) total += Read();
            long end = Stopwatch.GetTimestamp();
            GC.KeepAlive(total.ToString());
            return (end - start) / (double)repeats;
        }
    }
}
