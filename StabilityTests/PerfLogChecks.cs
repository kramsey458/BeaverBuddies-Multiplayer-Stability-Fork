#nullable enable
using System.Diagnostics;
using System.Globalization;
using BeaverBuddies.Perf;
using TimberNet.Perf;

// The frame rate log: the timing core against a fake clock, the row text, the buffer and the writer. The parts that need the
// running game (which methods are patched, the file's location, the settings) cannot run here and are checked in the game.
static class PerfLogChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");
    static void Near(double expected, double actual, double tolerance = .01) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"expected about {expected}, got {actual}");

    static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    static double[] Row(char type, double frame = 0)
    {
        var row = new double[PerfColumns.Count];
        row[PerfColumns.Type] = type;
        row[PerfColumns.Frame] = frame;
        return row;
    }

    static string TempFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bb-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "log.csv");
    }

    static PatchRecord Patch(string kind, string owner, int priority, string assembly) =>
        new PatchRecord { Kind = kind, Owner = owner, Priority = priority, Assembly = assembly, PatchMethod = assembly + ".Patch." + kind };

    static PatchedMethodRecord Method(string fullTypeName, string methodName, params PatchRecord[] patches) =>
        new PatchedMethodRecord
        {
            TypeName = fullTypeName.Substring(fullTypeName.LastIndexOf('.') + 1),
            MethodName = methodName,
            FullName = fullTypeName + "." + methodName,
            Patches = patches.ToList(),
        };

    /// <summary>The probe running on this thread, with a clock the test moves by hand.</summary>
    sealed class Rig : IDisposable
    {
        public long Now = Ms(1000);
        public readonly PerfRing Ring = new PerfRing(PerfColumns.Count, 64);

        public Rig(double thresholdMs = 50, int summaryTicks = 100000)
        {
            PerfProbe.TestClock = () => Now;
            PerfProbe.Start(Ring, Environment.CurrentManagedThreadId, thresholdMs, summaryTicks);
        }

        public void Advance(double milliseconds) => Now += Ms(milliseconds);

        public void Frame(int tick = 1) => PerfProbe.OnFrame(tick, 1f, 1f, 0, 100, false, true);

        public List<double[]> Rows()
        {
            var buffer = new double[64 * PerfColumns.Count];
            int count = Ring.Drain(buffer, 64);
            var rows = new List<double[]>();
            for (int i = 0; i < count; i++)
                rows.Add(buffer.AsSpan(i * PerfColumns.Count, PerfColumns.Count).ToArray());
            return rows;
        }

        public void Dispose()
        {
            PerfProbe.Stop();
            PerfProbe.TestClock = null;
        }
    }

    /// <summary>
    /// Writes two example logs of one made-up session, to try compare_perf_logs.py on without playing: the host stalls on a
    /// collection and on a save and the guest waits it out and catches up, both collect at once, and the guest hitches alone.
    /// Made with the real probe and writer on a scripted clock, so it also shows the file format. Not from a game.
    /// </summary>
    public static void WriteSample(string directory)
    {
        Directory.CreateDirectory(directory);
        WriteSampleLog(Path.Combine(directory, "perf-host-Example-20260101-000000.csv"), true);
        WriteSampleLog(Path.Combine(directory, "perf-guest-Example-20260101-000000.csv"), false);
        Console.WriteLine("Wrote two example logs to " + directory);
    }

    static void WriteSampleLog(string path, bool host)
    {
        // The two computers' clocks do not agree, which is why the logs are lined up by tick.
        long now = Ms(host ? 1000000 : 7777777);
        var ring = new PerfRing(PerfColumns.Count, 4096);
        PerfProbe.TestClock = () => now;
        PerfProbe.Start(ring, Environment.CurrentManagedThreadId, 50, 100);
        try
        {
            int tick = 0;
            double sinceTick = 0;
            void Frame(float speed, int behind) => PerfProbe.OnFrame(tick, speed, 1f, behind, 100, false, true);
            Frame(1, 0);
            for (int frame = 1; frame <= 3000; frame++)
            {
                if (frame == 900 || frame == 2100)
                {
                    if (host)
                    {
                        long sim = PerfProbe.Begin(PerfSlot.Sim);
                        if (frame == 2100)
                        {
                            long save = PerfProbe.Begin(PerfSlot.Save);
                            now += Ms(300);
                            PerfProbe.End(save);
                        }
                        else { GC.Collect(); now += Ms(300); }
                        // The game catches its own ticks up after a stall, so the host runs the three it owed.
                        for (int t = 0; t < 3; t++) { tick++; PerfProbe.NoteTickStart(tick); }
                        PerfProbe.End(sim);
                        Frame(1, 0);
                    }
                    else
                    {
                        // The game wants the next tick every frame and the host has not sent it, then it runs to catch up.
                        for (int i = 0; i < 18; i++) { PerfProbe.NoteWaiting(tick); now += Ms(16); Frame(1, 0); }
                        long burst = PerfProbe.Begin(PerfSlot.Sim);
                        for (int t = 0; t < 3; t++) { tick++; PerfProbe.NoteTickStart(tick); }
                        now += Ms(60);
                        PerfProbe.End(burst);
                        Frame(3, 2);
                    }
                    sinceTick = 0;
                    continue;
                }
                if (frame == 1500)
                {
                    // Both computers collect at the same moment.
                    GC.Collect();
                    now += Ms(80);
                    Frame(1, host ? 0 : 1);
                    continue;
                }
                if (!host && frame == 1800)
                {
                    // Something outside this mod: no probe accounts for it and the host never notices.
                    now += Ms(120);
                    Frame(1, 1);
                    continue;
                }
                sinceTick += 16;
                long ordinary = PerfProbe.Begin(PerfSlot.Sim);
                if (sinceTick >= 100) { tick++; PerfProbe.NoteTickStart(tick); sinceTick -= 100; }
                now += Ms(4);
                PerfProbe.End(ordinary);
                now += Ms(12);
                Frame(1, host ? 0 : 1);
            }
        }
        finally
        {
            PerfProbe.Stop();
            PerfProbe.TestClock = null;
        }
        string build = "game=1.0.5;mod=1.0.10-perflog-preview;modBuild=" + (host ? "11111111-aaaa" : "11111111-aaaa") + ";netBuild=22222222-bbbb";
        var header = new List<string>
        {
            "# BeaverBuddies frame rate log, format 1",
            "# role: " + (host ? "host" : "guest"),
            "# player: Example",
            "# mod: 1.0.10-perflog-preview",
            "# build: " + build,
            "# detailedLogging: off",
            "# thresholdMs: 50",
            "# summaryTicks: 100",
            "# note: this file was made up by StabilityTests (--write-perf-sample); it is not from a game",
            "# mods: 3",
            "# mod|beaverbuddies|BeaverBuddies - Stability Fork|1.0.10-perflog-preview",
            "# mod|harmony|Harmony|v2.4.1",
            "# mod|eMka.ModSettings|Mod Settings|v1.1.0",
        };
        var writer = new PerfWriter(path, header, ring, null);
        if (!writer.Start()) throw new Exception("could not write " + path + ": " + writer.Failure);
        writer.Stop();
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Frame rate log: every column has a name, a kind and a way of being summarized", () =>
        {
            Equal(PerfColumns.Count, PerfColumns.Names.Length);
            Equal(PerfColumns.Count, PerfColumns.Kinds.Length);
            Equal(PerfColumns.Count, PerfColumns.Aggregates.Length);
            Equal(PerfColumns.Count, PerfColumns.Names.Distinct().Count());
            Equal(PerfColumns.Count, PerfColumns.HeaderLine().Split(',').Length);
            Equal("gameMs", PerfColumns.Names[PerfColumns.SlotBase]);
            Equal("saveMs", PerfColumns.Names[PerfColumns.SlotBase + PerfColumns.SlotCount - 1]);
            Equal("otherMs", PerfColumns.Names[PerfColumns.OtherMs]);
            Equal("dropped", PerfColumns.Names[PerfColumns.Count - 1]);
            Equal(Enum.GetValues(typeof(PerfSlot)).Length, PerfColumns.SlotCount);
        });

        yield return ("Frame rate log: a row is the same text in every language and never gains a column", () =>
        {
            var row = Row('F', 42);
            row[PerfColumns.FrameMs] = 12.5; row[PerfColumns.HeapMB] = 1234.56; row[PerfColumns.AllocKB] = -5.5;
            row[PerfColumns.UtcMs] = 1758244800123;
            CultureInfo? original = null; CultureInfo? german = null;
            try { german = new CultureInfo("de-DE"); } catch (CultureNotFoundException) { }
            if (german != null) { original = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = german; }
            try
            {
                var buffer = new char[1024];
                Check(PerfColumns.TryFormatRow(row, buffer, out int written));
                string text = new string(buffer, 0, written);
                Check(text.EndsWith("\n") && !text.Contains(';'));
                string[] fields = text.TrimEnd('\n').Split(',');
                Equal(PerfColumns.Count, fields.Length);
                Equal("F", fields[PerfColumns.Type]);
                Equal("42", fields[PerfColumns.Frame]);
                Equal("1758244800123", fields[PerfColumns.UtcMs]);
                Equal("12.50", fields[PerfColumns.FrameMs]);
                Equal("1234.6", fields[PerfColumns.HeapMB]);
                Equal("-5.5", fields[PerfColumns.AllocKB]);
            }
            finally { if (original != null) CultureInfo.CurrentCulture = original; }
        });

        yield return ("Frame rate log: a row that does not fit is refused, and odd numbers are written as zero", () =>
        {
            Check(!PerfColumns.TryFormatRow(Row('F'), new char[10], out int written)); Equal(0, written);
            var row = Row('S'); row[PerfColumns.FrameMs] = double.NaN; row[PerfColumns.WaitMs] = double.PositiveInfinity;
            var buffer = new char[1024];
            Check(PerfColumns.TryFormatRow(row, buffer, out written));
            string[] fields = new string(buffer, 0, written).TrimEnd('\n').Split(',');
            Equal("0.00", fields[PerfColumns.FrameMs]); Equal("0.00", fields[PerfColumns.WaitMs]);
        });

        yield return ("Frame rate log buffer: rows come out oldest first, across the wrap", () =>
        {
            var ring = new PerfRing(PerfColumns.Count, 3);
            foreach (int frame in new[] { 1, 2, 3 }) Check(ring.TryPush(Row('F', frame)));
            var buffer = new double[3 * PerfColumns.Count];
            Equal(2, ring.Drain(buffer, 2));
            Equal(1.0, buffer[PerfColumns.Frame]); Equal(2.0, buffer[PerfColumns.Count + PerfColumns.Frame]);
            Check(ring.TryPush(Row('F', 4))); Check(ring.TryPush(Row('F', 5)));
            Equal(3, ring.Drain(buffer, 10));
            Equal(3.0, buffer[PerfColumns.Frame]); Equal(4.0, buffer[PerfColumns.Count + PerfColumns.Frame]);
            Equal(5.0, buffer[2 * PerfColumns.Count + PerfColumns.Frame]);
            Equal(0, ring.Drain(buffer, 10)); Equal(0L, ring.Dropped);
        });

        yield return ("Frame rate log buffer: when full a new row is dropped and counted, never the old ones", () =>
        {
            var ring = new PerfRing(PerfColumns.Count, 2);
            Check(ring.TryPush(Row('F', 1))); Check(ring.TryPush(Row('F', 2)));
            Check(!ring.TryPush(Row('F', 3))); Check(!ring.TryPush(Row('F', 4)));
            Equal(2, ring.Count); Equal(2L, ring.Dropped);
            var buffer = new double[2 * PerfColumns.Count];
            Equal(2, ring.Drain(buffer, 2));
            Equal(1.0, buffer[PerfColumns.Frame]); Equal(2.0, buffer[PerfColumns.Count + PerfColumns.Frame]);
            Check(ring.TryPush(Row('F', 5)));
        });

        yield return ("Frame rate log: nested timing is exclusive, so slots add up to the frame with the rest as otherMs", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long sim = PerfProbe.Begin(PerfSlot.Sim);
            rig.Advance(10);
            long replay = PerfProbe.Begin(PerfSlot.Replay);
            rig.Advance(4);
            PerfProbe.End(replay);
            rig.Advance(6);
            PerfProbe.End(sim);
            rig.Advance(80);
            rig.Frame(2);
            var rows = rig.Rows();
            Equal(1, rows.Count);
            var row = rows[0];
            Equal('F', (char)row[PerfColumns.Type]);
            Near(100, row[PerfColumns.FrameMs]); Near(100, row[PerfColumns.MaxFrameMs]);
            Near(16, row[PerfColumns.SlotBase + (int)PerfSlot.Sim]);
            Near(4, row[PerfColumns.SlotBase + (int)PerfSlot.Replay]);
            Near(80, row[PerfColumns.OtherMs]);
            Equal(2.0, row[PerfColumns.Tick]);
            Check(row[PerfColumns.ProbeUs] >= 0);
        });

        yield return ("Frame rate log: a frame under the threshold gets no row of its own", () =>
        {
            using var rig = new Rig(thresholdMs: 50);
            rig.Frame();
            rig.Advance(49); rig.Frame(2);
            Equal(0, rig.Rows().Count);
            rig.Advance(50); rig.Frame(3);
            Equal(1, rig.Rows().Count);
        });

        yield return ("Frame rate log: a scope that was never ended is closed by the one around it", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long outer = PerfProbe.Begin(PerfSlot.Sim);
            rig.Advance(5);
            PerfProbe.Begin(PerfSlot.Send);
            rig.Advance(5);
            PerfProbe.End(outer);
            rig.Advance(90);
            rig.Frame(2);
            var row = rig.Rows().Single();
            Near(5, row[PerfColumns.SlotBase + (int)PerfSlot.Sim]);
            Near(5, row[PerfColumns.SlotBase + (int)PerfSlot.Send]);
        });

        yield return ("Frame rate log: a scope left open across a frame is ignored, not charged to the next", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long token = PerfProbe.Begin(PerfSlot.Sim);
            rig.Advance(30);
            rig.Frame(2);
            rig.Advance(30);
            PerfProbe.End(token);
            rig.Advance(30);
            rig.Frame(3);
            var row = rig.Rows().Single();
            Near(0, row[PerfColumns.SlotBase + (int)PerfSlot.Sim]);
            Near(60, row[PerfColumns.FrameMs]);
        });

        yield return ("Frame rate log: only the game thread is timed, but every thread is counted", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            long fromOtherThread = -1;
            var other = new Thread(() => fromOtherThread = PerfProbe.Begin(PerfSlot.Send));
            other.Start(); other.Join();
            Equal(0L, fromOtherThread);
            Task.Run(() => { PerfProbe.CountSent(100); PerfProbe.CountSent(50); PerfProbe.CountReceived(7); }).Wait();
            rig.Advance(60); rig.Frame(2);
            var row = rig.Rows().Single();
            Equal(2.0, row[PerfColumns.MsgOut]); Equal(150.0, row[PerfColumns.BytesOut]);
            Equal(1.0, row[PerfColumns.MsgIn]); Equal(7.0, row[PerfColumns.BytesIn]);
            rig.Advance(60); rig.Frame(3);
            Equal(0.0, rig.Rows().Single()[PerfColumns.MsgOut]);
        });

        yield return ("Frame rate log: a collection during a frame shows in that frame's row", () =>
        {
            using var rig = new Rig();
            rig.Frame();
            GC.Collect();
            rig.Advance(60); rig.Frame(2);
            var row = rig.Rows().Single();
            Check(row[PerfColumns.GcDelta] >= 1, "expected a collection to be counted");
            Check(row[PerfColumns.HeapMB] > 0);
        });

        yield return ("Frame rate log: a long wait for the other player gets its own row, keyed on the tick that ended it", () =>
        {
            using var rig = new Rig();
            rig.Frame(5);
            PerfProbe.NoteWaiting(5); rig.Advance(80); PerfProbe.NoteTickStart(6);
            var row = rig.Rows().Single();
            Equal('W', (char)row[PerfColumns.Type]);
            Equal(6.0, row[PerfColumns.Tick]);
            Near(80, row[PerfColumns.WaitMs]);
            Equal(1.0, row[PerfColumns.Waiting]);
        });

        yield return ("Frame rate log: a short wait has no row of its own but is counted in the frame that ends it", () =>
        {
            using var rig = new Rig();
            rig.Frame(6);
            PerfProbe.NoteWaiting(6); rig.Advance(10); PerfProbe.NoteTickStart(7);
            Equal(0, rig.Rows().Count);
            rig.Advance(70); rig.Frame(7);
            var row = rig.Rows().Single();
            Equal('F', (char)row[PerfColumns.Type]);
            Near(10, row[PerfColumns.WaitMs]); Equal(1.0, row[PerfColumns.Waiting]); Equal(1.0, row[PerfColumns.Ticks]);
        });

        yield return ("Frame rate log: a wait that continues over several frames is one wait", () =>
        {
            using var rig = new Rig();
            rig.Frame(9);
            for (int frame = 0; frame < 4; frame++)
            {
                PerfProbe.NoteWaiting(9); PerfProbe.NoteWaiting(9);
                rig.Advance(30); rig.Frame(9);
            }
            PerfProbe.NoteTickStart(10);
            var wait = rig.Rows().Single(r => (char)r[PerfColumns.Type] == 'W');
            Near(120, wait[PerfColumns.WaitMs]); Equal(4.0, wait[PerfColumns.Waiting]);
        });

        yield return ("Frame rate log: a wait nobody asked about again (a pause) is dropped, not reported as a wait", () =>
        {
            using var rig = new Rig();
            rig.Frame(3);
            PerfProbe.NoteWaiting(3);
            rig.Advance(5); rig.Frame(3);
            rig.Advance(5); rig.Frame(3);
            rig.Advance(500);
            PerfProbe.NoteTickStart(4);
            Equal(0, rig.Rows().Count(r => (char)r[PerfColumns.Type] == 'W'));
        });

        yield return ("Frame rate log: a summary covers every frame since the last, with times as averages", () =>
        {
            using var rig = new Rig(thresholdMs: 1000, summaryTicks: 10);
            rig.Frame(0);
            foreach ((int tick, double ms) in new[] { (2, 10.0), (4, 10.0), (6, 10.0), (8, 10.0) })
            {
                long token = PerfProbe.Begin(PerfSlot.Steam); rig.Advance(2); PerfProbe.End(token);
                PerfProbe.NoteTickStart(tick);
                rig.Advance(ms - 2); rig.Frame(tick);
            }
            Equal(0, rig.Rows().Count);
            PerfProbe.NoteTickStart(10); rig.Advance(30); rig.Frame(10);
            var summary = rig.Rows().Single();
            Equal('S', (char)summary[PerfColumns.Type]);
            Equal(5.0, summary[PerfColumns.Frames]);
            Near(14, summary[PerfColumns.FrameMs]); Near(30, summary[PerfColumns.MaxFrameMs]);
            Equal(5.0, summary[PerfColumns.Ticks]);
            Near(1.6, summary[PerfColumns.SlotBase + (int)PerfSlot.Steam]);
            Equal(10.0, summary[PerfColumns.Tick]);
            // The next summary starts fresh, ten ticks later.
            rig.Advance(10); rig.Frame(15);
            Equal(0, rig.Rows().Count);
        });

        yield return ("Frame rate log: stopping writes the summary of the frames not yet summarized", () =>
        {
            var rig = new Rig(thresholdMs: 1000, summaryTicks: 1000);
            rig.Frame(0);
            rig.Advance(10); rig.Frame(1); rig.Advance(10); rig.Frame(2);
            PerfProbe.Stop();
            var rows = rig.Rows();
            Equal(1, rows.Count); Equal('S', (char)rows[0][PerfColumns.Type]); Equal(2.0, rows[0][PerfColumns.Frames]);
            rig.Dispose();
        });

        yield return ("Frame rate log: switched off it does nothing and costs nothing to ask", () =>
        {
            var rig = new Rig();
            rig.Dispose();
            Check(!PerfProbe.Enabled);
            Equal(0L, PerfProbe.Begin(PerfSlot.Sim));
            PerfProbe.End(0); PerfProbe.NoteBucket(); PerfProbe.NoteWaiting(1); PerfProbe.NoteTickStart(2);
            PerfProbe.CountSent(5); PerfProbe.OnFrame(1, 1, 1, 0, 100, false, true);
            Equal(0, rig.Rows().Count);
        });

        yield return ("Frame rate log: a full buffer costs the game nothing and the drops are recorded in the rows", () =>
        {
            var ring = new PerfRing(PerfColumns.Count, 1);
            PerfProbe.TestClock = null;
            PerfProbe.Start(ring, Environment.CurrentManagedThreadId, 0, 1000000);
            try
            {
                for (int i = 0; i < 5; i++) { PerfProbe.OnFrame(i, 1, 1, 0, 100, false, true); System.Threading.Thread.Sleep(1); }
                Check(ring.Dropped >= 3, $"expected drops, got {ring.Dropped}");
                Check(PerfProbe.Enabled);
            }
            finally { PerfProbe.Stop(); }
        });

        yield return ("Frame rate log: the per-frame path allocates nothing", () =>
        {
            var ring = new PerfRing(PerfColumns.Count, 16);
            PerfProbe.TestClock = null;
            PerfProbe.Start(ring, Environment.CurrentManagedThreadId, 1e9, 100000000);
            try
            {
                void Iteration(int i)
                {
                    long a = PerfProbe.Begin(PerfSlot.Sim);
                    long b = PerfProbe.Begin(PerfSlot.Replay);
                    PerfProbe.End(b); PerfProbe.End(a);
                    PerfProbe.NoteBucket(); PerfProbe.CountSent(10); PerfProbe.CountReceived(10);
                    PerfProbe.NoteWaiting(i); PerfProbe.NoteTickStart(i + 1);
                    PerfProbe.OnFrame(i, 1f, 1f, 0, 100, false, true);
                }
                for (int i = 0; i < 100; i++) Iteration(i);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) Iteration(i);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(allocated == 0, $"the probe allocated {allocated} bytes over 5000 frames");
            }
            finally { PerfProbe.Stop(); }
        });

        yield return ("Frame rate log file: header, column names, rows in order, and a closing line", () =>
        {
            string path = TempFile();
            var ring = new PerfRing(PerfColumns.Count, 16);
            ring.TryPush(Row('F', 1)); ring.TryPush(Row('W', 2)); ring.TryPush(Row('S', 3));
            var header = new[] { "# BeaverBuddies frame rate log, format 1", "# mod|beaverbuddies|BeaverBuddies|1.0.10" };
            var writer = new PerfWriter(path, header, ring, null);
            Check(writer.Start()); writer.Stop();
            Check(writer.Failure == null, writer.Failure ?? "");
            string[] lines = File.ReadAllLines(path);
            Equal(header[0], lines[0]); Equal(header[1], lines[1]);
            Equal(PerfColumns.HeaderLine(), lines[2]);
            Equal(7, lines.Length);
            Equal("F", lines[3].Split(',')[0]); Equal("W", lines[4].Split(',')[0]); Equal("S", lines[5].Split(',')[0]);
            Equal(PerfColumns.Count, lines[4].Split(',').Length);
            Equal("# end", lines[6]);
        });

        yield return ("Frame rate log file: rows appear while the game is still running", () =>
        {
            string path = TempFile();
            var ring = new PerfRing(PerfColumns.Count, 16);
            var writer = new PerfWriter(path, new[] { "# header" }, ring, null);
            Check(writer.Start());
            try
            {
                ring.TryPush(Row('F', 77));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                string content = "";
                while (DateTime.UtcNow < deadline && !content.Contains("F,77,"))
                {
                    System.Threading.Thread.Sleep(50);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                }
                Check(content.Contains("F,77,"), "the row was not written within three seconds");
            }
            finally { writer.Stop(); }
        });

        yield return ("Frame rate log file: rows lost because the writer fell behind are reported in the file", () =>
        {
            string path = TempFile();
            var ring = new PerfRing(PerfColumns.Count, 2);
            for (int i = 0; i < 5; i++) ring.TryPush(Row('F', i));
            var writer = new PerfWriter(path, new[] { "# header" }, ring, null);
            Check(writer.Start()); writer.Stop();
            Check(File.ReadAllText(path).Contains("# rows dropped so far because the writer fell behind: 3"));
        });

        yield return ("Frame rate log file: a file that cannot be opened is reported and nothing is started", () =>
        {
            string path = Path.Combine(TempFile(), "missing-folder", "log.csv");
            var writer = new PerfWriter(path, new[] { "# header" }, new PerfRing(PerfColumns.Count, 4), null);
            Check(!writer.Start());
            Check(!string.IsNullOrEmpty(writer.Failure));
            writer.Stop();
        });

        yield return ("Frame rate log patch report: a patch is one line with a fixed number of fields", () =>
        {
            string line = PerfPatchFormat.PatchLine("hot", "Timberborn.TickSystem.TickableBucketService.TickBuckets", "prefix",
                "beaverbuddies", 400, 0, new[] { "x", "y" }, null, "BeaverBuddies", "BeaverBuddies.Patcher.Prefix");
            Equal("# patch|hot|Timberborn.TickSystem.TickableBucketService.TickBuckets|prefix|beaverbuddies|priority=400|index=0|before=x;y|after=|BeaverBuddies|BeaverBuddies.Patcher.Prefix", line);
            Equal(11, line.Split('|').Length);
            string messy = PerfPatchFormat.PatchLine("other", "A.B", "postfix", "some|mod\r\nid", 1, 2, null, null, null, null);
            Check(!messy.Contains('\n') && !messy.Contains('\r'));
            Equal(11, messy.Split('|').Length);
        });

        yield return ("Frame rate log patch report: the hot list names what this mod patches every tick and frame", () =>
        {
            Check(PerfPatchFormat.IsHot("TickableBucketService", "TickBuckets"));
            Check(PerfPatchFormat.IsHot("TickableEntity", "Tick"));
            Check(PerfPatchFormat.IsHot("MovementAnimator", "Update"));
            Check(PerfPatchFormat.IsHot("RandomNumberGenerator", "Range"));
            Check(PerfPatchFormat.IsHot("DayNightCycle", "get_FluidSecondsPassedToday"));
            Check(!PerfPatchFormat.IsHot("Floodgate", "SetHeightAndSynchronize"));
            Check(!PerfPatchFormat.IsHot(null, "Tick") && !PerfPatchFormat.IsHot("TickableEntity", null));
            Check(PerfPatchFormat.HotMethodCount >= 30);
            Equal("a/b  c", PerfPatchFormat.Clean("a|b\r\nc"));
            Equal("", PerfPatchFormat.Clean(null));
        });

        yield return ("Frame rate log: a player's name is safe in a file name", () =>
        {
            Equal("Kyler", PerfFileName.Safe("Kyler"));
            Equal("a_b_c_d_e", PerfFileName.Safe("a b/c\\d:e"));
            Equal("Player", PerfFileName.Safe("")); Equal("Player", PerfFileName.Safe("   ")); Equal("Player", PerfFileName.Safe(null!));
            Equal(PerfFileName.MaxLength, PerfFileName.Safe(new string('x', 100)).Length);
            Equal("_", PerfFileName.Safe(((char)233).ToString()));
            Equal("a-b_1", PerfFileName.Safe("a-b_1"));
        });

        yield return ("Frame rate log patch report: hot, shared and other methods are listed, this mod's own quiet ones only counted", () =>
        {
            var lines = new List<string>();
            PerfPatchBuilder.Build(new[]
            {
                Method("Timberborn.TickSystem.TickableBucketService", "TickBuckets", Patch("prefix", "beaverbuddies", 400, "BeaverBuddies"), Patch("prefix", "other.mod", 600, "OtherMod")),
                Method("Some.Quiet.Thing", "Set", Patch("prefix", "beaverbuddies", 400, "BeaverBuddies")),
                Method("Baz.Shared", "Run", Patch("postfix", "a", 400, "A"), Patch("postfix", "beaverbuddies", 400, "BeaverBuddies")),
                Method("Solo.Only", "Thing", Patch("transpiler", "z", 400, "Z")),
            }, "beaverbuddies", lines);
            Equal("# patches: methods=4 hot=1 shared=1 other=1 ownedOnlyByThisMod=1", lines[0]);
            var owners = lines.Where(l => l.StartsWith("# patch-owner|")).ToList();
            Check(owners.SequenceEqual(new[] { "# patch-owner|a|methods=1", "# patch-owner|beaverbuddies|methods=3", "# patch-owner|other.mod|methods=1", "# patch-owner|z|methods=1" }), string.Join(" / ", owners));
            var patches = lines.Where(l => l.StartsWith("# patch|")).ToList();
            Equal(5, patches.Count);
            Check(patches.All(l => l.Split('|').Length == 11));
            // In name order, so two logs list the same methods in the same order.
            Check(patches.Select(l => l.Split('|')[1]).SequenceEqual(new[] { "shared", "shared", "other", "hot", "hot" }), string.Join(" / ", patches));
            Check(patches[3].Contains("|beaverbuddies|priority=400|") && patches[4].Contains("|other.mod|priority=600|"));
            Check(!lines.Any(l => l.Contains("Some.Quiet.Thing")));
        });

        yield return ("Frame rate log patch report: a hot method is listed even when only this mod patches it", () =>
        {
            var lines = new List<string>();
            PerfPatchBuilder.Build(new[] { Method("Timberborn.CharacterMovementSystem.MovementAnimator", "Update", Patch("prefix", "beaverbuddies", 400, "BeaverBuddies")) }, "beaverbuddies", lines);
            Check(lines[0].Contains("hot=1") && lines[0].Contains("ownedOnlyByThisMod=0"));
            Equal(1, lines.Count(l => l.StartsWith("# patch|hot|")));
        });

        yield return ("Frame rate log patch report: a huge number of other mods' patches is cut off and says so", () =>
        {
            var lines = new List<string>();
            PerfPatchBuilder.Build(Enumerable.Range(0, 700).Select(i => Method("Big.Type" + i.ToString("D4"), "M", Patch("prefix", "x", 400, "X"))), "beaverbuddies", lines);
            Equal(PerfPatchBuilder.MaxDetailLines, lines.Count(l => l.StartsWith("# patch|")));
            Check(lines.Any(l => l == "# patches-truncated: 100 more patch lines not shown"));
            Check(lines.Any(l => l == "# patch-owner|x|methods=700"));
        });

        yield return ("Frame rate log patch report: no patches at all is a valid, empty report", () =>
        {
            var lines = new List<string>();
            PerfPatchBuilder.Build(new PatchedMethodRecord[0], "beaverbuddies", lines);
            Equal("# patches: methods=0 hot=0 shared=0 other=0 ownedOnlyByThisMod=0", lines[0]);
            Equal(0, lines.Count(l => l.StartsWith("# patch|")));
        });
    }
}
