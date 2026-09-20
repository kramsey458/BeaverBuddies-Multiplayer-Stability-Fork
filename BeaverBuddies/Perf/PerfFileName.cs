using System;

namespace BeaverBuddies.Perf
{
    /// <summary>Makes a player's name safe to put in the frame rate log's file name, on any system.</summary>
    public static class PerfFileName
    {
        public const int MaxLength = 24;

        /// <summary>Keeps ASCII letters and digits, - and _, replaces anything else with _, and cuts it short. Never empty.</summary>
        public static string Safe(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Player";
            var clean = new char[Math.Min(name.Length, MaxLength)];
            for (int i = 0; i < clean.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                clean[i] = ok ? c : '_';
            }
            return new string(clean);
        }
    }
}
