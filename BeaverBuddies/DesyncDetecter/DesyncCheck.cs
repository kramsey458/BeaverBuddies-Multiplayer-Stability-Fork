using System;
using System.Collections.Generic;
using TimberNet;

namespace BeaverBuddies.DesyncDetecter
{
    /*
     * The always-on desync check. The host notes what its game looked like just before it played each event, and
     * at the start of every tick on the heartbeat; each guest compares that with its own game at the same point. A
     * difference in the random state stops the session (ReplayService.HandleDesync). A difference in the entities or
     * the walkers is only written to the log, once per game (see TickMismatch). It runs whatever the detailed-logging
     * settings are, on every player, so nothing here may depend on anything that differs between computers.
     *
     * What is compared:
     * - Unity's random state: all four of its 32-bit words, folded into one hash, next to the first word on its
     *   own. Earlier builds sent only the first word, so a game whose other 96 bits differed passed the check
     *   until the difference reached the first word.
     * - On the heartbeat only: which entities tick in each bucket, and where every walking character stands (see
     *   TickHashes), as the previous tick left them. A beaver in another place, or an entity one game has and the
     *   other has not, used to go unseen until it changed a random draw.
     *
     * No Unity or game types here, so the test project can exercise it.
     */

    /// <summary>What the check reads from this player's game.</summary>
    public struct GameState
    {
        // UnityEngine.Random.state.
        public int S0, S1, S2, S3;
        // TickHashes, as TEBPatcher keeps them.
        public int EntityOrder;
        public int WalkerPositions;
    }

    public static class DesyncCheck
    {
        /// <summary>
        /// All four words of Unity's random state in one number. A difference in any single word always changes it
        /// (each word is multiplied by an odd number, which cannot turn a difference into zero).
        /// </summary>
        public static int RandomStateHash(int s0, int s1, int s2, int s3)
        {
            return TimberNetBase.CombineHash(TimberNetBase.CombineHash(TimberNetBase.CombineHash(s0, s1), s2), s3);
        }

        /// <summary>
        /// Null when this game agrees with everything the host sent, otherwise a line for the log that says what
        /// differs. A value the host did not send (null) is not compared: only heartbeats carry the entity and
        /// walker hashes, and an event the host did not play carries no random state.
        /// </summary>
        public static string Mismatch(int? hostS0, int? hostRandomState, int? hostEntityOrder, int? hostWalkerPositions, GameState local)
        {
            return RandomMismatch(hostS0, hostRandomState, local) ?? TickMismatch(hostEntityOrder, hostWalkerPositions, local);
        }

        /// <summary>
        /// Null when this game's random state is the host's. A difference here stops the session: the games have
        /// already drawn different numbers, or are about to.
        /// </summary>
        public static string RandomMismatch(int? hostS0, int? hostRandomState, GameState local)
        {
            // The original line, kept word for word for anyone who searches logs for it.
            if (hostS0 != null && local.S0 != hostS0)
                return $"Random state mismatch: {local.S0:X8} != {hostS0:X8}";
            if (hostRandomState != null)
            {
                int random = RandomStateHash(local.S0, local.S1, local.S2, local.S3);
                if (random != hostRandomState)
                    return $"Random state mismatch: the first word agrees but the rest does not " +
                        $"(here {local.S0:X8} {local.S1:X8} {local.S2:X8} {local.S3:X8}, hash {random:X8}; host's hash {hostRandomState:X8})";
            }
            return null;
        }

        /// <summary>
        /// Null when the entities that tick and the walkers' positions are the host's. A difference here is only
        /// logged, once per game, and the session goes on: this comparison has not been played for long yet, and a
        /// walker that something moves on the frame (an Earth Repopulator pilot flying its plane, for example) would
        /// end a game whose simulation still agrees. If the games really went apart, the random state follows and
        /// stops the session; the log line then says when the walkers first differed.
        /// </summary>
        public static string TickMismatch(int? hostEntityOrder, int? hostWalkerPositions, GameState local)
        {
            if (hostEntityOrder != null && local.EntityOrder != hostEntityOrder)
                return $"Entity mismatch: the entities that tick here are not the host's (hash {local.EntityOrder:X8} != {hostEntityOrder:X8})";
            if (hostWalkerPositions != null && local.WalkerPositions != hostWalkerPositions)
                return $"Walker mismatch: a walking character is not where the host has it (hash {local.WalkerPositions:X8} != {hostWalkerPositions:X8})";
            return null;
        }
    }

    /// <summary>
    /// Two running hashes over what ticks, kept by TEBPatcher just before each bucket of entities ticks and sent on
    /// every heartbeat: which entities are in each bucket, and where each walking character stands. They run from
    /// the moment a multiplayer game loads, on every player, so a difference stays visible once it has happened.
    ///
    /// Cost: TEBPatcher already visits every entity in the bucket and every walker's position, to keep the walkers'
    /// animation in step. On top of that this reads one number per bucket (its size), one entity ID in
    /// <see cref="IdStride"/>, and three floats per walker. For a colony the size of the one measured for 1.1.10
    /// (11,464 entities, 361 walkers, 128 buckets) a synthetic run on .NET 8 puts that at about 3 µs a tick on top of
    /// the visit itself (about 11 µs), against about 14 µs for hashing every ID, as every tick did before 1.1.10. The
    /// game runs on Unity's slower Mono runtime, so the figure there has not been measured; the ratio is what matters.
    ///
    /// The size of a bucket catches an entity that one game has and the other does not at once. Which IDs are read
    /// moves along by one each tick, so an entity whose ID differs (in the same bucket, at the same place) is caught
    /// within IdStride ticks. A walker's position is compared bit for bit, like the walker trace.
    /// </summary>
    public sealed class TickHashes
    {
        public const int IdStride = 8;

        public int EntityOrder { get; private set; }
        public int WalkerPositions { get; private set; }
        // Whether this game already logged an entity or walker difference (see DesyncCheck.TickMismatch). The hashes
        // add up, so once they differ they differ on every tick after; one line is enough.
        public bool DifferenceLogged { get; set; }

        // Which position in each bucket is read first on this tick: the same on every player, since it only
        // depends on the tick.
        private int firstSampled;

        /// <summary>Back to where every player starts: called when a multiplayer game loads.</summary>
        public void Reset()
        {
            EntityOrder = 0;
            WalkerPositions = 0;
            firstSampled = 0;
            DifferenceLogged = false;
        }

        public void StartTick(int tick)
        {
            firstSampled = ((tick % IdStride) + IdStride) % IdStride;
        }

        /// <summary>A bucket about to tick, in the order it ticks in (the game keeps it sorted by entity ID).</summary>
        public void AddBucket<T>(IList<T> entities, Func<T, Guid> idOf)
        {
            int count = entities.Count;
            int hash = TimberNetBase.CombineHash(EntityOrder, count);
            for (int i = firstSampled; i < count; i += IdStride)
            {
                hash = TimberNetBase.CombineHash(hash, idOf(entities[i]).GetHashCode());
            }
            EntityOrder = hash;
        }

        /// <summary>A walking character's position just before its bucket ticks, as its exact bits.</summary>
        public void AddWalker(float x, float y, float z)
        {
            int hash = TimberNetBase.CombineHash(WalkerPositions, BitConverter.SingleToInt32Bits(x));
            hash = TimberNetBase.CombineHash(hash, BitConverter.SingleToInt32Bits(y));
            WalkerPositions = TimberNetBase.CombineHash(hash, BitConverter.SingleToInt32Bits(z));
        }
    }
}
