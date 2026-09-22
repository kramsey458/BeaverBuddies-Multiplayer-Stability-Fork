#nullable enable
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TimberNet;

// The direct (non-Steam) connection: Nagle's algorithm is off on both ends, and only the save sent to a joining guest
// is paced. Real loopback sockets for the first, the production TimberServer/TimberClient over in-memory pipes for the
// rest. Nothing listens beyond loopback.
static class DirectTcpChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }

    // A pipe that says it may only carry `bytesPerSecond`: pacing a frame of several chunks on it takes
    // chunk * 1000 / bytesPerSecond milliseconds between each two chunks.
    sealed class RatedStream : ISocketStream
    {
        readonly ISocketStream inner;
        public RatedStream(ISocketStream inner, int chunk, int bytesPerSecond)
        {
            this.inner = inner; MaxChunkSize = chunk; MaxBytesPerSecond = bytesPerSecond;
        }
        public bool Connected => inner.Connected;
        public string? Name => inner.Name;
        public int MaxChunkSize { get; }
        public int MaxBytesPerSecond { get; }
        public Task ConnectAsync() => inner.ConnectAsync();
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public void Close() => inner.Close();
    }

    // A host whose writes to its one guest go through `hostSide`, and that guest, once the save has arrived.
    static (TimberServer Host, TimberClient Guest, TimeSpan MapTime) Session(Func<ISocketStream, ISocketStream> hostSide, byte[] map)
    {
        var (hostStream, guestStream) = PipeStream.Pair();
        var host = new TimberServer(new PipeListener(hostSide(hostStream)), () => Task.FromResult(map), null);
        var guest = new TimberClient(guestStream);
        byte[]? received = null;
        guest.OnMapReceived += bytes => received = bytes;
        var clock = Stopwatch.StartNew();
        host.Start(); guest.Start();
        Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return received != null; }, 3000), "the save never arrived");
        clock.Stop();
        Check(received!.SequenceEqual(map), "the save arrived damaged");
        return (host, guest, clock.Elapsed);
    }

    static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Direct TCP: a connection it makes or is handed sends each frame at once (NoDelay)", () =>
        {
            var handed = new TCPClientWrapper(new TcpClient());
            var made = new TCPClientWrapper("127.0.0.1", 1);
            try
            {
                Check(handed.NoDelay, "a wrapped socket (every connection the host accepts) keeps Nagle's algorithm on");
                Check(made.NoDelay, "the socket a guest connects with keeps Nagle's algorithm on");
            }
            finally { handed.Close(); made.Close(); }
        });
        yield return ("Direct TCP: both ends of a real loopback connection send each frame at once", () =>
        {
            // Accepted the way TCPListenerWrapper.AcceptClient does it, but only on loopback.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TCPClientWrapper? accepted = null;
            var guest = new TCPClientWrapper("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            try
            {
                var accept = Task.Run(() => accepted = new TCPClientWrapper(listener.AcceptTcpClient()));
                Check(guest.ConnectAsync().Wait(2000) && accept.Wait(2000), "loopback connection never completed");
                Check(guest.Connected && accepted!.Connected);
                Check(guest.NoDelay, "the guest's connected socket has Nagle's algorithm on");
                Check(accepted!.NoDelay, "the host's accepted socket has Nagle's algorithm on");
            }
            finally { guest.Close(); accepted?.Close(); listener.Stop(); }
        });
        yield return ("A gameplay frame of many chunks is written without sleeping on the sending thread", () =>
        {
            // One byte a second: pacing this frame would sleep 1024 s between each two of its chunks.
            var (host, guest, _) = Session(stream => new RatedStream(stream, chunk: 1024, bytesPerSecond: 1), new byte[] { 7, 8, 9 });
            Task? send = null;
            try
            {
                // Random text barely compresses, so the frame stays many chunks long.
                string payload = Convert.ToBase64String(Noise(24 * 1024, 5));
                var big = new JObject { [TimberNetBase.TYPE_KEY] = "Big", [TimberNetBase.TICKS_KEY] = 1, ["payload"] = payload };
                // What ReplayService.SendEvents does on the host's game thread for a tick's events.
                send = Task.Run(() => host.DoUserInitiatedEvent(big));
                // (The runner stops any check at five seconds.)
                Check(send.Wait(1500), "sending one gameplay frame blocked its thread: it slept between chunks");
                JObject? received = null;
                Check(SpinWait.SpinUntil(() =>
                {
                    guest.Update();
                    received = guest.ReadEvents(1).FirstOrDefault(e => (string?)e[TimberNetBase.TYPE_KEY] == "Big");
                    return received != null;
                }, 2000), "the frame never arrived");
                Check((string?)received!["payload"] == payload, "the frame arrived damaged");
            }
            finally
            {
                guest.Close();
                // A send still asleep holds the host's client lock, and closing the host would wait for it.
                if (send == null || send.IsCompleted) host.Close();
            }
        });
        yield return ("The save sent to a joining guest is still paced, on the join's own thread", () =>
        {
            // 1 KB chunks at 20 KB/s: 50 ms between each two chunks, so 10 chunks take at least 450 ms.
            var (host, guest, mapTime) = Session(stream => new RatedStream(stream, chunk: 1024, bytesPerSecond: 20 * 1024), Noise(10 * 1024, 6));
            try
            {
                Check(mapTime >= TimeSpan.FromMilliseconds(400), $"the save arrived in {mapTime.TotalMilliseconds:F0} ms: it was not paced");
            }
            finally { host.Close(); guest.Close(); }
        });
        yield return ("Ending the session while a guest downloads the save does not wait for the paced save", () =>
        {
            // 1 KB chunks at 1 KB/s: the 20 KB save takes about 19 s to send.
            var (hostStream, guestStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(new RatedStream(hostStream, chunk: 1024, bytesPerSecond: 1024)),
                () => Task.FromResult(Noise(20 * 1024, 7)), null);
            var guest = new TimberClient(guestStream);
            Task? abort = null;
            try
            {
                host.Start(); guest.Start();
                // The guest counts as connected once its save has started (TimberServer.StartQueuing).
                Check(SpinWait.SpinUntil(() => { host.Update(); guest.Update(); return host.ClientCount == 1; }, 2000),
                    "the guest never started receiving the save");
                // The save send now holds the guest's stream, asleep between chunks.
                Thread.Sleep(100);
                // What ReplayService.AbortReplay does on the host's game thread when a replayed action fails.
                abort = Task.Run(() => host.AbortSession("test"));
                Check(abort.Wait(1000), "ending the session waited for the joining guest's paced save");
                Check(!hostStream.Connected, "the joining guest's connection was left open");
            }
            finally
            {
                guest.Close();
                if (abort == null || abort.IsCompleted) host.Close();
            }
        });
    }
}
