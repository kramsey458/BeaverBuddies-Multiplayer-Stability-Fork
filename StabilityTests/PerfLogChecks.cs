#nullable enable
using System.Text;
using BeaverBuddies.Perf;

// The frame rate log: the ring the game thread writes into, the CSV, and the writer thread.
// The probe is exercised through its real entry points, so a change that made it allocate or
// miscount would fail here rather than in a session.
static class PerfLogChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    static PerfSample Row(PerfRowKind kind = PerfRowKind.Spike, int tick = 0) =>
        new PerfSample { Kind = kind, Tick = tick };

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Ring: drains in the order it was filled", () =>
        {
            var ring = new PerfRing(8);
            for (int i = 1; i <= 5; i++) ring.Add(Row(tick: i));
            Equal(5, ring.Count);
            var taken = new PerfSample[8];
            Equal(5, ring.Drain(taken));
            for (int i = 0; i < 5; i++) Equal(i + 1, taken[i].Tick);
            Equal(0, ring.Count);
            Equal(0, ring.Drain(taken));
        });
        yield return ("Ring: wraps around without growing, and keeps working", () =>
        {
            var ring = new PerfRing(4);
            for (int i = 1; i <= 4; i++) ring.Add(Row(tick: i));
            var taken = new PerfSample[4];
            Equal(4, ring.Drain(taken));
            Equal(1, taken[0].Tick);
            for (int i = 5; i <= 8; i++) ring.Add(Row(tick: i));
            Equal(4, ring.Drain(taken));
            Equal(5, taken[0].Tick);
            Equal(8, taken[3].Tick);
            Equal(0L, ring.Dropped);
        });
        yield return ("Ring: a writer that cannot keep up drops the oldest and says how many", () =>
        {
            var ring = new PerfRing(3);
            for (int i = 1; i <= 6; i++) ring.Add(Row(tick: i));
            Equal(3, ring.Count);
            Equal(3L, ring.Dropped);
            var taken = new PerfSample[3];
            Equal(3, ring.Drain(taken));
            // The three most recent survived, so a burst keeps the frames nearest the problem.
            Equal(4, taken[0].Tick);
            Equal(6, taken[2].Tick);
        });
        yield return ("Ring: drains in batches when the buffer given is smaller", () =>
        {
            var ring = new PerfRing(16);
            for (int i = 1; i <= 10; i++) ring.Add(Row(tick: i));
            var taken = new PerfSample[4];
            Equal(4, ring.Drain(taken)); Equal(1, taken[0].Tick);
            Equal(4, ring.Drain(taken)); Equal(5, taken[0].Tick);
            Equal(2, ring.Drain(taken)); Equal(9, taken[0].Tick);
            Equal(0, ring.Drain(taken));
        });

        yield return ("CSV: a row has one value per column, in the header's order", () =>
        {
            string[] columns = PerfCsv.Header.Split(',');
            string[] values = PerfCsv.Format(Row()).Split(',');
            Equal(columns.Length, values.Length);
            Equal("kind", columns[0]);
            Equal("spike", values[0]);
        });
        yield return ("CSV: numbers are written the same way whatever the computer's language", () =>
        {
            var was = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                // A language that writes 1,5 rather than 1.5 would otherwise split a row into extra columns.
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var sample = Row();
                sample.FrameMs = 1.5f;
                sample.HeapBytes = 1234567890L;
                string[] columns = PerfCsv.Header.Split(',');
                string[] values = PerfCsv.Format(sample).Split(',');
                Equal(columns.Length, values.Length);
                Equal("1.5", values[Array.IndexOf(columns, "frameMs")]);
                Equal("1234567890", values[Array.IndexOf(columns, "heapBytes")]);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = was; }
        });
        yield return ("CSV: the summary rows are labelled, and flags are 1 or 0", () =>
        {
            string[] columns = PerfCsv.Header.Split(',');
            var sample = Row(PerfRowKind.Summary);
            sample.Holding = true;
            sample.Saving = false;
            string[] values = PerfCsv.Format(sample).Split(',');
            Equal("summary", values[0]);
            Equal("1", values[Array.IndexOf(columns, "holding")]);
            Equal("0", values[Array.IndexOf(columns, "saving")]);
        });
        yield return ("CSV: writing a row reuses the caller's builder instead of allocating", () =>
        {
            var line = new StringBuilder();
            var sample = Row();
            PerfCsv.Format(line, in sample);
            Check(line.Length > 0);
            Equal(PerfCsv.Format(sample), line.ToString());
        });

        yield return ("Probe: with logging off nothing is recorded and no clock is read", () =>
        {
            PerfProbe.StopSession();
            Equal(false, PerfProbe.Enabled);
            Equal(0L, PerfProbe.Begin());
            PerfProbe.BeginFrame();
            PerfProbe.CountTick();
            PerfProbe.NoteWaitingForPeer();
            PerfFrameState state = default;
            PerfProbe.EndFrame(in state);
            // Nothing above may throw, and with no ring attached there is nowhere for a row to go.
        });
        yield return ("Probe: a slow frame is recorded, a quick one is not", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 1000, ticksPerSummary: 1000000);
            try
            {
                PerfProbe.BeginFrame();
                System.Threading.Thread.Sleep(10);
                PerfProbe.EndFrame(Frame(tick: 7));
                Equal(0, ring.Count);

                PerfProbe.StartSession(ring, spikeThresholdMs: 5, ticksPerSummary: 1000000);
                PerfProbe.BeginFrame();
                System.Threading.Thread.Sleep(20);
                PerfProbe.EndFrame(Frame(tick: 8));
                Equal(1, ring.Count);
                var taken = new PerfSample[4];
                ring.Drain(taken);
                Equal(PerfRowKind.Spike, taken[0].Kind);
                Equal(8, taken[0].Tick);
                Equal(1, taken[0].Frames);
                Check(taken[0].FrameMs >= 5, $"expected the frame to be timed, got {taken[0].FrameMs} ms");
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: waiting for the other player is counted as frames, not as a blocked wait", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 0.0001f, ticksPerSummary: 1000000);
            try
            {
                // A frame that ran no ticks because the other player's events had not arrived.
                PerfProbe.BeginFrame();
                PerfProbe.NoteWaitingForPeer();
                PerfProbe.EndFrame(Frame(tick: 20, ticksBehind: 3));
                // Then one that ticked twice and did not wait.
                PerfProbe.BeginFrame();
                PerfProbe.CountTick();
                PerfProbe.CountTick();
                PerfProbe.EndFrame(Frame(tick: 22));

                var taken = new PerfSample[4];
                Equal(2, ring.Drain(taken));
                Equal(1, taken[0].WaitFrames);
                Equal(0, taken[0].TicksDone);
                Equal(3, taken[0].TicksBehind);
                Equal(0, taken[1].WaitFrames);
                Equal(2, taken[1].TicksDone);
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: each frame's counts start again, so nothing carries over", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 0.0001f, ticksPerSummary: 1000000);
            try
            {
                PerfProbe.BeginFrame();
                PerfProbe.CountSend(4096);
                PerfProbe.CountSpeedChange();
                PerfProbe.EndFrame(Frame(tick: 1));
                PerfProbe.BeginFrame();
                PerfProbe.EndFrame(Frame(tick: 2));

                var taken = new PerfSample[4];
                Equal(2, ring.Drain(taken));
                Equal(4096, taken[0].SendBytes);
                Equal(1, taken[0].SendCount);
                Equal(1, taken[0].SpeedChanges);
                Equal(0, taken[1].SendBytes);
                Equal(0, taken[1].SendCount);
                Equal(0, taken[1].SpeedChanges);
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: a span's time lands in its own column", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 0.0001f, ticksPerSummary: 1000000);
            try
            {
                PerfProbe.BeginFrame();
                long started = PerfProbe.Begin();
                System.Threading.Thread.Sleep(12);
                PerfProbe.End(PerfProbe.Span.Send, started);
                PerfProbe.EndFrame(Frame(tick: 3));

                var taken = new PerfSample[4];
                Equal(1, ring.Drain(taken));
                Check(taken[0].SendMs >= 8, $"expected the sleep to be timed, got {taken[0].SendMs} ms");
                Equal(0f, taken[0].ReadMs);
                Equal(0f, taken[0].HashMs);
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: time spent on another thread is not charged to this frame", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 0.0001f, ticksPerSummary: 1000000);
            try
            {
                PerfProbe.BeginFrame();
                // The activity and chat lanes write from their own threads while a frame runs.
                // A real thread, not a task: waiting on a task can run it on this thread instead.
                var elsewhere = new System.Threading.Thread(() =>
                {
                    long started = PerfProbe.Begin();
                    System.Threading.Thread.Sleep(15);
                    PerfProbe.End(PerfProbe.Span.Send, started);
                    PerfProbe.CountSend(9999);
                });
                elsewhere.Start();
                elsewhere.Join();
                PerfProbe.EndFrame(Frame(tick: 4));

                var taken = new PerfSample[4];
                Equal(1, ring.Drain(taken));
                Equal(0f, taken[0].SendMs);
                Equal(0, taken[0].SendBytes);
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: a summary is written every so many ticks, with the totals of the window", () =>
        {
            var ring = new PerfRing(64);
            // A threshold nothing reaches, so only summaries are written.
            PerfProbe.StartSession(ring, spikeThresholdMs: 100000, ticksPerSummary: 5);
            try
            {
                for (int tick = 0; tick <= 5; tick++)
                {
                    PerfProbe.BeginFrame();
                    PerfProbe.CountTick();
                    PerfProbe.EndFrame(Frame(tick: tick));
                }
                var taken = new PerfSample[8];
                Equal(1, ring.Drain(taken));
                Equal(PerfRowKind.Summary, taken[0].Kind);
                Equal(5, taken[0].Tick);
                Equal(6, taken[0].Frames);
                Equal(6, taken[0].TicksDone);
                Check(taken[0].MaxFrameMs >= 0);
            }
            finally { PerfProbe.StopSession(); }
        });
        yield return ("Probe: a frame that threw on the way out does not swallow the next one", () =>
        {
            var ring = new PerfRing(16);
            PerfProbe.StartSession(ring, spikeThresholdMs: 0.0001f, ticksPerSummary: 1000000);
            try
            {
                // Two starts in a row: the first frame never ended.
                PerfProbe.BeginFrame();
                PerfProbe.CountTick();
                PerfProbe.BeginFrame();
                PerfProbe.EndFrame(Frame(tick: 9));

                var taken = new PerfSample[4];
                Equal(1, ring.Drain(taken));
                Equal(9, taken[0].Tick);
                // The abandoned frame's tick is not added to this one.
                Equal(0, taken[0].TicksDone);
            }
            finally { PerfProbe.StopSession(); }
        });

        yield return ("Writer: writes the header, then the rows, on a thread of its own", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "bb-perf-" + Guid.NewGuid().ToString("N") + ".csv");
            var ring = new PerfRing(32);
            try
            {
                var writer = new PerfWriter(ring,
                    () => new StreamWriter(path, false),
                    () => new[] { "# format=test", "# mod=a|A|1" },
                    _ => { },
                    flushIntervalMs: 50);
                writer.Start();
                for (int i = 1; i <= 3; i++) ring.Add(Row(tick: i));
                Check(SpinWait.SpinUntil(() => writer.Written >= 3, 5000), "the writer did not write the rows");
                writer.Stop();

                string[] lines = File.ReadAllLines(path);
                Equal("# format=test", lines[0]);
                Equal("# mod=a|A|1", lines[1]);
                Equal(PerfCsv.Header, lines[2]);
                // Two header lines, the column names, then one line per row.
                Equal(6, lines.Length);
                Check(lines[3].StartsWith("spike,1,"), lines[3]);
                Check(lines[4].StartsWith("spike,2,"), lines[4]);
                Check(lines[5].StartsWith("spike,3,"), lines[5]);
            }
            finally { try { File.Delete(path); } catch (Exception) { } }
        });
        yield return ("Writer: rows added while it is stopping are still written", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "bb-perf-" + Guid.NewGuid().ToString("N") + ".csv");
            var ring = new PerfRing(32);
            try
            {
                var writer = new PerfWriter(ring, () => new StreamWriter(path, false), null, _ => { }, flushIntervalMs: 10000);
                writer.Start();
                for (int i = 1; i <= 4; i++) ring.Add(Row(tick: i));
                writer.Stop();
                Equal(4L, writer.Written);
                Equal(5, File.ReadAllLines(path).Length);
            }
            finally { try { File.Delete(path); } catch (Exception) { } }
        });
        yield return ("Writer: a file it cannot open is reported once and never throws", () =>
        {
            var ring = new PerfRing(8);
            string reported = "";
            var writer = new PerfWriter(ring,
                () => throw new IOException("no such folder"),
                null,
                message => reported = message,
                flushIntervalMs: 10);
            writer.Start();
            Check(SpinWait.SpinUntil(() => writer.Failed, 5000), "the failure was not reported");
            ring.Add(Row());
            writer.Stop();
            Check(reported.Contains("no such folder"), reported);
            Equal(0L, writer.Written);
        });
    }

    static PerfFrameState Frame(int tick, int ticksBehind = 0) => new PerfFrameState
    {
        Tick = tick,
        TicksBehind = ticksBehind,
        Speed = 1,
        TargetSpeed = 1,
        HostPacingPercent = 100,
        FpsPacingPercent = 100,
    };
}
