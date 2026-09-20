using System;

namespace TimberNet.Perf
{
    /// <summary>
    /// Rows waiting to be written, in storage allocated once. The game thread pushes a row (a copy of a
    /// few hundred bytes under a lock held for that long and no longer) and the writer thread drains them.
    /// When it is full the new row is dropped and counted, so a stalled disk can never grow memory or
    /// make the game wait.
    /// </summary>
    public sealed class PerfRing
    {
        readonly double[] data;
        readonly int columns, capacity;
        readonly object gate = new object();
        int head, count;
        long dropped;

        public PerfRing(int columns, int capacity)
        {
            if (columns <= 0 || capacity <= 0) throw new ArgumentOutOfRangeException();
            this.columns = columns; this.capacity = capacity;
            data = new double[checked(columns * capacity)];
        }

        public int Capacity => capacity;

        public long Dropped { get { lock (gate) return dropped; } }

        public int Count { get { lock (gate) return count; } }

        /// <summary>Copies a row in. False, with the drop counted, if there is no room.</summary>
        public bool TryPush(double[] row)
        {
            lock (gate)
            {
                if (count == capacity) { dropped++; return false; }
                Array.Copy(row, 0, data, ((head + count) % capacity) * columns, columns);
                count++;
                return true;
            }
        }

        /// <summary>Moves up to <paramref name="maxRows"/> rows, oldest first, into <paramref name="destination"/>.</summary>
        public int Drain(double[] destination, int maxRows)
        {
            lock (gate)
            {
                int n = Math.Min(Math.Min(count, maxRows), destination.Length / columns);
                for (int i = 0; i < n; i++)
                    Array.Copy(data, ((head + i) % capacity) * columns, destination, i * columns, columns);
                head = (head + n) % capacity;
                count -= n;
                return n;
            }
        }
    }
}
