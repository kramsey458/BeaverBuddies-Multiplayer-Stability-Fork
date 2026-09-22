#nullable enable
using BeaverBuddies.DesyncDetecter;
using Newtonsoft.Json.Linq;
using TimberNet;

// The always-on desync check: what the host puts on each tick's heartbeat, carried by the production
// TimberServer/TimberClient over in-memory streams, and compared by a guest at the same point in its own game.
// The two games are doubles: Unity's four random words, and buckets of entities (some of them walking) fed
// through the production TickHashes the way TEBPatcher feeds it before each bucket ticks.
static class DesyncCheckChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    sealed class Entity
    {
        public Guid Id;
        public bool Walks;
        public float X, Y, Z;
        public Entity Copy() => (Entity)MemberwiseClone();
    }

    sealed class Game
    {
        public int S0 = 0x01234567, S1 = 0x089ABCDE, S2 = 0x013579BD, S3 = 0x02468ACE;
        public List<List<Entity>> Buckets = new();
        public readonly TickHashes Hashes = new();

        public static Game Colony(int seed = 7, int buckets = 16, int perBucket = 40)
        {
            var random = new Random(seed);
            var game = new Game();
            for (int b = 0; b < buckets; b++)
            {
                var bucket = new List<Entity>();
                for (int i = 0; i < perBucket; i++)
                {
                    var bytes = new byte[16]; random.NextBytes(bytes);
                    bucket.Add(new Entity { Id = new Guid(bytes), Walks = random.Next(10) == 0,
                        X = random.Next(256) + .5f, Y = random.Next(20), Z = random.Next(256) + .25f });
                }
                // The game keeps each bucket sorted by entity ID.
                bucket.Sort((a, c) => a.Id.CompareTo(c.Id));
                game.Buckets.Add(bucket);
            }
            return game;
        }

        public Game Copy()
        {
            var copy = new Game { S0 = S0, S1 = S1, S2 = S2, S3 = S3 };
            copy.Buckets = Buckets.Select(b => b.Select(e => e.Copy()).ToList()).ToList();
            return copy;
        }

        // One tick's buckets through the hashes, in bucket order, as TEBPatcher does just before each bucket ticks.
        public void Tick(int tick)
        {
            Hashes.StartTick(tick);
            foreach (var bucket in Buckets)
            {
                Hashes.AddBucket(bucket, e => e.Id);
                foreach (var e in bucket) if (e.Walks) Hashes.AddWalker(e.X, e.Y, e.Z);
            }
            // Everyone walks the same way on both computers, so the positions change but stay in step.
            foreach (var bucket in Buckets) foreach (var e in bucket) if (e.Walks) e.X += .125f;
        }

        public GameState State => new GameState
        {
            S0 = S0, S1 = S1, S2 = S2, S3 = S3,
            EntityOrder = Hashes.EntityOrder, WalkerPositions = Hashes.WalkerPositions,
        };
    }

    // What the host's ReplayService puts on the heartbeat at the start of a tick (field names as on the wire).
    static JObject Heartbeat(Game host, int tick) => new JObject
    {
        [TimberNetBase.TYPE_KEY] = "HeartbeatEvent",
        [TimberNetBase.TICKS_KEY] = tick,
        ["randomS0Before"] = host.S0,
        ["randomStateHashBefore"] = DesyncCheck.RandomStateHash(host.S0, host.S1, host.S2, host.S3),
        ["entityOrderHash"] = host.Hashes.EntityOrder,
        ["walkerPositionHash"] = host.Hashes.WalkerPositions,
    };

    // What the guest's ReplayService finds when it plays that heartbeat: null when it is in step with the host.
    static string? GuestCheck(JObject heartbeat, Game guest) => DesyncCheck.Mismatch(
        (int?)heartbeat["randomS0Before"], (int?)heartbeat["randomStateHashBefore"],
        (int?)heartbeat["entityOrderHash"], (int?)heartbeat["walkerPositionHash"], guest.State);

    // The heartbeat goes through the production host and guest, as every heartbeat does.
    static JObject Deliver(ActivityTransportChecks.Session s, JObject heartbeat)
    {
        int tick = TimberNetBase.GetTick(heartbeat);
        s.Host.DoUserInitiatedEvent(heartbeat);
        JObject? received = null;
        Check(SpinWait.SpinUntil(() =>
        {
            received = s.Guests[0].ReadEvents(tick).FirstOrDefault(e => (string?)e[TimberNetBase.TYPE_KEY] == "HeartbeatEvent");
            return received != null;
        }, 3000), $"the heartbeat for tick {tick} never arrived");
        return received!;
    }

    // Plays ticks 1..ticks on both games and returns the first tick whose heartbeat the guest finds out of step, or -1.
    static int FirstMismatch(Game host, Game guest, int ticks, out string? mismatch)
    {
        using var s = new ActivityTransportChecks.Session(1);
        mismatch = null;
        for (int tick = 1; tick <= ticks; tick++)
        {
            host.Tick(tick); guest.Tick(tick);
            mismatch = GuestCheck(Deliver(s, Heartbeat(host, tick)), guest);
            if (mismatch != null) return tick;
        }
        return -1;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Desync check: two games in step pass every heartbeat", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            int first = FirstMismatch(host, guest, 2 * TickHashes.IdStride, out string? mismatch);
            Check(first == -1, $"a false desync at tick {first}: {mismatch}");
        });
        foreach (int word in new[] { 1, 2, 3 })
            yield return ($"Desync check: a random state that differs from the host's only in s{word} is caught", () =>
            {
                var host = Game.Colony(); var guest = host.Copy();
                if (word == 1) guest.S1 ^= 1; else if (word == 2) guest.S2 ^= 1 << 31; else guest.S3 += 1;
                int first = FirstMismatch(host, guest, 1, out string? mismatch);
                Check(first == 1, $"a difference in s{word} alone was not caught (s0 agrees)");
                Check(mismatch!.StartsWith("Random state mismatch"), mismatch);
            });
        yield return ("Desync check: a different s0 is still reported with the original log line", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            guest.S0 = 0x0BADF00D;
            FirstMismatch(host, guest, 1, out string? mismatch);
            Equal($"Random state mismatch: 0BADF00D != {host.S0:X8}", mismatch);
        });
        yield return ("Desync check: an entity the host does not have is caught at the next heartbeat", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            // Same random state throughout: only the set of entities that tick differs.
            guest.Buckets[3].Add(new Entity { Id = new Guid("ffffffff-0000-0000-0000-000000000001") });
            int first = FirstMismatch(host, guest, 1, out string? mismatch);
            Check(first == 1, "an extra entity with the random state unchanged was not caught");
            Check(mismatch!.Contains("entities"), mismatch);
        });
        yield return ("Desync check: an entity with another ID, at the same place in its bucket, is caught within IdStride ticks", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            // Same count in every bucket: only the sampled IDs can tell the two games apart.
            var bucket = guest.Buckets[5];
            bucket[9].Id = new Guid(bucket[9].Id.ToByteArray().Select((b, i) => i == 15 ? (byte)(b ^ 1) : b).ToArray());
            int first = FirstMismatch(host, guest, TickHashes.IdStride, out string? mismatch);
            Check(first >= 1, $"an entity with a different ID was not caught within {TickHashes.IdStride} ticks");
        });
        yield return ("Desync check: a walker one bit away from where the host has it is caught", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            var walker = guest.Buckets.SelectMany(b => b).First(e => e.Walks);
            walker.Z = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(walker.Z) + 1);
            int first = FirstMismatch(host, guest, 1, out string? mismatch);
            Check(first == 1, "a walker in a different place with the random state unchanged was not caught");
            Check(mismatch!.Contains("walking"), mismatch);
        });
        yield return ("Desync check: what the host did not send is not compared", () =>
        {
            var guest = Game.Colony(); guest.Tick(1);
            // An event other than the heartbeat carries only the random state; the walker and entity hashes are
            // not on it, so a guest with other values must not be stopped by it.
            var state = guest.State; state.EntityOrder ^= 1; state.WalkerPositions ^= 1;
            Check(DesyncCheck.Mismatch(guest.S0, DesyncCheck.RandomStateHash(guest.S0, guest.S1, guest.S2, guest.S3), null, null, state) == null);
            Check(DesyncCheck.Mismatch(null, null, null, null, state) == null);
        });
        yield return ("Desync check: only the random state stops the session; an entity or walker difference is logged once", () =>
        {
            var host = Game.Colony(); var guest = host.Copy();
            var walker = guest.Buckets.SelectMany(b => b).First(e => e.Walks);
            walker.Z = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(walker.Z) + 1);
            guest.Tick(1);
            var state = guest.State;
            int random = DesyncCheck.RandomStateHash(guest.S0, guest.S1, guest.S2, guest.S3);
            // The walker differs, the random state agrees: reported, but not a reason to stop (ReplayService only
            // stops on RandomMismatch).
            Check(DesyncCheck.TickMismatch(null, state.WalkerPositions ^ 1, state) != null, "a walker difference is not reported");
            Check(DesyncCheck.RandomMismatch(guest.S0, random, state) == null, "a walker difference alone would stop the session");
            Check(DesyncCheck.Mismatch(guest.S0, random, null, state.WalkerPositions ^ 1, state)!.Contains("walking"));
            // A random difference still stops it, whatever the walkers say.
            Check(DesyncCheck.RandomMismatch(guest.S0, random ^ 1, state) != null, "a random state difference would not stop the session");
            Check(DesyncCheck.RandomMismatch(guest.S0 ^ 1, null, state) != null, "an s0 difference would not stop the session");
            // One log line per game: the hashes add up, so a difference stays on every tick after.
            var hashes = new TickHashes();
            Check(!hashes.DifferenceLogged);
            hashes.DifferenceLogged = true;
            hashes.Reset();
            Check(!hashes.DifferenceLogged, "a new game would not log its first difference");
        });
        yield return ("Tick hashes: one ID in IdStride is read on a tick, and every entity within IdStride ticks", () =>
        {
            var hashes = new TickHashes();
            var ids = Enumerable.Range(0, 100).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();
            var seen = new HashSet<Guid>();
            for (int tick = 1; tick <= TickHashes.IdStride; tick++)
            {
                int reads = 0;
                hashes.StartTick(tick);
                hashes.AddBucket(ids, id => { reads++; seen.Add(id); return id; });
                Check(reads <= (ids.Count + TickHashes.IdStride - 1) / TickHashes.IdStride, $"tick {tick} read {reads} IDs of {ids.Count}");
            }
            Equal(ids.Count, seen.Count);
        });
        yield return ("Tick hashes: a new game starts them from where every player starts them", () =>
        {
            var played = Game.Colony();
            for (int tick = 1; tick <= 5; tick++) played.Tick(tick);
            Check(played.Hashes.EntityOrder != 0 && played.Hashes.WalkerPositions != 0);
            played.Hashes.Reset();
            Equal(0, played.Hashes.EntityOrder); Equal(0, played.Hashes.WalkerPositions);
            // A player who has been in another game since starting the program must agree with one who has not.
            var fresh = Game.Colony(); var reused = Game.Colony();
            reused.Hashes.StartTick(3); reused.Hashes.AddBucket(new List<Guid> { Guid.NewGuid() }, id => id); reused.Hashes.AddWalker(1, 2, 3);
            reused.Hashes.Reset();
            fresh.Tick(1); reused.Tick(1);
            Equal(fresh.Hashes.EntityOrder, reused.Hashes.EntityOrder); Equal(fresh.Hashes.WalkerPositions, reused.Hashes.WalkerPositions);
        });
    }
}
