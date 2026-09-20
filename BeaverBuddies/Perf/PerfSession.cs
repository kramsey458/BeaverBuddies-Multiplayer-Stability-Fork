using System;
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

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// One frame rate log, from the moment a co-op game starts to the moment it ends. It exists only when the
    /// "Log Frame Rate Details" setting was on when the session started, and it only observes: the timing lives
    /// in <see cref="PerfProbe"/> and the file is written by <see cref="PerfWriter"/> on a thread of its own.
    /// See PERFORMANCE-LOG.md.
    /// </summary>
    internal static class PerfSession
    {
        // 8192 rows is about 2.7 MB, allocated once. At most a few rows a second are written, and they are emptied twice a second.
        const int RingRows = 8192;

        static PerfRing ring;
        static PerfWriter writer;
        static bool quittingHooked;

        internal static void Start(bool isHost)
        {
            Stop();
            if (!Settings.PerfLogEnabled) return;
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "BeaverBuddiesDiagnostics");
                Directory.CreateDirectory(directory);
                string role = isHost ? "host" : "guest";
                string player = PerfFileName.Safe(Settings.PingDisplayName);
                // The time, not a GUID: Guid.NewGuid is patched to draw from the game's random numbers.
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(directory, $"perf-{role}-{player}-{stamp}.csv");

                var newRing = new PerfRing(PerfColumns.Count, RingRows);
                var newWriter = new PerfWriter(path, BuildHeader(isHost), newRing, Plugin.LogWarning);
                if (!newWriter.Start())
                {
                    Plugin.LogWarning("The frame rate log could not be started: " + newWriter.Failure);
                    return;
                }
                ring = newRing;
                writer = newWriter;
                PerfProbe.Start(ring, Thread.CurrentThread.ManagedThreadId,
                    Settings.PerfLogThresholdMs, Settings.PerfLogSummaryEveryTicks);
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
                Stop();
            }
        }

        /// <summary>Finishes the file, if a log is being written. Safe to call at any time, any number of times.</summary>
        internal static void Stop()
        {
            if (writer == null && !PerfProbe.Enabled) return;
            try
            {
                PerfProbe.Stop();
                if (PerfProbe.LastFailure != null)
                    Plugin.LogWarning("The frame rate log switched itself off: " + PerfProbe.LastFailure);
                writer?.Stop();
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
                writer = null;
                ring = null;
            }
        }

        static List<string> BuildHeader(bool isHost)
        {
            var header = new List<string>();
            void Add(string key, string value) => header.Add("# " + key + ": " + PerfPatchFormat.Clean(value));

            header.Add("# BeaverBuddies frame rate log, format 1");
            Add("role", isHost ? "host" : "guest");
            Add("player", Settings.PingDisplayName);
            Add("started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " local, " +
                DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
            Add("mod", Plugin.Version);
            Add("build", BuildCompatibility.CreateIdentity());
            Add("game", GameVersions.CurrentVersion.ToString());
            Add("unity", Application.unityVersion);
            Add("os", SystemInfo.operatingSystem);
            Add("cpu", SystemInfo.processorType + " x" + SystemInfo.processorCount);
            Add("memoryMB", SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture));
            Add("gpu", SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + " MB)");
            Add("display", "vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate +
                " resolution=" + Screen.width + "x" + Screen.height);
            Add("gc", DescribeGc());
            Add("detailedLogging", Settings.Debug ? "on" : "off");
            Add("verboseLogging", Settings.VerboseLogging ? "on" : "off");
            Add("thresholdMs", Settings.PerfLogThresholdMs.ToString(CultureInfo.InvariantCulture));
            Add("summaryTicks", Settings.PerfLogSummaryEveryTicks.ToString(CultureInfo.InvariantCulture));
            Add("clockHz", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            header.Add("# note: times are exclusive, so the slots never overlap. gameMs is the tick loop minus every other slot (the game's own ticking and " +
                       "other mods' patches on it); otherMs is the rest of the frame (drawing, other UI, other mods, the system). See PERFORMANCE-LOG.md.");

            List<ModEntry> mods = ModCompatibility.Local();
            Add("mods", mods.Count.ToString(CultureInfo.InvariantCulture));
            foreach (ModEntry mod in mods.OrderBy(m => m.Id, StringComparer.Ordinal))
                header.Add("# mod|" + PerfPatchFormat.Clean(mod.Id) + "|" + PerfPatchFormat.Clean(mod.Name) + "|" + PerfPatchFormat.Clean(mod.Version));

            PerfPatchReport.Append(header);
            return header;
        }

        static string DescribeGc()
        {
            try
            {
                return "mode=" + UnityEngine.Scripting.GarbageCollector.GCMode + " incremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental +
                    " maxGeneration=" + GC.MaxGeneration;
            }
            catch (Exception error)
            {
                return "unknown (" + error.GetType().Name + ")";
            }
        }
    }
}
