using System;
using System.Collections.Generic;
using System.Globalization;

namespace TimberNet.Perf
{
    /// <summary>
    /// The places whose time is measured. Times are exclusive ("self") time: a scope nested inside another is taken out of
    /// the outer one, so the slots never overlap and add up to the time inside any of them.
    /// </summary>
    public enum PerfSlot
    {
        /// <summary>gameMs: the tick loop itself, minus everything below. The game's own ticking, and other mods' patches on it.</summary>
        Sim = 0,
        /// <summary>tebMs: the per-tick pass over every entity (order and position hash), minus the two carved out below.</summary>
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
        /// <summary>animMs: this mod's replacement of MovementAnimator.Update, for every animated character, every frame.</summary>
        Anim,
        /// <summary>speedMs: changing the game speed (which notifies every animated building).</summary>
        Speed,
        /// <summary>tebLookMs: looking up each entity's MovementAnimator in the entity pass (sampled, then scaled).</summary>
        TebLookup,
        /// <summary>tebAnimMs: bringing each walker's model up to date in the entity pass.</summary>
        TebAnimate,
        /// <summary>parMs: the game thread waiting for the parallel part of the tick to finish.</summary>
        Parallel,
    }

    /// <summary>Things that are counted, not timed. Each is a column: how many times it happened in the row.</summary>
    public enum PerfCounter
    {
        Entities = 0,   // entities visited by the entity pass
        Movers,         // of those, the ones with a MovementAnimator
        EntityTicks,    // calls of TickableEntity.Tick
        Anim,           // MovementAnimator.Update replacements
        TimeCalls,      // reads of UnityEngine.Time.time (the detour)
        DayNight,       // reads of DayNightCycle.FluidSecondsPassedToday
        Rng,            // calls of the patched random number generator
        Guid,           // calls of Guid.NewGuid
        Spawn,          // entities created
        Sound,          // SoundEmitter.Update calls
        Input,          // InputService.UpdateSingleton calls
        Ticker,         // Ticker.Update calls
        SpeedChanges,   // SetSpeedSilentlyNow calls
    }

    public enum PerfColumnKind { Text, Int, Fixed1, Fixed2 }

    /// <summary>How a column of frame rows turns into a column of a summary row over many frames.</summary>
    public enum PerfAggregate { Last, Sum, SumPositive, Mean, Max }

    /// <summary>A set of columns: their names, how each is written, and the text of a row. Nothing here allocates per row.</summary>
    public sealed class PerfTable
    {
        public readonly string[] Names;
        public readonly PerfColumnKind[] Kinds;
        public int Count => Names.Length;

        public PerfTable(string[] names, PerfColumnKind[] kinds)
        {
            if (names.Length != kinds.Length) throw new ArgumentException("every column needs a kind");
            Names = names; Kinds = kinds;
        }

        public string HeaderLine() => string.Join(",", Names);

        /// <summary>
        /// Writes one row as text, comma separated and ending in a newline, without allocating. Numbers are always written the
        /// invariant way, so a language that writes 1,5 cannot add columns. False if it did not fit.
        /// </summary>
        public bool TryFormatRow(ReadOnlySpan<double> row, Span<char> destination, out int written)
        {
            written = 0;
            int position = 0;
            for (int i = 0; i < Names.Length; i++)
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

    /// <summary>
    /// The columns of the log, in order. Every row has all of them; which ones mean something depends on the row type:
    /// F (one frame over the threshold), W (one wait for the other player over the threshold) and S (a summary of every frame
    /// since the last one). In S rows times are averages per frame, and allocation, counts and histograms are totals.
    /// </summary>
    public static class PerfColumns
    {
        public const int SlotCount = 19;
        public const int CounterCount = 13;
        public const int PhaseCount = 8;
        /// <summary>Values the game side supplies every frame (draw calls, Unity's frame timing...).</summary>
        public const int ExtraPerFrameCount = 11;
        /// <summary>Values the game side supplies only when a row is written (memory sizes).</summary>
        public const int ExtraHeavyCount = 4;
        public const int FrameHistCount = 17;
        public const int TickHistCount = 6;

        public const char FrameRow = 'F', WaitRow = 'W', SummaryRow = 'S';

        /// <summary>Upper edges in milliseconds of the frame time buckets; the last bucket is everything above.</summary>
        public static readonly double[] FrameEdgesMs = { 4, 6, 8.5, 11.5, 14, 17.5, 21, 25, 30, 35, 42, 50, 75, 100, 200, 400 };

        public static readonly string[] SlotTimeNames =
        {
            "gameMs", "tebMs", "replayMs", "serMs", "hashMs", "compMs", "sendMs", "recvMs", "deserMs", "steamMs",
            "logMs", "detailMs", "uiMs", "saveMs", "animMs", "speedMs", "tebLookMs", "tebAnimMs", "parMs",
        };

        public static readonly string[] SlotAllocNames =
        {
            "gameKB", "tebKB", "replayKB", "serKB", "hashKB", "compKB", "sendKB", "recvKB", "deserKB", "steamKB",
            "logKB", "detailKB", "uiKB", "saveKB", "animKB", "speedKB", "tebLookKB", "tebAnimKB", "parKB",
        };

        /// <summary>Slots whose allocation is measured. The others are called too often, or are carved out of one that is.</summary>
        public static readonly bool[] SlotTracksAlloc =
        {
            true, true, true, true, true, true, true, true, true, false,
            false, true, true, true, false, true, false, false, true,
        };

        public static readonly string[] CounterNames =
        {
            "entities", "movers", "entTicks", "nAnim", "nTime", "nDayNight", "nRng", "nGuid", "nSpawn", "nSound", "nInput", "nTicker", "nSpeed",
        };

        public static readonly string[] PhaseNames = { "plTime", "plInit", "plEarly", "plFixed", "plPre", "plUpdate", "plLate", "plPost" };

        public static readonly string[] ExtraPerFrameNames =
        {
            "prGcBytes", "prGcCount", "prDraw", "prSetPass", "prBatches", "prTris", "ftCpu", "ftMain", "ftRender", "ftGpu", "ftWait",
        };

        public static readonly string[] ExtraHeavyNames = { "monoHeapMB", "monoUsedMB", "nativeMB", "workingMB" };

        // ---- the table is built in one place, in order, so no column is ever numbered by hand ----
        static readonly List<string> names = new List<string>();
        static readonly List<PerfColumnKind> kinds = new List<PerfColumnKind>();
        static readonly List<PerfAggregate> aggregates = new List<PerfAggregate>();

        static int Add(string name, PerfColumnKind kind, PerfAggregate aggregate)
        {
            names.Add(name); kinds.Add(kind); aggregates.Add(aggregate);
            return names.Count - 1;
        }

        static int AddGroup(string[] groupNames, PerfColumnKind kind, PerfAggregate aggregate)
        {
            int first = names.Count;
            foreach (string name in groupNames) Add(name, kind, aggregate);
            return first;
        }

        static int AddIndexed(string prefix, int count, PerfColumnKind kind, PerfAggregate aggregate)
        {
            int first = names.Count;
            for (int i = 0; i < count; i++) Add(prefix + i, kind, aggregate);
            return first;
        }

        public static readonly int Type = Add("type", PerfColumnKind.Text, PerfAggregate.Last);
        public static readonly int Frame = Add("frame", PerfColumnKind.Int, PerfAggregate.Last);
        public static readonly int Tick = Add("tick", PerfColumnKind.Int, PerfAggregate.Last);
        public static readonly int UtcMs = Add("utcMs", PerfColumnKind.Int, PerfAggregate.Last);
        public static readonly int Frames = Add("frames", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int FrameMs = Add("frameMs", PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int MaxFrameMs = Add("maxFrameMs", PerfColumnKind.Fixed2, PerfAggregate.Max);
        public static readonly int Ticks = Add("ticks", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int Buckets = Add("buckets", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int Waiting = Add("waiting", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int WaitMs = Add("waitMs", PerfColumnKind.Fixed2, PerfAggregate.Sum);
        public static readonly int Speed = Add("speed", PerfColumnKind.Fixed2, PerfAggregate.Last);
        public static readonly int Target = Add("target", PerfColumnKind.Fixed2, PerfAggregate.Last);
        public static readonly int Behind = Add("behind", PerfColumnKind.Int, PerfAggregate.Max);
        public static readonly int PacePct = Add("pacePct", PerfColumnKind.Int, PerfAggregate.Last);
        public static readonly int Hold = Add("hold", PerfColumnKind.Int, PerfAggregate.Last);
        public static readonly int Saving = Add("saving", PerfColumnKind.Int, PerfAggregate.Max);
        public static readonly int Focused = Add("focused", PerfColumnKind.Int, PerfAggregate.Last);

        public static readonly int SlotBase = AddGroup(SlotTimeNames, PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int OtherMs = Add("otherMs", PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int AllocBase = AddGroup(SlotAllocNames, PerfColumnKind.Fixed1, PerfAggregate.Sum);
        public static readonly int OtherKB = Add("otherKB", PerfColumnKind.Fixed1, PerfAggregate.Sum);

        public static readonly int GcDelta = Add("gcDelta", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int HeapMB = Add("heapMB", PerfColumnKind.Fixed1, PerfAggregate.Last);
        public static readonly int AllocKB = Add("allocKB", PerfColumnKind.Fixed1, PerfAggregate.SumPositive);
        public static readonly int MsgOut = Add("msgOut", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int BytesOut = Add("bytesOut", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int MsgIn = Add("msgIn", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int BytesIn = Add("bytesIn", PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int ProbeUs = Add("probeUs", PerfColumnKind.Fixed1, PerfAggregate.Mean);
        public static readonly int OverheadUs = Add("overheadUs", PerfColumnKind.Fixed1, PerfAggregate.Mean);
        public static readonly int Dropped = Add("dropped", PerfColumnKind.Int, PerfAggregate.Last);

        public static readonly int CounterBase = AddGroup(CounterNames, PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int ParTickMs = Add("parTickMs", PerfColumnKind.Fixed2, PerfAggregate.Sum);

        public static readonly int MainCpuMs = Add("mainCpuMs", PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int MainMcyc = Add("mainMcyc", PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int ProcCpuMs = Add("procCpuMs", PerfColumnKind.Fixed2, PerfAggregate.Mean);

        public static readonly int PhaseBase = AddGroup(PhaseNames, PerfColumnKind.Fixed2, PerfAggregate.Mean);
        public static readonly int ExtraBase = AddGroup(ExtraPerFrameNames, PerfColumnKind.Fixed1, PerfAggregate.Mean);
        public static readonly int HeavyBase = AddGroup(ExtraHeavyNames, PerfColumnKind.Fixed1, PerfAggregate.Last);

        public static readonly int FrameHistBase = AddIndexed("fh", FrameHistCount, PerfColumnKind.Int, PerfAggregate.Sum);
        public static readonly int TickHistBase = AddIndexed("th", TickHistCount, PerfColumnKind.Int, PerfAggregate.Sum);

        public static readonly int Count = names.Count;
        public static readonly string[] Names = names.ToArray();
        public static readonly PerfColumnKind[] Kinds = kinds.ToArray();
        public static readonly PerfAggregate[] Aggregates = aggregates.ToArray();
        public static readonly PerfTable Main = new PerfTable(Names, Kinds);

        public static string HeaderLine() => Main.HeaderLine();

        public static bool TryFormatRow(ReadOnlySpan<double> row, Span<char> destination, out int written) =>
            Main.TryFormatRow(row, destination, out written);

        /// <summary>Which frame time bucket a frame of this many milliseconds falls in.</summary>
        public static int FrameBucket(double milliseconds)
        {
            for (int i = 0; i < FrameEdgesMs.Length; i++)
                if (milliseconds < FrameEdgesMs[i]) return i;
            return FrameEdgesMs.Length;
        }

        /// <summary>Which bucket a count of ticks run in one frame falls in: 0, 1, 2, 3-4, 5-9, 10 or more.</summary>
        public static int TickBucket(int ticks) => ticks <= 2 ? Math.Max(0, ticks) : ticks <= 4 ? 3 : ticks <= 9 ? 4 : 5;
    }
}
