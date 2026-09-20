using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using Timberborn.TimeSystem;
using UnityEngine;

namespace BeaverBuddies.Perf
{
    /*
     * Turns the performance log on and off for a session, and is the only part of it that knows about Unity,
     * the settings or the game.
     *
     * Off unless the player turns it on. Everything it does is read the clock and a few numbers the game
     * already keeps, so a session with it on simulates exactly what the same session would without it.
     */
    internal static class PerfSession
    {
        public const string FolderName = "BeaverBuddiesDiagnostics";

        // Two thousand rows is about half a minute of the worst case (a row every frame) before the writer,
        // which runs twice a second, would have to drop any.
        const int RingCapacity = 2048;

        static PerfRing ring;
        static PerfWriter writer;
        static SpeedManager speedManager;
        static ReplayService replayService;
        static bool isHost;
        static string path;

        public static bool IsRunning => writer != null;

        /// <summary>The file being written, for the log line that tells the player where it is.</summary>
        public static string Path => path;

        public static void Start(ReplayService replay, SpeedManager speed, bool host, string mapName)
        {
            Stop();
            if (!Settings.PerformanceLoggingEnabled) return;
            try
            {
                replayService = replay;
                speedManager = speed;
                isHost = host;
                string directory = System.IO.Path.Combine(Application.persistentDataPath, FolderName);
                Directory.CreateDirectory(directory);
                path = System.IO.Path.Combine(directory,
                    "perf-" + FileSafe(Settings.PingDisplayName) + "-" + (host ? "host" : "guest") + "-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");

                string savedMapName = mapName;
                ring = new PerfRing(RingCapacity);
                writer = new PerfWriter(ring,
                    () => new StreamWriter(path, false, new UTF8Encoding(false)),
                    () => Header(savedMapName, host),
                    Plugin.LogWarning);
                writer.Start();
                PerfProbe.StartSession(ring, Settings.PerformanceSpikeMs, Settings.PerformanceSummaryTicks);
                // Counts only what this thread wrote; the activity and chat lanes write from their own threads.
                TimberNet.TimberNetBase.BytesWritten = PerfProbe.CountSend;
                Plugin.Log($"Performance logging is on (over {Settings.PerformanceSpikeMs} ms, " +
                           $"summary every {Settings.PerformanceSummaryTicks} ticks): {path}");
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Performance logging could not be started: " + error.Message);
                Stop();
            }
        }

        public static void Stop()
        {
            TimberNet.TimberNetBase.BytesWritten = null;
            PerfProbe.StopSession();
            PerfWriter finishing = writer;
            writer = null;
            if (finishing != null)
            {
                finishing.Stop();
                Plugin.Log($"Performance log closed after {finishing.Written} rows: {path}");
            }
            ring = null;
            speedManager = null;
            replayService = null;
            path = null;
        }

        /// <summary>Called at the start of the game's per-frame tick entry.</summary>
        public static void BeginFrame()
        {
            PerfProbe.BeginFrame();
        }

        /// <summary>Called as that entry returns, whether or not it threw.</summary>
        public static void EndFrame()
        {
            if (!PerfProbe.Enabled) return;
            PerfFrameState state = default;
            try
            {
                EventIO io = EventIO.Get();
                ReplayService replay = replayService;
                state.Tick = replay?.TicksSinceLoad ?? 0;
                state.TicksBehind = io?.TicksBehind ?? 0;
                state.Speed = speedManager?.CurrentSpeed ?? 0;
                state.TargetSpeed = replay?.TargetSpeed ?? 0;
                state.HostPacingPercent = replay?.HostPacingPercent ?? 100;
                state.FpsPacingPercent = replay?.FrameRatePacingPercent ?? 100;
                state.Holding = replay?.HostPacingHolding == true;
                state.Saving = GameSaverSavePatcher.IsSaving;
            }
            catch (Exception)
            {
                // A row with missing context is worth more than a frame that threw out of the probe.
            }
            PerfProbe.EndFrame(in state);
        }

        // Gathered on the writer thread: the mod list and the Harmony report are both slow to read.
        static IEnumerable<string> Header(string mapName, bool host)
        {
            var lines = new List<string>
            {
                "# format=beaverbuddies-perf-1",
                "# version=" + Value(Plugin.Version) +
                    " role=" + (host ? "host" : "guest") +
                    " player=" + Value(Settings.PingDisplayName) +
                    " map=" + Value(mapName) +
                    " started=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                "# detailedLogging=" + Flag(Settings.Debug) +
                    " verboseLogging=" + Flag(Settings.VerboseLogging) +
                    " spikeMs=" + Settings.PerformanceSpikeMs.ToString(CultureInfo.InvariantCulture) +
                    " summaryTicks=" + Settings.PerformanceSummaryTicks.ToString(CultureInfo.InvariantCulture) +
                    " stopwatchHiRes=" + Flag(System.Diagnostics.Stopwatch.IsHighResolution),
            };
            try
            {
                List<ModEntry> mods = ModCompatibility.Local();
                lines.Add("# mods=" + mods.Count.ToString(CultureInfo.InvariantCulture));
                foreach (ModEntry mod in mods)
                {
                    lines.Add("# mod=" + Value(mod.Id) + "|" + Value(mod.Name) + "|" + Value(mod.Version));
                }
            }
            catch (Exception error)
            {
                lines.Add("# mods-unavailable=" + Value(error.Message));
            }
            try
            {
                lines.AddRange(PerfPatchReport.Describe());
            }
            catch (Exception error)
            {
                lines.Add("# patches-unavailable=" + Value(error.Message));
            }
            return lines;
        }

        static string Flag(bool value) => value ? "1" : "0";

        // The header is read by a script that splits on spaces and '|', so neither may appear in a value.
        static string Value(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Replace(' ', '_').Replace('|', '/').Replace('\r', '_').Replace('\n', '_');
        }

        static string FileSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "player";
            var clean = new StringBuilder(text.Length);
            foreach (char character in text)
            {
                clean.Append(char.IsLetterOrDigit(character) ? character : '-');
            }
            string result = clean.ToString().Trim('-');
            return result.Length == 0 ? "player" : result;
        }
    }
}
