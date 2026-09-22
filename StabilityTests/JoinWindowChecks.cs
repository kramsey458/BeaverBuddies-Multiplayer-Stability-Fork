using Newtonsoft.Json.Linq;
using TimberNet;

// Joining can close while a guest is still joining: the host plays something that changes the game before the
// first tick (see ReplayService.CloseJoiningIfGameChanged), or the first tick runs. A guest already admitted gets
// everything the host plays from then on; one that is not admitted yet must be refused with the host's reason,
// because it would load a save without what was just played and never be sent it.
static class JoinWindowChecks
{
    const string Closed = "Joining closed for this test. Ask the Host to rehost.";

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Joining: a guest whose save is being prepared when joining closes is refused with the host's reason", () =>
        {
            var (hostStream, clientStream) = PipeStream.Pair();
            using var mapRequested = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.Run(() =>
            {
                mapRequested.Set();
                release.Wait(3000);
                return new byte[] { 7, 8, 9 };
            }), null) { CompatibilityIdentity = "same" };
            var client = new TimberClient(clientStream) { CompatibilityIdentity = "same" };
            int maps = 0; string? error = null;
            client.OnMapReceived += _ => maps++;
            client.OnError += message => error = message;
            try
            {
                host.Start(); client.Start();
                // The build check has passed and the host is reading the save: the guest is not admitted yet.
                Check(mapRequested.Wait(2000), "the host never started preparing the save");
                host.StopAcceptingClients(Closed);
                release.Set();
                Check(SpinWait.SpinUntil(() => { host.Update(); client.Update(); return maps > 0 || error != null; }, 2000),
                    "the guest heard nothing");
                Check(maps == 0, "the guest was sent the save after joining closed, so it would miss what the host just played");
                Check(error != null && error.Contains(Closed), $"the guest was not told why: {error}");
                Check(host.ClientCount == 0, "the refused guest was added to the players");
            }
            finally { release.Set(); host.Close(); client.Close(); }
        });

        yield return ("Joining: a guest whose build check is running when joining closes is told why", () =>
        {
            var (hostStream, pipe) = PipeStream.Pair();
            // Holds the guest's answer to the build check until joining has closed on the host.
            var clientStream = new HeldFirstWrite(pipe);
            int mapRequests = 0;
            var host = new TimberServer(new PipeListener(hostStream), () =>
            {
                Interlocked.Increment(ref mapRequests);
                return Task.FromResult(new byte[] { 7, 8, 9 });
            }, null) { CompatibilityIdentity = "same" };
            var client = new TimberClient(clientStream) { CompatibilityIdentity = "same" };
            int maps = 0; string? error = null;
            client.OnMapReceived += _ => maps++;
            client.OnError += message => error = message;
            try
            {
                host.Start(); client.Start();
                Check(clientStream.Entered.Wait(2000), "the guest never answered the build check");
                host.StopAcceptingClients(Closed);
                clientStream.Release.Set();
                Check(SpinWait.SpinUntil(() => { host.Update(); client.Update(); return maps > 0 || error != null; }, 2000),
                    "the guest heard nothing");
                Check(maps == 0 && mapRequests == 0, "the guest was sent the save after joining closed");
                Check(error != null && error.Contains(Closed), $"the guest was not told why: {error}");
            }
            finally { clientStream.Release.Set(); host.Close(); client.Close(); }
        });

        yield return ("Joining: a guest admitted before joining closes still gets what the host plays next", () =>
        {
            var (hostStream, clientStream) = PipeStream.Pair();
            var host = new TimberServer(new PipeListener(hostStream), () => Task.FromResult(new byte[] { 7, 8, 9 }), null)
                { CompatibilityIdentity = "same" };
            var client = new TimberClient(clientStream) { CompatibilityIdentity = "same" };
            int maps = 0; string? error = null;
            client.OnMapReceived += _ => maps++;
            client.OnError += message => error = message;
            try
            {
                host.Start(); client.Start();
                Check(SpinWait.SpinUntil(() => { host.Update(); client.Update(); return maps > 0 || error != null; }, 2000),
                    "the guest never joined");
                Check(maps == 1 && error == null, $"the guest was refused: {error}");
                // What closes joining is an action the host plays; it still goes to everyone already admitted.
                host.StopAcceptingClients(Closed);
                host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "BuildingPlacedEvent", [TimberNetBase.TICKS_KEY] = 0 });
                // Look for the action itself: the save's SetState frame is also a tick-0 message, so a check for
                // any tick-0 message (HasEventsForTick(0)) passes without it.
                var received = new List<JObject>();
                Check(SpinWait.SpinUntil(() =>
                {
                    host.Update();
                    received.AddRange(client.ReadEvents(0));
                    return received.Any(e => TimberNetBase.GetType(e) == "BuildingPlacedEvent");
                }, 2000), "the admitted guest never got the action");
                Check(host.ClientCount == 1 && error == null, "closing joining dropped a guest that had already joined");
            }
            finally { host.Close(); client.Close(); }
        });
    }

    // Blocks the first write (the guest's answer to the build check) until Release is set.
    sealed class HeldFirstWrite : ISocketStream
    {
        readonly ISocketStream inner;
        int writes;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public HeldFirstWrite(ISocketStream inner) => this.inner = inner;
        public bool Connected => inner.Connected;
        public string? Name => inner.Name;
        public int MaxChunkSize => inner.MaxChunkSize;
        public int MaxBytesPerSecond => inner.MaxBytesPerSecond;
        public Task ConnectAsync() => inner.ConnectAsync();
        public int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref writes) == 1) { Entered.Set(); Release.Wait(3000); }
            inner.Write(buffer, offset, count);
        }
        public void Close() => inner.Close();
    }
}
