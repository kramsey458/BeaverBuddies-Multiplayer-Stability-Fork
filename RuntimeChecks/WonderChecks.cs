using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// SF8. Every Wonder's activation animation, and the Iron Teeth Earth Repopulator's plane launch
// (animation, catapult, runway, launcher rotation), are simulation that the game advances on
// render-frame time. In multiplayer the mod runs them once per tick instead, on the tick interval
// (BeaverBuddies/Doc/WonderTiming.md). Harmony is not installed here: these checks decode the
// installed game's IL, run the mod's transpilers on it, and call the mod's prefixes, postfix and
// tick step the way Harmony and the game would. Unity's native calls in the cloned game methods
// (curves, transforms, the frame clock) are replaced by the stand-ins below; everything else is
// the game's own code.
internal static class WonderChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    // TickTimeSpec.TickIntervalInSeconds in the installed game's blueprints.
    private const float TickSeconds = 0.6f;
    // A player capped at 10 FPS, a typical 30 and a fast 144.
    private static readonly int[] FrameRates = {10, 30, 144};

    public static float FrameDuration;
    public static float FrameDelta() => FrameDuration;
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
    private static Type controllerType, wonderType, animatorType, catapultType, rotatorType, planeType;

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
                if (prefixes.Count == 0) throw new Exception($"{type.Name}.{name} has no multiplayer gate");
                foreach (var prefix in prefixes)
                {
                    if (prefix.ReturnType != typeof(bool)) throw new Exception($"{type.Name}.{name}'s prefix cannot skip the frame update");
                    if (!HasLastPriority(prefix)) throw new Exception($"{type.Name}.{name}'s prefix replaces the original without [HarmonyPriority(Priority.Last)]");
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
                    bool runs = RunPrefixes(type, name, instance, 0.02f);
                    if (runs != inMultiplayer)
                        throw new Exception($"In multiplayer, {type.Name}.{name} {(runs ? "still runs" : "was skipped")} outside the tick");
                }
                var io = EventField();
                object session = io.GetValue(null);
                io.SetValue(null, null);
                try
                {
                    foreach (var (type, name, instance, _) in gates)
                        if (!RunPrefixes(type, name, instance, 0.02f))
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

        const float animationLength = 4.19f;
        test("Real Wonder animation finishes on a different tick at 10, 30 and 144 FPS before the timing fix", () =>
        {
            var ticks = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, fixedStep: false).Tick).ToList();
            Console.WriteLine($"  Animation of {animationLength}s ends on tick: " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {ticks[i]}")));
            if (ticks.Distinct().Count() == 1) throw new Exception("Frame-rate dependence was not reproduced");
        });

        test("With the timing fix, the Wonder animation ends on the same tick at every frame rate, with the same saved time", () =>
        {
            var runs = FrameRates.Select(fps => AnimationFinishTick(fps, animationLength, fixedStep: true)).ToList();
            Console.WriteLine($"  Animation of {animationLength}s ends on tick: " + string.Join(", ", FrameRates.Select((f, i) => $"{f} FPS -> {runs[i].Tick}")));
            if (runs.Select(r => r.Tick).Distinct().Count() != 1 || runs.Select(r => r.TimeBits).Distinct().Count() != 1)
                throw new Exception("The animation's end or its saved time still depends on the frame rate");
            // 7 ticks of 0.6 s are the first to reach 4.19 s.
            if (runs[0].Tick != 7) throw new Exception($"Ended on tick {runs[0].Tick}, expected 7");
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

    // A Wonder's animation of the given length, just started. Per frame the game advances every
    // animator (AnimatorRegistry.UpdateSingleton) and then the Wonder's controller checks for the
    // end (BaseComponentUpdateUnityAdapter.Update).
    private static AnimationRun AnimationFinishTick(int fps, float length, bool fixedStep)
    {
        AnimationRun result = default;
        Action run = () =>
        {
            var animator = NewAnimation(length, out object controller);
            int ticks = 0, finished = -1;
            controllerType.GetEvent("StartAnimationFinished").AddEventHandler(controller, new EventHandler((_, _) => finished = ticks));
            if (fixedStep) RunStartPostfix(controller);
            var update = animatorType.GetMethod("UpdateAnimation", All);
            var check = controllerType.GetMethod("Update", All);
            var stepWonder = fixedStep ? Timing().GetMethod("StepWonder", All) : null;
            RunFrames(fps, () => finished >= 0, () =>
            {
                ticks++;
                if (fixedStep) RunTick(() => stepWonder.Invoke(null, new[] {controller, null, null}));
            }, () =>
            {
                if (!fixedStep || RunPrefixes(animatorType, "UpdateAnimation", animator, FrameDuration)) update.Invoke(animator, new object[] {FrameDuration});
                if (!fixedStep || RunPrefixes(controllerType, "Update", controller, FrameDuration)) check.Invoke(controller, null);
            });
            result = new AnimationRun(finished, BitConverter.SingleToInt32Bits((float)animatorType.GetProperty("Time").GetValue(animator)));
        };
        if (fixedStep) WithMultiplayer(run); else run();
        return result;
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
            rotatorType.GetEvent("RotationFinished").AddEventHandler(rotator, new EventHandler((_, _) => finished = ticks));
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
                if (!fixedStep || RunPrefixes(rotatorType, "Update", rotator, FrameDuration)) step();
            });
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
            catapultType.GetEvent("PlaneCatapulted").AddEventHandler(catapult, new EventHandler((_, _) => finished = ticks));
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
                if (!Disabled && (!fixedStep || RunPrefixes(catapultType, "Update", catapult, FrameDuration))) catapultUpdate.Invoke(null, new[] {catapult});
                if (!fixedStep || RunPrefixes(planeType, "Update", plane, FrameDuration)) planeUpdate.Invoke(null, new[] {plane});
            });
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
                tick();
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
        try { action(); }
        finally { reset?.Invoke(null, null); io.SetValue(null, prior); }
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

    // Harmony's prefix semantics: the original runs only if every bool prefix returns true.
    private static bool RunPrefixes(Type type, string name, object instance, float deltaTime)
    {
        bool runs = true;
        foreach (var prefix in Patches(type, name, "Prefix"))
        {
            object result = Invoke(prefix, instance, deltaTime);
            if (result is bool b && !b) runs = false;
        }
        return runs;
    }

    private static object Invoke(MethodInfo patch, object instance, float? deltaTime)
    {
        var args = patch.GetParameters().Select(p => p.Name switch
        {
            "__instance" => instance,
            "deltaTime" => (object)deltaTime,
            _ => throw new Exception($"Unexpected patch parameter {p.Name} on {patch.DeclaringType.Name}"),
        }).ToArray();
        try { return patch.Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
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
            var patch = type.GetMethod(kind, All | BindingFlags.DeclaredOnly);
            if (patch != null) result.Add(patch);
        }
        return result;
    }

    private static IList<CustomAttributeData> Attributes(Type type)
    {
        try { return type.GetCustomAttributesData(); }
        catch (Exception) { return Array.Empty<CustomAttributeData>(); }
    }

    private static bool HasLastPriority(MethodInfo patch) => patch.GetCustomAttributesData().Any(a =>
        a.AttributeType.FullName == "HarmonyLib.HarmonyPriority" && a.ConstructorArguments.Count == 1 &&
        Convert.ToInt32(a.ConstructorArguments[0].Value) == 0);

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
