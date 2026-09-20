using System;
using System.Globalization;

namespace TimberNet.Perf
{
    /// <summary>
    /// The places whose time is measured. Times are exclusive ("self" time): a scope nested inside another
    /// is taken out of the outer one, so the slots never overlap and add up to the time inside any of them.
    /// </summary>
    public enum PerfSlot
    {
        /// <summary>gameMs: the tick loop itself, minus everything below. The game's own ticking, and other mods' patches on it.</summary>
        Sim = 0,
        /// <summary>tebMs: the per-tick pass over every entity (order and position hash, animator sync).</summary>
        EntityHash,
        /// <summary>replayMs: replaying the events of a tick (the game work an event does), not reading them.</summary>
        Replay,
        /// <summary>serMs: turning an event into JSON and parsing it back to send it.</summary>
        Serialize,
        /// <summary>hashMs: adding an event to the event-stream hash.</summary>
        EventHash,
        /// <summary>compMs: JSON text and gzip for a message about to be sent (game thread only).</summary>
        Compress,
        /// <summary>sendMs: writing to the connection, including waiting for its lock (game thread only).</summary>
        Send,
        /// <summary>recvMs: the game thread's side of receiving: queues, ordering, waiting checks.</summary>
        Recv,
        /// <summary>deserMs: turning received JSON into events.</summary>
        Deserialize,
        /// <summary>steamMs: serving Steam networking.</summary>
        Steam,
        /// <summary>logMs: writing this mod's log lines.</summary>
        Log,
        /// <summary>detailMs: work only done while detailed logging is on (traces, whole-map hashes).</summary>
        Detail,
        /// <summary>uiMs: the connection panel and the player activity overlay.</summary>
        Ui,
        /// <summary>saveMs: saving the game.</summary>
        Save,
    }

    public enum PerfColumnKind { Text, Int, Fixed1, Fixed2 }

    /// <summary>How a column of frame rows turns into a column of a summary row over many frames.</summary>
    public enum PerfAggregate { Last, Sum, SumPositive, Mean, Max }

    /// <summary>
    /// The columns of the log, in order. Every row has all of them; which ones mean something depends on the row type:
    /// F (one frame over the threshold), W (one wait for the other player over the threshold) and S (a summary of
    /// every frame since the last one, with times as averages per frame).
    /// </summary>
    public static class PerfColumns
    {
        public const int SlotCount = 14;

        public const int Type = 0, Frame = 1, Tick = 2, UtcMs = 3, Frames = 4, FrameMs = 5, MaxFrameMs = 6,
            Ticks = 7, Buckets = 8, Waiting = 9, WaitMs = 10, Speed = 11, Target = 12, Behind = 13,
            PacePct = 14, Hold = 15, Saving = 16, Focused = 17,
            SlotBase = 18,
            OtherMs = SlotBase + SlotCount,
            GcDelta = OtherMs + 1, HeapMB = OtherMs + 2, AllocKB = OtherMs + 3,
            MsgOut = OtherMs + 4, BytesOut = OtherMs + 5, MsgIn = OtherMs + 6, BytesIn = OtherMs + 7,
            ProbeUs = OtherMs + 8, Dropped = OtherMs + 9,
            Count = OtherMs + 10;

        public const char FrameRow = 'F', WaitRow = 'W', SummaryRow = 'S';

        public static readonly string[] Names =
        {
            "type", "frame", "tick", "utcMs", "frames", "frameMs", "maxFrameMs",
            "ticks", "buckets", "waiting", "waitMs", "speed", "target", "behind",
            "pacePct", "hold", "saving", "focused",
            "gameMs", "tebMs", "replayMs", "serMs", "hashMs", "compMs", "sendMs", "recvMs", "deserMs",
            "steamMs", "logMs", "detailMs", "uiMs", "saveMs",
            "otherMs",
            "gcDelta", "heapMB", "allocKB",
            "msgOut", "bytesOut", "msgIn", "bytesIn",
            "probeUs", "dropped",
        };

        public static readonly PerfColumnKind[] Kinds = BuildKinds();
        public static readonly PerfAggregate[] Aggregates = BuildAggregates();

        static PerfColumnKind[] BuildKinds()
        {
            var kinds = new PerfColumnKind[Count];
            for (int i = 0; i < Count; i++) kinds[i] = PerfColumnKind.Int;
            kinds[Type] = PerfColumnKind.Text;
            foreach (int c in new[] { FrameMs, MaxFrameMs, WaitMs, Speed, Target }) kinds[c] = PerfColumnKind.Fixed2;
            for (int i = 0; i <= SlotCount; i++) kinds[SlotBase + i] = PerfColumnKind.Fixed2;
            foreach (int c in new[] { HeapMB, AllocKB, ProbeUs }) kinds[c] = PerfColumnKind.Fixed1;
            return kinds;
        }

        static PerfAggregate[] BuildAggregates()
        {
            var modes = new PerfAggregate[Count];
            for (int i = 0; i < Count; i++) modes[i] = PerfAggregate.Last;
            foreach (int c in new[] { Frames, Ticks, Buckets, Waiting, WaitMs, GcDelta, MsgOut, BytesOut, MsgIn, BytesIn }) modes[c] = PerfAggregate.Sum;
            modes[AllocKB] = PerfAggregate.SumPositive;
            foreach (int c in new[] { MaxFrameMs, Behind, Saving }) modes[c] = PerfAggregate.Max;
            modes[FrameMs] = PerfAggregate.Mean; modes[OtherMs] = PerfAggregate.Mean; modes[ProbeUs] = PerfAggregate.Mean;
            for (int i = 0; i < SlotCount; i++) modes[SlotBase + i] = PerfAggregate.Mean;
            return modes;
        }

        public static string HeaderLine() => string.Join(",", Names);

        /// <summary>
        /// Writes one row as text, comma separated and ending in a newline, without allocating. Numbers are always
        /// written the invariant way, so a language that writes 1,5 cannot add columns. False if it did not fit.
        /// </summary>
        public static bool TryFormatRow(ReadOnlySpan<double> row, Span<char> destination, out int written)
        {
            written = 0;
            int position = 0;
            for (int i = 0; i < Count; i++)
            {
                if (i > 0)
                {
                    if (position >= destination.Length) return false;
                    destination[position++] = ',';
                }
                double value = row[i];
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
                int length;
                switch (Kinds[i])
                {
                    case PerfColumnKind.Text:
                        if (position >= destination.Length) return false;
                        destination[position++] = (char)(int)value;
                        continue;
                    case PerfColumnKind.Int:
                        if (!((long)Math.Round(value)).TryFormat(destination.Slice(position), out length, default, CultureInfo.InvariantCulture)) return false;
                        break;
                    case PerfColumnKind.Fixed1:
                        if (!value.TryFormat(destination.Slice(position), out length, "F1", CultureInfo.InvariantCulture)) return false;
                        break;
                    default:
                        if (!value.TryFormat(destination.Slice(position), out length, "F2", CultureInfo.InvariantCulture)) return false;
                        break;
                }
                position += length;
            }
            if (position >= destination.Length) return false;
            destination[position++] = '\n';
            written = position;
            return true;
        }
    }
}
