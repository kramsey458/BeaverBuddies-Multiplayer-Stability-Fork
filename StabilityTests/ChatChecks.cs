#nullable enable
using System.Collections.Concurrent;
using BeaverBuddies.Panel;
using Newtonsoft.Json.Linq;
using TimberNet;

// Chat: the wire format, the host-held history, and the production TimberServer/TimberClient over in-memory streams.
static class ChatChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    static ChatMessage Msg(int sequence, int player = 1, string name = "Ann", string color = "FF8800", string text = "hi") =>
        new ChatMessage(sequence, player, name, color, text);

    static JObject Json(Action<JObject>? mutate = null)
    {
        var json = Msg(5, 2).ToJson();
        mutate?.Invoke(json);
        return json;
    }

    static bool Parses(Action<JObject>? mutate = null) => ChatMessage.TryParse(Json(mutate), out _);

    static JObject Forged(int seq, int player, string text) => new JObject
    {
        ["type"] = ChatMessage.MessageType, ["seq"] = seq, ["player"] = player,
        ["name"] = "Liar", ["color"] = "00FF00", ["text"] = text
    };

    static string[] Texts(TimberNetBase net) => net.Chat.All().Select(m => m.Text).ToArray();

    static void WaitForChat(TimberNetBase net, int count, int ms = 3000) =>
        Check(SpinWait.SpinUntil(() => net.Chat.Count >= count, ms), $"chat never arrived: {net.Chat.Count} of {count}");

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        // ---- what a message looks like ----
        yield return ("Chat text is cleaned to one plain line", () =>
        {
            Equal("hello world there", ChatMessage.CleanText("  hello   world \n\t there "));
            Equal("bhi/b", ChatMessage.CleanText("<b>hi</b>"));
            Equal("ab", ChatMessage.CleanText("a\u0007b"));
            Equal("ab", ChatMessage.CleanText("a\u202Eb\u200F"));
            Equal("", ChatMessage.CleanText("   \n "));
            Equal("", ChatMessage.CleanText("<><>"));
            Equal(ChatMessage.MaxTextLength, ChatMessage.CleanText(new string('x', 900)).Length);
            // A character made of two UTF-16 units is never cut in half at the limit.
            string cut = ChatMessage.CleanText(new string('a', 199) + "\uD83D\uDE00");
            Equal(199, cut.Length);
            Check(!char.IsHighSurrogate(cut[cut.Length - 1]));
            Equal("bBob", Msg(1, name: " <b>Bob\n").Name);
            Equal("FF8800", new ChatMessage(1, 1, "a", "ff8800", "x").Color);
            Equal("FFFFFF", new ChatMessage(1, 1, "a", "not-a-color", "x").Color);
        });
        yield return ("Chat JSON round-trips through validation", () =>
        {
            Check(ChatMessage.TryParse(Json(), out var m) && m != null);
            Check(m!.Sequence == 5 && m.PlayerId == 2 && m.Name == "Ann" && m.Color == "FF8800" && m.Text == "hi");
        });
        yield return ("Malformed chat is rejected", () =>
        {
            Check(!Parses(j => j["type"] = "Other"), "wrong type");
            Check(!Parses(j => j["seq"] = "5"), "string sequence");
            Check(!Parses(j => j["seq"] = -1), "negative sequence");
            Check(!Parses(j => j["player"] = -1), "negative player");
            Check(!Parses(j => j["player"] = 1.5), "fractional player");
            Check(!Parses(j => j["color"] = "GG0000"), "bad hex");
            Check(!Parses(j => j["color"] = "FF88"), "short color");
            Check(!Parses(j => j["text"] = 7), "numeric text");
            Check(!Parses(j => j["text"] = new string('a', 1025)), "huge text");
            Check(!Parses(j => j["text"] = "<><>"), "nothing left after cleaning");
            Check(!Parses(j => j["name"] = new string('a', 65)), "long name");
            Check(!Parses(j => j.Remove("text")), "missing text");
            Check(!Parses(j => j.Remove("seq")), "missing sequence");
            Check(!Parses(j => j["extra"] = 1), "too many keys");
            Check(Parses(j => j["text"] = new string('a', 1024)), "long but allowed text is cut, not refused");
        });
        yield return ("Chat that is only markup or blank cannot be created", () =>
        {
            Check(!ChatMessage.TryCreate("A", "FFFFFF", "<><>", out _));
            Check(!ChatMessage.TryCreate("A", "FFFFFF", "   ", out _));
            Check(!ChatMessage.TryCreate(null, null, null, out _));
            Check(ChatMessage.TryCreate(null, null, "ok", out var m) && m!.Name == "Player" && m.Color == "FFFFFF");
        });

        // ---- history frames ----
        yield return ("A history goes out in frames of at most a hundred messages and round-trips", () =>
        {
            var all = Enumerable.Range(1, 250).Select(i => Msg(i, text: "m" + i)).ToList();
            var frames = ChatMessage.HistoryFrames(all).ToList();
            Equal(3, frames.Count);
            var back = new List<ChatMessage>();
            foreach (var frame in frames)
            {
                Check(ChatMessage.TryParseHistory(frame, out var part), "frame rejected");
                Check(part.Count <= ChatMessage.MaxHistoryBatch);
                back.AddRange(part);
            }
            Check(back.Select(m => m.Sequence).SequenceEqual(Enumerable.Range(1, 250)), "order or content changed");
            Equal(0, ChatMessage.HistoryFrames(new List<ChatMessage>()).Count());
        });
        yield return ("A history frame with one bad entry, or too many, is refused whole", () =>
        {
            var frame = ChatMessage.HistoryFrames(Enumerable.Range(1, 3).Select(i => Msg(i)).ToList()).Single();
            Check(ChatMessage.TryParseHistory((JObject)frame.DeepClone(), out _));
            var bad = (JObject)frame.DeepClone(); bad["messages"]![1]!["color"] = "zz";
            Check(!ChatMessage.TryParseHistory(bad, out var none) && none.Count == 0, "bad entry");
            var many = new JObject { ["type"] = ChatMessage.HistoryType, ["messages"] = new JArray(Enumerable.Range(1, 201).Select(i => (object)Msg(i).ToJson())) };
            Check(!ChatMessage.TryParseHistory(many, out _), "more than 200 entries");
            var extra = (JObject)frame.DeepClone(); extra["more"] = 1;
            Check(!ChatMessage.TryParseHistory(extra, out _), "extra key");
            var notArray = new JObject { ["type"] = ChatMessage.HistoryType, ["messages"] = "x" };
            Check(!ChatMessage.TryParseHistory(notArray, out _), "not an array");
        });

        // ---- the log and the rate limit ----
        yield return ("The chat log keeps order, refuses duplicates and drops the oldest past its cap", () =>
        {
            var log = new ChatLog();
            Check(log.Add(Msg(1)) && log.Add(Msg(2)) && log.Add(Msg(3)));
            Check(!log.Add(Msg(2)) && !log.Add(Msg(3)) && !log.Add(Msg(1)), "duplicate or stale accepted");
            Equal(3, log.LastSequence);
            Check(log.Since(1).Select(m => m.Sequence).SequenceEqual(new[] { 2, 3 }));
            Equal(0, log.Since(3).Length);
            for (int i = 4; i <= ChatLog.MaxMessages + 5; i++) log.Add(Msg(i));
            Equal(ChatLog.MaxMessages, log.Count);
            Equal(6, log.All()[0].Sequence);
            var some = log.Since(0, 10);
            Equal(10, some.Length); Equal(6, some[0].Sequence); Equal(15, some[9].Sequence);
            Equal(ChatLog.MaxMessages + 5, log.LastSequence);
        });
        yield return ("The rate limiter allows a burst, then a steady rate", () =>
        {
            var limiter = new ChatRateLimiter(6, 2);
            int allowed = 0;
            for (int i = 0; i < 20; i++) if (limiter.TryTake(1000)) allowed++;
            Equal(6, allowed);
            Check(!limiter.TryTake(1200), "0.4 tokens is not enough");
            Check(limiter.TryTake(1500), "half a second refills one");
            Check(!limiter.TryTake(1500));
            allowed = 0;
            for (int i = 0; i < 20; i++) if (limiter.TryTake(60_000)) allowed++;
            Equal(6, allowed);
            Check(!limiter.TryTake(-5000), "a clock that steps back gives nothing extra");
        });

        // ---- the lane ----
        yield return ("Ordered frames all arrive, in order, while a write is stalled", () =>
        {
            var gate = new ManualResetEventSlim(); var started = new ManualResetEventSlim();
            var written = new ConcurrentQueue<int>();
            var channel = new ActivityChannel(new ReadStream(Array.Empty<byte>()), (s, m) =>
            {
                if ((string?)m["type"] == ChatMessage.MessageType) written.Enqueue((int)m["seq"]!);
                started.Set();
                if (!gate.Wait(3000)) throw new TimeoutException();
            }, (s, e) => throw new Exception(e));
            channel.PostOrdered(Msg(0).ToJson());
            Check(started.Wait(1000));
            for (int i = 1; i <= 300; i++)
            {
                channel.PostOrdered(Msg(i).ToJson());
                // Latest-wins frames are mixed in and must not disturb the ordered ones.
                channel.Post(new PlayerActivity(1, "Ann", "FF8800", true, i, 0, 0));
            }
            gate.Set();
            Check(SpinWait.SpinUntil(() => written.Count == 301, 3000), "written: " + written.Count);
            Check(written.ToArray().SequenceEqual(Enumerable.Range(0, 301)), "reordered or lost");
        });
        yield return ("The ordered queue is bounded when a connection stalls", () =>
        {
            var gate = new ManualResetEventSlim(); var started = new ManualResetEventSlim();
            int written = 0;
            var channel = new ActivityChannel(new ReadStream(Array.Empty<byte>()), (s, m) =>
            {
                Interlocked.Increment(ref written); started.Set();
                if (!gate.Wait(3000)) throw new TimeoutException();
            }, (s, e) => throw new Exception(e));
            channel.PostOrdered(Msg(0).ToJson());
            Check(started.Wait(1000));
            for (int i = 1; i <= 6000; i++) channel.PostOrdered(Msg(i).ToJson());
            gate.Set();
            Check(SpinWait.SpinUntil(() => Volatile.Read(ref written) >= 4000, 3000), "nothing was written");
            Thread.Sleep(200);
            Check(Volatile.Read(ref written) < 6000, "the queue grew without a limit: " + written);
        });

        // ---- host and guests ----
        yield return ("The host numbers chat, ignores a guest's claims, and every player sees one order", () =>
        {
            using var s = new ActivityTransportChecks.Session(2);
            // This guest claims to be player 42 and to be message 999; the host must ignore both.
            s.Guests[0].SendRaw(Forged(999, 42, "forged"));
            WaitForChat(s.Host, 1);
            var first = s.Host.Chat.All().Single();
            Check(first.PlayerId == 1 && first.Sequence == 1 && first.Text == "forged", $"host kept player {first.PlayerId} seq {first.Sequence}");
            Check(s.Guests[1].Chat.Count == 1 || SpinWait.SpinUntil(() => s.Guests[1].Chat.Count == 1, 3000));

            // Each message reaches the host before the next is sent, so the order below is the order sent.
            Check(s.Guests[1].SendChat("Two", "445566", "second"));
            WaitForChat(s.Host, 2);
            Check(s.Host.SendChat("Host", "FFFFFF", "third"));
            Check(s.Guests[0].SendChat("One", "112233", "fourth"));
            foreach (TimberNetBase net in new TimberNetBase[] { s.Host, s.Guests[0], s.Guests[1] }) WaitForChat(net, 4);
            var expected = new[] { "forged", "second", "third", "fourth" };
            Check(Texts(s.Host).SequenceEqual(expected), string.Join("|", Texts(s.Host)));
            Check(Texts(s.Guests[0]).SequenceEqual(expected), "guest one saw " + string.Join("|", Texts(s.Guests[0])));
            Check(Texts(s.Guests[1]).SequenceEqual(expected), "guest two saw " + string.Join("|", Texts(s.Guests[1])));
            Check(s.Guests[1].Chat.All().Select(m => m.PlayerId).SequenceEqual(new[] { 1, 2, 0, 1 }));
            Check(s.Guests[1].Chat.All().Select(m => m.Sequence).SequenceEqual(new[] { 1, 2, 3, 4 }));
            Check(s.Guests[1].Chat.All().Select(m => m.Name).SequenceEqual(new[] { "Liar", "Two", "Host", "One" }));
        });
        yield return ("The sender sees their own message only after the host has numbered it", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            Check(s.Guests[0].SendChat("One", "112233", "hello"));
            WaitForChat(s.Guests[0], 1);
            Equal("hello", s.Guests[0].Chat.All().Single().Text);
            Equal(1, s.Guests[0].Chat.All().Single().Sequence);
            Thread.Sleep(150);
            Equal(1, s.Guests[0].Chat.Count);
        });
        yield return ("Chat never changes the hash, tick progress or replay script", () =>
        {
            using var s = new ActivityTransportChecks.Session(2);
            foreach (var g in s.Guests)
            {
                Check(SpinWait.SpinUntil(() => g.HasEventsForTick(0), 2000), "init event never arrived");
                g.ReadEvents(0); Thread.Sleep(150); g.ReadEvents(0);
            }
            int hostHash = s.Host.Hash, guestHash = s.Guests[0].Hash;
            for (int i = 0; i < 100; i++) s.Host.SendChat("Host", "FFFFFF", "line " + i);
            s.Guests[0].SendChat("One", "112233", "from a guest");
            WaitForChat(s.Guests[1], 101);
            Equal(hostHash, s.Host.Hash); Equal(guestHash, s.Guests[0].Hash);
            Equal(0, s.Host.ReadEvents(0).Count); Equal(0, s.Guests[0].ReadEvents(0).Count);
            Check(!s.Host.HasEventsForTick(0) && !s.Guests[0].HasEventsForTick(0));
            Equal(0, s.Host.TicksBehind); Equal(0, s.Guests[0].TicksBehind);
        });
        yield return ("Gameplay events keep their order under a chat flood", () =>
        {
            using var s = new ActivityTransportChecks.Session(2);
            var stop = new ManualResetEventSlim();
            var flood = Task.Run(() => { for (int i = 0; !stop.IsSet && i < 400; i++) { s.Host.SendChat("Host", "FFFFFF", "spam " + i); Thread.Sleep(0); } });
            try
            {
                for (int i = 0; i < 50; i++)
                    s.Host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Seq", [TimberNetBase.TICKS_KEY] = 0, ["n"] = i });
                var received = new List<int>();
                Check(SpinWait.SpinUntil(() =>
                {
                    foreach (var e in s.Guests[0].ReadEvents(0)) if ((string?)e["type"] == "Seq") received.Add((int)e["n"]!);
                    return received.Count == 50;
                }, 4000), "received " + received.Count);
                Check(received.SequenceEqual(Enumerable.Range(0, 50)), "events reordered or lost");
                Check(!s.Guests[0].IsStopped && !s.Host.IsStopped);
            }
            finally { stop.Set(); flood.Wait(); }
        });
        yield return ("Malformed chat frames from a guest are dropped without ending the session", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            s.Guests[0].SendRaw(new JObject { ["type"] = ChatMessage.MessageType, ["player"] = "x" });
            s.Guests[0].SendRaw(new JObject { ["type"] = ChatMessage.MessageType });
            s.Guests[0].SendRaw(new JObject { ["type"] = ChatMessage.HistoryType, ["messages"] = "nope" });
            // A guest may not send history: only the host's catch-up batches are history.
            s.Guests[0].SendRaw(ChatMessage.HistoryFrames(new List<ChatMessage> { Msg(1, text: "planted") }).Single());
            Check(s.Guests[0].SendChat("One", "112233", "real"));
            WaitForChat(s.Host, 1);
            Thread.Sleep(150);
            Check(Texts(s.Host).SequenceEqual(new[] { "real" }), string.Join("|", Texts(s.Host)));
            Check(!s.Host.IsStopped && !s.Guests[0].IsStopped);
            s.Guests[0].DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "Ping", [TimberNetBase.TICKS_KEY] = 0 });
            Check(SpinWait.SpinUntil(() => { s.Host.Update(); return s.Host.HasEventsForTick(0); }, 2000), "gameplay stopped working");
        });
        yield return ("A guest that floods chat is limited by the host", () =>
        {
            using var s = new ActivityTransportChecks.Session(2);
            for (int i = 0; i < 100; i++) s.Guests[0].SendChat("One", "112233", "flood " + i);
            Thread.Sleep(500);
            int kept = s.Host.Chat.Count;
            Check(kept >= 6 && kept <= 10, "the host kept " + kept);
            Check(SpinWait.SpinUntil(() => s.Guests[1].Chat.Count == kept, 2000), "the other guest saw " + s.Guests[1].Chat.Count);
            Check(!s.Host.IsStopped && !s.Guests[0].IsStopped, "chat flood ended the session");
            // The host itself is not limited.
            for (int i = 0; i < 30; i++) Check(s.Host.SendChat("Host", "FFFFFF", "host " + i));
            WaitForChat(s.Guests[1], kept + 30);
        });
        yield return ("Only text worth showing can be sent, and a guest cannot chat before it has the map", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            Check(!s.Host.SendChat("A", "FFFFFF", "<><>") && !s.Host.SendChat("A", "FFFFFF", "   "));
            Check(!s.Guests[0].SendChat("A", "FFFFFF", "  "));
            Equal(0, s.Host.Chat.Count);
            var (_, guestSide) = PipeStreamFactory.Pair();
            var early = new ActivityTransportChecks.TestGuest(guestSide);
            Check(!early.SendChat("A", "FFFFFF", "too early"));
            early.Close();
        });
        yield return ("A guest that leaves stops receiving chat and the rest carry on", () =>
        {
            using var s = new ActivityTransportChecks.Session(2);
            s.Guests[1].Close();
            Check(SpinWait.SpinUntil(() => { s.Host.SendChat("Host", "FFFFFF", "x"); s.Host.DoUserInitiatedEvent(new JObject { [TimberNetBase.TYPE_KEY] = "x", [TimberNetBase.TICKS_KEY] = 0 }); Thread.Sleep(20); return s.Host.ClientCount == 1; }, 3000));
            Check(s.Guests[0].SendChat("One", "112233", "still here"));
            Check(SpinWait.SpinUntil(() => s.Guests[0].Chat.All().Any(m => m.Text == "still here"), 3000));
            Check(!s.Host.IsStopped && !s.Guests[0].IsStopped);
        });

        // ---- history for a late joiner ----
        yield return ("A joining guest is sent the whole history, in order, after the init event and before anything live", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            for (int i = 0; i < 250; i++) Check(s.Host.SendChat("Host", "FFFFFF", "m" + i));
            WaitForChat(s.Guests[0], 250);
            // The new guest's join is held at the init event, so it is admitted but not yet given its lane.
            var initGate = new ManualResetEventSlim();
            s.InitGate = initGate;
            var tap = s.AddGuestWithTap();
            Check(SpinWait.SpinUntil(() => s.InitBlocked, 2000), "join never reached the init event");
            for (int i = 0; i < 5; i++) Check(s.Host.SendChat("Host", "FFFFFF", "live" + i));
            initGate.Set();
            var late = s.Guests[1];
            WaitForChat(late, 255);
            var expected = Enumerable.Range(0, 250).Select(i => "m" + i).Concat(Enumerable.Range(0, 5).Select(i => "live" + i)).ToArray();
            Check(Texts(late).SequenceEqual(expected), "the late guest's log differs from the host's");
            Check(late.Chat.All().Select(m => m.Sequence).SequenceEqual(Enumerable.Range(1, 255)), "sequence numbers differ");
            // A message sent after the join arrives once, after all of it.
            Check(s.Host.SendChat("Host", "FFFFFF", "after"));
            WaitForChat(late, 256);
            Thread.Sleep(150);
            Equal(256, late.Chat.Count);
            Equal("after", late.Chat.All()[255].Text);
            // On the wire: the history is a few modest frames, and none of it precedes the init event.
            var types = tap.FrameTypes();
            int init = types.IndexOf("InitProbe");
            Check(init > 0, "no init event: " + string.Join(",", types));
            Check(types.IndexOf(ChatMessage.HistoryType) > init, "history arrived before the init event");
            Check(types.Count(t => t == ChatMessage.HistoryType) == 3, "history frames: " + types.Count(t => t == ChatMessage.HistoryType));
            Check(types.LastIndexOf(ChatMessage.HistoryType) < types.IndexOf(ChatMessage.MessageType), "a live message overtook the history");
        });
        yield return ("A guest who joins an empty chat is sent no history", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            var tap = s.AddGuestWithTap();
            Check(SpinWait.SpinUntil(() => tap.FrameTypes().Contains("InitProbe"), 2000));
            Thread.Sleep(200);
            Check(!tap.FrameTypes().Contains(ChatMessage.HistoryType));
            Equal(0, s.Guests[1].Chat.Count);
        });
        yield return ("Two guests joining while chat is busy each end up with the same complete log", () =>
        {
            using var s = new ActivityTransportChecks.Session(1);
            var stop = new ManualResetEventSlim();
            int sent = 0;
            var talker = Task.Run(() =>
            {
                for (int i = 0; !stop.IsSet && i < 1500; i++)
                {
                    s.Host.SendChat("Host", "FFFFFF", "t" + i); Interlocked.Increment(ref sent);
                    if (i % 10 == 0) Thread.Sleep(1);
                }
            });
            try
            {
                s.AddGuest(); s.AddGuest();
                stop.Set(); talker.Wait();
                int total = Volatile.Read(ref sent);
                foreach (var g in s.Guests) WaitForChat(g, total, 4000);
                var reference = Texts(s.Host);
                Equal(total, reference.Length);
                foreach (var g in s.Guests)
                    Check(Texts(g).SequenceEqual(reference), "a guest's log has a gap, a duplicate or the wrong order");
            }
            finally { stop.Set(); }
        });

        // ---- the key binding ----
        yield return ("The chat key binding names a real label and the id the panel listens for", () =>
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "BeaverBuddies.sln"))) root = Path.GetDirectoryName(root)!;
            Check(root != null, "could not find the repository root");
            string mod = Path.Combine(root!, "BeaverBuddies");
            var spec = JObject.Parse(File.ReadAllText(Path.Combine(mod, "KeyBindings", "BeaverBuddies.KeyBind.FocusChat.blueprint.json")))["KeyBindingSpec"]!;
            string id = (string)spec["Id"]!, label = (string)spec["LocKey"]!;
            Equal("KeyBindingGroup.BeaverBuddies", (string)spec["GroupId"]!);
            Check(File.ReadAllText(Path.Combine(mod, "Panel", "ConnectionPanelService.cs")).Contains("\"" + id + "\""),
                "the panel never listens for " + id);
            string csv = File.ReadAllText(Path.Combine(mod, "Localizations", "enUS_BeaverBuddie.csv"));
            Check(System.Text.RegularExpressions.Regex.IsMatch(csv, "^" + System.Text.RegularExpressions.Regex.Escape(label) + ",", System.Text.RegularExpressions.RegexOptions.Multiline),
                "no English label " + label);
            // Two bindings in one group must not share an order, or the options screen lists them unpredictably.
            var orders = Directory.GetFiles(Path.Combine(mod, "KeyBindings"), "*.blueprint.json")
                .Select(f => (int)JObject.Parse(File.ReadAllText(f))["KeyBindingSpec"]!["Order"]!).ToList();
            Equal(orders.Count, orders.Distinct().Count());
        });

        // ---- how a line is written ----
        yield return ("A chat line colors only the name, the message stays in the panel's own color, and no message can add markup", () =>
        {
            Equal("<color=#FFFF00>Ann</color>: hi there", ChatFormat.Line("Ann", "FFFF00", "hi there"));
            Equal("<color=#FFFF00>bAnn/b</color>: bhi/b", ChatFormat.Line("<b>Ann</b>", "FFFF00", "<b>hi</b>"));
            Equal("<color=#F2E8D0>Ann</color>: x", ChatFormat.Line("Ann", "nonsense", "x"));
            // The message is outside the color tags, so it is drawn in the label's own text color.
            string line = ChatFormat.Line("Ann", "4D96FF", "hello");
            Check(line.EndsWith("</color>: hello"), line);
            Check(!line.Substring(line.IndexOf("</color>")).Contains("<color"), "the message is colored: " + line);
            // The only tags in a line are the one pair that colors the name, whatever was typed.
            string hostile = ChatFormat.Line("</color><size=99>", "FFFF00", "</color><color=#FF0000>red</color>");
            Equal(2, hostile.Count(c => c == '<'));
            Check(hostile.StartsWith("<color=#FFFF00>") && hostile.Contains("</color>: ") && !hostile.EndsWith("</color>"), hostile);
        });
        yield return ("A dark name color is lightened until it can be read, and a light one is left alone", () =>
        {
            Equal("FFFF00", ChatFormat.ReadableHex("FFFF00"));
            Equal("FFFFFF", ChatFormat.ReadableHex("FFFFFF"));
            Equal("88DD55", ChatFormat.ReadableHex("88DD55"));
            Equal("FFFFFF", ChatFormat.ReadableHex("ffffff"));
            string dark = ChatFormat.ReadableHex("000000");
            Check(dark != "000000" && dark[0] == dark[2] && dark[2] == dark[4], "black should become a plain grey: " + dark);
            string blue = ChatFormat.ReadableHex("0000FF");
            Check(blue.EndsWith("FF") && blue != "0000FF", "blue should be lightened and stay blue: " + blue);
            foreach (string hex in new[] { "000000", "0000FF", "330000", "101010", "202080" })
            {
                string result = ChatFormat.ReadableHex(hex);
                int rgb = Convert.ToInt32(result, 16);
                double brightness = .2126 * ((rgb >> 16) & 255) / 255 + .7152 * ((rgb >> 8) & 255) / 255 + .0722 * (rgb & 255) / 255;
                Check(brightness >= .49, hex + " -> " + result + " is still too dark");
            }
            Equal("F2E8D0", ChatFormat.ReadableHex("12"));
            Equal("F2E8D0", ChatFormat.ReadableHex(null!));
        });
    }
}
