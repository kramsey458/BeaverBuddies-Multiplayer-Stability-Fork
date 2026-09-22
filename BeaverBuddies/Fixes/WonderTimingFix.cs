using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.SaveSystem;
using Timberborn.TickSystem;
using Timberborn.TimbermeshAnimations;
using Timberborn.WonderPlanes;
using Timberborn.Wonders;
using UnityEngine;
using WonderPlane = Timberborn.WonderPlanes.Plane;

namespace BeaverBuddies.Fixes
{
    /*
     * The game runs part of every Wonder, and all of the Iron Teeth Earth Repopulator's plane
     * launch, on render-frame time: a Wonder's activation animation (AnimatorRegistry, Time.deltaTime)
     * and the check for its end (WonderAnimationController.Update), the catapult's wait and runway
     * (PlaneCatapult.Update), the plane's movement along the runway (Plane.Update) and the turn of
     * the launcher between planes (PlaneLauncherRotator.Update). Their ends spawn the next plane,
     * deactivate the Wonder (its effect stops and the pilots are destroyed half an hour later) and
     * decide whether a Wonder can be activated again. A player at a different frame rate, or a guest
     * waiting for the host while its frames go on, reaches each of them on a different tick, and
     * the planes are created outside a tick, so with entity IDs from the wrong random numbers.
     *
     * In multiplayer the tick runs them instead: once per tick, in a fixed order, with the tick
     * interval as their clock, calling the game's own methods. The per-frame updates only draw,
     * between the last two ticks' poses, and never change what the simulation reads.
     * See BeaverBuddies/Doc/WonderTiming.md.
     *
     * Ported from the Stability Fork's PR #46 (not merged there), with one change: a game update that changes how
     * often these methods read the frame clock no longer throws out of the mod's patching (which would have left the
     * mod half patched, its desync fixes included). The transpiler leaves that method as the game has it, logs it, and
     * switches this whole takeover off (WonderTiming.Unavailable): the Wonders then run on frame time as before 1.1.14.
     */
    public class WonderTickService : RegisteredSingleton, ITickableSingleton, IResettableSingleton
    {
        private static readonly Comparison<Wonder> ByEntityId = (a, b) =>
            a.GetComponent<EntityComponent>().EntityId.CompareTo(b.GetComponent<EntityComponent>().EntityId);

        private readonly EntityComponentRegistry _entityComponentRegistry;
        private readonly ITickService _tickService;
        private readonly List<Wonder> _wonders = new List<Wonder>();
        private readonly List<WonderTiming.Parts> _parts = new List<WonderTiming.Parts>();

        public WonderTickService(EntityComponentRegistry entityComponentRegistry, ITickService tickService,
            ITickableBucketService tickableBucketService)
        {
            _entityComponentRegistry = entityComponentRegistry;
            _tickService = tickService;
            WonderTiming.Buckets = tickableBucketService as TickableBucketService;
        }

        public void Tick()
        {
            if (WonderTiming.Unavailable) return;
            _wonders.Clear();
            _parts.Clear();
            foreach (Wonder wonder in _entityComponentRegistry.GetAll<Wonder>())
            {
                _wonders.Add(wonder);
            }
            // Every player has the same Wonders. In entity ID order, the planes they create take
            // their IDs in the same order however the Wonders were loaded or built.
            _wonders.Sort(ByEntityId);
            foreach (Wonder wonder in _wonders)
            {
                _parts.Add(new WonderTiming.Parts(wonder.GetComponent<WonderAnimationController>(),
                    wonder.GetComponent<PlaneCatapult>(), wonder.GetComponent<PlaneLauncherRotator>()));
            }
            WonderTiming.Tick(_parts, _tickService.TickIntervalInSeconds);
        }

        public void Reset()
        {
            WonderTiming.Reset();
        }
    }

    public static class WonderTiming
    {
        /// <summary>True while the tick runs the Wonders; their clock then reads the tick interval.</summary>
        internal static bool InTick { get; private set; }

        /// <summary>
        /// A timing transpiler found a method body it did not expect (a game update): the Wonders stay on frame time, as
        /// before this fix, rather than half on the tick. Set while the mod patches, for the rest of the program's run.
        /// </summary>
        internal static bool Unavailable { get; private set; }
        private static float tickSeconds;
        internal static TickableBucketService Buckets;
        private static bool loggedFailure;

        // Drawing only: what each moving part showed at the last two ticks. The simulation never
        // reads these, and the tick puts every part back where it left it before it runs.
        private static readonly List<AnimationView> animations = new List<AnimationView>();
        private static readonly List<PoseView> poses = new List<PoseView>();

        internal static void Reset()
        {
            InTick = false;
            tickSeconds = 0f;
            Buckets = null;
            loggedFailure = false;
            animations.Clear();
            poses.Clear();
        }

        /// <summary>The parts of one Wonder that the tick runs; the catapult and launcher only exist on the Earth Repopulator.</summary>
        internal readonly struct Parts
        {
            public readonly WonderAnimationController Controller;
            public readonly PlaneCatapult Catapult;
            public readonly PlaneLauncherRotator Rotator;

            public Parts(WonderAnimationController controller, PlaneCatapult catapult, PlaneLauncherRotator rotator)
            {
                Controller = controller;
                Catapult = catapult;
                Rotator = rotator;
            }
        }

        /// <summary>One tick of every registered Wonder, in the given (entity ID) order.</summary>
        internal static void Tick(List<Parts> wonders, float seconds)
        {
            // A deleted Wonder is no longer registered: its animation is not drawn any more.
            for (int i = animations.Count - 1; i >= 0; i--)
            {
                if (!IsListed(animations[i].Animator, wonders)) animations.RemoveAt(i);
            }
            if (EventIO.IsNull)
            {
                // The session ended mid-game (the connection dropped, or a desync let this player play on):
                // the game runs on its own again, as in single player, and every gate lets its frame
                // updates through. Hand each part back where the last tick left it and stop stepping.
                Release();
                return;
            }
            RestorePoses();
            RunTick(seconds, () =>
            {
                foreach (Parts wonder in wonders)
                {
                    try
                    {
                        StepWonder(wonder.Controller, wonder.Catapult, wonder.Rotator);
                    }
                    catch (Exception e)
                    {
                        // Every player runs the same step and fails the same way; keep the tick going.
                        if (!loggedFailure) Plugin.LogError($"Wonder tick failed: {e}");
                        loggedFailure = true;
                    }
                }
            });
            // Start drawing this tick from where the last one was drawn.
            ShowPoses(0f);
        }

        /// <summary>
        /// Stops taking over: every moving part is put where the last tick left it and each animation
        /// shows the tick's pose, so the game's own frame updates carry on from the simulated state.
        /// </summary>
        internal static void Release()
        {
            if (animations.Count == 0 && poses.Count == 0) return;
            RestorePoses();
            poses.Clear();
            foreach (AnimationView view in animations)
            {
                view.Show(1f);
            }
            animations.Clear();
        }

        private static bool IsListed(TimbermeshAnimator animator, List<Parts> wonders)
        {
            foreach (Parts wonder in wonders)
            {
                if (wonder.Controller != null && ReferenceEquals(wonder.Controller._animator, animator)) return true;
            }
            return false;
        }

        internal static void RunTick(float seconds, Action step)
        {
            bool wasInTick = InTick;
            float wasSeconds = tickSeconds;
            InTick = true;
            tickSeconds = seconds;
            try
            {
                step();
            }
            finally
            {
                InTick = wasInTick;
                tickSeconds = wasSeconds;
            }
        }

        /// <summary>
        /// One tick of one Wonder, in the order animation, catapult and runway, launcher. A step can
        /// start the next part (the animation's end starts the catapult, the runway's end starts a
        /// turn, a turn's end the next plane or the reverse animation); that part then runs from this
        /// tick if it comes later in the order, from the next one otherwise.
        /// </summary>
        internal static void StepWonder(WonderAnimationController controller, PlaneCatapult catapult, PlaneLauncherRotator rotator)
        {
            if (controller != null && controller.Enabled) StepAnimation(controller);
            if (catapult != null && catapult.Enabled) StepCatapult(catapult);
            if (rotator != null && rotator.Enabled) StepRotator(rotator);
        }

        private static void StepAnimation(WonderAnimationController controller)
        {
            if (controller._animator == null) return;
            var animator = controller._animator as TimbermeshAnimator;
            AnimationView view = animator != null ? Find(animator) : null;
            if (!controller.IsAnimating)
            {
                if (view != null)
                {
                    // The animation ended on the last tick; draw its last pose and stop drawing it here.
                    view.Show(1f);
                    animations.Remove(view);
                }
                return;
            }
            float from = animator != null ? animator.Time : 0f;
            // The game's own step (AnimatorRegistry), then its own check for the end (Update).
            animator?.UpdateAnimation(tickSeconds);
            controller.Update();
            if (animator != null)
            {
                view = view ?? Track(animator);
                view.From = from;
                view.To = animator.Time;
            }
        }

        private static void StepCatapult(PlaneCatapult catapult)
        {
            WonderPlane plane = catapult._catapultedPlane;
            // The game's own wait, runway speed and end of the runway.
            catapult.Update();
            if ((bool)plane && ReferenceEquals(catapult._catapultedPlane, plane))
            {
                Vector3 from = plane.Transform.position;
                // The game's own movement; the catapult reads where it got to on the next tick.
                plane.Update();
                Track(plane, plane.Transform, turns: false).MoveTo(from, plane.Transform.position);
            }
            else if (!ReferenceEquals(plane, null))
            {
                // Off the runway: free flight is drawn frame by frame, as in the game.
                Untrack(plane);
            }
        }

        private static void StepRotator(PlaneLauncherRotator rotator)
        {
            Transform element = rotator._rotatedElement;
            Quaternion from = element.localRotation;
            // The game's own turn, or its end: the next plane, or the Wonder's deactivation.
            rotator.Update();
            if (rotator.Enabled)
            {
                Track(rotator, element, turns: true).TurnTo(from, element.localRotation);
            }
            else
            {
                Untrack(rotator);
            }
        }

        /// <summary>A Wonder animation started (activation, deactivation or load): take it off the frame clock now.</summary>
        internal static void AnimationStarted(WonderAnimationController controller)
        {
            if (Unavailable) return;
            if (!(controller._animator is TimbermeshAnimator animator))
            {
                Plugin.LogWarning($"Wonder animator {controller._animator?.GetType().Name} is not a Timbermesh animator; its timing is not fixed");
                return;
            }
            AnimationView view = Find(animator) ?? Track(animator);
            view.From = view.To = animator.Time;
        }

        internal static bool RunsThisFrame => Unavailable || InTick || EventIO.IsNull;

        /// <summary>The frame update of an animator: a Wonder's only draws (see HoldClock), every other one runs as in the game.</summary>
        internal static bool AnimatorRunsThisFrame(TimbermeshAnimator animator)
        {
            if (animations.Count == 0 || RunsThisFrame) return true;
            return Find(animator) == null;
        }

        /// <summary>A Wonder animator's clock as the last tick left it: what UpdateTime changes.</summary>
        internal struct AnimatorClock
        {
            public bool Held;
            public float Time, RepeatedTime;
            public bool PlayingFinished;
        }

        /// <summary>
        /// Before a frame update of a Wonder's animator, and before any other mod's prefix on it: the
        /// tick's clock, to be put back afterwards. Other mods may advance the animator's time themselves
        /// and skip the rest (LateGamePerformance's AnimatorCulling does, off screen and far away); that
        /// frame time must not reach the simulation either.
        /// </summary>
        internal static AnimatorClock HoldClock(TimbermeshAnimator animator)
        {
            if (animations.Count == 0 || RunsThisFrame || Find(animator) == null) return default;
            return new AnimatorClock
            {
                Held = true,
                Time = animator.Time,
                RepeatedTime = animator.RepeatedTime,
                PlayingFinished = animator.PlayingFinished,
            };
        }

        /// <summary>After that frame update: the tick's clock back, whatever ran, and the pose between the last two ticks drawn.</summary>
        internal static void ReleaseClock(TimbermeshAnimator animator, AnimatorClock clock)
        {
            if (!clock.Held) return;
            animator.Time = clock.Time;
            animator.RepeatedTime = clock.RepeatedTime;
            animator.PlayingFinished = clock.PlayingFinished;
            Find(animator)?.Show(Progress());
        }

        internal static void ShowPose(BaseComponent owner)
        {
            PoseView pose = FindPose(owner);
            if (pose != null) pose.Show(Progress());
        }

        /// <summary>Before a save writes anything: every moving part as the last tick left it, so every entity (the pilot on the plane's seat too) saves the tick's pose.</summary>
        internal static void RestorePosesForSave()
        {
            if (EventIO.IsNull) return;
            RestorePoses();
        }

        internal static float GetDeltaTime()
        {
            if (InTick && !EventIO.IsNull) return tickSeconds;
            return FrameClock();
        }

        // The game's frame clock, outside the multiplayer tick. A field so that RuntimeChecks, which has
        // no Unity, can stand in for it; nothing in the game changes it.
        internal static Func<float> FrameClock = GetFrameDeltaTime;

        // Keep the Unity native call outside the multiplayer path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float GetFrameDeltaTime() => Time.deltaTime;

        internal static IEnumerable<CodeInstruction> UseTickClock(IEnumerable<CodeInstruction> instructions, int expected)
        {
            var original = new List<CodeInstruction>(instructions);
            var clock = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            int reads = 0;
            foreach (var instruction in original)
            {
                if (instruction.Calls(clock)) reads++;
            }
            if (reads != expected)
            {
                // Not thrown: the mod's patches all go in with one PatchAll, and a throw here would stop the rest.
                if (!Unavailable)
                    Plugin.LogError($"Wonders stay on frame time in multiplayer: a Wonder method reads the frame clock {reads} " +
                        $"time(s) where {expected} were expected (a game update?). Wonder activations may desync until the mod is updated.");
                Unavailable = true;
                return original;
            }
            var replacement = AccessTools.Method(typeof(WonderTiming), nameof(GetDeltaTime));
            foreach (var instruction in original)
            {
                if (instruction.Calls(clock))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
            }
            return original;
        }

        // How far the current tick is through its buckets: 0 just after the Wonders ticked, 1 when
        // the tick is complete (or none is running).
        private static float Progress()
        {
            TickableBucketService buckets = Buckets;
            if (buckets == null) return 1f;
            int next = buckets._nextBucketIndex;
            return next == 0 ? 1f : (float)next / buckets.TotalNumberOfBuckets;
        }

        private static AnimationView Find(TimbermeshAnimator animator)
        {
            for (int i = 0; i < animations.Count; i++)
            {
                if (ReferenceEquals(animations[i].Animator, animator)) return animations[i];
            }
            return null;
        }

        private static AnimationView Track(TimbermeshAnimator animator)
        {
            var view = new AnimationView { Animator = animator };
            animations.Add(view);
            return view;
        }

        private static PoseView FindPose(BaseComponent owner)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                if (ReferenceEquals(poses[i].Owner, owner)) return poses[i];
            }
            return null;
        }

        private static PoseView Track(BaseComponent owner, Transform transform, bool turns)
        {
            PoseView pose = FindPose(owner);
            if (pose == null)
            {
                pose = new PoseView { Owner = owner, Transform = transform, Turns = turns };
                poses.Add(pose);
            }
            return pose;
        }

        private static void Untrack(BaseComponent owner)
        {
            PoseView pose = FindPose(owner);
            if (pose != null) poses.Remove(pose);
        }

        private static void RestorePoses()
        {
            for (int i = poses.Count - 1; i >= 0; i--)
            {
                // A deleted Wonder or plane has nothing left to put back.
                if (!poses[i].Owner)
                {
                    poses.RemoveAt(i);
                    continue;
                }
                poses[i].Restore();
                // Stopped other than by its own step: leave it where the simulation has it.
                if (!poses[i].Owner.Enabled) poses.RemoveAt(i);
            }
        }

        private static void ShowPoses(float progress)
        {
            foreach (PoseView pose in poses)
            {
                if (pose.Owner) pose.Show(progress);
            }
        }

        private sealed class AnimationView
        {
            public TimbermeshAnimator Animator;
            public float From, To;

            // TimbermeshAnimator.UpdateAnimationUpdaters, at a time between the last two ticks.
            // Only the models move: the animator's time, and whether it has finished, stay the tick's.
            public void Show(float progress)
            {
                TimbermeshAnimator animator = Animator;
                AnimationMetadata animation = animator._currentAnimation;
                IAnimationUpdater[] updaters = animator._animationUpdaters;
                // Started again since the last tick: Play has drawn its first pose already.
                if (animation == null || updaters == null || animator.Time != To) return;
                float length = animation.Length;
                if (length <= 0f) return;
                float time = Mathf.Lerp(From, To, progress);
                float repeated = animator._looped ? Mathf.Repeat(time, length) : Mathf.Min(time, length);
                float normalized = Mathf.Clamp01(repeated / length);
                if (animator._playBackwards) normalized = 1f - normalized;
                for (int i = 0; i < updaters.Length; i++)
                {
                    updaters[i].UpdateAnimation(normalized);
                }
            }
        }

        // A launcher's turning part (local rotation) or a plane on the runway (position): where the
        // last two ticks left it. The tick's pose is what the game reads (the plane's spawn point
        // turns with the launcher; the catapult measures the runway) and what it saves.
        private sealed class PoseView
        {
            public BaseComponent Owner;
            public Transform Transform;
            public bool Turns;
            private Vector3 fromPosition, toPosition;
            private Quaternion fromRotation, toRotation;

            public void MoveTo(Vector3 from, Vector3 to)
            {
                fromPosition = from;
                toPosition = to;
            }

            public void TurnTo(Quaternion from, Quaternion to)
            {
                fromRotation = from;
                toRotation = to;
            }

            public void Restore()
            {
                if (Turns) Transform.localRotation = toRotation;
                else Transform.position = toPosition;
            }

            public void Show(float progress)
            {
                if (Turns) Transform.localRotation = Quaternion.Slerp(fromRotation, toRotation, progress);
                else Transform.position = Vector3.Lerp(fromPosition, toPosition, progress);
            }
        }
    }

    // The frame updates the tick has taken over. Outside the tick, in multiplayer, they only draw.

    [HarmonyPatch(typeof(WonderAnimationController), nameof(WonderAnimationController.Update))]
    static class WonderAnimationControllerUpdatePatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix()
        {
            return WonderTiming.RunsThisFrame;
        }
    }

    [HarmonyPatch(typeof(TimbermeshAnimator), nameof(TimbermeshAnimator.UpdateAnimation))]
    static class TimbermeshAnimatorUpdateAnimationPatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(TimbermeshAnimator __instance)
        {
            return WonderTiming.AnimatorRunsThisFrame(__instance);
        }
    }

    // The gate above only sees the frame update if every earlier prefix let it through. Another mod's
    // prefix that keeps the animator's time itself and returns false (LateGamePerformance's
    // AnimatorCulling, for an animator off screen or far away) skips it, and would move the Wonder's
    // animation on frame time. So, for a Wonder's animator outside the tick, the tick's clock is kept
    // first and put back last. This prefix never skips anything (it returns void, so it is not a
    // replacing prefix); Priority.First only makes it see the clock before anyone else changes it.
    [HarmonyPatch(typeof(TimbermeshAnimator), nameof(TimbermeshAnimator.UpdateAnimation))]
    static class TimbermeshAnimatorClockPatcher
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(TimbermeshAnimator __instance, out WonderTiming.AnimatorClock __state)
        {
            __state = WonderTiming.HoldClock(__instance);
        }

        // Postfixes run whether or not a prefix skipped the original.
        [HarmonyPriority(Priority.First)]
        static void Postfix(TimbermeshAnimator __instance, WonderTiming.AnimatorClock __state)
        {
            WonderTiming.ReleaseClock(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(WonderAnimationController), nameof(WonderAnimationController.StartAnimation))]
    static class WonderAnimationControllerStartAnimationPatcher
    {
        static void Postfix(WonderAnimationController __instance)
        {
            if (EventIO.IsNull) return;
            WonderTiming.AnimationStarted(__instance);
        }
    }

    [HarmonyPatch(typeof(PlaneCatapult), nameof(PlaneCatapult.Update))]
    static class PlaneCatapultUpdatePatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix()
        {
            return WonderTiming.RunsThisFrame;
        }
    }

    [HarmonyPatch(typeof(PlaneLauncherRotator), nameof(PlaneLauncherRotator.Update))]
    static class PlaneLauncherRotatorUpdatePatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(PlaneLauncherRotator __instance)
        {
            if (WonderTiming.RunsThisFrame) return true;
            WonderTiming.ShowPose(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(WonderPlane), nameof(WonderPlane.Update))]
    static class PlaneUpdatePatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(WonderPlane __instance)
        {
            // In free flight nothing simulated reads the plane: it flies on frame time as in the game.
            if (WonderTiming.RunsThisFrame || __instance._isFreeFlying) return true;
            WonderTiming.ShowPose(__instance);
            return false;
        }
    }

    // The tick's clock for the game's own arithmetic.

    [HarmonyPatch(typeof(PlaneCatapult), nameof(PlaneCatapult.UpdatePlane))]
    static class PlaneCatapultUpdatePlanePatcher
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return WonderTiming.UseTickClock(instructions, 1);
        }
    }

    [HarmonyPatch(typeof(PlaneLauncherRotator), nameof(PlaneLauncherRotator.UpdateRotation))]
    static class PlaneLauncherRotatorUpdateRotationPatcher
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return WonderTiming.UseTickClock(instructions, 2);
        }
    }

    [HarmonyPatch(typeof(WonderPlane), nameof(WonderPlane.Update))]
    static class PlaneUpdateClockPatcher
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return WonderTiming.UseTickClock(instructions, 2);
        }
    }

    // A save stores the tick's pose, whatever the frames have drawn since. The keys are the game's.
    // Every save (the game's, the rehost save, the map sent to a joining player) is written here,
    // before any entity's Save runs; the pilots ride the plane's seat and are saved before the plane.

    [HarmonyPatch(typeof(SaveWriter), nameof(SaveWriter.WriteToSaveStream))]
    static class SaveWriterWonderPosePatcher
    {
        // Before any other mod's prefix, which may take the save's snapshot.
        [HarmonyPriority(Priority.First)]
        static void Prefix()
        {
            WonderTiming.RestorePosesForSave();
        }
    }
}
