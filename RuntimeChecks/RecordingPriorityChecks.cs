using System.Reflection;

// A prefix that records a multiplayer action skips the game's method on the computer where the player acted, and lets
// it run while the action is played on every computer. It must run before any other mod's prefix on that method: a
// prefix ahead of it runs at the click, on that computer only, and if it returns false Harmony skips every later
// prefix, so the action is never recorded or sent. So a recording prefix carries [HarmonyPriority(Priority.First)],
// where a prefix that replaces the game's method carries Priority.Last (see StabilityTests/README.md). The recording
// prefixes are found from the compiled mod's instructions, not from a list, so a new one is checked as well: a prefix
// records if it reaches ReplayService.RecordEvent itself, through its lambdas, or through a ReplayEvent's methods
// (ReplayEvent.DoPrefix, DoEntityPrefix and each event's own DoPrefix helper).
internal static class RecordingPriorityChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    // HarmonyLib.Priority.First and HarmonyLib.Priority.Normal (a prefix that names no priority).
    private const int First = 800, Normal = 400;

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var replayEvent = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var types = LoadableTypes(mod);
        var attributePrefixes = types.SelectMany(AttributePrefixes).ToList();
        var prefixes = attributePrefixes.Concat(types.SelectMany(ManualPrefixes)).Distinct().ToList();
        var recording = prefixes.Where(p => p.ReturnType == typeof(bool) && Records(p, replayEvent)).ToList();

        test("Recording prefixes: the scan finds them, and only them", () =>
        {
            // A scan that finds nothing would pass the check below. These must be found: the goods allower's two
            // (MixedStorage's Priority.Last prefixes on the same methods rely on them running first), the automation
            // prefix (applied with harmony.Patch, not an attribute), and three that record in their own way.
            var names = recording.Select(Name).ToHashSet();
            string[] expected =
            {
                "SingleGoodAllowerAllowPatcher.Prefix", "SingleGoodAllowerDisallowPatcher.Prefix", "AutomationEvent.UniversalPrefix",
                // RecordEvent called directly, not through DoPrefix.
                "SpeedChangePatcher.Prefix", "TickerTickOncePatcher.Prefix",
                // Records from the unlock dialog's callback, a lambda inside a lambda.
                "WorkerTypeToggleTryToUnlockPatcher.Prefix",
            };
            var missing = expected.Where(n => !names.Contains(n)).ToList();
            Require(missing.Count == 0, "not found as recording prefixes: " + string.Join(", ", missing));
            // The mod records about 60 kinds of player action.
            Require(recording.Count >= 50, $"only {recording.Count} recording prefixes found");
            // And prefixes that replace the game's method, or pass it through, are not taken for recording ones.
            string[] notRecording =
            {
                "PlantingLeveledCoordinatesPatcher.Prefix", "DistrictPreviewsValidatorReplayPatcher.Prefix",
                "TickableBucketServiceTickUpdatePatcher.Prefix", "SpeedLockPatcher.Prefix", "RelayFragmentRemoveRowPatch.Prefix",
            };
            var wrong = notRecording.Where(names.Contains).ToList();
            Require(wrong.Count == 0, "taken for recording prefixes: " + string.Join(", ", wrong));
            Require(notRecording.All(n => prefixes.Any(p => Name(p) == n)), "a prefix the scan should see and leave out is gone");
        });
        test("Recording prefixes: every prefix that records a multiplayer action is [HarmonyPriority(Priority.First)]", () =>
        {
            var late = recording.Where(p => Priority(p) != First).Select(p => $"{Name(p)} ({Priority(p)})").OrderBy(n => n).ToList();
            Require(late.Count == 0, $"{late.Count} of {recording.Count} recording prefixes do not run first: " + string.Join(", ", late));
        });
        test("Recording prefixes: no other prefix of the mod patches the same method", () =>
        {
            // Two prefixes of the mod on one method run in the order of their priorities, and an equal priority
            // leaves it to the order the classes are patched in. None shares a method with a recording prefix today;
            // one that does must be given an order on purpose. Overloads count as one method here, and the
            // automation prefix's methods (a list in AutomationEvent.ApplyAutomationPatches) are not seen.
            var clashes = recording
                .Select(p => (prefix: p, target: Target(p)))
                .Where(r => r.target != null)
                .SelectMany(r => attributePrefixes.Where(o => o != r.prefix && Target(o) == r.target)
                    .Select(o => $"{Name(r.prefix)} and {Name(o)} on {r.target}"))
                .ToList();
            Require(clashes.Count == 0, "give these an order: " + string.Join("; ", clashes));
        });
    }

    private static string Name(MethodInfo method) => method.DeclaringType.Name + "." + method.Name;

    private static List<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes().ToList(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToList(); }
    }

    private static IEnumerable<CustomAttributeData> HarmonyAttributes(MemberInfo member, string name) =>
        member.GetCustomAttributesData().Where(a => a.AttributeType.FullName == "HarmonyLib." + name);

    // The prefixes harmony.PatchAll finds: in a [HarmonyPatch] class, the method named Prefix or marked [HarmonyPrefix].
    private static IEnumerable<MethodInfo> AttributePrefixes(Type type)
    {
        var methods = type.GetMethods(All);
        if (!HarmonyAttributes(type, "HarmonyPatch").Any() && !methods.Any(m => HarmonyAttributes(m, "HarmonyPatch").Any()))
            return Enumerable.Empty<MethodInfo>();
        return methods.Where(m => m.Name == "Prefix" || HarmonyAttributes(m, "HarmonyPrefix").Any());
    }

    // Prefixes applied with harmony.Patch: a method that calls Harmony.Patch and names (nameof) a method of its own type.
    private static IEnumerable<MethodInfo> ManualPrefixes(Type type)
    {
        foreach (var method in type.GetMethods(All))
        {
            List<IlScan.Instruction> instructions;
            try { instructions = method.GetMethodBody() == null ? new() : IlScan.Instructions(method); }
            catch (Exception) { continue; }
            if (!instructions.Any(i => i.Calls && i.Is("HarmonyLib.Harmony", "Patch"))) continue;
            foreach (string name in instructions.Where(i => i.Text != null).Select(i => i.Text).Distinct())
                foreach (var named in type.GetMethods(All).Where(m => m.Name == name && m.IsStatic))
                    yield return named;
        }
    }

    private static bool Records(MethodInfo prefix, Type replayEvent)
    {
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        queue.Enqueue(prefix);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!seen.Add(method)) continue;
            List<MethodBase> called;
            try { called = method.GetMethodBody() == null ? new() : IlScan.Members(method).OfType<MethodBase>().ToList(); }
            catch (Exception) { continue; }
            foreach (var next in called)
            {
                if (next.DeclaringType?.FullName == "BeaverBuddies.ReplayService" && next.Name == "RecordEvent") return true;
                // Followed: the prefix's lambdas and local functions (called, or passed on as delegates), and the events'
                // own helpers. Not followed: anything else the prefix calls, such as the ReplayService's tick.
                bool generated = next.Name.Contains('<') || (next.DeclaringType?.Name.Contains('<') ?? false);
                bool eventMethod = next.DeclaringType != null && replayEvent.IsAssignableFrom(next.DeclaringType);
                if (next.Module == prefix.Module && (generated || eventMethod)) queue.Enqueue(next);
            }
        }
        return false;
    }

    // The priority Harmony gives a prefix: its own [HarmonyPriority], else its class's, else Priority.Normal.
    private static int Priority(MethodInfo prefix)
    {
        int? Declared(MemberInfo member) => HarmonyAttributes(member, "HarmonyPriority")
            .Select(a => (int?)(int)a.ConstructorArguments[0].Value).FirstOrDefault();
        return Declared(prefix) ?? Declared(prefix.DeclaringType) ?? Normal;
    }

    // The game method a [HarmonyPatch] prefix patches, as "Type.Method" plus its method type (a property's getter or
    // setter). The prefix's own attributes win over its class's, as in Harmony. Null for a prefix applied at run time.
    private static string Target(MethodInfo prefix)
    {
        Type type = null;
        string name = null, kind = null;
        foreach (var attribute in HarmonyAttributes(prefix, "HarmonyPatch").Concat(HarmonyAttributes(prefix.DeclaringType, "HarmonyPatch")))
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is Type t) type ??= t;
                else if (argument.Value is string s) name ??= s;
                else if (argument.ArgumentType.FullName == "HarmonyLib.MethodType") kind ??= argument.Value.ToString();
            }
        return type == null || name == null ? null : $"{type.FullName}.{name}{(kind == null ? "" : " " + kind)}";
    }
}
