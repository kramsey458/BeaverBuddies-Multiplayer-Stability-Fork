using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.Buildings;
using Timberborn.BuildingTools;
using Timberborn.DeconstructionSystem;
using Timberborn.RecoveredGoodSystem;

namespace BeaverBuddies.Fixes
{
    /// <summary>
    /// Two dev mode keys, both on Ctrl, are read by the game where it changes the world, not where the player clicks:
    /// "place finished" inside BuildingPlacer.Place, and "don't recover goods" whenever a building is deconstructed.
    /// In co-op both run on every computer (a replayed placement or deletion), so they read the keyboard of whoever's
    /// computer it is: with dev mode on, a player only holding Ctrl while another player's placement or deletion played
    /// got a finished building, or no recovered goods, on their computer alone. In a co-op game neither key is read,
    /// not even for the player who clicked: the building's own spec decides, and goods are always recovered. Single
    /// player is unchanged.
    /// </summary>
    [HarmonyPatch(typeof(BuildingPlacer), nameof(BuildingPlacer.ShouldBePlacedFinished))]
    static class PlaceFinishedKeyCoopPatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(BuildingSpec buildingSpec, ref bool __result)
        {
            if (EventIO.IsNull) return true;
            __result = buildingSpec.PlaceFinished;
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
     * 09/22/2026 (Timberborn 1.1.2.4)
        if (!_inputService.IsKeyHeld(DontRecoverGoodsKey))
        {
            PrepareToSpawning(buildingDeconstructedEvent.Deconstructible, buildingDeconstructedEvent.Coordinates);
        }
     */
    [HarmonyPatch(typeof(BuildingGoodsRecoveryService), nameof(BuildingGoodsRecoveryService.OnBuildingDeconstructed))]
    static class DontRecoverGoodsKeyCoopPatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(BuildingGoodsRecoveryService __instance, BuildingDeconstructedEvent buildingDeconstructedEvent)
        {
            if (EventIO.IsNull) return true;
            __instance.PrepareToSpawning(buildingDeconstructedEvent.Deconstructible, buildingDeconstructedEvent.Coordinates);
            return false;
        }
    }
}
