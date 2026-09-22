using System.Collections.Concurrent;
using System.Diagnostics;
using BeaverBuddies.Steam;
using TimberNet;

// The connection panel's ping over Steam, as a function of how long the players' game frames are.
//
// Steam is only served from the game thread (see SteamLinkSocket), and the game thread serves it once per frame.
// A probe therefore waits for a pump at each of its four hops: the host sending it, the guest receiving it, the guest
// sending the reply and the host receiving that. A reply is queued just after the pump that delivered the probe, so
// it waits a whole frame; a message that arrives at a random moment waits half a frame on average. That is about a
// frame and a half for each player, and at a high game speed most of a frame is simulation, so frames are long and
// every one of those waits grows with them. This runs the production transport, TimberServer,
// TimberClient and ping tracker over a fake Steam network with a fixed one-way delay, with each player's game thread
// spending its frames the way a fast game does: simulation, then the rest of the frame.
static class PingCadenceChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);
    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    static void SpinFor(long ticks)
    {
        long end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(30);
    }

    // ---------------- fake Steam with a fixed delay ----------------

    sealed class DelayedWire
    {
        readonly object gate = new();
        readonly Queue<(long Due, byte[] Data)>[] inbox = { new(), new() };
        public long DelayTicks;

        public void Post(int toSide, byte[] data)
        {
            lock (gate) inbox[toSide].Enqueue((Stopwatch.GetTimestamp() + DelayTicks, data));
        }

        public int Take(int side, Action<byte[]> deliver, int max)
        {
            int taken = 0;
            while (taken < max)
            {
                byte[] data;
                lock (gate)
                {
                    if (inbox[side].Count == 0 || inbox[side].Peek().Due > Stopwatch.GetTimestamp()) break;
                    data = inbox[side].Dequeue().Data;
                }
                deliver(data);
                taken++;
            }
            return taken;
        }
    }

    sealed class SharedLink { public volatile bool GuestStarted, HostAccepted; }

    // One player's Steam. Like the real one it is only ever called on that player's game thread.
    sealed class DelayedBackend : ISteamLinkBackend
    {
        readonly DelayedWire wire; readonly int side; readonly SharedLink link;
        readonly object gate = new();
        readonly List<(ulong Conn, ulong Remote)> incoming = new();
        public DelayedBackend Other;
        public volatile int GameThreadId;
        public ulong ListenHandle;
        public int SendCount;

        public DelayedBackend(DelayedWire wire, int side, SharedLink link) { this.wire = wire; this.side = side; this.link = link; }

        void OnGameThread()
        {
            if (Environment.CurrentManagedThreadId != GameThreadId) throw new InvalidOperationException("Steam was called off the game thread");
        }

        public (ulong Conn, ulong Remote)[] TakeIncoming()
        {
            lock (gate) { var all = incoming.ToArray(); incoming.Clear(); return all; }
        }

        public ulong CreateListenSocket() { OnGameThread(); return ListenHandle = 5; }
        public void CloseListenSocket(ulong listen) { OnGameThread(); }

        public ulong Connect(ulong remote)
        {
            OnGameThread();
            link.GuestStarted = true;
            lock (Other.gate) Other.incoming.Add((20, 2));
            return 10;
        }

        public bool Accept(ulong connection) { OnGameThread(); link.HostAccepted = true; return true; }
        public void Configure(ulong connection) { OnGameThread(); }

        public LinkState GetState(ulong connection, out int endReason, out string endDebug)
        {
            OnGameThread();
            endReason = 0; endDebug = "";
            return link.GuestStarted && link.HostAccepted ? LinkState.Connected : LinkState.Connecting;
        }

        public LinkSend Send(ulong connection, byte[] data, int offset, int count)
        {
            OnGameThread();
            var copy = new byte[count]; Buffer.BlockCopy(data, offset, copy, 0, count);
            Interlocked.Increment(ref SendCount);
            wire.Post(1 - side, copy);
            return LinkSend.Ok;
        }

        public int Receive(ulong connection, Action<byte[]> deliver, int max) { OnGameThread(); return wire.Take(side, deliver, max); }
        public void Close(ulong connection, int reason, string debug, bool linger) { OnGameThread(); }
    }

    // ---------------- a player's game loop ----------------

    // What one frame of a game at some speed is made of.
    sealed record FrameShape(double FrameMs, bool PumpBetweenTicks, double UnpumpableMs = 0)
    {
        // Everything that is not simulation: rendering, UI, the garbage collector. Steam is not served during it.
        public const double OtherMs = 8;
        public static readonly FrameShape Fast = new(2, false);
    }

    sealed class GameLoop : IDisposable
    {
        readonly Thread thread; volatile bool stop;
        readonly ConcurrentQueue<Action> work = new();
        public readonly DelayedBackend Backend;
        public readonly SteamLinkManager Manager;
        public volatile TimberNetBase Net;
        public volatile FrameShape Shape = FrameShape.Fast;
        long nextUnpumpableAt;
        readonly Random random;

        public GameLoop(DelayedBackend backend, SteamLinkManager manager, string name, int seed)
        {
            Backend = backend; Manager = manager; random = new Random(seed);
            thread = new Thread(Loop) { IsBackground = true, Name = name };
            thread.Start();
            SpinWait.SpinUntil(() => backend.GameThreadId != 0);
        }

        public void Run(Action action)
        {
            if (Environment.CurrentManagedThreadId == thread.ManagedThreadId) { action(); return; }
            using var done = new ManualResetEventSlim(); Exception error = null;
            work.Enqueue(() => { try { action(); } catch (Exception e) { error = e; } finally { done.Set(); } });
            if (!done.Wait(4000)) throw new TimeoutException("the game thread did not run the work");
            if (error != null) throw new Exception("on the game thread: " + error.Message, error);
        }

        public T Run<T>(Func<T> function) { T result = default; Run((Action)(() => result = function())); return result; }

        void Loop()
        {
            Backend.GameThreadId = Environment.CurrentManagedThreadId;
            while (!stop)
            {
                long start = Stopwatch.GetTimestamp();
                FrameShape shape = Shape;
                // Real frames vary. Without this two players with the same frame length stay locked in step, and
                // where their pumps fall relative to each other, not the frame length, decides the ping.
                double frameMs = shape.FrameMs * (0.85 + 0.3 * random.NextDouble());
                while (work.TryDequeue(out var action)) action();
                foreach (var (connection, remote) in Backend.TakeIncoming())
                    Manager.OnIncomingConnection(Backend.ListenHandle, connection, remote);
                Net?.Update();
                // SteamNetPump.Update: the once-per-frame pump.
                Manager.Pump();
                Simulate(shape, frameMs);
                // Whatever is left of the frame.
                while (Stopwatch.GetTimestamp() < start + Ticks(frameMs)) Thread.SpinWait(30);
            }
        }

        // The game's ticks: many small buckets, and at the start of each tick a part that nothing can interrupt (the
        // singletons and the wait for the parallel work).
        void Simulate(FrameShape shape, double frameMs)
        {
            long end = Stopwatch.GetTimestamp() + Ticks(Math.Max(0, frameMs - FrameShape.OtherMs));
            long bucket = Ticks(0.2), tick = Ticks(85);
            while (Stopwatch.GetTimestamp() < end)
            {
                long now = Stopwatch.GetTimestamp();
                if (shape.UnpumpableMs > 0 && now >= nextUnpumpableAt)
                {
                    nextUnpumpableAt = now + tick;
                    SpinFor(Math.Min(Ticks(shape.UnpumpableMs), end - now));
                    continue;
                }
                SpinFor(bucket);
                if (shape.PumpBetweenTicks) Manager.PumpBetweenTicks(false);
            }
        }

        public void Dispose() { stop = true; thread.Join(2000); }
    }

    // A host and a guest in one process, joined over the delayed fake Steam and running the production session code.
    sealed class Session : IDisposable
    {
        public readonly GameLoop Host, Guest;
        public readonly TimberServer Server;
        public TimberClient Client;

        public Session(double oneWayMs)
        {
            var wire = new DelayedWire { DelayTicks = Ticks(oneWayMs) };
            var shared = new SharedLink();
            var hostBackend = new DelayedBackend(wire, 0, shared); var guestBackend = new DelayedBackend(wire, 1, shared);
            hostBackend.Other = guestBackend; guestBackend.Other = hostBackend;
            Func<double> clock = Now;
            Host = new GameLoop(hostBackend, new SteamLinkManager(hostBackend, clock, id => "host-" + id), "host game thread", 11);
            Guest = new GameLoop(guestBackend, new SteamLinkManager(guestBackend, clock, id => "guest-" + id), "guest game thread", 22);
            var listener = new SteamLinkListener(Host.Manager, Host.Run, _ => true);
            Server = new TimberServer(listener, () => Task.FromResult(new byte[1000]), null) { CompatibilityIdentity = "same" };
            Host.Net = Server;
            try
            {
                Server.Start();
                var socket = Guest.Run(() => Guest.Manager.Connect(1001));
                Client = new TimberClient(socket) { CompatibilityIdentity = "same" };
                Guest.Net = Client;
                Guest.Run(() => Client.Start());
                Check(SpinWait.SpinUntil(() => HostPing() != null, 6000), "the host never measured a ping over the fake Steam");
            }
            catch (Exception) { Dispose(); throw; }
        }

        public double? HostPing()
        {
            var peers = Server.GetNetworkStatus().Peers;
            return peers.Count == 1 ? peers[0].RttMs : null;
        }

        // The median of the ping the connection panel would show, after the frames have settled to this shape.
        public double MeasurePing(FrameShape host, FrameShape guest, int settleMs, int measureMs)
        {
            Host.Shape = host; Guest.Shape = guest;
            Thread.Sleep(settleMs);
            var samples = new List<double>();
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < measureMs)
            {
                double? ping = HostPing();
                if (ping != null) samples.Add(ping.Value);
                Thread.Sleep(10);
            }
            Check(samples.Count > 0, "no ping was measured");
            samples.Sort();
            return samples[samples.Count / 2];
        }

        public void Dispose()
        {
            try { Host.Run(() => { Server.Close(); }); Guest.Run(() => { Client.Close(); }); } catch (Exception) { }
            Host.Dispose(); Guest.Dispose();
        }
    }

    // Probes as often as the game thread comes round to send them, so a run needs fewer seconds.
    static void WithFastProbes(Action run)
    {
        int previous = TimberServer.StatusIntervalMs;
        TimberServer.StatusIntervalMs = 30;
        try { run(); } finally { TimberServer.StatusIntervalMs = previous; }
    }

    // ---------------- report ----------------

    /// <summary>Prints the ping the panel would show for a range of frame lengths, with and without the between-ticks pump.</summary>
    public static void PrintReport()
    {
        WithFastProbes(PrintReportRows);
    }

    static void PrintReportRows()
    {
        const double oneWay = 5;
        Console.WriteLine($"Ping shown for a guest over Steam, with a one-way delay of {oneWay} ms (a {2 * oneWay} ms round trip on the wire).");
        Console.WriteLine("Frames are 8 ms of rendering and the rest simulation; 'per frame' serves Steam once per frame, 'between ticks' every 1 ms during the simulation.");
        Console.WriteLine();
        Console.WriteLine("host frame  guest frame  |  per frame (ms)  predicted  |  between ticks (ms)  |  with a 12 ms unpumpable part per tick (ms)");
        var rows = new (double Host, double Guest)[] { (8, 8), (17, 17), (17, 33), (17, 60), (17, 100), (17, 200), (60, 17), (100, 17), (100, 100), (200, 200) };
        foreach (var (hostMs, guestMs) in rows)
        {
            double perFrame, between, worst;
            int settle = (int)Math.Max(800, (hostMs + guestMs) * 12);
            using (var session = new Session(oneWay)) perFrame = session.MeasurePing(new FrameShape(hostMs, false), new FrameShape(guestMs, false), settle, 800);
            using (var session = new Session(oneWay)) between = session.MeasurePing(new FrameShape(hostMs, true), new FrameShape(guestMs, true), settle, 800);
            using (var session = new Session(oneWay)) worst = session.MeasurePing(new FrameShape(hostMs, true, 12), new FrameShape(guestMs, true, 12), settle, 800);
            double predicted = 2 * oneWay + 1.5 * (hostMs + guestMs);
            Console.WriteLine($"{hostMs,10:0}  {guestMs,11:0}  |  {perFrame,15:0}  {predicted,9:0}  |  {between,19:0}  |  {worst,43:0}");
        }
    }

    // ---------------- checks ----------------

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Pump timing records the wait between pumps and forgets it per window", () =>
        {
            var timing = new PumpTiming();
            timing.Record(1.000); Equal(0, timing.Gaps);          // the first pump has no gap before it
            timing.Record(1.010); timing.Record(1.030); timing.Record(1.130);
            Equal(3, timing.Gaps); Equal(1, timing.SlowGaps);
            Check(Math.Abs(timing.MeanMs - 43.333) < 0.01, "mean " + timing.MeanMs);
            Check(Math.Abs(timing.LongestMs - 100) < 0.01, "longest " + timing.LongestMs);
            timing.StartNewWindow(); Equal(0, timing.Gaps); Equal(0.0, timing.LongestMs);
            timing.Record(1.140); Equal(1, timing.Gaps);           // the last pump is remembered across windows
            timing.Reset(); timing.Record(5.0); Equal(0, timing.Gaps);
            timing.Record(4.0); Equal(0, timing.Gaps);             // a clock that goes backwards is not a gap
        });
        yield return ("The timing line puts the once-per-frame wait next to the wait actually seen", () =>
        {
            var frames = new PumpTiming(); var all = new PumpTiming();
            for (int i = 0; i < 10; i++) frames.Record(i * 0.1);
            for (int i = 0; i < 1000; i++) all.Record(i * 0.001);
            string line = PumpTiming.Describe(60, frames, all);
            Check(line.Contains("over 60 s") && line.Contains("100.0 ms on average") && line.Contains("1.0 ms on average"), line);
            Check(line.Contains("9 gaps over 50 ms") && line.Contains("(0 gaps over 50 ms)"), line);
        });
        yield return ("Pumping between ticks moves data by itself, at most once a millisecond, and only for a live connection", () =>
        {
            double now = 100;
            var wire = new DelayedWire(); var shared = new SharedLink();
            var hostBackend = new DelayedBackend(wire, 0, shared) { GameThreadId = Environment.CurrentManagedThreadId };
            var guestBackend = new DelayedBackend(wire, 1, shared) { GameThreadId = Environment.CurrentManagedThreadId };
            hostBackend.Other = guestBackend; guestBackend.Other = hostBackend;
            var host = new SteamLinkManager(hostBackend, () => now, id => "host");
            var guest = new SteamLinkManager(guestBackend, () => now, id => "guest");
            var listener = new SteamLinkListener(host, action => action(), _ => true);
            listener.Start();
            var guestSocket = guest.Connect(1001);
            // Nothing is up yet: pumping between ticks must do nothing and must not throw.
            host.PumpBetweenTicks(true); guest.PumpBetweenTicks(true);
            Equal(0, guestBackend.SendCount);
            foreach (var (connection, remote) in hostBackend.TakeIncoming()) host.OnIncomingConnection(hostBackend.ListenHandle, connection, remote);
            for (int i = 0; i < 3; i++) { now += 0.02; host.Pump(); guest.Pump(); }
            var hostSocket = (SteamLinkSocket)Task.Run(() => listener.AcceptClient()).GetAwaiter().GetResult();
            Check(guestSocket.Connected && hostSocket.Connected, "the connection never came up");

            var payload = new byte[] { 1, 2, 3, 4, 5 };
            guestSocket.Write(payload, 0, payload.Length);
            int sentBefore = guestBackend.SendCount;
            // Only 0.5 ms since the last pump: not due, and nothing is sent.
            now += 0.0005; guest.PumpBetweenTicks(false);
            Equal(sentBefore, guestBackend.SendCount);
            // A millisecond has passed: the data goes, with no full pump.
            now += 0.0006; guest.PumpBetweenTicks(false);
            Equal(sentBefore + 1, guestBackend.SendCount);
            // And it arrives on the other side, again with no full pump.
            now += 0.002; host.PumpBetweenTicks(false);
            var read = new byte[5];
            Check(Task.Run(() => hostSocket.Read(read, 0, 5)).Wait(2000), "the data never arrived");
            Check(read.SequenceEqual(payload), "the data changed on the way");
            // Force skips the wait.
            guestSocket.Write(payload, 0, payload.Length);
            guest.PumpBetweenTicks(true);
            Equal(sentBefore + 2, guestBackend.SendCount);
            // A connection that is being closed is left to the full pump, which lets it drain and closes it.
            guestSocket.Write(payload, 0, payload.Length);
            guestSocket.Close();
            now += 0.01; guest.PumpBetweenTicks(true);
            Equal(sentBefore + 2, guestBackend.SendCount);
        });
        yield return ("The timing report is due once a minute, and only while someone is connected", () =>
        {
            double now = 10;
            var wire = new DelayedWire(); var shared = new SharedLink();
            var hostBackend = new DelayedBackend(wire, 0, shared) { GameThreadId = Environment.CurrentManagedThreadId };
            var guestBackend = new DelayedBackend(wire, 1, shared) { GameThreadId = Environment.CurrentManagedThreadId };
            hostBackend.Other = guestBackend; guestBackend.Other = hostBackend;
            var host = new SteamLinkManager(hostBackend, () => now, id => "host");
            var listener = new SteamLinkListener(host, action => action(), _ => true);
            listener.Start();
            for (int i = 0; i < 100; i++) { now += 0.02; host.Pump(); }
            Check(host.TakeTimingReport() == null, "a report with nobody connected");
            var guest = new SteamLinkManager(guestBackend, () => now, id => "guest");
            guest.Connect(1001);
            foreach (var (connection, remote) in hostBackend.TakeIncoming()) host.OnIncomingConnection(hostBackend.ListenHandle, connection, remote);
            for (int i = 0; i < 4000; i++) { now += 0.017; host.Pump(); host.PumpBetweenTicks(false); }
            string report = host.TakeTimingReport();
            Check(report != null && report.StartsWith("Steam link timing over "), "no report after a minute: " + report);
            Check(host.TakeTimingReport() == null, "a second report straight away");
        });
        yield return ("A guest's ping over Steam grows with the players' frame length, and pumping between ticks removes that", () =>
        {
            // Both players at 50 ms frames (a game at a high speed on a busy colony), 3 ms each way on the wire.
            // The waits for the pump add up to about three frames: half a frame each for a message that arrives, and a
            // whole frame for each player's outgoing message, which is queued just after the pump that could send it.
            double perFrame = 0, between = 0;
            WithFastProbes(() =>
            {
            // One session after the other: each keeps two game threads busy for the whole run, and two sessions at once
            // leave a 4-core machine (a GitHub Windows runner) no time for the network threads. About 3.6 s in all.
            var both = Task.Run(() =>
            {
                using (var s = new Session(3)) perFrame = s.MeasurePing(new FrameShape(50, false), new FrameShape(50, false), 900, 700);
                using (var s = new Session(3)) between = s.MeasurePing(new FrameShape(50, true), new FrameShape(50, true), 900, 700);
            });
            Check(both.Wait(4500), "the measurements took too long");
            both.GetAwaiter().GetResult();
            });
            Check(perFrame > 90, $"once per frame the ping should follow the frame length, but it was {perFrame:0} ms");
            Check(between < 40, $"pumping between ticks should keep the ping near the network, but it was {between:0} ms");
            Check(perFrame > 3 * between, $"the ping should drop to a fraction: {perFrame:0} ms became {between:0} ms");
        });
    }
}
