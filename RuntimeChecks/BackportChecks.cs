#nullable enable
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

// 1.1.15's fixes, taken from Timber Together (whose shared-colony code is this fork's), against the compiled mod
// and the installed game's assemblies: the panel controls that changed one computer's game are shared, an unlock is
// checked when it is played, detailed-logging traces stay bounded, and the Steam callbacks let go of the scene they were
// made in. (Joining closing before tick 1 is sent: JoinClosingChecks. A guest leaving over an action it cannot read:
// UnreadableFrameChecks.)
internal static class BackportChecks
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        Type Game(string assembly, string type) => Assembly.Load(assembly).GetType(type, true)!;
        MethodInfo Only(Type type, string name) =>
            type.GetMethods(All).SingleOrDefault(m => m.Name == name) ?? throw new Exception($"{type.FullName}.{name} is gone");
        Type Mod(string name) => mod.GetType(name, false) ?? throw new Exception(name + " is missing");
        List<IlScan.Instruction> Code(MethodBase method) => IlScan.Instructions(method);
        bool Calls(MethodBase method, string declaringType, string name) =>
            Code(method).Any(i => i.Calls && i.Is(declaringType, name));
        int CallAt(List<IlScan.Instruction> code, string declaringType, string name) =>
            code.FindIndex(i => i.Calls && i.Is(declaringType, name));

        // Plugin.Log* would otherwise reach Unity's native logger.
        FieldInfo pluginLogger = Mod("BeaverBuddies.Plugin").GetField("logger", All)!;
        void Quietly(Action run)
        {
            object? previous = pluginLogger.GetValue(null);
            pluginLogger.SetValue(null, DispatchProxy.Create(Mod("BeaverBuddies.Util.Logging.ILogger"), typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previous); }
        }

        // AutomationEvent's list of shared game methods: the (typeof(T), "Name") pairs in ApplyAutomationPatches.
        List<(Type Type, string Name)> AutomationList()
        {
            MethodInfo apply = Only(Mod("BeaverBuddies.Events.AutomationEvent"), "ApplyAutomationPatches");
            byte[] body = apply.GetMethodBody()!.GetILAsByteArray()!;
            var code = Code(apply);
            var pairs = new List<(Type, string)>();
            for (int n = 0; n < code.Count; n++)
            {
                if (code[n].Op != OpCodes.Ldtoken) continue;
                Type? type = null;
                try { type = apply.Module.ResolveType(BitConverter.ToInt32(body, code[n].Offset + 1)); } catch { }
                if (type == null || type.Assembly == mod) continue;
                string? name = code.Skip(n + 1).Take(3).FirstOrDefault(i => i.Text != null)?.Text;
                if (name != null) pairs.Add((type, name));
            }
            if (pairs.Count < 60) throw new Exception($"only {pairs.Count} shared automation methods were found; the search is broken");
            return pairs;
        }

        // ---- The panel controls that changed one computer's game ----

        var panelControls = new (string Assembly, string Panel, string Method, string Setter, string Name)[]
        {
            ("Timberborn.WaterBuildingsUI", "Timberborn.WaterBuildingsUI.WaterMoverFragment", "SetFlowRate", "Timberborn.WaterBuildings.WaterMover", "SetFlowRate"),
            ("Timberborn.WaterBuildingsUI", "Timberborn.WaterBuildingsUI.ThrottlingValveFragment", "SetOutflowLimit", "Timberborn.WaterBuildings.ThrottlingValve", "SetOutflowLimitEnabledAndSynchronize"),
            ("Timberborn.PowerGenerationUI", "Timberborn.PowerGenerationUI.AdjustableStrengthPowerGeneratorFragment", "ChangeValue", "Timberborn.PowerGeneration.AdjustableStrengthPowerGenerator", "set_GeneratorStrength"),
            ("Timberborn.PowerGenerationUI", "Timberborn.PowerGenerationUI.AdjustableStrengthPowerGeneratorFragment", "FlipRotation", "Timberborn.PowerGeneration.AdjustableStrengthPowerGenerator", "FlipRotation"),
        };

        test("Panel controls: the pumps' flow rate, the throttling valve's limit toggle and the dev generator's strength and flip are shared", () =>
        {
            var shared = AutomationList().Select(p => p.Type.FullName + "." + p.Name).ToHashSet();
            foreach (var c in panelControls)
            {
                MethodInfo panel = Only(Game(c.Assembly, c.Panel), c.Method);
                if (!Calls(panel, c.Setter, c.Name)) throw new Exception($"the game's {c.Panel}.{c.Method} no longer calls {c.Setter}.{c.Name}");
                if (!shared.Contains(c.Setter + "." + c.Name)) throw new Exception($"{c.Setter}.{c.Name} is not shared: it changes the dragging player's game alone");
            }
            // What they change is simulation: the pump moves EffectiveFlowRate's worth of water every tick, and the valve
            // limits the flow only while OutflowLimitEnabled is on.
            Type mover = Game("Timberborn.WaterBuildings", "Timberborn.WaterBuildings.WaterMover");
            if (!Code(Only(mover, "Tick")).Any(i => i.Calls && i.Member?.Name == "get_EffectiveFlowRate")) throw new Exception("WaterMover.Tick no longer moves EffectiveFlowRate");
            Type valve = Game("Timberborn.WaterBuildings", "Timberborn.WaterBuildings.ThrottlingValve");
            if (!Code(Only(valve, "GetTargetOutflowLimit")).Any(i => i.Calls && i.Member?.Name == "get_OutflowLimitEnabled")) throw new Exception("the valve's limit no longer depends on OutflowLimitEnabled");
            // The dev generator is placed with dev mode, but its panel shows without it.
            Type fragment = Game("Timberborn.PowerGenerationUI", "Timberborn.PowerGenerationUI.AdjustableStrengthPowerGeneratorFragment");
            if (fragment.GetFields(All).Any(f => f.FieldType.Name == "DevModeManager")) throw new Exception("the dev generator's panel now asks for dev mode (no longer needs sharing)");
        });

        test("Panel controls: every method AutomationEvent shares is found in the game, once, on a component", () =>
        {
            // ApplyAutomationPatches patches each without a null check: one the game renamed would stop the mod's start.
            Type component = Game("Timberborn.BaseComponentSystem", "Timberborn.BaseComponentSystem.BaseComponent");
            var problems = new List<string>();
            foreach (var (type, name) in AutomationList())
            {
                try
                {
                    if (type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null) problems.Add($"{type.FullName}.{name} is gone");
                }
                catch (AmbiguousMatchException) { problems.Add($"{type.FullName}.{name} has overloads"); }
                if (!component.IsAssignableFrom(type)) problems.Add($"{type.FullName} is not a component");
            }
            if (problems.Count > 0) throw new Exception(string.Join("; ", problems));
        });

        test("Panel controls: the newly shared methods are called by the game's panels, never by its simulation", () =>
        {
            // A shared method's call from the simulation would be taken for a click on every computer. The dev generator
            // sets its own strength as it is created: a placement is played as its own action, and a load records nothing.
            string managed = Path.GetDirectoryName(Assembly.Load("Timberborn.WaterBuildings").Location)!;
            var targets = panelControls.Select(c => c.Setter + "." + c.Name).ToHashSet();
            var owners = panelControls.Select(c => c.Setter.Substring(0, c.Setter.LastIndexOf('.'))).ToHashSet();
            var found = new HashSet<string>();
            var problems = new List<string>();
            int scanned = 0;
            foreach (string file in Directory.GetFiles(managed, "Timberborn.*.dll"))
            {
                Assembly assembly;
                try { assembly = Assembly.Load(Path.GetFileNameWithoutExtension(file)); } catch { continue; }
                // Only an assembly that is, or refers to, one of the setters' can call them.
                if (!owners.Contains(assembly.GetName().Name!) && !assembly.GetReferencedAssemblies().Any(r => owners.Contains(r.Name!))) continue;
                bool ui = assembly.GetName().Name!.EndsWith("UI");
                Type[] types;
                try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                foreach (Type type in types)
                foreach (MethodBase method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    if (method.GetMethodBody() == null) continue;
                    scanned++;
                    List<IlScan.Instruction> code;
                    try { code = Code(method); } catch { continue; }
                    foreach (var i in code.Where(i => i.Calls && i.Member?.DeclaringType != null))
                    {
                        string called = i.Member!.DeclaringType!.FullName + "." + i.Member.Name;
                        if (!targets.Contains(called)) continue;
                        found.Add(called);
                        bool created = method.Name == "InitializeEntity" && type == i.Member.DeclaringType;
                        if (!ui && !created) problems.Add($"{type.FullName}.{method.Name} calls {called}");
                    }
                }
            }
            if (scanned < 500) throw new Exception($"only {scanned} game methods were read; the search is broken");
            if (!targets.SetEquals(found)) throw new Exception("no caller found for " + string.Join(", ", targets.Except(found)));
            if (problems.Count > 0) throw new Exception("the game's simulation calls a shared panel method: " + string.Join("; ", problems));
        });

        // ---- Unlocks, checked when they are played ----

        test("Unlocks: the game pays again for a building already unlocked; the replay skips it, and one it can no longer afford", () =>
        {
            Type unlocking = Game("Timberborn.ScienceSystem", "Timberborn.ScienceSystem.BuildingUnlockingService");
            var unlock = IlScan.Members(Only(unlocking, "Unlock"));
            if (unlock.Any(m => m.Name == "Unlocked") || !unlock.Any(m => m.Name == "SubtractPoints"))
                throw new Exception("the game's BuildingUnlockingService.Unlock changed: it now asks whether the building is unlocked, or no longer pays");
            var replay = Code(Only(Mod("BeaverBuddies.Events.BuildingUnlockedEvent"), "Replay"));
            // The profile-remembered buildings (UnlockableOnceSpec: the HTTP Lever and Adapter) may differ between players.
            bool once = replay.Any(i => i.Calls && i.Member is MethodInfo m && m.Name == "HasSpec" && m.IsGenericMethod
                && m.GetGenericArguments()[0].Name == "UnlockableOnceSpec");
            int unlocked = CallAt(replay, unlocking.FullName!, "Unlocked");
            int affordable = CallAt(replay, unlocking.FullName!, "Unlockable");
            int pay = CallAt(replay, unlocking.FullName!, "Unlock");
            if (!once || unlocked < 0) throw new Exception("the replay does not skip a building already unlocked (all but UnlockableOnceSpec)");
            if (pay < 0) throw new Exception("the replay no longer unlocks");
            if (affordable < 0 || !(unlocked < affordable && affordable < pay))
                throw new Exception($"the replay does not ask whether the science is still there before paying (unlocked {unlocked}, affordable {affordable}, pay {pay})");
        });

        test("Unlocks: a bot unlock the science no longer covers is skipped when played, not thrown", () =>
        {
            var replay = Code(Only(Mod("BeaverBuddies.Events.WorkerTypeUnlockedEvent"), "Replay"));
            int pay = replay.FindIndex(i => i.Calls && i.Member?.Name == "Unlock");
            if (pay < 0) throw new Exception("the replay no longer unlocks");
            Type service = replay[pay].Member!.DeclaringType!;
            if (!IlScan.Members(Only(service, "Unlock")).Any(m => m.Name == "SubtractPoints"))
                throw new Exception($"the game's {service.FullName}.Unlock no longer pays");
            int unlocked = CallAt(replay, service.FullName!, "Unlocked");
            int affordable = CallAt(replay, service.FullName!, "Unlockable");
            if (unlocked < 0 || affordable < 0 || !(unlocked < affordable && affordable < pay))
                throw new Exception($"the replay does not ask whether it is unlocked, then affordable, before paying (unlocked {unlocked}, affordable {affordable}, pay {pay})");
        });

        // ---- Detailed-logging traces ----

        test("Traces: detailed-logging traces stay bounded: a guest whose host logs nothing, logging switched on mid-session, and none outside a session", () => Quietly(() =>
        {
            Type service = Mod("BeaverBuddies.DesyncDetecter.DesyncDetecterService");
            PropertyInfo debug = Mod("BeaverBuddies.Settings").GetProperty("TemporarilyDebug", All)!;
            var traces = (IList)service.GetField("traces", All)!.GetValue(null)!;
            MethodInfo startTick = Only(service, "StartTick"), trace = Only(service, "Trace");
            object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(service);
            void Reset() => Only(service, "Reset").Invoke(instance, null);
            void Log(string text) => trace.Invoke(null, new object[] { text, true, true });
            debug.SetValue(null, true);
            try
            {
                using (ScopeChecks.Multiplayer(mod))
                {
                    // A guest's ticks, with nothing from the host to check them against.
                    Reset();
                    for (int tick = 0; tick < 1000; tick++) { startTick.Invoke(null, new object[] { tick }); Log("a draw"); }
                    if (traces.Count > 200) throw new Exception($"a guest kept {traces.Count} ticks of traces with nothing to check them against");
                    // Logging switched on at tick 20,000 of a session that had it off (the ticks were never counted).
                    Reset();
                    startTick.Invoke(null, new object[] { 20000 });
                    if (traces.Count > 200) throw new Exception($"switching logging on at tick 20,000 made {traces.Count} ticks of traces at once");
                }
                // Single player, or a session ended by a desync: nobody will check them.
                Reset();
                int before = traces.Count > 0 ? ((IList)traces[traces.Count - 1]!).Count : 0;
                for (int i = 0; i < 100; i++) Log("a draw");
                int after = traces.Count > 0 ? ((IList)traces[traces.Count - 1]!).Count : 0;
                if (after != before) throw new Exception($"{after - before} traces kept outside a session");
            }
            finally { debug.SetValue(null, false); Reset(); }
        }));

        test("Traces: switching detailed logging on mid-session never reads the other player's earlier traces as a desync", () => Quietly(() =>
        {
            Type service = Mod("BeaverBuddies.DesyncDetecter.DesyncDetecterService");
            PropertyInfo debug = Mod("BeaverBuddies.Settings").GetProperty("TemporarilyDebug", All)!;
            object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(service);
            void Reset() => Only(service, "Reset").Invoke(instance, null);
            debug.SetValue(null, true);
            try
            {
                using var session = ScopeChecks.Multiplayer(mod);
                // This computer turns logging on at tick 2,000 (its ticks were not counted while it was off)...
                Reset();
                Only(service, "StartTick").Invoke(null, new object[] { 2000 });
                // ...and the other player, logging all along, sends its traces of tick 1,999. Making a tick of traces
                // reading "Tick 2000 started" for every tick played, and comparing that with them, stopped the session.
                Type traceType = Mod("BeaverBuddies.DesyncDetecter.Trace");
                var theirs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(traceType))!;
                object line = Activator.CreateInstance(traceType)!;
                traceType.GetField("message")!.SetValue(line, "Tick 1999 started");
                theirs.Add(line);
                if (!(bool)Only(service, "VerifyTraces").Invoke(null, new object[] { 1999, theirs })!)
                    throw new Exception("the other player's traces of a tick before logging was on here were called a desync");
            }
            finally { debug.SetValue(null, false); Reset(); }
        }));

        // ---- Steam ----

        test("Steam: the disposed callbacks are let go, not kept with the scene they were made in", () =>
        {
            // Only the Steam build has the callbacks (IS_STEAM; the other build keeps an empty service).
            Type? steam = mod.GetType("BeaverBuddies.Steam.SteamOverlayConnectionService", false);
            if (steam?.GetField("callbacks", All) == null) return;
            var update = Code(Only(steam, "UpdateSingleton"));
            bool OnList(IlScan.Instruction i, string name) => i.Calls && i.Member?.Name == name && i.Member.DeclaringType?.Name.StartsWith("List") == true;
            int dispose = update.FindIndex(i => i.Calls && i.Is("System.IDisposable", "Dispose"));
            int clear = update.FindIndex(i => OnList(i, "Clear"));
            int add = update.FindIndex(i => OnList(i, "Add"));
            if (clear < 0) throw new Exception("the disposed Steam callbacks stay in the static list, each holding its scene");
            if (!(dispose >= 0 && dispose < clear && clear < add))
                throw new Exception($"the callbacks are not disposed, let go, then made again, in that order (dispose {dispose}, clear {clear}, add {add})");
        });
    }
}
