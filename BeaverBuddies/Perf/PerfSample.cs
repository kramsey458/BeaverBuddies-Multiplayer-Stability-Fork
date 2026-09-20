using System;
using System.Globalization;
using System.Text;

namespace BeaverBuddies.Perf
{
    public enum PerfRowKind
    {
        /// <summary>One frame that took longer than the threshold.</summary>
        Spike = 0,
        /// <summary>Everything since the last summary, so the log has a baseline and not only the spikes.</summary>
        Summary = 1,
    }

    /*
     * One row of the performance log.
     *
     * A plain value with no Unity or game types in it, so the game thread can fill one without allocating,
     * another thread can turn it into text, and the test project can exercise the formatting.
     *
     * Times are milliseconds. On a spike row they describe that one frame. On a summary row they are the
     * totals over the window and Frames says how many frames that was, so a mean is Total / Frames.
     */
    public struct PerfSample
    {
        public PerfRowKind Kind;

        /// <summary>The game tick this frame (or window) ended on. Both players' logs line up on this.</summary>
        public int Tick;
        public int Frames;

        /// <summary>Wall time from one frame to the next, measured at the game's own per-frame tick entry.</summary>
        public float FrameMs;
        /// <summary>The worst single frame. Equal to FrameMs on a spike row.</summary>
        public float MaxFrameMs;

        /// <summary>Ticks the simulation actually ran. Zero over several frames is a player waiting.</summary>
        public int TicksDone;
        /// <summary>
        /// Frames whose tick loop stopped because the other player's events for the next tick had not arrived.
        /// A guest does not block on a wait handle: it simply runs no ticks, so this is how waiting shows up.
        /// </summary>
        public int WaitFrames;
        public int TicksBehind;

        // Our own sync work, each timed separately.
        public float ReadMs;       // reading events off the connection, including deserializing them
        public float ReplayMs;     // replaying those events
        public float SendMs;       // serializing, hashing, compressing and writing our own events
        public float HashMs;       // the per-tick entity order and position hash
        public float IoUpdateMs;   // draining the network queues outside of a read
        public float SteamPumpMs;  // handing data to Steam between ticks
        public float TraceMs;      // the detailed-logging work: water and moisture hashes, trace events
        public float WaitCheckMs;  // asking whether the other player's events have arrived

        /// <summary>Compressed bytes handed to the socket from the game thread, and how many messages.</summary>
        public int SendBytes;
        public int SendCount;

        // Collections since the previous row, and the managed heap right now.
        public int Gc0, Gc1, Gc2;
        public long HeapBytes;

        // Why a frame was long is often the speed, not the work: a player that fell behind runs the
        // simulation faster to catch up, which lengthens frames until it is level again.
        public float Speed, TargetSpeed;
        public int HostPacingPercent, FpsPacingPercent;
        public bool Holding;
        /// <summary>True if a save was in progress, so an autosave collision is visible rather than inferred.</summary>
        public bool Saving;
        /// <summary>Speed changes applied this frame. Each one is broadcast to every animated building.</summary>
        public int SpeedChanges;
    }

    /// <summary>Turns samples into CSV. Pure text handling, kept apart from everything that measures.</summary>
    public static class PerfCsv
    {
        public const string Header =
            "kind,tick,frames,frameMs,maxFrameMs,ticksDone,waitFrames,ticksBehind," +
            "readMs,replayMs,sendMs,hashMs,ioUpdateMs,steamPumpMs,traceMs,waitCheckMs," +
            "sendBytes,sendCount,gc0,gc1,gc2,heapBytes," +
            "speed,targetSpeed,hostPacingPct,fpsPacingPct,holding,saving,speedChanges";

        public static void Format(StringBuilder line, in PerfSample sample)
        {
            var culture = CultureInfo.InvariantCulture;
            line.Append(sample.Kind == PerfRowKind.Spike ? "spike" : "summary");
            Number(line, sample.Tick);
            Number(line, sample.Frames);
            Number(line, sample.FrameMs);
            Number(line, sample.MaxFrameMs);
            Number(line, sample.TicksDone);
            Number(line, sample.WaitFrames);
            Number(line, sample.TicksBehind);
            Number(line, sample.ReadMs);
            Number(line, sample.ReplayMs);
            Number(line, sample.SendMs);
            Number(line, sample.HashMs);
            Number(line, sample.IoUpdateMs);
            Number(line, sample.SteamPumpMs);
            Number(line, sample.TraceMs);
            Number(line, sample.WaitCheckMs);
            Number(line, sample.SendBytes);
            Number(line, sample.SendCount);
            Number(line, sample.Gc0);
            Number(line, sample.Gc1);
            Number(line, sample.Gc2);
            line.Append(',').Append(sample.HeapBytes.ToString(culture));
            Number(line, sample.Speed);
            Number(line, sample.TargetSpeed);
            Number(line, sample.HostPacingPercent);
            Number(line, sample.FpsPacingPercent);
            line.Append(',').Append(sample.Holding ? '1' : '0');
            line.Append(',').Append(sample.Saving ? '1' : '0');
            Number(line, sample.SpeedChanges);
        }

        public static string Format(in PerfSample sample)
        {
            var line = new StringBuilder(256);
            Format(line, in sample);
            return line.ToString();
        }

        static void Number(StringBuilder line, int value) =>
            line.Append(',').Append(value.ToString(CultureInfo.InvariantCulture));

        // Three decimals is well under the resolution of anything being measured, and keeps the file small.
        static void Number(StringBuilder line, float value) =>
            line.Append(',').Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /*
     * A fixed ring of samples: the game thread adds, the writer thread drains.
     *
     * The buffer is allocated once and never grows, so adding a sample allocates nothing. If the writer ever
     * falls behind, the oldest samples are dropped rather than the buffer growing, and the number dropped is
     * recorded so the log can say so. A logger that pauses the game it is measuring is worse than no logger.
     */
    public sealed class PerfRing
    {
        readonly PerfSample[] items;
        readonly object gate = new object();
        int start, count;
        long dropped;

        public PerfRing(int capacity)
        {
            items = new PerfSample[Math.Max(1, capacity)];
        }

        public int Capacity => items.Length;

        public int Count { get { lock (gate) return count; } }

        /// <summary>Samples thrown away because the writer could not keep up. Normally zero.</summary>
        public long Dropped { get { lock (gate) return dropped; } }

        public void Add(in PerfSample sample)
        {
            lock (gate)
            {
                if (count == items.Length)
                {
                    // Full: drop the oldest, which is the one least likely to still matter.
                    items[start] = sample;
                    start = (start + 1) % items.Length;
                    dropped++;
                    return;
                }
                items[(start + count) % items.Length] = sample;
                count++;
            }
        }

        /// <summary>Moves everything waiting into <paramref name="destination"/> and returns how many.</summary>
        public int Drain(PerfSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            lock (gate)
            {
                int taken = Math.Min(destination.Length, count);
                for (int i = 0; i < taken; i++)
                {
                    destination[i] = items[(start + i) % items.Length];
                }
                start = (start + taken) % items.Length;
                count -= taken;
                return taken;
            }
        }
    }
}
