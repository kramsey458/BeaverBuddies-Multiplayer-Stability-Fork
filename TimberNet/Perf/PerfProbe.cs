using System;
using System.Diagnostics;
using System.Threading;

namespace TimberNet.Perf
{
    /// <summary>
    /// Measures where a frame's time goes, for the frame rate log. It only observes: it reads clocks and counters,
    /// writes into buffers it allocated up front, and never touches anything the simulation reads. Every measured
    /// point starts with one read of <see cref="Enabled"/>, so with the log off the cost is a predictable branch.
    ///
    /// Time is recorded per slot (see <see cref="PerfSlot"/>) as exclusive time, and only on the game thread; other
    /// threads are ignored, except for the message and byte counters. A frame is the time between two calls of
    /// <see cref="OnFrame"/>, which the game makes once per frame. Anything that goes wrong here switches the probe
    /// off instead of reaching the game.
    /// </summary>
    public static class PerfProbe
    {
        /// <summary>True while a log is being written.</summary>
        public static volatile bool Enabled;

        /// <summary>Replaces the clock. For tests only; the values are in <see cref="Stopwatch"/> ticks.</summary>
        public static Func<long>? TestClock;

        /// <summary>Why the probe switched itself off, if it did.</summary>
        public static string? LastFailure { get; private set; }

        const int MaxDepth = 32;
        static readonly int[] stackSlot = new int[MaxDepth];
        static readonly long[] stackStart = new long[MaxDepth];
        static readonly long[] stackChild = new long[MaxDepth];
        static int depth, generation, mainThreadId;

        static readonly long[] frameSelf = new long[PerfColumns.SlotCount];
        static readonly double[] row = new double[PerfColumns.Count];
        static readonly double[] window = new double[PerfColumns.Count];
        static int windowFrames;

        static PerfRing? ring;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long epochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        static double thresholdMs;
        static int summaryTicks, nextSummaryTick, lastTick;
        static long lastFrameTs;
        static int frameNo, lastGc;
        static long lastMemory;

        static int frameTicks, frameBuckets, frameWaitFrames;
        static double frameWaitMs;
        static bool waiting;
        static int waitTick, waitFrames, lastWaitFrameNo;
        static long waitStart;

        static long messagesOut, bytesOut, messagesIn, bytesIn;

        static long Now()
        {
            Func<long>? clock = TestClock;
            return clock != null ? clock() : Stopwatch.GetTimestamp();
        }

        // ---- lifecycle ----

        /// <param name="target">Where finished rows go.</param>
        /// <param name="gameThreadId">The managed id of the game thread: the only one whose time is recorded.</param>
        /// <param name="thresholdMilliseconds">A frame this long or longer gets a row of its own.</param>
        /// <param name="summaryEveryTicks">A summary row is written each time the game tick advances this far.</param>
        public static void Start(PerfRing target, int gameThreadId, double thresholdMilliseconds, int summaryEveryTicks)
        {
            Stop();
            ring = target;
            mainThreadId = gameThreadId;
            thresholdMs = thresholdMilliseconds;
            summaryTicks = Math.Max(1, summaryEveryTicks);
            depth = 0; generation++;
            Array.Clear(frameSelf, 0, frameSelf.Length);
            Array.Clear(window, 0, window.Length);
            windowFrames = 0;
            frameNo = 0; lastFrameTs = 0; lastTick = 0; nextSummaryTick = 0;
            ResetFrame();
            waiting = false; waitFrames = 0; lastWaitFrameNo = -1;
            Interlocked.Exchange(ref messagesOut, 0); Interlocked.Exchange(ref bytesOut, 0);
            Interlocked.Exchange(ref messagesIn, 0); Interlocked.Exchange(ref bytesIn, 0);
            LastFailure = null;
            Enabled = true;
        }

        /// <summary>Writes the summary of the frames not yet summarized, and stops.</summary>
        public static void Stop()
        {
            if (Enabled)
            {
                try { if (windowFrames > 0) EmitSummary(); }
                catch (Exception e) { LastFailure = e.Message; }
            }
            Enabled = false;
            ring = null;
        }

        static void Fail(Exception e)
        {
            LastFailure = e.ToString();
            Enabled = false;
        }

        // ---- scopes ----

        /// <summary>Starts timing a slot. Pass the result to <see cref="End"/>. Zero, and free, when the log is off.</summary>
        public static long Begin(PerfSlot slot)
        {
            if (!Enabled) return 0;
            return BeginCore((int)slot);
        }

        /// <summary>Stops the timing that <see cref="Begin"/> started.</summary>
        public static void End(long token)
        {
            if (token != 0) EndCore(token);
        }

        static long BeginCore(int slot)
        {
            try
            {
                if (Environment.CurrentManagedThreadId != mainThreadId) return 0;
                int d = depth;
                if (d >= MaxDepth) return 0;
                stackSlot[d] = slot;
                stackChild[d] = 0;
                stackStart[d] = Now();
                depth = d + 1;
                return ((long)generation << 32) | (uint)(d + 1);
            }
            catch (Exception e) { Fail(e); return 0; }
        }

        static void EndCore(long token)
        {
            try
            {
                if ((int)(token >> 32) != generation) return;
                int level = (int)(token & 0xFFFFFFFF);
                if (level > depth) return;
                long now = Now();
                // A scope inside this one that never ended (an exception skipped it) is closed here.
                while (depth >= level)
                {
                    int d = depth - 1;
                    long elapsed = now - stackStart[d];
                    long self = elapsed - stackChild[d];
                    if (self < 0) self = 0;
                    frameSelf[stackSlot[d]] += self;
                    depth = d;
                    if (d > 0) stackChild[d - 1] += elapsed;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        // ---- counters ----

        /// <summary>The loop that ticks the simulation took one step (a bucket, or the replay service's turn).</summary>
        public static void NoteBucket()
        {
            if (Enabled) frameBuckets++;
        }

        /// <summary>A message went out. Callable from any thread.</summary>
        public static void CountSent(int bytes)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref messagesOut);
            Interlocked.Add(ref bytesOut, bytes);
        }

        /// <summary>A message arrived. Callable from any thread.</summary>
        public static void CountReceived(int bytes)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref messagesIn);
            Interlocked.Add(ref bytesIn, bytes);
        }

        // ---- waiting for the other player ----

        /// <summary>
        /// The game wanted to start a tick and could not, because the events for it have not arrived. Called each time
        /// that happens; the first call for a tick starts the clock.
        /// </summary>
        public static void NoteWaiting(int tick)
        {
            if (!Enabled) return;
            try
            {
                if (!waiting || waitTick != tick)
                {
                    waiting = true; waitTick = tick; waitStart = Now(); waitFrames = 0; lastWaitFrameNo = -1;
                }
                if (lastWaitFrameNo != frameNo)
                {
                    lastWaitFrameNo = frameNo;
                    waitFrames++;
                    frameWaitFrames++;
                }
            }
            catch (Exception e) { Fail(e); }
        }

        /// <summary>A tick has started. Ends a wait, if there was one, and writes it up if it was long.</summary>
        public static void NoteTickStart(int tick)
        {
            if (!Enabled) return;
            try
            {
                frameTicks++;
                if (!waiting) return;
                waiting = false;
                double ms = (Now() - waitStart) * msPerTick;
                frameWaitMs += ms;
                if (ms < thresholdMs) return;
                double[] r = row;
                Array.Clear(r, 0, r.Length);
                r[PerfColumns.Type] = PerfColumns.WaitRow;
                r[PerfColumns.Frame] = frameNo;
                r[PerfColumns.Tick] = tick;
                r[PerfColumns.UtcMs] = UnixMs();
                r[PerfColumns.Waiting] = waitFrames;
                r[PerfColumns.WaitMs] = ms;
                ring?.TryPush(r);
            }
            catch (Exception e) { Fail(e); }
        }

        // ---- frames ----

        /// <summary>
        /// Called once per frame by the game, at the same place every time. Ends the previous frame: writes a row for
        /// it if it was long, adds it to the running summary, and starts the next one.
        /// </summary>
        public static void OnFrame(int tick, float speed, float targetSpeed, int ticksBehind, int pacePercent, bool holding, bool focused)
        {
            if (!Enabled) return;
            try { Frame(tick, speed, targetSpeed, ticksBehind, pacePercent, holding, focused); }
            catch (Exception e) { Fail(e); }
        }

        static void Frame(int tick, float speed, float targetSpeed, int ticksBehind, int pacePercent, bool holding, bool focused)
        {
            long start = Now();
            if (lastFrameTs == 0)
            {
                // The first call only sets the clocks.
                lastFrameTs = start;
                lastGc = GC.CollectionCount(0);
                lastMemory = GC.GetTotalMemory(false);
                nextSummaryTick = tick + summaryTicks;
                lastTick = tick;
                ResetFrame();
                return;
            }

            double frameMs = (start - lastFrameTs) * msPerTick;
            lastFrameTs = start;
            lastTick = tick;
            frameNo++;
            // A wait that was not asked about this frame was interrupted (a pause, say), not a wait for anyone.
            if (waiting && lastWaitFrameNo != frameNo - 1) waiting = false;

            int gc = GC.CollectionCount(0);
            long memory = GC.GetTotalMemory(false);

            double[] r = row;
            Array.Clear(r, 0, r.Length);
            r[PerfColumns.Type] = PerfColumns.FrameRow;
            r[PerfColumns.Frame] = frameNo;
            r[PerfColumns.Tick] = tick;
            r[PerfColumns.UtcMs] = UnixMs();
            r[PerfColumns.Frames] = 1;
            r[PerfColumns.FrameMs] = frameMs;
            r[PerfColumns.MaxFrameMs] = frameMs;
            r[PerfColumns.Ticks] = frameTicks;
            r[PerfColumns.Buckets] = frameBuckets;
            r[PerfColumns.Waiting] = frameWaitFrames;
            r[PerfColumns.WaitMs] = frameWaitMs;
            r[PerfColumns.Speed] = speed;
            r[PerfColumns.Target] = targetSpeed;
            r[PerfColumns.Behind] = ticksBehind;
            r[PerfColumns.PacePct] = pacePercent;
            r[PerfColumns.Hold] = holding ? 1 : 0;
            r[PerfColumns.Focused] = focused ? 1 : 0;
            double accounted = 0;
            for (int i = 0; i < PerfColumns.SlotCount; i++)
            {
                double ms = frameSelf[i] * msPerTick;
                r[PerfColumns.SlotBase + i] = ms;
                accounted += ms;
            }
            r[PerfColumns.Saving] = frameSelf[(int)PerfSlot.Save] > 0 ? 1 : 0;
            r[PerfColumns.OtherMs] = Math.Max(0, frameMs - accounted);
            r[PerfColumns.GcDelta] = gc - lastGc;
            r[PerfColumns.HeapMB] = memory / 1048576.0;
            r[PerfColumns.AllocKB] = (memory - lastMemory) / 1024.0;
            r[PerfColumns.MsgOut] = Interlocked.Exchange(ref messagesOut, 0);
            r[PerfColumns.BytesOut] = Interlocked.Exchange(ref bytesOut, 0);
            r[PerfColumns.MsgIn] = Interlocked.Exchange(ref messagesIn, 0);
            r[PerfColumns.BytesIn] = Interlocked.Exchange(ref bytesIn, 0);
            r[PerfColumns.Dropped] = ring?.Dropped ?? 0;
            lastGc = gc;
            lastMemory = memory;
            ResetFrame();

            Accumulate(r);
            // What this call itself cost, so the log shows whether it is a problem of its own making.
            r[PerfColumns.ProbeUs] = (Now() - start) * msPerTick * 1000;
            window[PerfColumns.ProbeUs] += r[PerfColumns.ProbeUs];
            if (frameMs >= thresholdMs) ring?.TryPush(r);
            if (tick >= nextSummaryTick)
            {
                EmitSummary();
                nextSummaryTick = tick + summaryTicks;
            }
        }

        static void ResetFrame()
        {
            Array.Clear(frameSelf, 0, frameSelf.Length);
            depth = 0; generation++;
            frameTicks = 0; frameBuckets = 0; frameWaitFrames = 0; frameWaitMs = 0;
        }

        static void Accumulate(double[] r)
        {
            for (int c = 0; c < PerfColumns.Count; c++)
            {
                switch (PerfColumns.Aggregates[c])
                {
                    case PerfAggregate.Sum:
                    case PerfAggregate.Mean:
                        window[c] += r[c];
                        break;
                    case PerfAggregate.SumPositive:
                        if (r[c] > 0) window[c] += r[c];
                        break;
                    case PerfAggregate.Max:
                        if (windowFrames == 0 || r[c] > window[c]) window[c] = r[c];
                        break;
                    default:
                        window[c] = r[c];
                        break;
                }
            }
            windowFrames++;
        }

        static void EmitSummary()
        {
            if (windowFrames == 0) return;
            double[] r = row;
            Array.Copy(window, r, r.Length);
            for (int c = 0; c < PerfColumns.Count; c++)
                if (PerfColumns.Aggregates[c] == PerfAggregate.Mean) r[c] /= windowFrames;
            r[PerfColumns.Type] = PerfColumns.SummaryRow;
            r[PerfColumns.Dropped] = ring?.Dropped ?? 0;
            ring?.TryPush(r);
            Array.Clear(window, 0, window.Length);
            windowFrames = 0;
        }

        static double UnixMs()
        {
            Func<long>? clock = TestClock;
            if (clock != null) return 1700000000000.0 + clock() * msPerTick;
            return (DateTime.UtcNow.Ticks - epochTicks) / 10000.0;
        }
    }
}
