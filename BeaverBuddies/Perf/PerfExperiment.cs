using System;
using UnityEngine.Scripting;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// One optional experiment, run once and only when its own setting is on: if the game is not collecting garbage incrementally,
    /// try to switch that on while it runs, and write down whether it worked. The frame rate log's slow frames before and after that
    /// tick then show whether the long pauses shrink. It changes how the game collects garbage, never what it simulates, and it
    /// does nothing at all if the game already collects incrementally.
    /// </summary>
    internal static class PerfExperiment
    {
        /// <summary>About nine minutes into a session at a normal speed.</summary>
        internal const int TriggerTick = 4000;

        /// <summary>Unity's default time slice for incremental collection, in nanoseconds (3 ms).</summary>
        const ulong TimeSliceNanoseconds = 3_000_000UL;

        static bool done;

        internal static void Reset() => done = false;

        internal static void OnFrame(int tick)
        {
            if (done || tick < TriggerTick || !Settings.PerfLogGcExperimentEnabled) return;
            done = true;
            Run(tick);
        }

        static void Run(int tick)
        {
            try
            {
                bool before = GarbageCollector.isIncremental;
                string state = Describe();
                PerfSession.Note(tick, "gc-experiment|before|" + state);
                if (before)
                {
                    PerfSession.Note(tick, "gc-experiment|nothing to change|the game already collects incrementally");
                    return;
                }
                GarbageCollector.incrementalTimeSliceNanoseconds = TimeSliceNanoseconds;
                PerfSession.Note(tick, "gc-experiment|after setting the time slice to " + TimeSliceNanoseconds + " ns|" + Describe());
                bool worked = GarbageCollector.isIncremental;
                PerfSession.Note(tick, "gc-experiment|result|" + (worked
                    ? "the game now collects incrementally; compare the collection pauses before and after this tick"
                    : "it still does not collect incrementally: this cannot be switched on while the game runs"));
            }
            catch (Exception error)
            {
                PerfSession.Note(tick, "gc-experiment|failed|" + error.GetType().Name + " " + error.Message);
            }
        }

        internal static string Describe() =>
            "mode=" + GarbageCollector.GCMode + " incremental=" + GarbageCollector.isIncremental +
            " timeSliceNs=" + GarbageCollector.incrementalTimeSliceNanoseconds;
    }
}
