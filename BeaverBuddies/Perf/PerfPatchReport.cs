using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BeaverBuddies.Perf
{
    /*
     * Who has patched what, written into the header of the performance log.
     *
     * The methods are not listed by name here. Asking Harmony for every patched method in the process and
     * keeping the ones this mod also patches gives the same list without a hand-written set of type names
     * that would quietly go stale, and it catches another mod stacked on one of them, which is the thing
     * worth knowing. A separate count per mod covers the methods this mod does not touch at all.
     *
     * Slow (it walks every patch in the process), so it only ever runs on the writer thread.
     */
    internal static class PerfPatchReport
    {
        /// <summary>Harmony's id for this mod's own patches.</summary>
        const string OurId = Plugin.ID;

        /*
         * The methods from the investigation that run every tick or every frame. A patch from another mod on
         * one of these is on a hot path; anything else is a one-off. Matched on "DeclaringType.Method", so a
         * name that no longer exists costs nothing but a missing flag.
         */
        static readonly HashSet<string> HotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "TickableBucketService.TickBuckets",
            "TickableBucketService.FinishFullTick",
            "TickableEntityBucket.TickAll",
            "TickableEntityBucket.Add",
            "TickableEntity.Tick",
            "TickableSingletonService.FinishParallelTick",
            "TickableSingletonService.StartParallelTick",
            "TickableSingletonService.TickSingletons",
            "Ticker.Update",
            "MovementAnimator.Update",
            "RandomNumberGenerator.Range",
            "RandomNumberGenerator.InsideUnitCircle",
            "DayNightCycle.FluidSecondsPassedToday",
            "DayNightCycle.get_FluidSecondsPassedToday",
            "InputService.UpdateSingleton",
            "SoundEmitter.Update",
            "Walker.FindPath",
            "Walker.StopMoving",
            "BehaviorManager.TickRunningExecutor",
            "ThreadSafeWaterMap.Update",
            "SoilMoistureService.UpdateMoistureLevels",
            "WaterSource.Tick",
            "WaterSourceRegistry.UpdateThreadSafeRegistry",
            "WaterDepthStrengthModifier.GetStrengthModifier",
            "ZiplineWaterPenaltyModifier.WaterPenaltyModifier",
            "ZiplineWaterPenaltyModifier.get_WaterPenaltyModifier",
            "DistrictConnections.GetDistrictsConnectedWith",
            "DistrictConnections.AreDistrictsConnected",
            "SpeedManager.ChangeSpeed",
            "SpeedManager.ChangeSpeedScale",
            "ParameterProvider.GetParameters",
            "DateTime.ToString",
            "Guid.NewGuid",
        };

        /// <summary>Header lines, each already starting with '#'. Never throws.</summary>
        public static IEnumerable<string> Describe()
        {
            var lines = new List<string>();
            MethodBase[] patched;
            try
            {
                patched = Harmony.GetAllPatchedMethods().Where(method => method != null).ToArray();
            }
            catch (Exception error)
            {
                lines.Add("# patches-unavailable=" + Clean(error.Message));
                return lines;
            }

            var perOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var hotPerOwner = new Dictionary<string, int>(StringComparer.Ordinal);
            var detail = new List<string>();

            foreach (MethodBase method in patched)
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(method); }
                catch (Exception) { continue; }
                if (info == null) continue;

                string name = Name(method);
                bool hot = HotMethods.Contains(name);
                bool ours = false;
                var entries = new List<string>();

                foreach (var (kind, patches) in Kinds(info))
                {
                    foreach (Patch patch in patches)
                    {
                        string owner = string.IsNullOrEmpty(patch.owner) ? "?" : patch.owner;
                        perOwner[owner] = perOwner.TryGetValue(owner, out int n) ? n + 1 : 1;
                        if (hot) hotPerOwner[owner] = hotPerOwner.TryGetValue(owner, out int h) ? h + 1 : 1;
                        if (owner == OurId) ours = true;
                        entries.Add(string.Join("|", owner, kind,
                            patch.priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            patch.index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Clean(patch.PatchMethod?.DeclaringType?.Name)));
                    }
                }

                // Everything this mod patches, plus anything on a hot method even if this mod is not on it.
                if ((ours || hot) && entries.Count > 0)
                {
                    detail.Add("# patch=" + name + (hot ? " hot=1" : " hot=0") + " " + string.Join(" ", entries));
                }
            }

            lines.Add("# patched-methods=" + patched.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var pair in perOwner.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                hotPerOwner.TryGetValue(pair.Key, out int hot);
                lines.Add("# owner=" + pair.Key + " patches=" + pair.Value + " onHotMethods=" + hot);
            }
            detail.Sort(StringComparer.Ordinal);
            lines.AddRange(detail);
            return lines;
        }

        static IEnumerable<(string Kind, IList<Patch> Patches)> Kinds(Patches info)
        {
            yield return ("prefix", info.Prefixes);
            yield return ("postfix", info.Postfixes);
            yield return ("transpiler", info.Transpilers);
            yield return ("finalizer", info.Finalizers);
        }

        static string Name(MethodBase method)
        {
            string type = method.DeclaringType?.Name ?? "?";
            return Clean(type + "." + method.Name);
        }

        // The header is read by a script that splits on spaces and '|', so neither may appear in a value.
        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Replace(' ', '_').Replace('|', '/').Replace('\r', '_').Replace('\n', '_').Replace(',', '_');
        }
    }
}
