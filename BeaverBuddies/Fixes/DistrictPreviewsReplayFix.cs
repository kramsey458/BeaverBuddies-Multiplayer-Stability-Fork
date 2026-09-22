using HarmonyLib;
using Timberborn.GameDistrictsUI;

namespace BeaverBuddies.Fixes
{
    /*
9/22/2026 (Timberborn 1.1.2.4), DistrictPreviewsValidator:
public bool IsValid(BlockObject blockObject, out string errorMessage)
{
    if (IsPreviewDistrictInConflict(blockObject))
    {
        errorMessage = _loc.T(ErrorMessageLocKey);
        return false;
    }
    errorMessage = null;
    return true;
}

private bool IsPreviewDistrictInConflict(BlockObject blockObject)
{
    if (blockObject.IsPreview)
    {
        Vector3Int? previewDistrictCenter = blockObject.GetComponent<DistrictCenter>()?.CenterCoordinates;
        return _districtService.IsPreviewDistrictInConflict(previewDistrictCenter);
    }
    return false;
}
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
    /// Priority.Last, the rule for a prefix that replaces the original: another mod's prefix on this check runs
    /// first. Ported from BeaverBuddies-MultiColony (8af51cd), without its colony rules.
    /// </summary>
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
