using System;
using System.Collections.Generic;
using TimberNet.Perf;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// What Unity itself can say about a frame, for the frame rate log: draw calls and allocation counters from its profiler, the
    /// main thread's wait for the graphics card from its frame timing, and the sizes of its memory pools. Each source is optional:
    /// a release build may not provide it, so each says whether it worked (in the header and again at the end of the file) and a
    /// source that does not work is simply left at zero.
    /// </summary>
    internal static class PerfExtras
    {
        struct Counter
        {
            public string Name;
            public ProfilerRecorder Recorder;
            public long Largest;
        }

        static readonly List<Counter> counters = new List<Counter>();
        static readonly FrameTiming[] timings = new FrameTiming[1];
        static bool frameTimingOn, frameTimingSeen;
        static int frames;

        // The order is the order of the per-frame extra columns: prGcBytes, prGcCount, prDraw, prSetPass, prBatches, prTris.
        static readonly (ProfilerCategory Category, string Name)[] wanted =
        {
            (ProfilerCategory.Memory, "GC Allocated In Frame"),
            (ProfilerCategory.Memory, "GC Allocation In Frame Count"),
            (ProfilerCategory.Render, "Draw Calls Count"),
            (ProfilerCategory.Render, "SetPass Calls Count"),
            (ProfilerCategory.Render, "Batches Count"),
            (ProfilerCategory.Render, "Triangles Count"),
        };

        internal static void Start()
        {
            Stop();
            frames = 0;
            foreach (var (category, name) in wanted)
            {
                var counter = new Counter { Name = name };
                try { counter.Recorder = ProfilerRecorder.StartNew(category, name); }
                catch (Exception) { }
                counters.Add(counter);
            }
            try { frameTimingOn = FrameTimingManager.IsFeatureEnabled(); }
            catch (Exception) { frameTimingOn = false; }
            frameTimingSeen = false;
            PerfProbe.HeavySampler = Heavy;
        }

        internal static void Stop()
        {
            foreach (Counter counter in counters)
            {
                try { if (counter.Recorder.Valid) counter.Recorder.Dispose(); } catch (Exception) { }
            }
            counters.Clear();
            PerfProbe.HeavySampler = null;
        }

        /// <summary>Reads this frame's values into the probe. Called once per frame, just before the frame is closed.</summary>
        internal static void Sample()
        {
            frames++;
            double[] extra = PerfProbe.Extra;
            try
            {
                for (int i = 0; i < counters.Count; i++)
                {
                    Counter counter = counters[i];
                    if (!counter.Recorder.Valid) { extra[i] = 0; continue; }
                    long value = counter.Recorder.LastValue;
                    extra[i] = value;
                    if (value > counter.Largest) { counter.Largest = value; counters[i] = counter; }
                }
            }
            catch (Exception) { }
            if (!frameTimingOn) return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    FrameTiming t = timings[0];
                    extra[6] = t.cpuFrameTime; extra[7] = t.cpuMainThreadFrameTime; extra[8] = t.cpuRenderThreadFrameTime;
                    extra[9] = t.gpuFrameTime; extra[10] = t.cpuMainThreadPresentWaitTime;
                    if (t.cpuFrameTime > 0 || t.gpuFrameTime > 0) frameTimingSeen = true;
                }
            }
            catch (Exception) { frameTimingOn = false; }
        }

        /// <summary>The sizes of Unity's memory pools, read only when a row is written: monoHeapMB, monoUsedMB, nativeMB, workingMB.</summary>
        static void Heavy(double[] extra)
        {
            int at = PerfColumns.ExtraPerFrameCount;
            try
            {
                extra[at] = Profiler.GetMonoHeapSizeLong() / 1048576.0;
                extra[at + 1] = Profiler.GetMonoUsedSizeLong() / 1048576.0;
                extra[at + 2] = Profiler.GetTotalAllocatedMemoryLong() / 1048576.0;
                extra[at + 3] = Environment.WorkingSet / 1048576.0;
            }
            catch (Exception) { }
        }

        /// <summary>Says which sources are supposed to work, for the header.</summary>
        internal static List<string> StartLines()
        {
            var lines = new List<string>
            {
                "# capability|frameTiming|" + (frameTimingOn ? "enabled" : "off (the game's player settings leave Unity's frame timing off, or it is unavailable)"),
            };
            foreach (Counter counter in counters)
                lines.Add("# capability|profilerRecorder|" + counter.Name + "|" + (counter.Recorder.Valid ? "started" : "not available"));
            return lines;
        }

        /// <summary>Says which sources actually produced a value, for the end of the file. Read on the game thread, before the log stops.</summary>
        internal static List<string> FinalLines()
        {
            var lines = new List<string>
            {
                "# capability-final|frameTiming|" + (frameTimingSeen ? "produced values" : "never produced a value"),
            };
            foreach (Counter counter in counters)
                lines.Add("# capability-final|profilerRecorder|" + counter.Name + "|" +
                    (counter.Largest > 0 ? "produced values, largest " + counter.Largest : "never produced a value") + "|frames=" + frames);
            return lines;
        }
    }
}
