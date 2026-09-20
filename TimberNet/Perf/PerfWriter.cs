using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace TimberNet.Perf
{
    /// <summary>
    /// Writes the log's rows to a file on a thread of its own, twice a second, so no disk work ever happens on the
    /// frame being measured. Rows are formatted into buffers allocated once. If the file cannot be opened or written
    /// the writer gives up quietly and the game carries on.
    /// </summary>
    public sealed class PerfWriter
    {
        public const int DrainMilliseconds = 500;
        const int BatchRows = 256;
        const int LineChars = 1024;

        readonly string path;
        readonly IReadOnlyList<string> headerLines;
        readonly PerfRing ring;
        readonly Action<string>? report;
        readonly ManualResetEventSlim wake = new ManualResetEventSlim(false);
        Thread? thread;
        StreamWriter? output;
        volatile bool stopping;
        long droppedReported;

        /// <summary>Set when writing failed and the writer stopped. The reason, for the game's log.</summary>
        public string? Failure { get; private set; }

        public string Path => path;

        /// <param name="headerLines">Lines written before the column names, each starting with #. No newlines inside.</param>
        /// <param name="report">Told once if writing fails; called on the writer thread.</param>
        public PerfWriter(string path, IReadOnlyList<string> headerLines, PerfRing ring, Action<string>? report)
        {
            this.path = path; this.headerLines = headerLines; this.ring = ring; this.report = report;
        }

        /// <summary>Opens the file and starts writing. False, with <see cref="Failure"/> set, if it could not be opened.</summary>
        public bool Start()
        {
            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                output = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
                foreach (string line in headerLines) output.WriteLine(line);
                output.WriteLine(PerfColumns.HeaderLine());
                output.Flush();
            }
            catch (Exception e)
            {
                Failure = e.Message;
                try { output?.Dispose(); } catch { }
                output = null;
                return false;
            }
            thread = new Thread(Run) { IsBackground = true, Name = "BeaverBuddies frame rate log" };
            thread.Start();
            return true;
        }

        /// <summary>Writes what is left, closes the file and waits (briefly) for the writer thread to finish.</summary>
        public void Stop()
        {
            stopping = true;
            wake.Set();
            Thread? t = thread;
            if (t != null && !t.Join(3000)) Failure ??= "The writer did not finish in time.";
            thread = null;
        }

        void Run()
        {
            var batch = new double[BatchRows * PerfColumns.Count];
            var line = new char[LineChars];
            try
            {
                while (!stopping)
                {
                    wake.Wait(DrainMilliseconds);
                    DrainAll(batch, line);
                }
                DrainAll(batch, line);
                output!.WriteLine("# end");
                output.Flush();
            }
            catch (Exception e)
            {
                Failure = e.Message;
                try { report?.Invoke("The frame rate log stopped: " + e.Message); } catch { }
            }
            finally
            {
                try { output?.Dispose(); } catch { }
                output = null;
            }
        }

        void DrainAll(double[] batch, char[] line)
        {
            int n;
            do
            {
                n = ring.Drain(batch, BatchRows);
                for (int i = 0; i < n; i++)
                {
                    var row = new ReadOnlySpan<double>(batch, i * PerfColumns.Count, PerfColumns.Count);
                    if (PerfColumns.TryFormatRow(row, line, out int written)) output!.Write(line, 0, written);
                }
            } while (n == BatchRows);
            long dropped = ring.Dropped;
            if (dropped != droppedReported)
            {
                droppedReported = dropped;
                output!.WriteLine("# rows dropped so far because the writer fell behind: " + dropped);
            }
            output!.Flush();
        }
    }
}
