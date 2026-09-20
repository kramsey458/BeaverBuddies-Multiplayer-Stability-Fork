using System;
using System.Diagnostics;
using System.Threading;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// What the probe cannot time for itself, read from the game once per frame and handed in at the end of it.
    /// A plain value so <see cref="PerfProbe"/> stays free of Unity and game types.
    /// </summary>
    public struct PerfFrameState
    {
        public int Tick;
        public int TicksBehind;
        public float Speed, TargetSpeed;
        public int HostPacingPercent, FpsPacingPercent;
        public bool Holding, Saving;
    }

    /*
     * Measures where a frame's time went, and writes a row when one is slow.
     *
     * It observes and nothing else. It never calls the game's random number generator, never records or
     * replays an event, never touches the desync traces and never changes anything the simulation reads, so
     * turning it on cannot change what either player simulates.
     *
     * Nothing here allocates while a session runs: the rows go into a ring that was allocated up front, the
     * numbers are struct fields, and no text is produced until another thread writes the file. Every entry
     * point begins by reading one static bool, so with logging off the cost is a predictable branch.
     *
     * Times come from Stopwatch, never from UnityEngine.Time: the game's clock is detoured by this mod to
     * return simulation time, which is not what a frame took.
     */
    public static class PerfProbe
    {
        public enum Span
        {
            Read = 0,
            Replay = 1,
            Send = 2,
            Hash = 3,
            IoUpdate = 4,
            SteamPump = 5,
            Trace = 6,
            WaitCheck = 7,
        }

        const int SpanCount = 8;

        // A summary is due every so many ticks, but a paused game stops ticking, so it is also due after
        // this long, to keep a baseline while nothing is happening.
        const double SummaryFallbackSeconds = 10;

        // Read once per call site and nothing else, so keep it a plain field rather than a property.
        public static bool Enabled;

        static readonly double MsPerTimestamp = 1000.0 / Stopwatch.Frequency;
        static readonly float[] frameSpans = new float[SpanCount];
        static readonly float[] windowSpans = new float[SpanCount];

        static PerfRing ring;
        static int mainThreadId;
        static float spikeMs = 50;
        static int summaryTicks = 100;

        // This frame.
        static long frameStartedAt;
        static bool inFrame;
        static int frameTicksDone, frameSendBytes, frameSendCount, frameSpeedChanges;
        static bool frameWaited;

        // Since the last summary row.
        static int windowFrames, windowTicksDone, windowWaitFrames, windowSendBytes, windowSendCount, windowSpeedChanges;
        static float windowFrameMs, windowMaxFrameMs;
        static int windowGc0, windowGc1, windowGc2;
        static int windowStartTick;
        static long windowStartedAt;

        static int lastGc0, lastGc1, lastGc2;

        /// <summary>Starts measuring into <paramref name="target"/>. Everything is reset first.</summary>
        public static void StartSession(PerfRing target, float spikeThresholdMs, int ticksPerSummary)
        {
            ring = target ?? throw new ArgumentNullException(nameof(target));
            spikeMs = spikeThresholdMs > 0 ? spikeThresholdMs : 50;
            summaryTicks = ticksPerSummary > 0 ? ticksPerSummary : 100;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            ResetAll();
            lastGc0 = GC.CollectionCount(0);
            lastGc1 = GC.CollectionCount(1);
            lastGc2 = GC.CollectionCount(2);
            windowStartedAt = Stopwatch.GetTimestamp();
            Enabled = true;
        }

        public static void StopSession()
        {
            Enabled = false;
            ring = null;
            ResetAll();
        }

        static void ResetAll()
        {
            Array.Clear(frameSpans, 0, SpanCount);
            Array.Clear(windowSpans, 0, SpanCount);
            frameStartedAt = 0;
            inFrame = false;
            frameTicksDone = frameSendBytes = frameSendCount = frameSpeedChanges = 0;
            frameWaited = false;
            windowFrames = windowTicksDone = windowWaitFrames = 0;
            windowSendBytes = windowSendCount = windowSpeedChanges = 0;
            windowFrameMs = windowMaxFrameMs = 0;
            windowGc0 = windowGc1 = windowGc2 = 0;
            windowStartTick = 0;
        }

        // ---- timing a span ----

        /// <summary>Starts timing. Returns 0 when logging is off, which <see cref="End"/> then ignores.</summary>
        public static long Begin()
        {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void End(Span span, long started)
        {
            if (!Enabled || started == 0) return;
            // Some of these call sites are also reached from network threads, whose time is not this frame's.
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            frameSpans[(int)span] += (float)((Stopwatch.GetTimestamp() - started) * MsPerTimestamp);
        }

        // ---- counting ----

        /// <summary>Compressed bytes written to a connection. Only the game thread's writes hold up a frame.</summary>
        public static void CountSend(int bytes)
        {
            if (!Enabled) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            frameSendBytes += bytes;
            frameSendCount++;
        }

        public static void CountSpeedChange()
        {
            if (!Enabled) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId) return;
            frameSpeedChanges++;
        }

        public static void CountTick()
        {
            if (!Enabled) return;
            frameTicksDone++;
        }

        /// <summary>The tick loop stopped because the other player's events for the next tick had not arrived.</summary>
        public static void NoteWaitingForPeer()
        {
            if (!Enabled) return;
            frameWaited = true;
        }

        // ---- the frame ----

        public static void BeginFrame()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            if (inFrame)
            {
                // The previous frame never ended (an exception on the way out). Start again rather than
                // reporting one enormous frame.
                ClearFrame();
            }
            frameStartedAt = now;
            inFrame = true;
        }

        public static void EndFrame(in PerfFrameState state)
        {
            if (!Enabled || !inFrame) return;
            inFrame = false;
            long now = Stopwatch.GetTimestamp();
            float frameMs = (float)((now - frameStartedAt) * MsPerTimestamp);

            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            int d0 = gc0 - lastGc0, d1 = gc1 - lastGc1, d2 = gc2 - lastGc2;
            lastGc0 = gc0; lastGc1 = gc1; lastGc2 = gc2;

            if (windowFrames == 0) windowStartTick = state.Tick;
            windowFrames++;
            windowFrameMs += frameMs;
            if (frameMs > windowMaxFrameMs) windowMaxFrameMs = frameMs;
            windowTicksDone += frameTicksDone;
            if (frameWaited) windowWaitFrames++;
            windowSendBytes += frameSendBytes;
            windowSendCount += frameSendCount;
            windowSpeedChanges += frameSpeedChanges;
            windowGc0 += d0; windowGc1 += d1; windowGc2 += d2;
            for (int i = 0; i < SpanCount; i++) windowSpans[i] += frameSpans[i];

            if (frameMs >= spikeMs)
            {
                PerfSample spike = default;
                spike.Kind = PerfRowKind.Spike;
                spike.Tick = state.Tick;
                spike.Frames = 1;
                spike.FrameMs = frameMs;
                spike.MaxFrameMs = frameMs;
                spike.TicksDone = frameTicksDone;
                spike.WaitFrames = frameWaited ? 1 : 0;
                spike.TicksBehind = state.TicksBehind;
                spike.ReadMs = frameSpans[(int)Span.Read];
                spike.ReplayMs = frameSpans[(int)Span.Replay];
                spike.SendMs = frameSpans[(int)Span.Send];
                spike.HashMs = frameSpans[(int)Span.Hash];
                spike.IoUpdateMs = frameSpans[(int)Span.IoUpdate];
                spike.SteamPumpMs = frameSpans[(int)Span.SteamPump];
                spike.TraceMs = frameSpans[(int)Span.Trace];
                spike.WaitCheckMs = frameSpans[(int)Span.WaitCheck];
                spike.SendBytes = frameSendBytes;
                spike.SendCount = frameSendCount;
                spike.Gc0 = d0; spike.Gc1 = d1; spike.Gc2 = d2;
                spike.HeapBytes = GC.GetTotalMemory(false);
                Fill(ref spike, in state);
                spike.SpeedChanges = frameSpeedChanges;
                ring.Add(in spike);
            }

            ClearFrame();

            bool byTicks = summaryTicks > 0 && state.Tick - windowStartTick >= summaryTicks;
            bool byTime = (now - windowStartedAt) * MsPerTimestamp >= SummaryFallbackSeconds * 1000;
            if (byTicks || byTime) EmitSummary(state, now);
        }

        static void EmitSummary(in PerfFrameState state, long now)
        {
            PerfSample summary = default;
            summary.Kind = PerfRowKind.Summary;
            summary.Tick = state.Tick;
            summary.Frames = windowFrames;
            summary.FrameMs = windowFrameMs;
            summary.MaxFrameMs = windowMaxFrameMs;
            summary.TicksDone = windowTicksDone;
            summary.WaitFrames = windowWaitFrames;
            summary.TicksBehind = state.TicksBehind;
            summary.ReadMs = windowSpans[(int)Span.Read];
            summary.ReplayMs = windowSpans[(int)Span.Replay];
            summary.SendMs = windowSpans[(int)Span.Send];
            summary.HashMs = windowSpans[(int)Span.Hash];
            summary.IoUpdateMs = windowSpans[(int)Span.IoUpdate];
            summary.SteamPumpMs = windowSpans[(int)Span.SteamPump];
            summary.TraceMs = windowSpans[(int)Span.Trace];
            summary.WaitCheckMs = windowSpans[(int)Span.WaitCheck];
            summary.SendBytes = windowSendBytes;
            summary.SendCount = windowSendCount;
            summary.Gc0 = windowGc0; summary.Gc1 = windowGc1; summary.Gc2 = windowGc2;
            summary.HeapBytes = GC.GetTotalMemory(false);
            Fill(ref summary, in state);
            summary.SpeedChanges = windowSpeedChanges;
            ring.Add(in summary);

            Array.Clear(windowSpans, 0, SpanCount);
            windowFrames = windowTicksDone = windowWaitFrames = 0;
            windowSendBytes = windowSendCount = windowSpeedChanges = 0;
            windowFrameMs = windowMaxFrameMs = 0;
            windowGc0 = windowGc1 = windowGc2 = 0;
            windowStartTick = state.Tick;
            windowStartedAt = now;
        }

        static void Fill(ref PerfSample sample, in PerfFrameState state)
        {
            sample.Speed = state.Speed;
            sample.TargetSpeed = state.TargetSpeed;
            sample.HostPacingPercent = state.HostPacingPercent;
            sample.FpsPacingPercent = state.FpsPacingPercent;
            sample.Holding = state.Holding;
            sample.Saving = state.Saving;
        }

        static void ClearFrame()
        {
            Array.Clear(frameSpans, 0, SpanCount);
            frameTicksDone = frameSendBytes = frameSendCount = frameSpeedChanges = 0;
            frameWaited = false;
        }
    }
}
