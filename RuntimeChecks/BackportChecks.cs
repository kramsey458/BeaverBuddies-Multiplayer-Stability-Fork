#nullable enable
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

// 1.1.15's fixes, taken from BeaverBuddies MultiColony (whose shared-colony code is this fork's), against the compiled mod
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
    }
}
