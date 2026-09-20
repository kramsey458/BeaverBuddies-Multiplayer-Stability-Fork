using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace BeaverBuddies.Perf
{
    /*
     * Drains the ring and writes the CSV, on a thread of its own.
     *
     * Nothing here happens on the game thread. Opening the file, gathering the header (which reads the mod
     * list and asks Harmony who has patched what, both slow) and every write happen on this thread, in
     * batches, so measuring cannot itself become the stall being measured.
     *
     * Any failure switches writing off for the session and is reported once. A missing log is a nuisance; a
     * logger that throws into the game's update loop is a crash.
     */
    public sealed class PerfWriter
    {
        const int BatchSize = 256;

        readonly PerfRing ring;
        readonly Func<TextWriter> openFile;
        readonly Func<IEnumerable<string>> headerLines;
        readonly Action<string> log;
        readonly int flushIntervalMs;

        readonly PerfSample[] batch = new PerfSample[BatchSize];
        readonly StringBuilder line = new StringBuilder(256);
        readonly ManualResetEventSlim stopping = new ManualResetEventSlim(false);

        Thread thread;
        TextWriter file;
        bool failed;
        long written;

        /// <summary>Rows written so far. For the tests and for the closing log line.</summary>
        public long Written => Interlocked.Read(ref written);

        public bool Failed => failed;

        public PerfWriter(PerfRing ring, Func<TextWriter> openFile, Func<IEnumerable<string>> headerLines,
            Action<string> log, int flushIntervalMs = 500)
        {
            this.ring = ring ?? throw new ArgumentNullException(nameof(ring));
            this.openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
            this.headerLines = headerLines;
            this.log = log ?? (_ => { });
            this.flushIntervalMs = Math.Max(50, flushIntervalMs);
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run)
            {
                Name = "BeaverBuddies perf log",
                // Never keep the process alive: if the game is closing, an unwritten batch does not matter.
                IsBackground = true,
            };
            thread.Start();
        }

        /// <summary>Asks the writer to finish what is waiting and close the file. Safe to call more than once.</summary>
        public void Stop(int waitMs = 2000)
        {
            if (thread == null) return;
            stopping.Set();
            try { thread.Join(waitMs); }
            catch (Exception) { }
            thread = null;
        }

        void Run()
        {
            try
            {
                file = openFile();
                if (headerLines != null)
                {
                    foreach (string header in headerLines()) file.WriteLine(header);
                }
                file.WriteLine(PerfCsv.Header);
                file.Flush();
            }
            catch (Exception error)
            {
                failed = true;
                log("The performance log could not be started: " + error.Message);
                Close();
                return;
            }

            while (!stopping.IsSet)
            {
                stopping.Wait(flushIntervalMs);
                if (!WriteWaiting()) return;
            }
            // Whatever arrived while it was stopping.
            WriteWaiting();
            long dropped = ring.Dropped;
            if (dropped > 0)
            {
                try { file.WriteLine("# dropped=" + dropped.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                catch (Exception) { }
            }
            Close();
        }

        /// <summary>Writes everything waiting. False if writing failed and the thread should stop.</summary>
        bool WriteWaiting()
        {
            try
            {
                int taken;
                bool wrote = false;
                while ((taken = ring.Drain(batch)) > 0)
                {
                    for (int i = 0; i < taken; i++)
                    {
                        line.Clear();
                        PerfCsv.Format(line, in batch[i]);
                        file.WriteLine(line.ToString());
                    }
                    Interlocked.Add(ref written, taken);
                    wrote = true;
                    if (taken < batch.Length) break;
                }
                if (wrote) file.Flush();
                return true;
            }
            catch (Exception error)
            {
                failed = true;
                log("The performance log stopped being written: " + error.Message);
                Close();
                return false;
            }
        }

        void Close()
        {
            try { file?.Flush(); } catch (Exception) { }
            try { file?.Dispose(); } catch (Exception) { }
            file = null;
        }
    }
}
