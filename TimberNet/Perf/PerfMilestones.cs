using System;
using System.Collections.Generic;
using System.Globalization;

namespace TimberNet.Perf
{
    /// <summary>
    /// How big the managed heap and the process were at a few named moments (the mod starting, the game loading, the session
    /// beginning), so the log can say how much of the heap is there before anyone plays. Marks are always kept (there are few and each
    /// costs microseconds), and only written to a file if a log is started.
    /// </summary>
    public static class PerfMilestones
    {
        const int Max = 32;
        static readonly object gate = new object();
        static readonly List<string> lines = new List<string>();

        public static void Mark(string name)
        {
            try
            {
                string line = "# milestone|" + Clean(name) + "|" + DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "|" +
                    (GC.GetTotalMemory(false) / 1048576.0).ToString("F0", CultureInfo.InvariantCulture) + "|" +
                    (Environment.WorkingSet / 1048576.0).ToString("F0", CultureInfo.InvariantCulture);
                lock (gate)
                {
                    if (lines.Count < Max) lines.Add(line);
                }
            }
            catch (Exception) { }
        }

        /// <summary>The marks so far, oldest first, as header lines: <c># milestone|name|time (UTC)|managed heap MB|process working set MB</c>.</summary>
        public static List<string> Lines()
        {
            lock (gate) return new List<string>(lines);
        }

        public static void Clear()
        {
            lock (gate) lines.Clear();
        }

        static string Clean(string text) => text.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    }
}
