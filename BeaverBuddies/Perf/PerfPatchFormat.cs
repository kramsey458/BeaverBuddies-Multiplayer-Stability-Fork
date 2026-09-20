using System;
using System.Collections.Generic;
using System.Text;

// Pure text logic for the patch report in the frame rate log: no Unity, Timberborn or Harmony types, so it can be
// tested on its own.
namespace BeaverBuddies.Perf
{
    public static class PerfPatchFormat
    {
        // Methods that run every tick or every frame (or on every call of something that does) and that this mod
        // patches. Another mod's patch on one of them adds cost to a hot path, or changes what this mod's patch sees.
        // Written as "TypeName.MethodName" (getters as get_Name), matched against every overload.
        static readonly HashSet<string> hotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "TickableBucketService.TickBuckets",
            "TickableBucketService.FinishFullTick",
            "TickableEntityBucket.TickAll",
            "TickableEntityBucket.Add",
            "TickableEntity.Tick",
            "TickableSingletonService.FinishParallelTick",
            "TickableSingletonService.StartParallelTick",
            "Ticker.Update",
            "WaterSource.Tick",
            "WaterSourceRegistry.UpdateThreadSafeRegistry",
            "WaterDepthStrengthModifier.GetStrengthModifier",
            "ThreadSafeWaterMap.Update",
            "SoilMoistureService.UpdateMoistureLevels",
            "RandomNumberGenerator.Range",
            "RandomNumberGenerator.InsideUnitCircle",
            "Guid.NewGuid",
            "EntityService.Instantiate",
            "MovementAnimator.Update",
            "InputService.UpdateSingleton",
            "SoundEmitter.Update",
            "DayNightCycle.get_FluidSecondsPassedToday",
            "TickOnlyArrayService.get_AllowEdit",
            "ZiplineWaterPenaltyModifier.get_WaterPenaltyModifier",
            "DistrictObstacleService.SetObstacle",
            "DistrictObstacleService.UnsetObstacle",
            "DistrictConnections.GetDistrictsConnectedWith",
            "DistrictConnections.AreDistrictsConnected",
            "DistrictMap.AddDistrictCenter",
            "BehaviorManager.TickRunningExecutor",
            "Walker.FindPath",
            "Walker.StopMoving",
            "Enterer.Enter",
            "SlotManager.AddEnterer",
            "RecoveredGoodStackSpawner.UpdateSingleton",
            "SpeedManager.ChangeSpeedScale",
            "DateTime.ToString",
        };

        public static int HotMethodCount => hotMethods.Count;

        public static bool IsHot(string typeName, string methodName) =>
            typeName != null && methodName != null && hotMethods.Contains(typeName + "." + methodName);

        /// <summary>Makes text safe for one field of a log line: no line breaks and no field separator.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
                clean.Append(c == '|' ? '/' : c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
            return clean.ToString().Trim();
        }

        /// <summary>
        /// One patch as one line of the header: <c># patch|tag|method|kind|owner|priority|index|before|after|assembly|patch</c>.
        /// The tag says why it is listed: hot (on the list above), shared (more than one owner) or other (not this mod's).
        /// </summary>
        public static string PatchLine(string tag, string method, string kind, string owner, int priority, int index,
            IEnumerable<string> before, IEnumerable<string> after, string assembly, string patchMethod)
        {
            return "# patch|" + Clean(tag) + "|" + Clean(method) + "|" + Clean(kind) + "|" + Clean(owner) + "|" +
                "priority=" + priority + "|index=" + index + "|" +
                "before=" + Clean(Join(before)) + "|after=" + Clean(Join(after)) + "|" +
                Clean(assembly) + "|" + Clean(patchMethod);
        }

        static string Join(IEnumerable<string> items) => items == null ? "" : string.Join(";", items);
    }
}
