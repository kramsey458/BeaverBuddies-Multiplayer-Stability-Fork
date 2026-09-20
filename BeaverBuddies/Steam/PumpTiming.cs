using System.Globalization;

namespace BeaverBuddies.Steam
{
    /// <summary>
    /// Measures how long data waits for the game thread to hand it to Steam, as the gaps between pumps.
    /// Steam only moves data when the game thread asks it to, so this gap is added, in full, to every message in
    /// both directions and therefore to the ping the connection panel shows.
    /// </summary>
    public sealed class PumpTiming
    {
        /// <summary>A gap this long is one a player can feel in the ping.</summary>
        public const double SlowGapSeconds = 0.05;

        double lastAt = -1;
        int gaps, slowGaps;
        double totalSeconds, longestSeconds;

        public int Gaps => gaps;
        public int SlowGaps => slowGaps;
        public double MeanMs => gaps == 0 ? 0 : totalSeconds / gaps * 1000;
        public double LongestMs => longestSeconds * 1000;

        /// <summary>A pump happened at this time, in seconds.</summary>
        public void Record(double now)
        {
            if (lastAt >= 0 && now >= lastAt)
            {
                double gap = now - lastAt;
                gaps++;
                totalSeconds += gap;
                if (gap > longestSeconds) longestSeconds = gap;
                if (gap > SlowGapSeconds) slowGaps++;
            }
            lastAt = now;
        }

        /// <summary>Forgets everything, including the time of the last pump (so the next gap is not counted).</summary>
        public void Reset()
        {
            lastAt = -1;
            gaps = slowGaps = 0;
            totalSeconds = longestSeconds = 0;
        }

        /// <summary>Forgets the counts but keeps the time of the last pump, so the next gap still counts.</summary>
        public void StartNewWindow()
        {
            gaps = slowGaps = 0;
            totalSeconds = longestSeconds = 0;
        }

        /// <summary>
        /// One line for the log that puts the two cadences side by side: waiting for the end of each frame (how
        /// Steam was pumped before 1.0.9) and waiting for the next pump between ticks (how it is pumped now).
        /// </summary>
        public static string Describe(double windowSeconds, PumpTiming perFrame, PumpTiming overall)
        {
            var c = CultureInfo.InvariantCulture;
            return "Steam link timing over " + windowSeconds.ToString("0", c) + " s: data waited for the game thread " +
                perFrame.MeanMs.ToString("0.0", c) + " ms on average and up to " + perFrame.LongestMs.ToString("0", c) +
                " ms if Steam were only served once per frame (" + perFrame.SlowGaps + " gaps over 50 ms); with the pumping " +
                "between ticks it waited " + overall.MeanMs.ToString("0.0", c) + " ms on average and up to " +
                overall.LongestMs.ToString("0", c) + " ms (" + overall.SlowGaps + " gaps over 50 ms).";
        }
    }
}
