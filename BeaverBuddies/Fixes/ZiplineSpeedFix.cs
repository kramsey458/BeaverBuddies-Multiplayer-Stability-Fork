using BeaverBuddies.IO;
using HarmonyLib;
using Timberborn.ZiplineMovementSystem;

namespace BeaverBuddies.Fixes
{
    /*
     * A beaver's walking speed in a flooded tile is multiplied by every IWaterPenaltyModifier on it
     * (WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition). One of them is the zipline's:
     *
     *   public float WaterPenaltyModifier => !_ziplineVisitor.IsOnZipline ? 1f : 0.5f;
     *
     * ZiplineVisitor.IsOnZipline is not simulation state. It is switched by MovementAnimator's
     * GroupIdUpdated event, which is raised from the per-frame animation when the animated model
     * crosses onto or off a zipline corner. Which frame that happens in, and so whether it is before
     * or after that beaver's own tick, depends on the frame rate and on how many ticks a frame
     * carried. Two computers can therefore disagree for a tick about the speed of a beaver that is
     * getting on or off a zipline in water, its position drifts, and it arrives a tick apart:
     * first the move hash differs, a few ticks later the random state, which is the desync.
     * It is rare at low speeds (many frames per tick, so the switch lands at almost the same point
     * on both computers) and common at a true speed 7, above all on a guest that is catching up
     * with several ticks per frame.
     *
     * ZiplinePathTracker holds the same fact as simulation state: it follows the path corners the
     * walker actually moved along, from the tick (PathFollower.MovedAlongPath), and it is saved.
     * In a multiplayer session the modifier asks that instead. Only the speed rule changes source;
     * the animation, harness and swimming visuals still follow the animated model.
     */
    [HarmonyPatch(typeof(ZiplineWaterPenaltyModifier), nameof(ZiplineWaterPenaltyModifier.WaterPenaltyModifier), MethodType.Getter)]
    public class ZiplineWaterPenaltyModifierPatcher
    {
        static bool Prefix(ZiplineWaterPenaltyModifier __instance, ref float __result)
        {
            if (EventIO.IsNull) return true;
            ZiplinePathTracker tracker = __instance.GetComponent<ZiplinePathTracker>();
            if (ReferenceEquals(tracker, null)) return true;
            __result = ZiplineSpeedRule.Modifier(tracker._fromPoint.HasValue && tracker._toPoint.HasValue);
            return false;
        }
    }

    public static class ZiplineSpeedRule
    {
        // The game's own values.
        public static float Modifier(bool onZiplineEdge) => onZiplineEdge ? 0.5f : 1f;
    }
}
