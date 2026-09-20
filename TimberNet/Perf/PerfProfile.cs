using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TimberNet.Perf
{
    /// <summary>The start of one timed call, or nothing if this call was not sampled.</summary>
    public struct PerfSample
    {
        public bool On;
        public long Start;
        public long Alloc;
    }

    /// <summary>
    /// Says which entities and which singletons the tick's time and allocation go to, for the second file of the frame rate log.
    /// Entities are grouped by prefab (a beaver, a farm house), singletons by type. There can be thousands of entities a tick, so
    /// only every Nth call is timed and the result scaled up; N is chosen each window so that the measuring stays inside a small
    /// budget however many entities there are. Everything is recorded on the game thread, and only observed.
    /// </summary>
    public static class PerfProfile
    {
        public const char EntityKind = 'E', SingletonKind = 'G';

        public static readonly PerfTable Table = new PerfTable(
            new[] { "type", "window", "tick", "id", "calls", "sampled", "ms", "allocKB", "maxMs" },
            new[]
            {
                PerfColumnKind.Text, PerfColumnKind.Int, PerfColumnKind.Int, PerfColumnKind.Int, PerfColumnKind.Int,
                PerfColumnKind.Int, PerfColumnKind.Fixed2, PerfColumnKind.Fixed1, PerfColumnKind.Fixed2,
            });

        /// <summary>Every Nth entity tick is timed. Chosen again each window.</summary>
        public static int EntityInterval { get; private set; } = 16;

        /// <summary>Singletons are timed on every Nth tick. Chosen again each window.</summary>
        public static int SingletonEvery { get; private set; } = 1;

        /// <summary>How long the measuring may take per tick, in seconds, before the interval is widened.</summary>
        public const double BudgetSecondsPerTick = 0.00025;

        sealed class Entry
        {
            public char Kind;
            public string Name = "";
            public string Assembly = "";
        }

        static readonly object gate = new object();
        static readonly List<Entry> entries = new List<Entry>();
        static readonly Dictionary<string, int> entityIds = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly Dictionary<Type, int> singletonIds = new Dictionary<Type, int>();
        static long[] sampled = new long[64], ticks = new long[64], alloc = new long[64], maxTicks = new long[64];
        static int entityCounter, tickSerial, namesWritten, singletonCount;
        static readonly double[] row = new double[Table.Count];

        public static int NameCount { get { lock (gate) return entries.Count; } }

        /// <summary>Forgets everything. Called when a log starts.</summary>
        public static void Reset()
        {
            lock (gate)
            {
                entries.Clear();
                entityIds.Clear();
                singletonIds.Clear();
                namesWritten = 0;
            }
            Array.Clear(sampled, 0, sampled.Length); Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(alloc, 0, alloc.Length); Array.Clear(maxTicks, 0, maxTicks.Length);
            entityCounter = 0; tickSerial = 0; singletonCount = 0;
            EntityInterval = 16; SingletonEvery = 1;
        }

        /// <summary>Sets the sampling by hand. For tests only: the log chooses it itself from what measuring costs.</summary>
        public static void Configure(int entityInterval, int singletonEvery)
        {
            EntityInterval = Math.Max(1, entityInterval);
            SingletonEvery = Math.Max(1, singletonEvery);
        }

        /// <summary>A tick has started.</summary>
        public static void OnTick() => tickSerial++;

        // ---- entities ----

        /// <summary>Called before an entity ticks. Cheap unless this call is one of the sampled ones.</summary>
        public static PerfSample BeginEntity()
        {
            if (!PerfProbe.Enabled) return default;
            PerfProbe.Count(PerfCounter.EntityTicks);
            if (++entityCounter < EntityInterval) return default;
            entityCounter = 0;
            if (!PerfProbe.OnGameThread) return default;
            PerfProbe.NoteSampledPair();
            return new PerfSample { On = true, Start = PerfProbe.Timestamp(), Alloc = PerfAlloc.Read() };
        }

        public static void EndEntity(string? prefab, in PerfSample sample)
        {
            if (!sample.On) return;
            try
            {
                long elapsed = PerfProbe.Timestamp() - sample.Start;
                long allocated = PerfAlloc.Enabled ? Math.Max(0, PerfAlloc.Read() - sample.Alloc) : 0;
                string key = prefab ?? "?";
                if (!entityIds.TryGetValue(key, out int id))
                {
                    id = Register(EntityKind, key, "");
                    entityIds[key] = id;
                }
                Record(id, elapsed, allocated);
            }
            catch (Exception) { /* a failed measurement is not worth the game's time */ }
        }

        // ---- singletons ----

        /// <summary>Called before a singleton's tick. Sampled on some ticks only.</summary>
        public static PerfSample BeginSingleton()
        {
            if (!PerfProbe.Enabled) return default;
            if (tickSerial % SingletonEvery != 0 || !PerfProbe.OnGameThread) return default;
            PerfProbe.NoteSampledPair();
            return new PerfSample { On = true, Start = PerfProbe.Timestamp(), Alloc = PerfAlloc.Read() };
        }

        public static void EndSingleton(Type? type, in PerfSample sample)
        {
            if (!sample.On) return;
            try
            {
                long elapsed = PerfProbe.Timestamp() - sample.Start;
                long allocated = PerfAlloc.Enabled ? Math.Max(0, PerfAlloc.Read() - sample.Alloc) : 0;
                Type key = type ?? typeof(object);
                if (!singletonIds.TryGetValue(key, out int id))
                {
                    id = Register(SingletonKind, key.FullName ?? key.Name, key.Assembly.GetName().Name ?? "");
                    singletonIds[key] = id;
                    singletonCount++;
                }
                Record(id, elapsed, allocated);
            }
            catch (Exception) { }
        }

        static int Register(char kind, string name, string assembly)
        {
            int id;
            lock (gate)
            {
                id = entries.Count;
                entries.Add(new Entry { Kind = kind, Name = name, Assembly = assembly });
            }
            if (id >= sampled.Length)
            {
                int size = sampled.Length * 2;
                Array.Resize(ref sampled, size); Array.Resize(ref ticks, size);
                Array.Resize(ref alloc, size); Array.Resize(ref maxTicks, size);
            }
            return id;
        }

        static void Record(int id, long elapsed, long allocated)
        {
            sampled[id]++;
            ticks[id] += elapsed;
            alloc[id] += allocated;
            if (elapsed > maxTicks[id]) maxTicks[id] = elapsed;
        }

        // ---- output ----

        /// <summary>
        /// Writes one row per entity kind and singleton seen since the last call into <paramref name="target"/>, scaled up for the
        /// sampling, then forgets them and chooses the sampling for the next window.
        /// </summary>
        public static void FlushWindow(int window, int tick, double entityTicksPerTick, PerfRing? target)
        {
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            int count;
            lock (gate) count = entries.Count;
            for (int id = 0; id < count; id++)
            {
                if (sampled[id] == 0) continue;
                Entry entry = entries[id];
                double scale = entry.Kind == EntityKind ? EntityInterval : SingletonEvery;
                Array.Clear(row, 0, row.Length);
                row[0] = entry.Kind;
                row[1] = window;
                row[2] = tick;
                row[3] = id;
                row[4] = sampled[id] * scale;
                row[5] = sampled[id];
                row[6] = ticks[id] * msPerTick * scale;
                row[7] = alloc[id] / 1024.0 * scale;
                row[8] = maxTicks[id] * msPerTick;
                target?.TryPush(row);
                sampled[id] = 0; ticks[id] = 0; alloc[id] = 0; maxTicks[id] = 0;
            }
            Adapt(entityTicksPerTick);
        }

        static void Adapt(double entityTicksPerTick)
        {
            double pairSeconds = PerfProbe.SamplePairTicks / (double)Stopwatch.Frequency + 100e-9; // the dictionary lookup too
            if (PerfProbe.SamplePairTicks <= 0 || entityTicksPerTick <= 0) return;
            EntityInterval = Clamp((int)Math.Ceiling(entityTicksPerTick * pairSeconds / BudgetSecondsPerTick), 1, 1024);
            SingletonEvery = Clamp((int)Math.Ceiling(Math.Max(singletonCount, 1) * pairSeconds / BudgetSecondsPerTick), 1, 16);
        }

        static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;

        /// <summary>Writes a comment line for every kind and singleton not yet named in the file. Called on the writer thread.</summary>
        public static void WriteNewNames(TextWriter writer)
        {
            List<string>? lines = null;
            lock (gate)
            {
                for (; namesWritten < entries.Count; namesWritten++)
                {
                    Entry e = entries[namesWritten];
                    (lines ??= new List<string>()).Add("# name|" + e.Kind + "|" + namesWritten + "|" + Clean(e.Name) + "|" + Clean(e.Assembly));
                }
            }
            if (lines != null) foreach (string line in lines) writer.WriteLine(line);
        }

        static string Clean(string text)
        {
            if (text.Length == 0) return text;
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] == '|' || chars[i] == '\r' || chars[i] == '\n' || chars[i] == '\t') chars[i] = '_';
            return new string(chars);
        }
    }
}
