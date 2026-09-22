using HarmonyLib;
using Timberborn.GameDistrictsUI;

namespace BeaverBuddies.Fixes
{
    /*
        9/22/2026 (Timberborn 1.1.2.4), DistrictPreviewsValidator.IsValid(BlockObject, out string errorMessage):
        refuses with BuildingTools.DistrictsInConflict only when blockObject.IsPreview and
        IDistrictService.IsPreviewDistrictInConflict(its DistrictCenter's CenterCoordinates, or null) is true;
        otherwise it returns true with errorMessage null. Nothing else is checked, so skipping it loses no other rule.
     */
    /// <summary>
    /// A replayed building placement is checked on every computer (BuildingPlacedEvent.IsPlacementValid), on a
    /// preview copy of the building. For that copy this validator asks whether this computer's preview road graph
    /// joins two districts, a graph the copy itself is never added to. It holds whatever the local player is
    /// hovering or dragging with a tool, and construction and instant road changes only reach it once per frame
    /// (NavigationSynchronizer.LateUpdateSingleton). So a player whose path preview would join two districts (shown
    /// red, "Districts in conflict") refused every other player's building while the other computers placed it,
    /// and a paused guest that plays two of the host's batches in one frame could judge differently from the host.
    /// While events replay this check passes. No protection is lost: the placing player's own tool already refused
    /// a building that would join two districts before the click, and the blocks, terrain and every other
    /// validator still judge the replay. Outside a replay the game's check runs unchanged.
    /// This does not make the replay check fully independent of the local player: for buildings with a terrain
    /// physics check the game still asks this computer's preview blocks (TerrainPhysicsBlockObjectValidator), a
    /// separate follow-up.
    /// Priority.Last, the rule for a prefix that replaces the original: another mod's prefix on this check runs
    /// first. Ported from BeaverBuddies-MultiColony (8af51cd), without its colony rules.
    /// </summary>
    [ManualMethodOverwrite]
    [HarmonyPatch(typeof(DistrictPreviewsValidator), nameof(DistrictPreviewsValidator.IsValid))]
    static class DistrictPreviewsValidatorReplayPatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(ref bool __result, ref string errorMessage)
        {
            if (!ReplayService.IsReplayingEvents) return true;
            errorMessage = null;
            __result = true;
            return false;
        }
    }
}
