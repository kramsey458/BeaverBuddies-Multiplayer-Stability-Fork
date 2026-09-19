using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BeaverBuddies.DesyncDetecter
{
    /*
     * What every walking character looked like at the start of each recent tick, kept while debug mode is
     * on and written next to the water diagnostics when a desync is reported.
     *
     * Why: a desync is only noticed when the random state differs, which is several ticks after two
     * computers first disagree about where a beaver is (the "Move hash" in the verbose log). By then the
     * trace no longer says which beaver it was or why. With this file from both computers,
     * RuntimeChecks/compare_walker_traces.py names the first tick and character that differ, and which
     * of its inputs differed: where it was, its path, or its speed.
     *
     * The host is usually ten to twenty ticks ahead of a guest when a desync is reported, so enough
     * ticks are kept for the two files to overlap well before the first difference.
     *
     * No Unity or game types here, so the test project can exercise it.
     */
    public struct WalkerRecord
    {
        public string EntityId;
        public string Name;
        // Exact bits, so two computers can be compared without rounding.
        public float X, Y, Z;
        public int NextCornerIndex, CornerCount;
        public float LastCornerX, LastCornerY, LastCornerZ;
        public float CornerSpeed;
        public float BaseSpeed, BonusMultiplier;
        public bool OnZiplineEdge, AnimatedOnZipline;
    }

    public sealed class WalkerTrace
    {
        public const int DefaultTicksKept = 192;

        private readonly int ticksKept;
        private readonly LinkedList<KeyValuePair<int, List<WalkerRecord>>> ticks =
            new LinkedList<KeyValuePair<int, List<WalkerRecord>>>();

        public WalkerTrace(int ticksKept = DefaultTicksKept)
        {
            this.ticksKept = Math.Max(1, ticksKept);
        }

        public int TickCount => ticks.Count;

        public void Clear() => ticks.Clear();

        // Entities tick in buckets spread over a tick, so records for one tick arrive in several calls.
        public void Add(int tick, WalkerRecord record)
        {
            if (ticks.Count == 0 || ticks.Last.Value.Key != tick)
            {
                ticks.AddLast(new KeyValuePair<int, List<WalkerRecord>>(tick, new List<WalkerRecord>()));
                while (ticks.Count > ticksKept) ticks.RemoveFirst();
            }
            ticks.Last.Value.Value.Add(record);
        }

        public void Write(TextWriter writer)
        {
            writer.WriteLine("tick\tentity\tname\tx\ty\tz\tnextCorner\tcorners\tlastX\tlastY\tlastZ\tcornerSpeed\tbaseSpeed\tbonus\tonZiplineEdge\tanimatedOnZipline");
            var line = new StringBuilder(160);
            foreach (var tick in ticks)
            {
                foreach (WalkerRecord r in tick.Value)
                {
                    line.Clear();
                    line.Append(tick.Key).Append('\t').Append(r.EntityId).Append('\t').Append(Clean(r.Name));
                    foreach (float value in new[] { r.X, r.Y, r.Z }) line.Append('\t').Append(Bits(value));
                    line.Append('\t').Append(r.NextCornerIndex).Append('\t').Append(r.CornerCount);
                    foreach (float value in new[] { r.LastCornerX, r.LastCornerY, r.LastCornerZ, r.CornerSpeed, r.BaseSpeed, r.BonusMultiplier })
                        line.Append('\t').Append(Bits(value));
                    line.Append('\t').Append(r.OnZiplineEdge ? '1' : '0').Append('\t').Append(r.AnimatedOnZipline ? '1' : '0');
                    writer.WriteLine(line.ToString());
                }
            }
        }

        // "3F800000=1": the exact bits for comparing, the value for reading.
        public static string Bits(float value)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            return bits.ToString("X8", CultureInfo.InvariantCulture) + "=" + value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
