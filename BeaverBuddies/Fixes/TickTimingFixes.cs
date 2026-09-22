using BeaverBuddies.Events;
using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Timberborn.AutomationBuildings;
using Timberborn.BaseComponentSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.WalkingSystemUI;

namespace BeaverBuddies.Fixes
{
    /// <summary>
    /// A deleted entity is alive until the end of the frame. The game deletes with Unity's Object.Destroy, which waits for
    /// the end of the frame, and a component's own check that it still exists (BaseComponent's bool, "if (_reservable)")
    /// asks Unity whether its GameObject is alive. The mod spreads a tick over each computer's own frames, so in a later
    /// bucket of the same tick one computer, still in the frame the entity was deleted in, finds it alive (a lumberjack
    /// walks on to a tree an explosion took), and another, a frame further on, finds it gone. A paused action that
    /// deletes something and the unpause after it can likewise arrive in one frame on a guest and two on the host.
    /// So in a co-op game a deletion inside a tick or a replayed action ends this frame's ticking on every computer: the
    /// rest of the tick runs from the next frame, once Unity has destroyed what was deleted. TickingService gives the
    /// frame's unticked buckets back to the game's ticker, so the game runs no slower for it.
    /// </summary>
    [HarmonyPatch(typeof(EntityService), nameof(EntityService.Delete))]
    static class EntityDeletionEndsFramePatcher
    {
        static void Postfix()
        {
            if (EventIO.IsNull) return;
            if (!DeterminismService.IsTicking && !ReplayService.IsReplayingEvents) return;
            TickingService ticking = SingletonManager.GetSingleton<TickingService>();
            if (ticking != null) ticking.ShouldInterruptTicking = true;
        }
    }

    /// <summary>
    /// A spring-return lever switches itself off in the tick (SpringReturnService). The game does it through SwitchState,
    /// which a player's click on a lever is recorded from, so in co-op every computer took the tick's own switch for a
    /// click: the host played it a tick late, and each guest sent the host another. It also asked _isPressed, which is set
    /// on the computer of the player holding the lever's button and nowhere else. In a co-op game the lever now switches
    /// off in the tick, at once, on every computer alike, whoever holds the button (holding one on never worked in co-op:
    /// the other computers switched it off anyway). Single player is unchanged.
    /// </summary>
    [ManualMethodOverwrite]
    /*
     * 2026-09-22 (Timberborn 1.1.2.4, Lever.SpringReturnToOff)
        if (IsSpringReturn && !_isPressed)
        {
            SwitchOff();
        }
        _registeredForSpringReturn = false;
     */
    [HarmonyPatch(typeof(Lever), nameof(Lever.SpringReturnToOff))]
    static class LeverSpringReturnCoopPatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(Lever __instance)
        {
            if (EventIO.IsNull) return true;
            if (__instance.IsSpringReturn) ReplayEvent.RunAsSimulation(() => __instance.SwitchState(false));
            __instance._registeredForSpringReturn = false;
            return false;
        }
    }

    /// <summary>
    /// A paused building that stops being one (it is deleted in the tick, by an explosion say) resumes itself, through
    /// PausableBuilding.Resume, which a player's click is recorded from. Every computer took it for a click and sent it
    /// on, for a building already gone. In a co-op game it runs as the simulation's own call, as in single player.
    /// </summary>
    [HarmonyPatch]
    static class PausableBuildingStateExitPatcher
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PausableBuilding), nameof(PausableBuilding.OnExitFinishedState));
            yield return AccessTools.Method(typeof(PausableBuilding), nameof(PausableBuilding.OnExitUnfinishedState));
        }

        static void Prefix(out bool __state)
        {
            __state = !EventIO.IsNull;
            if (__state) ReplayEvent.EnterSimulationCall();
        }

        static void Finalizer(bool __state)
        {
            if (__state) ReplayEvent.ExitSimulationCall();
        }
    }

    /// <summary>
    /// The game's walker debugger (debug mode, a walker selected) scatters its path markers with UnityEngine.Random in
    /// LateUpdate, on that computer alone. That is the game's shared random state, which every computer keeps in step: in
    /// a co-op game it is put back as it was.
    /// </summary>
    [HarmonyPatch(typeof(WalkerDebugger), nameof(WalkerDebugger.ResetPathMarkers))]
    static class WalkerDebuggerRandomPatcher
    {
        static void Prefix(out UnityEngine.Random.State? __state)
        {
            __state = EventIO.IsNull ? (UnityEngine.Random.State?)null : UnityEngine.Random.state;
        }

        static void Finalizer(UnityEngine.Random.State? __state)
        {
            if (__state.HasValue) UnityEngine.Random.state = __state.Value;
        }
    }
}
