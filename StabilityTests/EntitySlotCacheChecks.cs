#nullable enable
using BeaverBuddies;

// The per-bucket memory of which entities have a MovementAnimator, so the entity pass does not look
// that up for every entity on every tick.
static class EntitySlotCacheChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    sealed class Ent
    {
        public readonly Guid Id;
        public readonly bool Mover;
        public Ent(Guid id, bool mover) { Id = id; Mover = mover; }
    }

    // An entity that claims to equal every other one, to show the cache compares objects, not values.
    sealed class EqualsEverything
    {
        public override bool Equals(object? obj) => true;
        public override int GetHashCode() => 0;
    }

    sealed class Animator { }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Slot cache: a position misses until it is set, then hits for the same entity only", () =>
        {
            var cache = new EntitySlotCache<Ent, string?>();
            var a = new Ent(Guid.NewGuid(), true); var b = new Ent(Guid.NewGuid(), false);
            Check(!cache.TryGet(0, a, out _));
            cache.Set(0, a, "animator");
            Check(cache.TryGet(0, a, out var value)); Equal("animator", value);
            Check(!cache.TryGet(0, b, out _), "another entity at the same position must miss");
            Check(!cache.TryGet(1, a, out _), "the same entity at another position must miss");
        });
        yield return ("Slot cache: 'no result' is remembered too, which is what saves the lookup for entities that do not move", () =>
        {
            var cache = new EntitySlotCache<Ent, Animator?>();
            var building = new Ent(Guid.NewGuid(), false);
            cache.Set(3, building, null);
            Check(cache.TryGet(3, building, out var value), "a remembered null result must count as a hit");
            Check(value == null);
        });
        yield return ("Slot cache: entities are compared by reference, not by Equals", () =>
        {
            var cache = new EntitySlotCache<EqualsEverything, string?>();
            var first = new EqualsEverything(); var second = new EqualsEverything();
            cache.Set(0, first, "x");
            Check(!cache.TryGet(0, second, out _));
            Check(cache.TryGet(0, first, out _));
        });
        yield return ("Slot cache: bad indexes and null entities never hit", () =>
        {
            var cache = new EntitySlotCache<Ent, string?>();
            Check(!cache.TryGet(-1, new Ent(Guid.NewGuid(), true), out _));
            Check(!cache.TryGet(100, new Ent(Guid.NewGuid(), true), out _));
            // An empty position holds a null entity: asking with null must not count as a hit.
            Check(!cache.TryGet(0, null!, out _));
            cache.Set(2, new Ent(Guid.NewGuid(), true), "x");
            Check(!cache.TryGet(0, null!, out _));
            try { cache.Set(-1, new Ent(Guid.NewGuid(), true), "x"); throw new Exception("expected an exception"); }
            catch (ArgumentOutOfRangeException) { }
        });
        yield return ("Slot cache: grows past its first size and keeps earlier positions", () =>
        {
            var cache = new EntitySlotCache<Ent, string?>();
            var all = Enumerable.Range(0, 5000).Select(i => new Ent(Guid.NewGuid(), i % 30 == 0)).ToList();
            for (int i = 0; i < all.Count; i++) cache.Set(i, all[i], all[i].Mover ? "animator" : null);
            for (int i = 0; i < all.Count; i++)
            {
                Check(cache.TryGet(i, all[i], out var value), $"position {i} lost");
                Equal(all[i].Mover ? "animator" : null, value);
            }
        });
        yield return ("Slot cache: after Trim the dropped positions miss and can be filled again", () =>
        {
            var cache = new EntitySlotCache<Ent, string?>();
            var list = Enumerable.Range(0, 10).Select(i => new Ent(Guid.NewGuid(), true)).ToList();
            for (int i = 0; i < list.Count; i++) cache.Set(i, list[i], "x");
            cache.Trim(4);
            for (int i = 0; i < 4; i++) Check(cache.TryGet(i, list[i], out _), $"position {i} should be kept");
            for (int i = 4; i < 10; i++) Check(!cache.TryGet(i, list[i], out _), $"position {i} should be dropped");
            cache.Set(4, list[4], "again");
            Check(cache.TryGet(4, list[4], out var value)); Equal("again", value);
            cache.Trim(0); Check(!cache.TryGet(0, list[0], out _));
            cache.Trim(-3); cache.Trim(50);   // out-of-range counts are harmless
        });
        yield return ("Slot cache: a second pass over an unchanged list does no lookups", () =>
        {
            var game = new SortedList<Guid, Ent>();
            for (int i = 0; i < 1000; i++) { var e = new Ent(Guid.NewGuid(), i % 30 == 0); game.Add(e.Id, e); }
            var cache = new EntitySlotCache<Ent, Animator?>();
            int lookups = 0;
            Pass(game, cache, () => lookups++);
            Equal(1000, lookups);
            lookups = 0;
            for (int i = 0; i < 5; i++) Pass(game, cache, () => lookups++);
            Equal(0, lookups);
        });
        yield return ("Slot cache: adding or removing an entity only repeats lookups for the positions that shifted", () =>
        {
            var game = new SortedList<Guid, Ent>();
            for (int i = 0; i < 200; i++) { var e = new Ent(Guid.NewGuid(), i % 7 == 0); game.Add(e.Id, e); }
            var cache = new EntitySlotCache<Ent, Animator?>();
            int lookups = 0;
            Pass(game, cache, () => lookups++);

            var added = new Ent(Guid.NewGuid(), true);
            game.Add(added.Id, added);
            int at = game.IndexOfKey(added.Id);
            lookups = 0;
            Pass(game, cache, () => lookups++);
            Equal(game.Count - at, lookups);   // the new entity and everything after it

            lookups = 0;
            Pass(game, cache, () => lookups++);
            Equal(0, lookups);

            int removedAt = 50;
            game.RemoveAt(removedAt);
            lookups = 0;
            Pass(game, cache, () => lookups++);
            Equal(game.Count - removedAt, lookups);
        });
        yield return ("Slot cache: never returns a wrong answer while entities are added and removed at random", () =>
        {
            var random = new Random(12345);
            var game = new SortedList<Guid, Ent>();
            var cache = new EntitySlotCache<Ent, Animator?>();
            for (int round = 0; round < 400; round++)
            {
                int adds = random.Next(0, 4), removes = random.Next(0, 4);
                for (int i = 0; i < adds; i++) { var e = new Ent(Guid.NewGuid(), random.Next(30) == 0); game.Add(e.Id, e); }
                for (int i = 0; i < removes && game.Count > 0; i++) game.RemoveAt(random.Next(game.Count));
                // Pass checks every entity's cached answer against the truth as it goes.
                Pass(game, cache, () => { });
            }
            Check(game.Count > 0);
        });
    }

    // What the entity pass does: use the remembered answer when there is one, otherwise look it up
    // and remember it. The answer is a fresh Animator for movers and null for everything else, and
    // every answer is checked against what a fresh lookup would give.
    static void Pass(SortedList<Guid, Ent> game, EntitySlotCache<Ent, Animator?> cache, Action onLookup)
    {
        var entities = game.Values;
        for (int i = 0; i < game.Count; i++)
        {
            var entity = entities[i];
            if (!cache.TryGet(i, entity, out Animator? animator))
            {
                onLookup();
                animator = entity.Mover ? new Animator() : null;
                cache.Set(i, entity, animator);
            }
            Check((animator != null) == entity.Mover, $"wrong cached answer at position {i}");
        }
        cache.Trim(game.Count);
    }
}
