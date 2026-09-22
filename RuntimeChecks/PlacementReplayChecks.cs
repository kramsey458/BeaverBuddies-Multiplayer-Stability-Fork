using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

// A replayed building placement is checked on every computer (BuildingPlacedEvent.IsPlacementValid) by the game's
// block-object validators. DistrictPreviewsValidator answers from this computer's preview road graph, which holds
// the local player's hovered and dragged tool previews, so it could refuse another player's building on one
// computer only. These checks run the game's own validator on a preview building, with a district service that
// reports what that graph would say, and the mod's prefixes on it the way Harmony runs them (this harness cannot
// install Harmony): highest priority first, and one that returns false skips the rest and the game's check.
internal static class PlacementReplayChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var blockSystem = Assembly.Load("Timberborn.BlockSystem");
        var components = Assembly.Load("Timberborn.BaseComponentSystem");
        var validatorType = Assembly.Load("Timberborn.GameDistrictsUI").GetType("Timberborn.GameDistrictsUI.DistrictPreviewsValidator", true);
        var validatorInterface = blockSystem.GetType("Timberborn.BlockSystem.IBlockObjectValidator", true);
        var blockObjectType = blockSystem.GetType("Timberborn.BlockSystem.BlockObject", true);
        var stateType = blockSystem.GetType("Timberborn.BlockSystem.BlockObjectState", true);
        var districtCenterType = Assembly.Load("Timberborn.GameDistricts").GetType("Timberborn.GameDistricts.DistrictCenter", true);
        var districtServiceType = Assembly.Load("Timberborn.Navigation").GetType("Timberborn.Navigation.IDistrictService", true);
        var locType = Assembly.Load("Timberborn.Localization").GetType("Timberborn.Localization.ILoc", true);
        var replaying = mod.GetType("BeaverBuddies.ReplayService", true).GetProperty("IsReplayingEvents", all);
        var isValid = validatorType.GetMethod("IsValid", all, null, new[] { blockObjectType, typeof(string).MakeByRefType() }, null);
        const string conflictKey = "BuildingTools.DistrictsInConflict";

        // A building that is not a District Center, marked as a preview as IsPlacementValid marks its copy. The
        // validator only reads its state and asks the component cache for a DistrictCenter (it has none).
        object PreviewBuilding()
        {
            object state = RuntimeHelpers.GetUninitializedObject(stateType);
            stateType.GetField("_state", all).SetValue(state, Enum.Parse(stateType.GetNestedType("State", all), "Preview"));
            object indexMap = Activator.CreateInstance(components.GetType("Timberborn.BaseComponentSystem.TypeIndexMap", true), true);
            ((IDictionary)indexMap.GetType().GetField("_typeIndex", all).GetValue(indexMap))[districtCenterType] = null;
            var cacheType = components.GetType("Timberborn.BaseComponentSystem.ComponentCache", true);
            object cache = RuntimeHelpers.GetUninitializedObject(cacheType);
            cacheType.GetField("_typeIndexMap", all).SetValue(cache, indexMap);
            object blockObject = RuntimeHelpers.GetUninitializedObject(blockObjectType);
            blockObjectType.GetField("_blockObjectState", all).SetValue(blockObject, state);
            components.GetType("Timberborn.BaseComponentSystem.BaseComponent", true).GetField("_componentCache", all).SetValue(blockObject, cache);
            if (!(bool)blockObjectType.GetProperty("IsPreview").GetValue(blockObject)) throw new Exception("the test building is not a preview");
            return blockObject;
        }
        (object validator, PreviewDistrictsProxy districts) Validator(bool previewRoadsInConflict)
        {
            var districts = (PreviewDistrictsProxy)DispatchProxy.Create(districtServiceType, typeof(PreviewDistrictsProxy));
            districts.InConflict = previewRoadsInConflict;
            object loc = DispatchProxy.Create(locType, typeof(KeyLocProxy));
            return (Activator.CreateInstance(validatorType, districts, loc), districts);
        }

        List<Type> ModTypes()
        {
            try { return mod.GetTypes().ToList(); }
            catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToList(); }
        }
        // What a patch class patches: [HarmonyPatch(type, method)] on the class or on its methods, or the type on the
        // class and the method name on a method.
        List<(Type type, string method)> HarmonyPatches(MemberInfo member) => member.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch")
            .Select(a => (a.ConstructorArguments.Select(c => c.Value).OfType<Type>().FirstOrDefault(),
                a.ConstructorArguments.Select(c => c.Value).OfType<string>().FirstOrDefault()))
            .ToList();
        IEnumerable<(Type type, string method)> Targets(Type patchClass)
        {
            var outer = HarmonyPatches(patchClass);
            Type classType = outer.Select(t => t.type).FirstOrDefault(t => t != null);
            string className = outer.Select(t => t.method).FirstOrDefault(m => m != null);
            if (classType != null) yield return (classType, className);
            foreach (MethodInfo method in patchClass.GetMethods(all | BindingFlags.DeclaredOnly))
            foreach (var (type, name) in HarmonyPatches(method))
                yield return (type ?? classType, name ?? className);
        }
        int? DeclaredPriority(MemberInfo member) => member.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority")
            .Select(a => (int?)(int)a.ConstructorArguments[0].Value).FirstOrDefault();
        int Priority(MethodInfo prefix) => DeclaredPriority(prefix) ?? DeclaredPriority(prefix.DeclaringType) ?? 400;
        List<MethodInfo> Prefixes() => ModTypes()
            .Where(t => Targets(t).Contains((validatorType, "IsValid")))
            .Select(t => t.GetMethod("Prefix", all))
            .OfType<MethodInfo>()
            .OrderByDescending(Priority)
            .ToList();
        MethodInfo Override() => Prefixes().SingleOrDefault()
            ?? throw new Exception("the mod does not patch DistrictPreviewsValidator.IsValid with exactly one prefix");

        // The prefixes share the call's result and out argument, as Harmony's generated method does.
        (bool valid, string error) Validate(object validator, object blockObject)
        {
            bool result = false;
            string error = null;
            foreach (MethodInfo prefix in Prefixes())
            {
                ParameterInfo[] parameters = prefix.GetParameters();
                object[] args = parameters.Select(p => p.Name switch
                {
                    "__instance" => validator,
                    "__result" => result,
                    "blockObject" => blockObject,
                    "errorMessage" => (object)error,
                    _ => throw new Exception($"Harmony cannot bind the prefix parameter {p.Name}"),
                }).ToArray();
                object runOriginal = prefix.Invoke(null, args);
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].Name == "__result") result = (bool)args[i];
                    if (parameters[i].Name == "errorMessage") error = (string)args[i];
                }
                if (runOriginal is false) return (result, error);
            }
            object[] call = { blockObject, error };
            return ((bool)isValid.Invoke(validator, call), (string)call[1]);
        }
        void WithReplay(bool active, Action run)
        {
            bool previous = (bool)replaying.GetValue(null);
            replaying.SetValue(null, active);
            try { run(); }
            finally { replaying.SetValue(null, previous); }
        }

        test("Placement replay: the game still validates placements with DistrictPreviewsValidator.IsValid(BlockObject, out string)", () =>
        {
            if (isValid == null || isValid.ReturnType != typeof(bool))
                throw new Exception("DistrictPreviewsValidator.IsValid(BlockObject, out string) returning bool is gone");
            if (!validatorInterface.IsAssignableFrom(validatorType)) throw new Exception("DistrictPreviewsValidator is no longer a block-object validator");
            string[] names = isValid.GetParameters().Select(p => p.Name).ToArray();
            if (!names.SequenceEqual(new[] { "blockObject", "errorMessage" })) throw new Exception($"parameters renamed: {string.Join(", ", names)}");
        });

        test("Placement replay: the mod patches DistrictPreviewsValidator.IsValid with a prefix Harmony can bind", () =>
        {
            MethodInfo prefix = Override();
            if (!prefix.IsStatic || prefix.ReturnType != typeof(bool)) throw new Exception("the prefix cannot skip the game's check");
            foreach (ParameterInfo p in prefix.GetParameters())
            {
                Type expected = p.Name switch
                {
                    "__instance" => validatorType,
                    "__result" => typeof(bool).MakeByRefType(),
                    "blockObject" => blockObjectType,
                    "errorMessage" => typeof(string).MakeByRefType(),
                    _ => throw new Exception($"Harmony cannot bind the prefix parameter {p.Name}"),
                };
                if (p.ParameterType != expected) throw new Exception($"the prefix parameter {p.Name} is {p.ParameterType}, not {expected}");
            }
        });

        test("Placement replay: the override lets other mods' prefixes run first (Priority.Last)", () =>
        {
            MethodInfo prefix = Override();
            if (Priority(prefix) != 0) throw new Exception($"the override's priority is {Priority(prefix)}, not Priority.Last (0)");
        });

        test("Placement replay: outside a replay the game still refuses a building while the preview roads join two districts", () =>
            WithReplay(false, () =>
            {
                object building = PreviewBuilding();
                var (refusing, asked) = Validator(previewRoadsInConflict: true);
                var (valid, error) = Validate(refusing, building);
                if (valid || error != KeyLocProxy.Text(conflictKey)) throw new Exception($"the game's check did not refuse the building (valid={valid}, error={error})");
                if (asked.Calls != 1) throw new Exception($"the preview roads were asked {asked.Calls} times, not once");
                var (accepting, _) = Validator(previewRoadsInConflict: false);
                (valid, error) = Validate(accepting, building);
                if (!valid || error != null) throw new Exception($"the game's check refused a building with no conflict (error={error})");
            }));

        // Hovering or dragging a path that would join two districts puts this computer's preview roads in conflict (and
        // a paused guest that plays two of the host's batches in one frame sees them a frame behind the host). Before
        // the fix the replay refused the building here while every other computer placed it.
        test("Placement replay: a replayed building is accepted whatever this computer's preview roads show", () =>
            WithReplay(true, () =>
            {
                foreach (bool inConflict in new[] { true, false })
                {
                    var (validator, districts) = Validator(inConflict);
                    var (valid, error) = Validate(validator, PreviewBuilding());
                    if (!valid) throw new Exception($"the replayed building was refused ({error}) with preview roads in conflict={inConflict}");
                    if (error != null) throw new Exception($"the accepted building carries an error message ({error})");
                    if (districts.Calls != 0) throw new Exception("the replay read this computer's preview roads");
                }
            }));

        test("Placement replay: every other placement check still runs during a replay", () =>
        {
            // Only DistrictPreviewsValidator may be overridden; the blocks, terrain and every other validator the game
            // and other mods bind still judge the replayed building on every computer.
            var guarded = new HashSet<Type> { blockObjectType, blockSystem.GetType("Timberborn.BlockSystem.BlockObjectValidationService", true),
                blockSystem.GetType("Timberborn.BlockSystem.BlockValidator", true) };
            foreach (Type type in ModTypes())
            foreach (var (target, method) in Targets(type))
            {
                if (target == null || target == validatorType) continue;
                if (guarded.Contains(target) && (target != blockObjectType || method == "IsValid")
                    || validatorInterface.IsAssignableFrom(target))
                    throw new Exception($"{type.Name} patches {target.Name}.{method}, a placement check");
            }
        });
    }
}

public class PreviewDistrictsProxy : DispatchProxy
{
    public bool InConflict;
    public int Calls;
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name != "IsPreviewDistrictInConflict") throw new NotSupportedException(targetMethod.Name);
        Calls++;
        return InConflict;
    }
}

public class KeyLocProxy : DispatchProxy
{
    public static string Text(string key) => "loc:" + key;
    protected override object Invoke(MethodInfo targetMethod, object[] args) => Text((string)args[0]);
}
