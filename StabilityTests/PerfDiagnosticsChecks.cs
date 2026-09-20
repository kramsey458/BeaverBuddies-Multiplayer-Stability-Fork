#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;
using TimberNet.Perf;

// The second-round frame rate log: allocation per section, counters, Unity phases, histograms, the per-entity and per-singleton
// profile, the sampling that keeps the measuring cheap, processor time, and what the probe costs itself.
static class PerfDiagnosticsChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
    static void Near(double expected, double actual, double tolerance = .01) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"expected about {expected}, got {actual}");

    static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);
    static double Col(double[] row, int column) => row[column];
    static int Slot(PerfSlot slot) => PerfColumns.SlotBase + (int)slot;
    static int AllocOf(PerfSlot slot) => PerfColumns.AllocBase + (int)slot;
    static int CounterOf(PerfCounter counter) => PerfColumns.CounterBase + (int)counter;

    /// <summary>The probe on this thread with a clock and an allocation counter the test moves by hand, and a second buffer for the profile.</summary>
    sealed class Rig : IDisposable
    {
        public long Now = Ms(1000);
        public long Bytes;
        public readonly PerfRing Ring = new PerfRing(PerfColumns.Count, 256);
        public readonly PerfRing Profile = new PerfRing(PerfProfile.Table.Count, 256);

        public Rig(double thresholdMs = 50, int summaryTicks = 100000)
        {
            PerfProbe.TestClock = () => Now;
            PerfAlloc.UseTestSource(() => Bytes);
            PerfProbe.Start(Ring, Environment.CurrentManagedThreadId, thresholdMs, summaryTicks, Profile);
        }

        public void Advance(double milliseconds) => Now += Ms(milliseconds);
        public void Frame(int tick = 1) => PerfProbe.OnFrame(tick, 1f, 1f, 0, 100, false, true);

        static List<double[]> Drain(PerfRing ring, int columns)
        {
            var buffer = new double[256 * columns];
            int count = ring.Drain(buffer, 256);
            var rows = new List<double[]>();
            for (int i = 0; i < count; i++) rows.Add(buffer.AsSpan(i * columns, columns).ToArray());
            return rows;
        }

        public List<double[]> Rows() => Drain(Ring, PerfColumns.Count);
        public List<double[]> ProfileRows() => Drain(Profile, PerfProfile.Table.Count);

        public void Dispose()
        {
            PerfProbe.Stop();
            PerfProbe.TestClock = null;
            PerfProbe.HeavySampler = null;
            PerfAlloc.Init();
        }
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Diagnostics: the columns line up, and buckets fall where they should", () =>
        {
            Equal(PerfColumns.Count, PerfColumns.Names.Length);
            Equal(PerfColumns.Count, PerfColumns.Names.Distinct().Count());
            Equal("parMs", PerfColumns.Names[PerfColumns.SlotBase + PerfColumns.SlotCount - 1]);
            Equal("parKB", PerfColumns.Names[PerfColumns.AllocBase + PerfColumns.SlotCount - 1]);
            Equal("otherKB", PerfColumns.Names[PerfColumns.OtherKB]);
            Equal("nSpeed", PerfColumns.Names[PerfColumns.CounterBase + PerfColumns.CounterCount - 1]);
            Equal("ftWait", PerfColumns.Names[PerfColumns.ExtraBase + PerfColumns.ExtraPerFrameCount - 1]);
            Equal("fh0", PerfColumns.Names[PerfColumns.FrameHistBase]);
            Equal("th5", PerfColumns.Names[PerfColumns.Count - 1]);
            Equal(PerfColumns.SlotCount, PerfColumns.SlotAllocNames.Length);
            Equal(PerfColumns.SlotCount, PerfColumns.SlotTracksAlloc.Length);
            Equal(PerfColumns.CounterCount, Enum.GetValues(typeof(PerfCounter)).Length);
            Equal(PerfColumns.FrameHistCount, PerfColumns.FrameEdgesMs.Length + 1);
            // A frame lands in the bucket whose upper edge it is below: vertical sync's 16.7 and 33.3 ms are told apart.
            Equal(0, PerfColumns.FrameBucket(3)); Equal(5, PerfColumns.FrameBucket(16.6)); Equal(6, PerfColumns.FrameBucket(17.5));
            Equal(9, PerfColumns.FrameBucket(33.4)); Equal(12, PerfColumns.FrameBucket(60)); Equal(16, PerfColumns.FrameBucket(5000));
            Equal(0, PerfColumns.TickBucket(0)); Equal(1, PerfColumns.TickBucket(1)); Equal(2, PerfColumns.TickBucket(2));
            Equal(3, PerfColumns.TickBucket(3)); Equal(3, PerfColumns.TickBucket(4)); Equal(4, PerfColumns.TickBucket(5));
            Equal(4, PerfColumns.TickBucket(9)); Equal(5, PerfColumns.TickBucket(10)); Equal(5, PerfColumns.TickBucket(300));
            // Every table writes a row with as many fields as it has names, whatever the language.
            var buffer = new char[4096];
            Check(PerfColumns.TryFormatRow(new double[PerfColumns.Count], buffer, out int written));
            Equal(PerfColumns.Count, new string(buffer, 0, written).TrimEnd('\n').Split(',').Length);
            Check(PerfProfile.Table.TryFormatRow(new double[PerfProfile.Table.Count], buffer, out written));
            Equal(PerfProfile.Table.Count, new string(buffer, 0, written).TrimEnd('\n').Split(',').Length);
        });

        yield return ("Diagnostics: allocation is charged to the section that made it, nested ones taken out of the outer", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long sim = PerfProbe.Begin(PerfSlot.Sim);
            rig.Bytes += 10 * 1024;
            long ser = PerfProbe.Begin(PerfSlot.Serialize);
            rig.Bytes += 4 * 1024;
            PerfProbe.End(ser);
            rig.Bytes += 1 * 1024;
            PerfProbe.End(sim);
            rig.Advance(60); rig.Frame(2);
            var row = rig.Rows().Single();
            Near(11, Col(row, AllocOf(PerfSlot.Sim)));
            Near(4, Col(row, AllocOf(PerfSlot.Serialize)));
            Near(0, Col(row, PerfColumns.OtherKB));
            // Bytes allocated outside every measured section are the remainder.
            rig.Bytes += 7 * 1024;
            rig.Advance(60); rig.Frame(3);
            Near(7, Col(rig.Rows().Single(), PerfColumns.OtherKB));
        });

        yield return ("Diagnostics: a section that is not measured hands what its measured children allocated to the one around it", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long sim = PerfProbe.Begin(PerfSlot.Sim);
            long steam = PerfProbe.Begin(PerfSlot.Steam);          // not measured for allocation
            long ser = PerfProbe.Begin(PerfSlot.Serialize);
            rig.Bytes += 8 * 1024;
            PerfProbe.End(ser);
            rig.Bytes += 2 * 1024;                                  // allocated in the unmeasured Steam scope
            PerfProbe.End(steam);
            PerfProbe.End(sim);
            rig.Advance(60); rig.Frame(2);
            var row = rig.Rows().Single();
            Near(8, Col(row, AllocOf(PerfSlot.Serialize)));
            // Sim would be charged the Steam scope's own bytes (2 KB) but never the 8 KB that Serialize already has.
            Near(2, Col(row, AllocOf(PerfSlot.Sim)));
            Near(0, Col(row, AllocOf(PerfSlot.Steam)));
        });

        yield return ("Diagnostics: a collection inside a section does not make its allocation negative or leak into the next", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long sim = PerfProbe.Begin(PerfSlot.Sim);
            rig.Bytes -= 500 * 1024;                                // a collection freed memory
            PerfProbe.End(sim);
            rig.Advance(60); rig.Frame(2);
            Near(0, Col(rig.Rows().Single(), AllocOf(PerfSlot.Sim)));
        });

        yield return ("Diagnostics: time measured by the caller is added to a slot and taken out of the open scope", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long teb = PerfProbe.Begin(PerfSlot.EntityHash);
            rig.Advance(10);
            PerfProbe.AddNested(PerfSlot.TebLookup, Ms(3));
            PerfProbe.AddNested(PerfSlot.TebAnimate, Ms(2));
            PerfProbe.End(teb);
            rig.Advance(50); rig.Frame(2);
            var row = rig.Rows().Single();
            Near(5, Col(row, Slot(PerfSlot.EntityHash)));
            Near(3, Col(row, Slot(PerfSlot.TebLookup)));
            Near(2, Col(row, Slot(PerfSlot.TebAnimate)));
            // Nothing open: nothing to take it out of, and it must not crash.
            PerfProbe.AddNested(PerfSlot.TebLookup, Ms(1));
        });

        yield return ("Diagnostics: counters count per frame and start again", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            PerfProbe.Count(PerfCounter.Rng, 5); PerfProbe.Count(PerfCounter.Anim); PerfProbe.Count(PerfCounter.Anim);
            PerfProbe.NoteParallelTick(4.5);
            rig.Advance(60); rig.Frame(2);
            var first = rig.Rows().Single();
            Equal(5.0, Col(first, CounterOf(PerfCounter.Rng))); Equal(2.0, Col(first, CounterOf(PerfCounter.Anim)));
            Near(4.5, Col(first, PerfColumns.ParTickMs));
            rig.Advance(60); rig.Frame(3);
            var second = rig.Rows().Single();
            Equal(0.0, Col(second, CounterOf(PerfCounter.Rng))); Near(0, Col(second, PerfColumns.ParTickMs));
        });

        yield return ("Diagnostics: the time in each of Unity's phases is recorded, and a phase that spans two frames is counted once", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            PerfProbe.PhaseMark(5, true); rig.Advance(12); PerfProbe.PhaseMark(5, false);
            PerfProbe.PhaseMark(7, true); rig.Advance(30); PerfProbe.PhaseMark(7, false);
            PerfProbe.PhaseMark(1, true);                            // begins here and ends in the next frame
            rig.Advance(20); rig.Frame(2);
            var first = rig.Rows().Single();
            Near(12, Col(first, PerfColumns.PhaseBase + 5)); Near(30, Col(first, PerfColumns.PhaseBase + 7));
            Near(0, Col(first, PerfColumns.PhaseBase + 1));
            // The whole length of the phase (20 ms in the first frame and 15 in the second) is counted, once, in the frame it ends in.
            rig.Advance(15); PerfProbe.PhaseMark(1, false); rig.Advance(50); rig.Frame(3);
            Near(35, Col(rig.Rows().Single(), PerfColumns.PhaseBase + 1));
            PerfProbe.PhaseMark(99, true); PerfProbe.PhaseMark(-1, false);   // never reaches the game
        });

        yield return ("Diagnostics: a summary counts frames per time bucket, and ticks per frame", () =>
        {
            using var rig = new Rig(thresholdMs: 1000, summaryTicks: 5);
            rig.Frame(0);
            foreach (double ms in new[] { 16.6, 16.6, 33.4, 60 })
            {
                rig.Advance(ms); PerfProbe.NoteTickStart(1); rig.Frame(1);
            }
            rig.Advance(16.6); PerfProbe.NoteTickStart(1); PerfProbe.NoteTickStart(2); rig.Frame(5);
            var s = rig.Rows().Single();
            Equal('S', (char)s[PerfColumns.Type]);
            Equal(5.0, Col(s, PerfColumns.Frames));
            Equal(3.0, Col(s, PerfColumns.FrameHistBase + 5));       // three frames under 17.5 ms
            Equal(1.0, Col(s, PerfColumns.FrameHistBase + 9));
            Equal(1.0, Col(s, PerfColumns.FrameHistBase + 12));
            Equal(4.0, Col(s, PerfColumns.TickHistBase + 1)); Equal(1.0, Col(s, PerfColumns.TickHistBase + 2));
        });

        yield return ("Diagnostics: what Unity supplies each frame is copied in, and the heavy figures are read only when a row is written", () =>
        {
            using var rig = new Rig(thresholdMs: 50);
            int heavyCalls = 0;
            PerfProbe.HeavySampler = extra =>
            {
                heavyCalls++;
                extra[PerfColumns.ExtraPerFrameCount] = 1700; extra[PerfColumns.ExtraPerFrameCount + 1] = 1500;
            };
            rig.Frame();
            PerfProbe.Extra[2] = 4200; PerfProbe.Extra[7] = 12.5;
            rig.Advance(20); rig.Frame(2);
            Equal(0, heavyCalls); Equal(0, rig.Rows().Count);
            rig.Advance(60); rig.Frame(3);
            Equal(1, heavyCalls);
            var row = rig.Rows().Single();
            Near(4200, Col(row, PerfColumns.ExtraBase + 2)); Near(12.5, Col(row, PerfColumns.ExtraBase + 7));
            Near(1700, Col(row, PerfColumns.HeavyBase)); Near(1500, Col(row, PerfColumns.HeavyBase + 1));
        });

        yield return ("Diagnostics: the profile counts sampled calls, scales them up, and names each entity kind and singleton once", () =>
        {
            using var rig = new Rig(thresholdMs: 1000, summaryTicks: 1);
            PerfProfile.Configure(2, 1);
            rig.Frame(0);
            // Eight entity ticks with every second one sampled: four samples, scaled by two.
            for (int i = 0; i < 8; i++)
            {
                PerfSample s = PerfProfile.BeginEntity();
                if (s.On) { rig.Advance(3); rig.Bytes += 2048; }
                PerfProfile.EndEntity((i / 2) % 2 == 0 ? "BeaverAdult" : "FarmHouse", s);
            }
            PerfSample g = PerfProfile.BeginSingleton();
            rig.Advance(5); rig.Bytes += 4096;
            PerfProfile.EndSingleton(typeof(string), g);
            PerfProbe.NoteTickStart(1);
            rig.Advance(20); rig.Frame(1);
            var rows = rig.ProfileRows();
            var entities = rows.Where(r => (char)r[0] == 'E').ToList();
            Equal(2, entities.Count);
            Equal(8.0, entities.Sum(r => r[4]));                     // estimated calls
            Equal(4.0, entities.Sum(r => r[5]));                     // sampled
            Near(24, entities.Sum(r => r[6]));                       // 4 samples x 3 ms x 2
            Near(16, entities.Sum(r => r[7]));                       // 4 samples x 2 KB x 2
            var singleton = rows.Single(r => (char)r[0] == 'G');
            Equal(1.0, singleton[4]); Near(5, singleton[6]); Near(4, singleton[7]); Near(5, singleton[8]);
            var names = new StringWriter();
            PerfProfile.WriteNewNames(names);
            string text = names.ToString();
            Check(text.Contains("|E|") && text.Contains("BeaverAdult") && text.Contains("FarmHouse") && text.Contains("|G|") && text.Contains("System.String"), text);
            var again = new StringWriter();
            PerfProfile.WriteNewNames(again);
            Equal("", again.ToString());
            Equal(3, PerfProfile.NameCount);                         // two entity kinds and one singleton
        });

        yield return ("Diagnostics: the sampling widens as there are more entities, and never below every call or above 1024", () =>
        {
            using var rig = new Rig(thresholdMs: 1000, summaryTicks: 1);
            long pair = Stopwatch.Frequency / 1000000;                // one microsecond
            PerfProbe.SamplePairTicks = pair;
            rig.Frame(0);
            PerfProbe.Count(PerfCounter.EntityTicks, 5000); PerfProbe.NoteTickStart(1);
            rig.Advance(10); rig.Frame(1);
            // 5000 entities x (1.0 + 0.1 microseconds) against a 250 microsecond budget is every 22nd.
            Check(PerfProfile.EntityInterval >= 20 && PerfProfile.EntityInterval <= 24, "interval " + PerfProfile.EntityInterval);
            PerfProbe.Count(PerfCounter.EntityTicks, 50); PerfProbe.NoteTickStart(2);
            rig.Advance(10); rig.Frame(2);
            Equal(1, PerfProfile.EntityInterval);
            PerfProbe.SamplePairTicks = 0;
        });

        yield return ("Diagnostics: profile rows for a window are written when the summary is, and again fresh next time", () =>
        {
            using var rig = new Rig(thresholdMs: 1000, summaryTicks: 1);
            PerfProfile.Configure(1, 1);
            rig.Frame(0);
            var s = PerfProfile.BeginEntity(); rig.Advance(1); PerfProfile.EndEntity("Beaver", s);
            PerfProbe.NoteTickStart(1); rig.Advance(5); rig.Frame(1);
            Equal(1, rig.ProfileRows().Count);
            PerfProfile.Configure(1, 1);
            rig.Advance(5); PerfProbe.NoteTickStart(2); rig.Frame(2);
            Equal(0, rig.ProfileRows().Count);
        });

        yield return ("Diagnostics: the allocation counter picks a working source and says which", () =>
        {
            PerfAlloc.Init();
            Check(PerfAlloc.Enabled);
            long before = PerfAlloc.Read();
            var keep = new byte[256 * 1024]; keep[0] = 1;
            long after = PerfAlloc.Read();
            GC.KeepAlive(keep);
            Check(after - before >= 200 * 1024 || PerfAlloc.Mode == PerfAlloc.ModeHeap, $"mode {PerfAlloc.ModeName} saw {after - before} bytes");
            Check(PerfAlloc.MeasureReadTicks(500) > 0);
            PerfAlloc.Init(preferHeap: true);
            Equal(PerfAlloc.ModeHeap, PerfAlloc.Mode);
            Equal("GC.GetTotalMemory(false)", PerfAlloc.ModeName);
            PerfAlloc.Init();
        });

        yield return ("Diagnostics: processor time is available on Windows and moves when the thread works", () =>
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { Check(!PerfCpu.Init()); return; }
            Check(PerfCpu.Init());
            Check(PerfCpu.Sample(out long cpu0, out ulong cycles0, out long proc0));
            long sink = 0; var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 60) for (int i = 0; i < 10000; i++) sink += i;
            GC.KeepAlive(sink.ToString());
            Check(PerfCpu.Sample(out long cpu1, out ulong cycles1, out long proc1));
            Check(cycles1 - cycles0 > 1_000_000, $"cycles {cycles1 - cycles0}");
            Check(cpu1 >= cpu0 && proc1 >= proc0);
        });

        yield return ("Diagnostics: calibration measures what the probe costs and leaves it quiet", () =>
        {
            PerfAlloc.Init();
            PerfProbe.ScopePairTicks = 0; PerfProbe.SamplePairTicks = 0;
            PerfProbe.Calibrate();
            Check(PerfProbe.ScopePairTicks > 0 && PerfProbe.SamplePairTicks > 0);
            Check(!PerfProbe.Enabled);
            // What one pair of readings costs is a few hundred nanoseconds at the very most on any machine that runs the game.
            Check(PerfProbe.SamplePairTicks < Stopwatch.Frequency / 200, "a sample pair took " + PerfProbe.SamplePairTicks + " ticks");
        });

        yield return ("Diagnostics: the estimate of what the log itself costs a frame follows the sections it measured", () =>
        {
            using var rig = new Rig();
            PerfProbe.ScopePairTicks = Ms(0.001); PerfProbe.SamplePairTicks = Ms(0.002);
            rig.Frame();
            for (int i = 0; i < 10; i++) { long t = PerfProbe.Begin(PerfSlot.Anim); PerfProbe.End(t); }
            PerfProbe.NoteSampledPair(); PerfProbe.NoteSampledPair();
            rig.Advance(60); rig.Frame(2);
            // 10 scopes x 1 us + 2 pairs x 2 us
            Near(14, Col(rig.Rows().Single(), PerfColumns.OverheadUs), .5);
            PerfProbe.ScopePairTicks = 0; PerfProbe.SamplePairTicks = 0;
        });

        yield return ("Diagnostics: the per-frame path still allocates nothing with allocation, processor time, phases and the profile all on", () =>
        {
            PerfAlloc.Init();
            PerfCpu.Init();
            var ring = new PerfRing(PerfColumns.Count, 16);
            var profile = new PerfRing(PerfProfile.Table.Count, 16);
            PerfProbe.TestClock = null;
            PerfProbe.Start(ring, Environment.CurrentManagedThreadId, 1e9, 100000000, profile);
            PerfProfile.Configure(4, 1);
            try
            {
                var names = new[] { "BeaverAdult", "FarmHouse", "Lodge" };
                void Iteration(int i)
                {
                    long a = PerfProbe.Begin(PerfSlot.Sim);
                    long b = PerfProbe.Begin(PerfSlot.Serialize);
                    PerfProbe.End(b);
                    PerfProbe.AddNested(PerfSlot.TebLookup, 5);
                    PerfProbe.End(a);
                    PerfProbe.Count(PerfCounter.Rng); PerfProbe.Count(PerfCounter.Anim, 3);
                    PerfProbe.PhaseMark(5, true); PerfProbe.PhaseMark(5, false);
                    PerfProbe.NoteParallelTick(1.5);
                    for (int e = 0; e < 8; e++) { PerfSample s = PerfProfile.BeginEntity(); PerfProfile.EndEntity(names[e % 3], s); }
                    PerfSample g = PerfProfile.BeginSingleton(); PerfProfile.EndSingleton(typeof(PerfProbe), g);
                    PerfProbe.NoteWaiting(i); PerfProbe.NoteTickStart(i + 1);
                    PerfProbe.OnFrame(i, 1f, 1f, 0, 100, false, true);
                }
                for (int i = 0; i < 200; i++) Iteration(i);              // the first sightings of each name allocate, once
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) Iteration(i);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(allocated == 0, $"the probe allocated {allocated} bytes over 5000 frames");
            }
            finally { PerfProbe.Stop(); }
        });

        yield return ("Diagnostics: a profile file has its own columns, and names are written before the rows that use them", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "bb-perf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "profile.csv");
            var ring = new PerfRing(PerfProfile.Table.Count, 16);
            var row = new double[PerfProfile.Table.Count];
            row[0] = 'E'; row[3] = 0; row[4] = 40; row[5] = 10; row[6] = 12.5; row[7] = 96;
            ring.TryPush(row);
            int notes = 0;
            var writer = new PerfWriter(path, new[] { "# profile" }, ring, null, PerfProfile.Table)
            {
                BeforeRows = w => { notes++; w.WriteLine("# name|E|0|BeaverAdult|"); },
                Trailer = w => w.WriteLine("# trailer"),
            };
            Check(writer.Start()); writer.Stop();
            string[] lines = File.ReadAllLines(path);
            Equal("# profile", lines[0]);
            Equal(PerfProfile.Table.HeaderLine(), lines[1]);
            Equal("# name|E|0|BeaverAdult|", lines[2]);
            Check(lines[3].StartsWith("E,0,0,0,40,10,12.50,96.0,"), lines[3]);
            Equal("# trailer", lines[4]); Equal("# end", lines[5]);
            Equal(1, notes);
        });
    }
}
