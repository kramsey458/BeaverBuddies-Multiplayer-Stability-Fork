using System;
using System.Diagnostics;
using System.Threading;

namespace TimberNet.Perf
{
    /// <summary>
    /// Measures where a frame's time goes, for the frame rate log. It only observes: it reads clocks and counters, writes into
    /// buffers it allocated up front, and never touches anything the simulation reads. Every measured point starts with one read of
    /// <see cref="Enabled"/>, so with the log off the cost is a predictable branch.
    ///
    /// Time is recorded per slot (see <see cref="PerfSlot"/>) as exclusive time, and only on the game thread; other threads are
    /// ignored, except for the message and byte counters. Allocation is recorded the same way for the slots that ask for it. A frame is
    /// the time between two calls of <see cref="OnFrame"/>, which the game makes once per frame. Anything that goes wrong here switches
    /// the probe off instead of reaching the game.
    /// </summary>
    public static class PerfProbe
    {
        /// <summary>True while a log is being written.</summary>
        public static volatile bool Enabled;

        /// <summary>Replaces the clock. For tests only; the values are in <see cref="Stopwatch"/> ticks.</summary>
        public static Func<long>? TestClock;

        /// <summary>Why the probe switched itself off, if it did.</summary>
        public static string? LastFailure { get; private set; }

        /// <summary>Values the game side writes before each <see cref="OnFrame"/>: first the per-frame extras, then the heavy ones.</summary>
        public static readonly double[] Extra = new double[PerfColumns.ExtraPerFrameCount + PerfColumns.ExtraHeavyCount];

        /// <summary>Fills the heavy extras. Called only when a row is about to be written.</summary>
        public static Action<double[]>? HeavySampler;

        /// <summary>What one timed scope costs, and one sampled pair (two clock and two allocation readings), in ticks. Set by <see cref="Calibrate"/>.</summary>
        public static double ScopePairTicks { get; set; }
        public static double SamplePairTicks { get; set; }

        public static bool OnGameThread => Environment.CurrentManagedThreadId == mainThreadId;

        const int MaxDepth = 32;
        static readonly int[] stackSlot = new int[MaxDepth];
        static readonly long[] stackStart = new long[MaxDepth];
        static readonly long[] stackChild = new long[MaxDepth];
        static readonly long[] stackAlloc = new long[MaxDepth];
        static readonly long[] stackChildAlloc = new long[MaxDepth];
        static int depth, generation, mainThreadId;

        static readonly long[] frameSelf = new long[PerfColumns.SlotCount];
        static readonly long[] frameAlloc = new long[PerfColumns.SlotCount];
        static readonly long[] counters = new long[PerfColumns.CounterCount];
        static readonly long[] phaseStart = new long[PerfColumns.PhaseCount];
        static readonly long[] framePhase = new long[PerfColumns.PhaseCount];
        static readonly double[] row = new double[PerfColumns.Count];
        static readonly double[] window = new double[PerfColumns.Count];
        static int windowFrames, windowNumber;

        static PerfRing? ring, profileRing;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long epochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        static double thresholdMs;
        static int summaryTicks, nextSummaryTick, lastTick;
        static long lastFrameTs;
        static int frameNo, lastGc;
        static long lastMemory, lastAllocSource;
        static long lastThreadCpu, lastProcessCpu;
        static ulong lastCycles;

        static int frameTicks, frameBuckets, frameWaitFrames, frameScopes, frameSampledPairs;
        static double frameWaitMs, frameParTickMs;
        static bool waiting;
        static int waitTick, waitFrames, lastWaitFrameNo;
        static long waitStart;

        static long messagesOut, bytesOut, messagesIn, bytesIn;

        static long Now()
        {
            Func<long>? clock = TestClock;
            return clock != null ? clock() : Stopwatch.GetTimestamp();
        }

        /// <summary>The probe's clock, for callers that time something themselves and report it with <see cref="AddNested"/>.</summary>
        public static long Timestamp() => Now();

        // ---- lifecycle ----

        /// <summary>Measures what the probe itself costs, so the log can say how much it distorts. Call before <see cref="Start"/>, on the game thread.</summary>
        public static void Calibrate()
        {
            try
            {
                int savedThread = mainThreadId;
                mainThreadId = Environment.CurrentManagedThreadId;
                Func<long>? savedClock = TestClock;
                TestClock = null;
                const int repeats = 4000; // a stable average is all that is needed, and a slow counter would otherwise cost the session a visible hitch
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) { long token = BeginCore((int)PerfSlot.Detail); EndCore(token); }
                ScopePairTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats;
                long sink = 0;
                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) sink += Stopwatch.GetTimestamp() + PerfAlloc.Read() + Stopwatch.GetTimestamp() + PerfAlloc.Read();
                SamplePairTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats;
                GC.KeepAlive(sink.ToString());
                TestClock = savedClock;
                mainThreadId = savedThread;
                ResetFrame();
                Array.Clear(frameSelf, 0, frameSelf.Length);
                Array.Clear(frameAlloc, 0, frameAlloc.Length);
            }
            catch (Exception e) { LastFailure = e.Message; }
        }

        /// <param name="target">Where finished rows go.</param>
        /// <param name="gameThreadId">The managed id of the game thread: the only one whose time is recorded.</param>
        /// <param name="thresholdMilliseconds">A frame this long or longer gets a row of its own.</param>
        /// <param name="summaryEveryTicks">A summary row is written each time the game tick advances this far.</param>
        /// <param name="profileTarget">Where the per-entity and per-singleton rows go, if wanted.</param>
        public static void Start(PerfRing target, int gameThreadId, double thresholdMilliseconds, int summaryEveryTicks, PerfRing? profileTarget = null)
        {
            Stop();
            ring = target;
            profileRing = profileTarget;
            mainThreadId = gameThreadId;
            thresholdMs = thresholdMilliseconds;
            summaryTicks = Math.Max(1, summaryEveryTicks);
            depth = 0; generation++;
            Array.Clear(frameSelf, 0, frameSelf.Length);
            Array.Clear(frameAlloc, 0, frameAlloc.Length);
            Array.Clear(counters, 0, counters.Length);
            Array.Clear(phaseStart, 0, phaseStart.Length);
            Array.Clear(framePhase, 0, framePhase.Length);
            Array.Clear(Extra, 0, Extra.Length);
            Array.Clear(window, 0, window.Length);
            windowFrames = 0; windowNumber = 0;
            frameNo = 0; lastFrameTs = 0; lastTick = 0; nextSummaryTick = 0;
            lastThreadCpu = 0; lastProcessCpu = 0; lastCycles = 0; lastAllocSource = 0;
            ResetFrame();
            PerfProfile.Reset();
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
            profileRing = null;
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
                stackChildAlloc[d] = 0;
                stackAlloc[d] = PerfColumns.SlotTracksAlloc[slot] && PerfAlloc.Enabled ? PerfAlloc.Read() : -1;
                frameScopes++;
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
                long allocNow = PerfAlloc.Enabled ? PerfAlloc.Read() : 0;
                // A scope inside this one that never ended (an exception skipped it) is closed here.
                while (depth >= level)
                {
                    int d = depth - 1;
                    long elapsed = now - stackStart[d];
                    long self = elapsed - stackChild[d];
                    if (self < 0) self = 0;
                    frameSelf[stackSlot[d]] += self;
                    long childAlloc = stackChildAlloc[d];
                    long a0 = stackAlloc[d];
                    long passUp;
                    if (a0 >= 0)
                    {
                        long elapsedAlloc = Math.Max(0, allocNow - a0);
                        frameAlloc[stackSlot[d]] += Math.Max(0, elapsedAlloc - childAlloc);
                        passUp = elapsedAlloc;
                    }
                    else passUp = childAlloc; // an unmeasured scope hands what its measured children took on to the one around it
                    depth = d;
                    if (d > 0)
                    {
                        stackChild[d - 1] += elapsed;
                        stackChildAlloc[d - 1] += passUp;
                    }
                }
            }
            catch (Exception e) { Fail(e); }
        }

        /// <summary>
        /// Adds time that was measured some other way (a few timestamps taken by the caller) to a slot, and takes it out of the scope
        /// that is open, so the slots still add up.
        /// </summary>
        public static void AddNested(PerfSlot slot, long ticks)
        {
            if (!Enabled || ticks <= 0 || Environment.CurrentManagedThreadId != mainThreadId) return;
            frameSelf[(int)slot] += ticks;
            if (depth > 0) stackChild[depth - 1] += ticks;
        }

        // ---- counters ----

        public static void Count(PerfCounter counter)
        {
            if (Enabled) counters[(int)counter]++;
        }

        public static void Count(PerfCounter counter, int amount)
        {
            if (Enabled) counters[(int)counter] += amount;
        }

        /// <summary>One sampled measurement pair was taken, for the estimate of what measuring costs.</summary>
        public static void NoteSampledPair() => frameSampledPairs++;

        /// <summary>The loop that ticks the simulation took one step (a bucket, or the replay service's turn).</summary>
        public static void NoteBucket()
        {
            if (Enabled) frameBuckets++;
        }

        /// <summary>How long the game says the last parallel tick took, in milliseconds.</summary>
        public static void NoteParallelTick(double milliseconds)
        {
            if (Enabled) frameParTickMs += milliseconds;
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

        /// <summary>A phase of Unity's frame began or ended (see <see cref="PerfColumns.PhaseNames"/>). Called from Unity's player loop.</summary>
        public static void PhaseMark(int phase, bool start)
        {
            if (!Enabled || (uint)phase >= PerfColumns.PhaseCount) return;
            long now = Now();
            if (start) phaseStart[phase] = now;
            else if (phaseStart[phase] != 0)
            {
                framePhase[phase] += now - phaseStart[phase];
                phaseStart[phase] = 0;
            }
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
                PerfProfile.OnTick();
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
            bool cpuWanted = PerfCpu.Available && TestClock == null;
            long threadCpu = 0, processCpu = 0;
            ulong cycles = 0;
            if (cpuWanted && !PerfCpu.Sample(out threadCpu, out cycles, out processCpu)) cpuWanted = false;

            if (lastFrameTs == 0)
            {
                // The first call only sets the clocks.
                lastFrameTs = start;
                lastGc = GC.CollectionCount(0);
                lastMemory = GC.GetTotalMemory(false);
                lastAllocSource = PerfAlloc.Enabled ? PerfAlloc.Read() : 0;
                nextSummaryTick = tick + summaryTicks;
                lastTick = tick;
                lastThreadCpu = threadCpu; lastProcessCpu = processCpu; lastCycles = cycles;
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

            bool willWriteFrame = frameMs >= thresholdMs;
            bool summaryDue = tick >= nextSummaryTick;
            if (willWriteFrame || summaryDue) HeavySampler?.Invoke(Extra);

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
            double allocAccounted = 0;
            for (int i = 0; i < PerfColumns.SlotCount; i++)
            {
                double ms = frameSelf[i] * msPerTick;
                r[PerfColumns.SlotBase + i] = ms;
                accounted += ms;
                double kb = frameAlloc[i] / 1024.0;
                r[PerfColumns.AllocBase + i] = kb;
                allocAccounted += kb;
            }
            r[PerfColumns.Saving] = frameSelf[(int)PerfSlot.Save] > 0 ? 1 : 0;
            r[PerfColumns.OtherMs] = Math.Max(0, frameMs - accounted);

            double allocKb = (memory - lastMemory) / 1024.0;
            // What was allocated outside every measured section, from the same counter the sections use (which never goes down at a collection).
            long allocSource = PerfAlloc.Enabled ? PerfAlloc.Read() : 0;
            r[PerfColumns.OtherKB] = Math.Max(0, (allocSource - lastAllocSource) / 1024.0 - allocAccounted);
            lastAllocSource = allocSource;
            r[PerfColumns.GcDelta] = gc - lastGc;
            r[PerfColumns.HeapMB] = memory / 1048576.0;
            r[PerfColumns.AllocKB] = allocKb;
            r[PerfColumns.MsgOut] = Interlocked.Exchange(ref messagesOut, 0);
            r[PerfColumns.BytesOut] = Interlocked.Exchange(ref bytesOut, 0);
            r[PerfColumns.MsgIn] = Interlocked.Exchange(ref messagesIn, 0);
            r[PerfColumns.BytesIn] = Interlocked.Exchange(ref bytesIn, 0);
            r[PerfColumns.Dropped] = ring?.Dropped ?? 0;
            r[PerfColumns.OverheadUs] = (frameScopes * ScopePairTicks + frameSampledPairs * SamplePairTicks) * msPerTick * 1000;

            for (int i = 0; i < PerfColumns.CounterCount; i++) r[PerfColumns.CounterBase + i] = counters[i];
            r[PerfColumns.ParTickMs] = frameParTickMs;

            if (cpuWanted)
            {
                r[PerfColumns.MainCpuMs] = (threadCpu - lastThreadCpu) / 10000.0;
                r[PerfColumns.MainMcyc] = (cycles - lastCycles) / 1000000.0;
                r[PerfColumns.ProcCpuMs] = (processCpu - lastProcessCpu) / 10000.0;
                lastThreadCpu = threadCpu; lastProcessCpu = processCpu; lastCycles = cycles;
            }

            for (int i = 0; i < PerfColumns.PhaseCount; i++) r[PerfColumns.PhaseBase + i] = framePhase[i] * msPerTick;
            for (int i = 0; i < PerfColumns.ExtraPerFrameCount; i++) r[PerfColumns.ExtraBase + i] = Extra[i];
            if (willWriteFrame || summaryDue)
                for (int i = 0; i < PerfColumns.ExtraHeavyCount; i++) r[PerfColumns.HeavyBase + i] = Extra[PerfColumns.ExtraPerFrameCount + i];
            else
                for (int i = 0; i < PerfColumns.ExtraHeavyCount; i++) r[PerfColumns.HeavyBase + i] = window[PerfColumns.HeavyBase + i];

            r[PerfColumns.FrameHistBase + PerfColumns.FrameBucket(frameMs)] = 1;
            r[PerfColumns.TickHistBase + PerfColumns.TickBucket(frameTicks)] = 1;

            lastGc = gc;
            lastMemory = memory;
            ResetFrame();

            Accumulate(r);
            // What this call itself cost, so the log shows whether it is a problem of its own making.
            r[PerfColumns.ProbeUs] = (Now() - start) * msPerTick * 1000;
            window[PerfColumns.ProbeUs] += r[PerfColumns.ProbeUs];
            if (willWriteFrame) ring?.TryPush(r);
            if (summaryDue)
            {
                EmitSummary();
                nextSummaryTick = tick + summaryTicks;
            }
        }

        static void ResetFrame()
        {
            Array.Clear(frameSelf, 0, frameSelf.Length);
            Array.Clear(frameAlloc, 0, frameAlloc.Length);
            Array.Clear(counters, 0, counters.Length);
            Array.Clear(framePhase, 0, framePhase.Length);
            depth = 0; generation++;
            frameTicks = 0; frameBuckets = 0; frameWaitFrames = 0; frameWaitMs = 0;
            frameScopes = 0; frameSampledPairs = 0; frameParTickMs = 0;
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
            double entityTicksPerTick = r[PerfColumns.Ticks] > 0 ? r[PerfColumns.CounterBase + (int)PerfCounter.EntityTicks] / r[PerfColumns.Ticks] : 0;
            ring?.TryPush(r);
            PerfProfile.FlushWindow(windowNumber++, lastTick, entityTicksPerTick, profileRing);
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
