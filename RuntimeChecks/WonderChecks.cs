using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// SF8. Every Wonder's activation animation, and the Iron Teeth Earth Repopulator's plane launch
// (animation, catapult, runway, launcher rotation), are simulation that the game advances on
// render-frame time. In multiplayer the mod runs them once per tick instead, on the tick interval
// (BeaverBuddies/Doc/WonderTiming.md). Harmony is not installed here (the workshop build cannot
// patch under .NET 8): these checks decode the installed game's IL, run the mod's transpilers on
// it, and call the mod's prefixes, postfixes and tick the way Harmony 2.4.1 and the game would,
// including Harmony's priority order and its skipping of later bool prefixes (CallPatched). Unity's
// native calls in the cloned game methods (curves, transforms, the frame clock) are replaced by the
// stand-ins below; everything else is the game's own code.
internal static class WonderChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    // TickTimeSpec.TickIntervalInSeconds in the installed game's blueprints.
    private const float TickSeconds = 0.6f;
    // A player capped at 10 FPS, a typical 30 and a fast 144.
    private static readonly int[] FrameRates = {10, 30, 144};

    public static float FrameDuration;
    public static float FrameDelta() => FrameDuration;
    // True while RunFrames is in a tick rather than a frame: whatever the simulation decides must happen then.
    public static bool TestTicking;
    // Another mod's prefix on TimbermeshAnimator.UpdateAnimation that keeps the animator's time itself
    // and skips the rest, as LateGamePerformance's AnimatorCulling does for an animator none of whose
    // renderers is visible (every frame) or one far from the camera (AnimatorLod: some frames). It has
    // no [HarmonyPriority], so Harmony's default, Normal. Culling picks the frames it acts on.
    public static Func<bool> Culling;
    private static MethodInfo updateTime;
    public static bool CullingPrefix(object __instance, float deltaTime)
    {
        if (Culling == null || !Culling()) return true;
        // LateGamePerformance's own guard: the game's.
        if (!(bool)animatorType.GetProperty("Enabled").GetValue(__instance) || (float)animatorType.GetField("_speed", All).GetValue(__instance) == 0f ||
            animatorType.GetField("_currentAnimation", All).GetValue(__instance) == null || (bool)animatorType.GetProperty("PlayingFinished").GetValue(__instance))
            return true;
        updateTime.Invoke(__instance, new object[] {deltaTime});
        return false;
    }
    public static float Degrees;
    public static float Runway;
    public static bool Disabled;
    // The Earth Repopulator's curves have this shape (PlaneLauncherRotatorSpec, PlaneCatapultSpec):
    // no turn for the first 40% of a rotation, then up to 20 degrees a second; the runway speed
    // rises from 3 to 30 over its 10 units.
    public static float RotationCurve(object curve, float t) => t < 0.4f ? 0f : t >= 1f ? 20f : (t - 0.4f) / 0.6f * 20f;
    public static float SpeedCurve(object curve, float t) => t <= 0f ? 3f : t >= 1f ? 30f : 3f + 27f * t * t;
    public static void Turn(float degrees) => Degrees += degrees;
    public static void Disable(object component) => Disabled = true;
    public static bool IsAlive(object component) => component != null;
    public static object NoTransform(object component) => null;
    public static void NoFlightTurn(object plane, float seconds) { }

    private static Assembly mod, harmony;
    private static Type codeType, time, vector3;
    private static Type controllerType, wonderType, animatorType, catapultType, rotatorType, planeType, saveWriterType;

    public static void Run(Assembly modAssembly, Action<string, Action> test)
    {
        mod = modAssembly;
        harmony = Assembly.Load("0Harmony");
        codeType = harmony.GetType("HarmonyLib.CodeInstruction", true);
        var unity = Assembly.Load("UnityEngine.CoreModule");
        time = unity.GetType("UnityEngine.Time", true);
        vector3 = unity.GetType("UnityEngine.Vector3", true);
        var wonders = Assembly.Load("Timberborn.Wonders");
        var planes = Assembly.Load("Timberborn.WonderPlanes");
        controllerType = wonders.GetType("Timberborn.Wonders.WonderAnimationController", true);
        wonderType = wonders.GetType("Timberborn.Wonders.Wonder", true);
        animatorType = Assembly.Load("Timberborn.TimbermeshAnimations").GetType("Timberborn.TimbermeshAnimations.TimbermeshAnimator", true);
        updateTime = animatorType.GetMethod("UpdateTime", All) ?? throw new Exception("TimbermeshAnimator.UpdateTime is gone");
        saveWriterType = Assembly.Load("Timberborn.SaveSystem").GetType("Timberborn.SaveSystem.SaveWriter", true);
        catapultType = planes.GetType("Timberborn.WonderPlanes.PlaneCatapult", true);
        rotatorType = planes.GetType("Timberborn.WonderPlanes.PlaneLauncherRotator", true);
        planeType = planes.GetType("Timberborn.WonderPlanes.Plane", true);

        var clockReaders = new (Type Type, string Method, int Reads)[]
        {
            (catapultType, "UpdatePlane", 1),
            (rotatorType, "UpdateRotation", 2),
            (planeType, "Update", 2),
        };

        test("Once the mod's patches apply, no Wonder logic method reads Unity's frame clock", () =>
        {
            foreach (var (type, name, reads) in clockReaders)
            {
                var method = type.GetMethod(name, All);
                var code = Decode(method);
                int before = ClockReads(code);
                if (before != reads)
                    throw new Exception($"{type.Name}.{name} reads the frame clock {before} times in this game build, expected {reads}");
                var transpilers = Patches(type, name, "Transpiler");
                if (transpilers.Count == 0)
                    throw new Exception($"{type.Name}.{name} still reads Time.deltaTime {before} time(s): no timing patch in the mod targets it");
                foreach (var transpiler in transpilers) code = transpiler.Invoke(null, new[] {code});
                int after = ClockReads(code), intoMod = CallsInto(code, mod);
                if (after != 0 || intoMod != reads)
                    throw new Exception($"{type.Name}.{name}: {after} frame-clock read(s) left, {intoMod} clock call(s) into the mod");
            }
        });

        test("Wonder timing transpilers refuse a method body with a different number of clock reads", () =>
        {
            foreach (var (type, name, reads) in clockReaders)
            {
                var transpilers = Patches(type, name, "Transpiler");
                if (transpilers.Count == 0) throw new Exception($"No timing patch targets {type.Name}.{name}");
                // The other shape: a body with one read where two are expected, and two where one is.
                var other = clockReaders.First(r => r.Reads != reads);
                foreach (var transpiler in transpilers)
                foreach (object body in new[] {Array.CreateInstance(codeType, 0), Decode(other.Type.GetMethod(other.Method, All))})
                {
                    try { transpiler.Invoke(null, new[] {body}); }
                    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { continue; }
                    throw new Exception($"The {type.Name}.{name} patch accepted an incompatible body");
                }
            }
        });

        test("In multiplayer the Wonder's per-frame updates do nothing outside the simulation tick", () =>
        {
            var gated = new[] {(controllerType, "Update"), (catapultType, "Update"), (rotatorType, "Update"), (planeType, "Update"), (animatorType, "UpdateAnimation")};
            foreach (var (type, name) in gated)
            {
                var prefixes = Patches(type, name, "Prefix");
                if (!prefixes.Any(p => p.ReturnType == typeof(bool))) throw new Exception($"{type.Name}.{name} has no multiplayer gate");
                foreach (var prefix in prefixes)
                {
                    // A prefix that can skip the original (returns bool) goes last; any other one returns void.
                    if (prefix.ReturnType == typeof(bool) && PriorityOf(prefix) != 0)
                        throw new Exception($"{type.Name}.{name}'s prefix replaces the original without [HarmonyPriority(Priority.Last)]");
                    if (prefix.ReturnType != typeof(bool) && prefix.ReturnType != typeof(void))
                        throw new Exception($"{type.Name}.{name}'s prefix returns {prefix.ReturnType.Name}");
                }
            }
            WithMultiplayer(() =>
            {
                var tracked = NewAnimation(4f, out object controller);
                RunStartPostfix(controller);
                var untracked = NewAnimation(4f, out _);
                var runwayPlane = New(planeType);
                var flyingPlane = New(planeType);
                planeType.GetField("_isFreeFlying", All).SetValue(flyingPlane, true);
                var gates = new (Type Type, string Method, object Instance, bool InMultiplayer)[]
                {
                    (controllerType, "Update", controller, false),
                    (catapultType, "Update", New(catapultType), false),
                    (rotatorType, "Update", New(rotatorType), false),
                    (planeType, "Update", runwayPlane, false),
                    // Free flight is drawing only: the plane has left the runway and nothing simulated reads it.
                    (planeType, "Update", flyingPlane, true),
                    (animatorType, "UpdateAnimation", tracked, false),
                    // Every other animator in the game keeps the game's own frame clock.
                    (animatorType, "UpdateAnimation", untracked, true),
                };
                foreach (var (type, name, instance, inMultiplayer) in gates)
                {
                    bool runs = CallPatched(type, name, instance, 0.02f, null);
                    if (runs != inMultiplayer)
                        throw new Exception($"In multiplayer, {type.Name}.{name} {(runs ? "still runs" : "was skipped")} outside the tick");
                }
                var io = EventField();
                object session = io.GetValue(null);
                io.SetValue(null, null);
                try
                {
                    foreach (var (type, name, instance, _) in gates)
                        if (!CallPatched(type, name, instance, 0.02f, null))
                            throw new Exception($"In single player, {type.Name}.{name} was skipped");
                }
                finally { io.SetValue(null, session); }
            });
        });

        test("Plane spawning is reached only from Wonder updates that the mod runs on the tick", () =>
        {
            var game = Scan(catapultType.Assembly).Concat(Scan(controllerType.Assembly)).ToList();
            var spawner = catapultType.Assembly.GetType("Timberborn.WonderPlanes.PlaneSpawner", true);
            var launcher = catapultType.Assembly.GetType("Timberborn.WonderPlanes.PlaneLauncher", true);
            Expect(Callers(game, spawner, "SpawnPlane"), "PlaneCatapult.CatapultPlane");
            Expect(Callers(game, catapultType, "CatapultPlane"), "PlaneLauncher.StartEjectingPlane");
            Expect(Callers(game, launcher, "StartEjectingPlane"), "PlaneLauncher.OnRotationFinished", "PlaneLauncher.OnStartAnimationFinished");
            // Those two handlers are only ever subscribed, in Awake, to these two events...
            Expect(Callers(game, launcher, "OnRotationFinished").Concat(Callers(game, launcher, "OnStartAnimationFinished")).Distinct(), "PlaneLauncher.Awake");
            // ...which are raised only from the two per-frame updates.
            Expect(Readers(game, rotatorType, "RotationFinished"), "PlaneLauncherRotator.Update");
            Expect(Readers(game, controllerType, "StartAnimationFinished"), "WonderAnimationController.InvokeAnimationFinishedEvent");
            Expect(Callers(game, controllerType, "InvokeAnimationFinishedEvent"), "WonderAnimationController.Update");
            var ours = Scan(mod);
            foreach (var type in new[] {controllerType, rotatorType, catapultType})
            {
                var callers = Callers(ours, type, "Update").ToList();
                if (callers.Count == 0) throw new Exception($"Nothing in the mod runs {type.Name}.Update on the tick");
                var timing = mod.GetType("BeaverBuddies.Fixes.WonderTiming", true);
                var stray = ours.Where(i => IsCall(i.Op) && i.Operand is MethodBase m && m.DeclaringType == type && m.Name == "Update" &&
                    i.Method.DeclaringType != timing).Select(i => Name(i.Method)).ToList();
                if (stray.Count > 0) throw new Exception($"{type.Name}.Update is also called from {string.Join(", ", stray)}");
            }
            foreach (var (type, name) in new[] {(spawner, "SpawnPlane"), (catapultType, "CatapultPlane"), (launcher, "StartEjectingPlane")})
                if (Callers(ours, type, name).Any()) throw new Exception($"The mod calls {type.Name}.{name} directly");
        });

        test("In multiplayer the tick runs every part of every Wonder, and saves and a session's end put the ticked poses back first", () =>
        {
            // The parts of the mod that only Unity can run (the Wonders' registry, transforms), from the mod's IL.
            var ours = Scan(mod);
            var timing = Timing();
            var service = mod.GetType("BeaverBuddies.Fixes.WonderTickService", true);
            var replay = mod.GetType("BeaverBuddies.ReplayService", true);
            var eventIO = mod.GetType("BeaverBuddies.IO.EventIO", true);
            var binders = BindersOf(ours, service).ToList();
            if (binders.Count == 0) throw new Exception("Nothing binds WonderTickService");
            if (!binders.All(m => BindersOf(ours, replay).Contains(m)))
                throw new Exception("WonderTickService is not bound with the co-op services (next to ReplayService)");
            ExpectCalls(ours, service, "Tick", (timing, "Tick"));
            // A session that ended hands everything back before anything is stepped; otherwise the
            // poses the frames drew are put back before the step reads them.
            ExpectCalls(ours, timing, "Tick", (eventIO, "get_IsNull"), (timing, "Release"), (timing, "RestorePoses"), (timing, "RunTick"));
            var steps = ours.Where(u => u.Method.DeclaringType == timing && u.Method.Name == "Tick" && u.Op == OpCodes.Ldftn)
                .Select(u => u.Operand as MethodBase).Where(m => m != null && CallsOf(ours, m).Any(c => c.DeclaringType == timing && c.Name == "StepWonder")).ToList();
            if (steps.Count == 0) throw new Exception("The tick's step does not run StepWonder");
            ExpectCalls(ours, timing, "StepWonder", (timing, "StepAnimation"), (timing, "StepCatapult"), (timing, "StepRotator"));
            ExpectCalls(ours, timing, "StepAnimation", (animatorType, "UpdateAnimation"), (controllerType, "Update"));
            ExpectCalls(ours, timing, "StepCatapult", (catapultType, "Update"), (planeType, "Update"));
            ExpectCalls(ours, timing, "StepRotator", (rotatorType, "Update"));
            ExpectCalls(ours, timing, "Release", (timing, "RestorePoses"));
            // Every save is written through SaveWriter.WriteToSaveStream; the poses go back before any
            // entity is saved (the pilots, on the plane's seat, are saved before their plane), and before
            // any other mod's prefix takes the save's snapshot.
            var savePrefixes = Patches(saveWriterType, "WriteToSaveStream", "Prefix");
            if (!savePrefixes.Any(p => p.ReturnType == typeof(void) && PriorityOf(p) == 800 &&
                    CallsOf(ours, p).Any(c => c.DeclaringType == timing && c.Name == "RestorePosesForSave")))
                throw new Exception("Nothing puts the ticked poses back before a save is written (a void Priority.First prefix on SaveWriter.WriteToSaveStream)");
            ExpectCalls(ours, timing, "RestorePosesForSave", (eventIO, "get_IsNull"), (timing, "RestorePoses"));
        });

        const float animationLength = 4.19f;
        test("Real Wonder animation finishes on a different tick at 10, 30 and 144 FPS before the timing fix", () =>
        {
            var ticks = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, Mode.Game).Tick).ToList();
            Console.WriteLine($"  Animation of {animationLength}s ends on tick: " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {ticks[i]}")));
            if (ticks.Distinct().Count() == 1) throw new Exception("Frame-rate dependence was not reproduced");
        });

        test("With the timing fix, the Wonder animation ends on the same tick at every frame rate, with the same saved time", () =>
        {
            var runs = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, Mode.Tick)).ToList();
            Console.WriteLine($"  Animation of {animationLength}s ends on tick: " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {runs[i].Tick}")));
            ExpectTickResult(runs, "The animation's end or its saved time still depends on the frame rate");
        });

        test("With another mod's animator culling ahead of the gate (LateGamePerformance), the Wonder animation still ends on the same tick", () =>
        {
            // What makes this hold: a prefix that cannot skip, before every other mod's, and a postfix.
            var guard = Patches(animatorType, "UpdateAnimation", "Prefix").Where(p => p.ReturnType == typeof(void) && PriorityOf(p) == 800).ToList();
            if (guard.Count == 0) throw new Exception("Nothing keeps a Wonder animator's clock before other mods' prefixes on UpdateAnimation run ([HarmonyPriority(Priority.First)], void)");
            if (!guard.Any(p => PatchMethod(p.DeclaringType, "Postfix") is MethodInfo post && PriorityOf(post) == 800))
                throw new Exception("Nothing puts a Wonder animator's clock back after the other mods' prefixes ran");
            var reference = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, Mode.Tick)).ToList();
            foreach (var (label, mode) in new[] {("off screen", Mode.TickOffScreen), ("far away", Mode.TickFarAway)})
            {
                var runs = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, mode)).ToList();
                Console.WriteLine($"  {label}: ends on tick " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {runs[i].Tick}")) +
                    $" (on screen: {reference[0].Tick})");
                ExpectTickResult(runs, $"With the Wonder {label} on this player, the animation still depends on the frame rate");
                if (runs.Select(r => r.TimeBits).Concat(reference.Select(r => r.TimeBits)).Distinct().Count() != 1)
                    throw new Exception($"With the Wonder {label}, the animation's time differs from on screen");
            }
        });

        test("After a co-op session ends mid-game, the Wonder animation runs on the game's frames alone, as in single player", () =>
        {
            var game = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, Mode.Game)).ToList();
            var ended = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, Mode.SessionEnded)).ToList();
            Console.WriteLine("  Ends on tick: " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {ended[i].Tick} (game: {game[i].Tick})")));
            for (int i = 0; i < FrameRates.Length; i++)
                if (ended[i] != game[i])
                    throw new Exception($"At {FrameRates[i]} FPS the animation ended on tick {ended[i].Tick}, the game's own frames end it on tick {game[i].Tick}");
        });

        test("Real launcher rotation differs at 10, 30 and 144 FPS before the timing fix", () =>
        {
            var runs = FrameRates.Select(fps => Rotation(fps, fixedStep: false)).ToList();
            Console.WriteLine("  45-degree turn: " + string.Join("; ", FrameRates.Select((f, i) =>
                $"{f} FPS -> {runs[i].States[4]:R} deg left after tick 4, ends on tick {runs[i].Tick}")));
            if (runs.Select(r => string.Join(",", r.States)).Distinct().Count() == 1) throw new Exception("Frame-rate dependence was not reproduced");
        });

        test("With the timing fix, the launcher rotation matches bit for bit and ends on the same tick at every frame rate", () =>
        {
            var runs = FrameRates.Select(fps => Rotation(fps, fixedStep: true)).ToList();
            Console.WriteLine($"  45-degree turn ends on tick {runs[0].Tick} ({runs[0].Degrees:R} degrees turned)");
            if (runs.Select(r => string.Join(",", r.States.Select(BitConverter.SingleToInt32Bits))).Distinct().Count() != 1 ||
                runs.Select(r => r.Tick).Distinct().Count() != 1)
                throw new Exception("The rotation still depends on the frame rate");
            if (runs[0].States.Last() != 0f || Math.Abs(runs[0].Degrees - 45f) > 1e-4f)
                throw new Exception($"Turned {runs[0].Degrees} degrees with {runs[0].States.Last()} left, instead of 45");
        });

        test("Real runway launch differs at 10, 30 and 144 FPS before the timing fix", () =>
        {
            var runs = FrameRates.Select(fps => Launch(fps, fixedStep: false)).ToList();
            Console.WriteLine("  Runway: " + string.Join("; ", FrameRates.Select((f, i) =>
                $"{f} FPS -> {runs[i].States[3]:R} units after tick 3, leaves on tick {runs[i].Tick}")));
            if (runs.Select(r => string.Join(",", r.States)).Distinct().Count() == 1) throw new Exception("Frame-rate dependence was not reproduced");
        });

        test("With the timing fix, the runway launch matches bit for bit and ends on the same tick at every frame rate", () =>
        {
            var runs = FrameRates.Select(fps => Launch(fps, fixedStep: true)).ToList();
            Console.WriteLine($"  Runway left on tick {runs[0].Tick}, {runs[0].States.Last():R} units from the spawn point");
            if (runs.Select(r => string.Join(",", r.States.Select(BitConverter.SingleToInt32Bits))).Distinct().Count() != 1 ||
                runs.Select(r => r.Tick).Distinct().Count() != 1)
                throw new Exception("The launch still depends on the frame rate");
        });

        test("The mod never replaces how the Wonder and plane components save and load", () =>
        {
            var pilot = catapultType.Assembly.GetType("Timberborn.WonderPlanes.Pilot", true);
            var launcher = catapultType.Assembly.GetType("Timberborn.WonderPlanes.PlaneLauncher", true);
            foreach (var type in new[] {wonderType, controllerType, catapultType, rotatorType, launcher, planeType, pilot})
            foreach (var name in new[] {"Save", "Load"})
            {
                if (Patches(type, name, "Transpiler").Count > 0) throw new Exception($"{type.Name}.{name} is transpiled");
                foreach (var prefix in Patches(type, name, "Prefix"))
                    if (prefix.ReturnType != typeof(void)) throw new Exception($"{type.Name}.{name}'s prefix can skip the game's save keys");
            }
        });
    }

    // ---- Simulated runs ----

    private record struct AnimationRun(int Tick, int TimeBits);

    private enum Mode
    {
        // The game as it is: no mod.
        Game,
        // Multiplayer with the mod, the Wonder on screen.
        Tick,
        // The same with another mod's culling prefix acting on every frame, or on every second one.
        TickOffScreen,
        TickFarAway,
        // Multiplayer with the mod, the session ended (EventIO reset) just after the animation started.
        SessionEnded,
    }

    private static void ExpectTickResult(List<AnimationRun> runs, string message)
    {
        if (runs.Select(r => r.Tick).Distinct().Count() != 1 || runs.Select(r => r.TimeBits).Distinct().Count() != 1) throw new Exception(message);
        // 7 ticks of 0.6 s are the first to reach 4.19 s.
        if (runs[0].Tick != 7) throw new Exception($"Ended on tick {runs[0].Tick}, expected 7");
    }

    // A Wonder's animation of the given length, just started. Per frame the game advances every
    // animator (AnimatorRegistry.UpdateSingleton) and then the Wonder's controller checks for the
    // end (BaseComponentUpdateUnityAdapter.Update). With the mod, each tick runs the mod's own
    // WonderTiming.Tick, as WonderTickService does, and each frame goes through the mod's patches
    // as Harmony would call them.
    private static AnimationRun AnimationFinishTick(int fps, float length, Mode mode)
    {
        AnimationRun result = default;
        Action run = () =>
        {
            var animator = NewAnimation(length, out object controller);
            int ticks = 0, finished = -1;
            bool outsideTick = false;
            controllerType.GetEvent("StartAnimationFinished").AddEventHandler(controller, new EventHandler((_, _) =>
            {
                finished = ticks;
                outsideTick |= !TestTicking;
            }));
            bool withMod = mode != Mode.Game;
            if (withMod) RunStartPostfix(controller);
            if (mode == Mode.SessionEnded) EventField().SetValue(null, null);
            int frame = 0;
            Culling = mode switch
            {
                Mode.TickOffScreen => () => true,
                Mode.TickFarAway => () => frame % 2 == 1,
                _ => null,
            };
            var update = animatorType.GetMethod("UpdateAnimation", All);
            var check = controllerType.GetMethod("Update", All);
            object wonders = withMod ? WonderList(controller) : null;
            var foreign = mode is Mode.TickOffScreen or Mode.TickFarAway ? new[] {typeof(WonderChecks).GetMethod(nameof(CullingPrefix))} : Array.Empty<MethodInfo>();
            try
            {
                RunFrames(fps, () => finished >= 0, () =>
                {
                    ticks++;
                    if (withMod) WonderTick(wonders);
                }, () =>
                {
                    frame++;
                    if (!withMod)
                    {
                        update.Invoke(animator, new object[] {FrameDuration});
                        check.Invoke(controller, null);
                        return;
                    }
                    CallPatched(animatorType, "UpdateAnimation", animator, FrameDuration, () => update.Invoke(animator, new object[] {FrameDuration}), foreign);
                    CallPatched(controllerType, "Update", controller, FrameDuration, () => check.Invoke(controller, null));
                });
            }
            finally { Culling = null; }
            if (withMod && mode != Mode.SessionEnded && outsideTick) throw new Exception("The animation's end was raised by a frame, outside the tick");
            result = new AnimationRun(finished, BitConverter.SingleToInt32Bits((float)animatorType.GetProperty("Time").GetValue(animator)));
        };
        if (mode != Mode.Game) WithMultiplayer(run); else run();
        return result;
    }

    // WonderTickService's list for WonderTiming.Tick: one Wonder with only the animation.
    private static object WonderList(object controller)
    {
        var parts = Timing().GetNestedType("Parts", All) ?? throw new Exception("WonderTiming.Parts is gone");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(parts));
        list.Add(Activator.CreateInstance(parts, All, null, new[] {controller, null, null}, null));
        return list;
    }

    // What WonderTickService.Tick does once the Wonders are listed.
    private static void WonderTick(object wonders)
    {
        var tick = Timing().GetMethod("Tick", All, new[] {wonders.GetType(), typeof(float)}) ?? throw new Exception("WonderTiming.Tick(List<Parts>, float) is gone");
        try { tick.Invoke(null, new[] {wonders, TickSeconds}); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }

    private record struct StepRun(int Tick, float Degrees, List<float> States);

    // A 45-degree turn of the launcher (one of eight planes: FullRotationDuration 10 s * 45 / 360).
    // States are the rotation left at each tick; Tick is when RotationFinished is raised.
    private static StepRun Rotation(int fps, bool fixedStep)
    {
        StepRun result = default;
        Action run = () =>
        {
            var update = CloneRotator(fixedStep);
            var rotator = New(rotatorType);
            rotatorType.GetField("_remainingRotation", All).SetValue(rotator, 45f);
            rotatorType.GetField("_rotationDuration", All).SetValue(rotator, 1.25f);
            int ticks = 0, finished = -1;
            bool outsideTick = false;
            rotatorType.GetEvent("RotationFinished").AddEventHandler(rotator, new EventHandler((_, _) =>
            {
                finished = ticks;
                outsideTick |= !TestTicking;
            }));
            Degrees = 0; Disabled = false;
            var states = new List<float>();
            Action step = () => { if (!Disabled) update.Invoke(null, new[] {rotator}); };
            RunFrames(fps, () => finished >= 0, () =>
            {
                ticks++;
                if (fixedStep) RunTick(step);
                states.Add((float)rotatorType.GetField("_remainingRotation", All).GetValue(rotator));
            }, () =>
            {
                if (Disabled) return;
                if (!fixedStep) step();
                else CallPatched(rotatorType, "Update", rotator, FrameDuration, step);
            });
            if (fixedStep && outsideTick) throw new Exception("The launcher's turn ended in a frame, outside the tick");
            result = new StepRun(finished, Degrees, states);
        };
        if (fixedStep) WithMultiplayer(run); else run();
        return result;
    }

    // One plane from the catapult to the end of the 10-unit runway. States are its distance from
    // the spawn point at each tick; Tick is when PlaneCatapulted is raised. With the fix, each tick
    // runs the catapult and then moves the plane while it is still on the runway, as the mod's
    // tick step does.
    private static StepRun Launch(int fps, bool fixedStep)
    {
        StepRun result = default;
        Action run = () =>
        {
            var (catapultUpdate, planeUpdate) = CloneRunway(fixedStep);
            var catapult = New(catapultType);
            var plane = New(planeType);
            var current = catapultType.GetField("_catapultedPlane", All);
            current.SetValue(catapult, plane);
            catapultType.GetField("_remainingPlaneWaitTime", All).SetValue(catapult, 1f);
            int ticks = 0, finished = -1;
            bool outsideTick = false;
            catapultType.GetEvent("PlaneCatapulted").AddEventHandler(catapult, new EventHandler((_, _) =>
            {
                finished = ticks;
                outsideTick |= !TestTicking;
            }));
            Runway = 0; Disabled = false;
            var states = new List<float>();
            RunFrames(fps, () => finished >= 0, () =>
            {
                ticks++;
                if (fixedStep) RunTick(() =>
                {
                    if (Disabled) return;
                    catapultUpdate.Invoke(null, new[] {catapult});
                    if (ReferenceEquals(current.GetValue(catapult), plane)) planeUpdate.Invoke(null, new[] {plane});
                });
                states.Add(Runway);
            }, () =>
            {
                Action catapultStep = () => { if (!Disabled) catapultUpdate.Invoke(null, new[] {catapult}); };
                Action planeStep = () => planeUpdate.Invoke(null, new[] {plane});
                if (!fixedStep) { catapultStep(); planeStep(); return; }
                CallPatched(catapultType, "Update", catapult, FrameDuration, catapultStep);
                CallPatched(planeType, "Update", plane, FrameDuration, planeStep);
            });
            if (fixedStep && outsideTick) throw new Exception("The plane left the runway in a frame, outside the tick");
            result = new StepRun(finished, Runway, states);
        };
        if (fixedStep) WithMultiplayer(run); else run();
        return result;
    }

    // The game's frame loop: the Ticker runs whatever ticks are due, then the per-frame updates run.
    private static void RunFrames(int fps, Func<bool> done, Action tick, Action frame)
    {
        FrameDuration = 1f / fps;
        double pending = 0;
        for (int f = 0; f < 100000 && !done(); f++)
        {
            pending += FrameDuration;
            while (pending >= TickSeconds - 1e-6 && !done())
            {
                pending -= TickSeconds;
                TestTicking = true;
                try { tick(); }
                finally { TestTicking = false; }
            }
            if (!done()) frame();
        }
        if (!done()) throw new Exception("The sequence never ended");
    }

    // ---- The mod ----

    private static FieldInfo EventField() => mod.GetType("BeaverBuddies.IO.EventIO", true).GetField("instance", All);

    private static void WithMultiplayer(Action action)
    {
        var io = EventField();
        object prior = io.GetValue(null);
        io.SetValue(null, DispatchProxy.Create(mod.GetType("BeaverBuddies.IO.EventIO", true), typeof(EmptyEventProxy)));
        var reset = mod.GetType("BeaverBuddies.Fixes.WonderTiming")?.GetMethod("Reset", All);
        reset?.Invoke(null, null);
        // Unity's frame clock is native; outside the tick the mod reads this instead, so a frame
        // update that reaches the logic shows up as frame-rate dependence, not as a crash.
        var clock = mod.GetType("BeaverBuddies.Fixes.WonderTiming")?.GetField("FrameClock", All);
        object priorClock = clock?.GetValue(null);
        clock?.SetValue(null, (Func<float>)FrameDelta);
        try { action(); }
        finally { reset?.Invoke(null, null); clock?.SetValue(null, priorClock); io.SetValue(null, prior); }
    }

    private static Type Timing() => mod.GetType("BeaverBuddies.Fixes.WonderTiming", true);

    // The mod's tick scope: inside it the Wonder's clock reads the tick interval.
    private static void RunTick(Action step)
    {
        try { Timing().GetMethod("RunTick", All).Invoke(null, new object[] {TickSeconds, step}); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }

    // What Harmony does after WonderAnimationController.StartAnimation.
    private static void RunStartPostfix(object controller)
    {
        var postfixes = Patches(controllerType, "StartAnimation", "Postfix");
        if (postfixes.Count == 0) throw new Exception("Nothing in the mod follows a Wonder animation's start");
        foreach (var postfix in postfixes) Invoke(postfix, controller, null);
    }

    // A call of a patched method as Harmony 2.4.1 makes it (MethodCreator; checked with Lib.Harmony
    // 2.4.1 on .NET 8): prefixes by priority, highest first, the mod's and the given other mods' ones
    // (default priority Normal); a prefix that returns bool runs only while every earlier one returned
    // true, and decides whether the original runs; a void prefix always runs; then the original, if it
    // runs; then every postfix by priority, whether or not the original ran. __state goes from a patch
    // class's prefix to its postfix. Returns whether the original ran.
    private static bool CallPatched(Type type, string name, object instance, float deltaTime, Action original, params MethodInfo[] foreign)
    {
        var prefixes = Patches(type, name, "Prefix").Concat(foreign).OrderByDescending(PriorityOf).ToList();
        var states = new Dictionary<Type, object>();
        bool runOriginal = true;
        foreach (var prefix in prefixes)
        {
            bool skips = prefix.ReturnType == typeof(bool);
            if (skips && !runOriginal) continue;
            object result = Invoke(prefix, instance, deltaTime, states);
            if (skips) runOriginal = (bool)result;
        }
        if (runOriginal) original?.Invoke();
        foreach (var postfix in Patches(type, name, "Postfix").OrderByDescending(PriorityOf))
            Invoke(postfix, instance, deltaTime, states);
        return runOriginal;
    }

    private static object Invoke(MethodInfo patch, object instance, float? deltaTime, Dictionary<Type, object> states = null)
    {
        var parameters = patch.GetParameters();
        var args = parameters.Select(p => p.Name switch
        {
            "__instance" => instance,
            "deltaTime" => (object)deltaTime,
            "__state" => states != null && states.TryGetValue(patch.DeclaringType, out object state) ? state
                : Activator.CreateInstance(p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType),
            _ => throw new Exception($"Unexpected patch parameter {p.Name} on {patch.DeclaringType.Name}"),
        }).ToArray();
        object result;
        try { result = patch.Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].Name == "__state" && parameters[i].ParameterType.IsByRef && states != null) states[patch.DeclaringType] = args[i];
        return result;
    }

    // The mod's Harmony patches of one method, found from its [HarmonyPatch] attributes.
    private static List<MethodInfo> Patches(Type target, string method, string kind)
    {
        var result = new List<MethodInfo>();
        foreach (var type in Types(mod))
        foreach (var data in Attributes(type))
        {
            if (data.AttributeType.FullName != "HarmonyLib.HarmonyPatch") continue;
            var args = data.ConstructorArguments;
            if (args.Count < 2 || !Equals(args[0].Value, target) || !Equals(args[1].Value, method)) continue;
            if (PatchMethod(type, kind) is MethodInfo patch) result.Add(patch);
        }
        return result;
    }

    private static MethodInfo PatchMethod(Type patchClass, string kind) => patchClass.GetMethod(kind, All | BindingFlags.DeclaredOnly);

    private static IList<CustomAttributeData> Attributes(Type type)
    {
        try { return type.GetCustomAttributesData(); }
        catch (Exception) { return Array.Empty<CustomAttributeData>(); }
    }

    // [HarmonyPriority] of a patch method: Priority.Last is 0, Normal 400 (the default), First 800.
    private static int PriorityOf(MethodInfo patch) => patch.GetCustomAttributesData()
        .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority" && a.ConstructorArguments.Count == 1)
        .Select(a => Convert.ToInt32(a.ConstructorArguments[0].Value)).DefaultIfEmpty(400).First();

    // ---- Game fixtures ----

    private static object New(Type type)
    {
        object instance = RuntimeHelpers.GetUninitializedObject(type);
        // BaseComponent.Enabled starts true in the game; the constructor did not run here.
        // Its setter is private to BaseComponent, so it is only reachable through the declaring type.
        var enabled = type.GetProperty("Enabled", All);
        enabled?.DeclaringType.GetProperty("Enabled", All).SetValue(instance, true);
        return instance;
    }

    // A playing Wonder animation, as Play leaves it: time 0, enabled, not finished.
    private static object NewAnimation(float length, out object controller)
    {
        var animator = RuntimeHelpers.GetUninitializedObject(animatorType);
        var metadata = animatorType.Assembly.GetType("Timberborn.TimbermeshAnimations.AnimationMetadata", true);
        var updater = animatorType.Assembly.GetType("Timberborn.TimbermeshAnimations.IAnimationUpdater", true);
        animatorType.GetProperty("Speed").SetValue(animator, 1f);
        animatorType.GetField("_currentAnimation", All).SetValue(animator, Activator.CreateInstance(metadata, All, null, new object[] {"Default", length}, null));
        animatorType.GetField("_animationUpdaters", All).SetValue(animator, Array.CreateInstance(updater, 0));
        animatorType.GetProperty("Time").SetValue(animator, 0f);
        animatorType.GetProperty("PlayingFinished").SetValue(animator, false);
        animatorType.GetProperty("Enabled").SetValue(animator, true);
        var wonder = New(wonderType);
        wonderType.GetProperty("IsActive").SetValue(wonder, true);
        controller = New(controllerType);
        controllerType.GetField("_animator", All).SetValue(controller, animator);
        controllerType.GetField("_wonder", All).SetValue(controller, wonder);
        return animator;
    }

    // PlaneLauncherRotator.Update and UpdateRotation, cloned from the installed game.
    private static DynamicMethod CloneRotator(bool patched)
    {
        var rotate = Stub("Rotate", typeof(void), new[] {typeof(object), vector3, typeof(float)}, il =>
        {
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, typeof(WonderChecks).GetMethod(nameof(Turn)));
        });
        var updateRotation = Clone(rotatorType.GetMethod("UpdateRotation", All), patched, m => m.Name switch
        {
            "Evaluate" => typeof(WonderChecks).GetMethod(nameof(RotationCurve)),
            "Rotate" => rotate,
            _ => null,
        });
        return Clone(rotatorType.GetMethod("Update", All), false, m => m.Name switch
        {
            "UpdateRotation" => updateRotation,
            "DisableComponent" => typeof(WonderChecks).GetMethod(nameof(Disable)),
            _ => null,
        });
    }

    // PlaneCatapult.Update with UpdatePlane, and Plane.Update, cloned from the installed game. The
    // plane's transform is its distance from the spawn point along its heading.
    private static (DynamicMethod Catapult, DynamicMethod Plane) CloneRunway(bool patched)
    {
        var ctor = vector3.GetConstructor(new[] {typeof(float), typeof(float), typeof(float)});
        DynamicMethod Vector(string name, Action<ILGenerator> z) => Stub(name, vector3, new[] {typeof(object)}, il =>
        {
            il.Emit(OpCodes.Ldc_R4, 0f);
            il.Emit(OpCodes.Ldc_R4, 0f);
            z(il);
            il.Emit(OpCodes.Newobj, ctor);
        });
        var position = Vector("GetPosition", il => il.Emit(OpCodes.Ldsfld, typeof(WonderChecks).GetField(nameof(Runway))));
        var forward = Vector("GetForward", il => il.Emit(OpCodes.Ldc_R4, 1f));
        var spawn = Vector("SpawnPosition", il => il.Emit(OpCodes.Ldc_R4, 0f));
        var setPosition = Stub("SetPosition", typeof(void), new[] {typeof(object), vector3}, il =>
        {
            il.Emit(OpCodes.Ldarga_S, (byte)1);
            il.Emit(OpCodes.Ldfld, vector3.GetField("z"));
            il.Emit(OpCodes.Stsfld, typeof(WonderChecks).GetField(nameof(Runway)));
        });
        MethodInfo Stand(MethodInfo m) => m.Name switch
        {
            "get_Transform" => typeof(WonderChecks).GetMethod(nameof(NoTransform)),
            "get_position" => position,
            "set_position" => setPosition,
            "get_forward" => forward,
            "get_SpawnPosition" => spawn,
            "Evaluate" => typeof(WonderChecks).GetMethod(nameof(SpeedCurve)),
            "DisableComponent" => typeof(WonderChecks).GetMethod(nameof(Disable)),
            "op_Implicit" => typeof(WonderChecks).GetMethod(nameof(IsAlive)),
            "RotateTowardHorizontalFlight" => typeof(WonderChecks).GetMethod(nameof(NoFlightTurn)),
            _ => null,
        };
        var updatePlane = Clone(catapultType.GetMethod("UpdatePlane", All), patched, Stand);
        var catapult = Clone(catapultType.GetMethod("Update", All), false, m => m.Name == "UpdatePlane" ? updatePlane : Stand(m));
        return (catapult, Clone(planeType.GetMethod("Update", All), patched, Stand));
    }

    private static DynamicMethod Stub(string name, Type returns, Type[] parameters, Action<ILGenerator> body)
    {
        var method = new DynamicMethod(name, returns, parameters, typeof(WonderChecks).Module, true);
        var il = method.GetILGenerator();
        body(il);
        il.Emit(OpCodes.Ret);
        return method;
    }

    // The game method's own IL, optionally through the mod's transpilers, with the native calls
    // replaced. The unpatched clone reads the frame clock from FrameDuration.
    private static DynamicMethod Clone(MethodInfo original, bool patched, Func<MethodInfo, MethodInfo> substitute)
    {
        var parameters = new[] {original.DeclaringType}.Concat(original.GetParameters().Select(p => p.ParameterType)).ToArray();
        var method = new DynamicMethod(original.Name + "Clone", original.ReturnType, parameters, typeof(WonderChecks).Module, true);
        var il = method.GetILGenerator();
        foreach (var local in original.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType, local.IsPinned);
        object instructions = TimingChecks.ReadInstructions(original, il, codeType);
        if (patched)
        {
            var transpilers = Patches(original.DeclaringType, original.Name, "Transpiler");
            if (transpilers.Count == 0) throw new Exception($"No timing patch targets {original.DeclaringType.Name}.{original.Name}");
            foreach (var transpiler in transpilers) instructions = transpiler.Invoke(null, new[] {instructions});
        }
        foreach (object instruction in (IEnumerable)instructions)
        {
            var type = instruction.GetType();
            foreach (Label label in (IEnumerable)type.GetField("labels").GetValue(instruction)) il.MarkLabel(label);
            var opcode = (OpCode)type.GetField("opcode").GetValue(instruction);
            var operand = type.GetField("operand").GetValue(instruction);
            if (operand is MethodInfo called)
            {
                var replacement = called.Name == "get_deltaTime" && called.DeclaringType == time
                    ? typeof(WonderChecks).GetMethod(nameof(FrameDelta))
                    : substitute(called);
                if (replacement != null)
                {
                    operand = replacement;
                    if (opcode == OpCodes.Callvirt) opcode = OpCodes.Call;
                }
            }
            switch (operand)
            {
                case null: il.Emit(opcode); break;
                case MethodInfo m: il.Emit(opcode, m); break;
                case FieldInfo f: il.Emit(opcode, f); break;
                case Label label: il.Emit(opcode, label); break;
                case float value: il.Emit(opcode, value); break;
                case int value: il.Emit(opcode, value); break;
                case byte value: il.Emit(opcode, value); break;
                case sbyte value: il.Emit(opcode, value); break;
                default: throw new Exception("Unsupported fixture operand: " + operand.GetType());
            }
        }
        return method;
    }

    private static object Decode(MethodInfo method)
    {
        var scratch = new DynamicMethod("Decode", typeof(void), Type.EmptyTypes, typeof(WonderChecks).Module, true);
        return TimingChecks.ReadInstructions(method, scratch.GetILGenerator(), codeType);
    }

    private static int ClockReads(object code) => Operands(code).Count(o => o is MethodInfo m && m.Name == "get_deltaTime" && m.DeclaringType == time);

    private static int CallsInto(object code, Assembly assembly) => Operands(code).Count(o => o is MethodInfo m && m.DeclaringType?.Assembly == assembly);

    private static IEnumerable<object> Operands(object code)
    {
        foreach (object instruction in (IEnumerable)code) yield return instruction.GetType().GetField("operand").GetValue(instruction);
    }

    // ---- IL call graph ----

    private record struct Use(MethodBase Method, OpCode Op, object Operand);

    private static bool IsCall(OpCode op) => op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn;

    private static string Name(MethodBase method) => method.DeclaringType?.Name + "." + method.Name;

    private static IEnumerable<string> Callers(List<Use> uses, Type type, string method) =>
        uses.Where(u => IsCall(u.Op) && u.Operand is MethodBase m && m.DeclaringType == type && m.Name == method).Select(u => Name(u.Method)).Distinct();

    // Methods that read an event's delegate field to raise it, other than the event's own add/remove accessors.
    private static IEnumerable<string> Readers(List<Use> uses, Type type, string field) =>
        uses.Where(u => u.Op == OpCodes.Ldfld && u.Operand is FieldInfo f && f.DeclaringType == type && f.Name == field &&
            u.Method.Name != "add_" + field && u.Method.Name != "remove_" + field).Select(u => Name(u.Method)).Distinct();

    // The methods a method calls, in IL order.
    private static List<MethodBase> CallsOf(List<Use> uses, MethodBase method) =>
        uses.Where(u => u.Method == method && IsCall(u.Op) && u.Operand is MethodBase).Select(u => (MethodBase)u.Operand).ToList();

    // type.method calls each of these, in this order (other calls may come between).
    private static void ExpectCalls(List<Use> uses, Type type, string method, params (Type Type, string Name)[] calls)
    {
        var made = uses.Where(u => u.Method.DeclaringType == type && u.Method.Name == method && IsCall(u.Op) && u.Operand is MethodBase)
            .Select(u => (MethodBase)u.Operand).ToList();
        int at = -1;
        foreach (var (callee, name) in calls)
        {
            int next = made.FindIndex(at + 1, m => m.DeclaringType == callee && m.Name == name);
            if (next < 0)
                throw new Exception($"{type.Name}.{method} does not call {callee.Name}.{name}" +
                    (at >= 0 ? $" after {made[at].DeclaringType.Name}.{made[at].Name}" : ""));
            at = next;
        }
    }

    // The methods that bind this type as a singleton: containerDefinition.Bind<T>().
    private static IEnumerable<MethodBase> BindersOf(List<Use> uses, Type bound) =>
        uses.Where(u => IsCall(u.Op) && u.Operand is MethodInfo m && m.Name == "Bind" && m.IsGenericMethod &&
            m.GetGenericArguments()[0] == bound).Select(u => u.Method).Distinct();

    private static void Expect(IEnumerable<string> actual, params string[] expected)
    {
        var got = actual.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var want = expected.OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (!got.SequenceEqual(want))
            throw new Exception($"Expected only [{string.Join(", ", want)}], found [{string.Join(", ", got)}]");
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    private static List<Use> Scan(Assembly assembly)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
        var uses = new List<Use>();
        foreach (var type in Types(assembly))
        foreach (MethodBase method in type.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                     .Concat(type.GetConstructors(All | BindingFlags.DeclaredOnly)))
        {
            byte[] body;
            try { body = method.GetMethodBody()?.GetILAsByteArray(); } catch { continue; }
            if (body == null) continue;
            var typeArgs = type.IsGenericType ? type.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int position = 0;
            while (position < body.Length)
            {
                ushort code = body[position++];
                if (code == 0xfe) code = (ushort)(0xfe00 | body[position++]);
                var op = opcodes[code];
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: position += 1; break;
                    case OperandType.InlineVar: position += 2; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: position += 8; break;
                    case OperandType.InlineSwitch: position += 4 + 4 * BitConverter.ToInt32(body, position); break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        int token = BitConverter.ToInt32(body, position);
                        position += 4;
                        object member = null;
                        try { member = method.Module.ResolveMember(token, typeArgs, methodArgs); } catch { }
                        uses.Add(new Use(method, op, member));
                        break;
                    default: position += 4; break;
                }
            }
        }
        return uses;
    }
}
