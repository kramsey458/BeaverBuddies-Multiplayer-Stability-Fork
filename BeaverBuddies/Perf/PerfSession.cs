using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using BeaverBuddies.Connect;
using BeaverBuddies.IO;
using TimberNet.Perf;
using Timberborn.Versioning;
using UnityEngine;
using UnityEngine.Scripting;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// One frame rate log, from the moment a co-op game starts to the moment it ends. It exists only when the
    /// "Log Frame Rate Details" setting was on when the session started, and it only observes: the timing lives
    /// in <see cref="PerfProbe"/> and the files are written by <see cref="PerfWriter"/> on threads of their own. Two files are
    /// written: the frame log, and a profile of which entities and singletons the tick's time and allocation went to.
    /// See PERFORMANCE-LOG.md.
    /// </summary>
    internal static class PerfSession
    {
        // The frame log's rows are about a kilobyte each; these are allocated once. At most a few rows a second are written, and they are emptied twice a second.
        const int RingRows = 4096, ProfileRingRows = 4096;

        static PerfRing ring, profileRing;
        static PerfWriter writer, profileWriter;
        static bool quittingHooked;
        static readonly ConcurrentQueue<string> notes = new ConcurrentQueue<string>();

        internal static void Start(bool isHost)
        {
            Stop();
            if (!Settings.PerfLogEnabled) return;
            try
            {
                PerfMilestones.Mark("session-start");
                string directory = Path.Combine(Application.persistentDataPath, "BeaverBuddiesDiagnostics");
                Directory.CreateDirectory(directory);
                string role = isHost ? "host" : "guest";
                string player = PerfFileName.Safe(Settings.PingDisplayName);
                // The time, not a GUID: Guid.NewGuid is patched to draw from the game's random numbers.
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string baseName = $"perf-{role}-{player}-{stamp}";
                string path = Path.Combine(directory, baseName + ".csv");
                string profilePath = Path.Combine(directory, baseName + "-profile.csv");

                // Find out what this computer can measure, and what measuring costs, before anything is written.
                PerfAlloc.Init();
                PerfCpu.Init();
                PerfProbe.Calibrate();
                PerfExtras.Start();
                PerfExperiment.Reset();
                while (notes.TryDequeue(out _)) { }

                var newRing = new PerfRing(PerfColumns.Count, RingRows);
                var newProfileRing = new PerfRing(PerfProfile.Table.Count, ProfileRingRows);
                var newWriter = new PerfWriter(path, BuildHeader(isHost, Path.GetFileName(profilePath)), newRing, Plugin.LogWarning)
                {
                    BeforeRows = WriteNotes,
                };
                if (!newWriter.Start())
                {
                    Plugin.LogWarning("The frame rate log could not be started: " + newWriter.Failure);
                    PerfExtras.Stop();
                    return;
                }
                ring = newRing;
                writer = newWriter;

                // The profile is a bonus: if its file cannot be opened the frame log still runs.
                var newProfileWriter = new PerfWriter(profilePath, BuildProfileHeader(isHost), newProfileRing, Plugin.LogWarning, PerfProfile.Table)
                {
                    BeforeRows = PerfProfile.WriteNewNames,
                };
                if (newProfileWriter.Start()) { profileRing = newProfileRing; profileWriter = newProfileWriter; }
                else Plugin.LogWarning("The frame rate log's profile file could not be started: " + newProfileWriter.Failure);

                PerfProbe.Start(ring, Thread.CurrentThread.ManagedThreadId,
                    Settings.PerfLogThresholdMs, Settings.PerfLogSummaryEveryTicks, profileRing);
                PerfPatches.Install();
                PerfPlayerLoop.Install();
                if (!quittingHooked)
                {
                    quittingHooked = true;
                    Application.quitting += Stop;
                }
                Plugin.Log("Frame rate log: writing to " + path);
            }
            catch (Exception error)
            {
                Plugin.LogWarning("The frame rate log could not be started: " + error.Message);
                // Stop() does nothing if no file was opened, so undo the parts that were already started.
                PerfPlayerLoop.Uninstall();
                PerfPatches.Uninstall();
                PerfExtras.Stop();
                Stop();
            }
        }

        /// <summary>Called once a frame, before the frame is closed: what Unity supplies, and the optional experiment.</summary>
        internal static void OnFrame(int tick)
        {
            PerfExtras.Sample();
            PerfExperiment.OnFrame(tick);
        }

        /// <summary>Writes a line into the log's file, at the next write, saying what happened at this tick.</summary>
        internal static void Note(int tick, string text)
        {
            if (writer == null) return;
            notes.Enqueue("# event|tick " + tick + "|" + PerfPatchFormat.Clean(text));
            Plugin.Log("Frame rate log: " + text);
        }

        static void WriteNotes(TextWriter target)
        {
            while (notes.TryDequeue(out string line)) target.WriteLine(line);
        }

        /// <summary>Finishes the files, if a log is being written. Safe to call at any time, any number of times.</summary>
        internal static void Stop()
        {
            if (writer == null && !PerfProbe.Enabled) return;
            try
            {
                // What the sources actually produced has to be read here, on the game thread, before they are shut down.
                List<string> final = PerfExtras.FinalLines();
                final.Add("# capability-final|playerLoop|" + (PerfPlayerLoop.Installed > 0 ? "timed " + PerfPlayerLoop.Installed + " phases" : "not installed"));
                final.Add("# capability-final|singletonPatch|" + (PerfPatches.Installed > 0 ? "installed" : "not installed"));
                if (writer != null) writer.Trailer = w => { foreach (string line in final) w.WriteLine(line); };
                PerfPlayerLoop.Uninstall();
                PerfPatches.Uninstall();
                PerfProbe.Stop();
                if (PerfProbe.LastFailure != null)
                    Plugin.LogWarning("The frame rate log switched itself off: " + PerfProbe.LastFailure);
                PerfExtras.Stop();
                writer?.Stop();
                profileWriter?.Stop();
                if (writer?.Failure != null)
                    Plugin.LogWarning("The frame rate log had a problem: " + writer.Failure);
                else if (writer != null)
                    Plugin.Log("Frame rate log finished: " + writer.Path);
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not finish the frame rate log cleanly: " + error.Message);
            }
            finally
            {
                writer = null; profileWriter = null;
                ring = null; profileRing = null;
            }
        }

        static double TicksToNs(double ticks) => ticks * 1e9 / Stopwatch.Frequency;

        static List<string> BuildHeader(bool isHost, string profileFileName)
        {
            var header = new List<string>();
            void Add(string key, string value) => header.Add("# " + key + ": " + PerfPatchFormat.Clean(value));
            void Line(params string[] parts) => header.Add("# " + string.Join("|", parts.Select(PerfPatchFormat.Clean)));

            header.Add("# BeaverBuddies frame rate log, format 2");
            Add("role", isHost ? "host" : "guest");
            Add("player", Settings.PingDisplayName);
            Add("started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " local, " +
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
            Add("mod", Plugin.Version);
            Add("build", BuildCompatibility.CreateIdentity());
            Add("game", GameVersions.CurrentVersion.ToString());
            Add("unity", Application.unityVersion);
            Add("os", SystemInfo.operatingSystem);
            Add("cpu", SystemInfo.processorType + " x" + SystemInfo.processorCount + " @ " + SystemInfo.processorFrequency + " MHz");
            Add("memoryMB", SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture));
            Add("gpu", SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + " MB)");
            Add("graphics", SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
            Add("display", "vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate +
                " resolution=" + Screen.width + "x" + Screen.height + " refreshHz=" + Describe(() => Screen.currentResolution.refreshRateRatio.value.ToString("F2", CultureInfo.InvariantCulture)) +
                " fullScreen=" + Screen.fullScreenMode);
            Add("quality", Describe(() => QualitySettings.names[QualitySettings.GetQualityLevel()]) + " antiAliasing=" + QualitySettings.antiAliasing);
            Add("gc", PerfExperiment.Describe() + " maxGeneration=" + GC.MaxGeneration);
            Add("detailedLogging", Settings.Debug ? "on" : "off");
            Add("verboseLogging", Settings.VerboseLogging ? "on" : "off");
            Add("gcExperiment", Settings.PerfLogGcExperimentEnabled ? "on (tick " + PerfExperiment.TriggerTick + ")" : "off");
            Add("thresholdMs", Settings.PerfLogThresholdMs.ToString(CultureInfo.InvariantCulture));
            Add("summaryTicks", Settings.PerfLogSummaryEveryTicks.ToString(CultureInfo.InvariantCulture));
            Add("clockHz", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            Add("profileFile", profileFileName);
            header.Add("# note: times are exclusive, so the slots never overlap. gameMs is the tick loop minus every other slot (the game's own ticking and " +
                       "other mods' patches on it); otherMs is the rest of the frame (drawing, other UI, other mods, the system). See PERFORMANCE-LOG.md.");
            header.Add("# note: in summary rows times and the extra counters are averages per frame; allocation (KB), counts and the two histograms are totals over the summary's frames.");

            Line("capability", "allocSource", PerfAlloc.ModeName);
            Line("capability", "cpuTimes", PerfCpu.Available ? "available" : "unavailable");
            header.AddRange(PerfExtras.StartLines());
            Line("calibration", "scopePairNs", TicksToNs(PerfProbe.ScopePairTicks).ToString("F0", CultureInfo.InvariantCulture),
                "samplePairNs", TicksToNs(PerfProbe.SamplePairTicks).ToString("F0", CultureInfo.InvariantCulture),
                "allocReadNs", TicksToNs(PerfAlloc.MeasureReadTicks()).ToString("F0", CultureInfo.InvariantCulture));
            Line("sampling", "entityInterval", PerfProfile.EntityInterval.ToString(CultureInfo.InvariantCulture),
                "singletonEvery", PerfProfile.SingletonEvery.ToString(CultureInfo.InvariantCulture),
                "budgetUsPerTick", (PerfProfile.BudgetSecondsPerTick * 1e6).ToString("F0", CultureInfo.InvariantCulture),
                "note", "the intervals are chosen again every summary window; the profile file says the scale it used per row");
            Line("histogram", "frameEdgesMs", string.Join(",", PerfColumns.FrameEdgesMs.Select(e => e.ToString(CultureInfo.InvariantCulture))));
            Line("histogram", "ticksPerFrameBuckets", "0,1,2,3-4,5-9,10+");
            Line("phases", string.Join(",", PerfColumns.PhaseNames), "each is the time from the start to the end of that phase of Unity's frame; the vertical sync wait is in one of them");

            foreach (string line in PerfMilestones.Lines()) header.Add(line);

            foreach (string line in ReadBootConfig()) Line("bootconfig", line);
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length && i < 40; i++) Line("cmdline", i == 0 ? Path.GetFileName(args[i]) : args[i]);

            List<ModEntry> mods = ModCompatibility.Local();
            Add("mods", mods.Count.ToString(CultureInfo.InvariantCulture));
            foreach (ModEntry mod in mods.OrderBy(m => m.Id, StringComparer.Ordinal))
                header.Add("# mod|" + PerfPatchFormat.Clean(mod.Id) + "|" + PerfPatchFormat.Clean(mod.Name) + "|" + PerfPatchFormat.Clean(mod.Version));

            PerfPatchReport.Append(header);
            return header;
        }

        static List<string> BuildProfileHeader(bool isHost)
        {
            var header = new List<string>
            {
                "# BeaverBuddies frame rate log profile, format 1",
                "# role: " + (isHost ? "host" : "guest"),
                "# player: " + PerfPatchFormat.Clean(Settings.PingDisplayName),
                "# note: one row per entity kind (E: a prefab such as a beaver or a farm house) and singleton (G) per summary window. calls, ms and allocKB are estimates scaled up from the sampled calls.",
                "# note: the entity rows add up to most of gameMs and the singleton rows to the rest of the tick; names are given in '# name|kind|id|name|assembly' lines before the rows that use them.",
            };
            return header;
        }

        static IEnumerable<string> ReadBootConfig()
        {
            var lines = new List<string>();
            try
            {
                string file = Path.Combine(Application.dataPath, "boot.config");
                if (!File.Exists(file)) { lines.Add("(no boot.config)"); return lines; }
                foreach (string line in File.ReadAllLines(file))
                {
                    if (lines.Count >= 60) break;
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
                }
            }
            catch (Exception error) { lines.Add("(could not read boot.config: " + error.GetType().Name + ")"); }
            return lines;
        }

        static string Describe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return "unknown"; }
        }
    }
}
