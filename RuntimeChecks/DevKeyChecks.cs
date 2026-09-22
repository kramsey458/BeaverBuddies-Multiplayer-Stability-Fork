using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

// Dev mode puts two keys on Ctrl that the game reads where the world changes, not where the player clicks: "place
// finished" inside BuildingPlacer.Place, and "don't recover goods" whenever a building is deconstructed. In co-op both
// run on every computer (a replayed placement or deletion), so each computer read its own keyboard. These checks run
// the game's own methods on a keyboard where the key is held, with the compiled mod's prefixes in front of them in
// Harmony's order (this harness cannot install Harmony), in a co-op game and in single player.
internal static class DevKeyChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        var keys = Assembly.Load("Timberborn.KeyBindingSystem");
        var bindingType = keys.GetType("Timberborn.KeyBindingSystem.KeyBinding", true);
        var registryType = keys.GetType("Timberborn.KeyBindingSystem.KeyBindingRegistry", true);
        var blockerType = keys.GetType("Timberborn.KeyBindingSystem.IKeyBindingBlocker", true);
        var inputType = Assembly.Load("Timberborn.InputSystem").GetType("Timberborn.InputSystem.InputService", true);
        var placerType = Assembly.Load("Timberborn.BuildingTools").GetType("Timberborn.BuildingTools.BuildingPlacer", true);
        var specType = Assembly.Load("Timberborn.Buildings").GetType("Timberborn.Buildings.BuildingSpec", true);
        var recovered = Assembly.Load("Timberborn.RecoveredGoodSystem");
        var recoveryType = recovered.GetType("Timberborn.RecoveredGoodSystem.BuildingGoodsRecoveryService", true);
        var spawnerType = recovered.GetType("Timberborn.RecoveredGoodSystem.RecoveredGoodStackSpawner", true);
        var providerType = Assembly.Load("Timberborn.RecoverableGoodSystem").GetType("Timberborn.RecoverableGoodSystem.RecoverableGoodProvider", true);
        var deconstruction = Assembly.Load("Timberborn.DeconstructionSystem");
        var deconstructibleType = deconstruction.GetType("Timberborn.DeconstructionSystem.Deconstructible", true);
        var deconstructedType = deconstruction.GetType("Timberborn.DeconstructionSystem.BuildingDeconstructedEvent", true);
        var components = Assembly.Load("Timberborn.BaseComponentSystem");
        var baseComponentType = components.GetType("Timberborn.BaseComponentSystem.BaseComponent", true);
        var cacheType = components.GetType("Timberborn.BaseComponentSystem.ComponentCache", true);
        var indexMapType = components.GetType("Timberborn.BaseComponentSystem.TypeIndexMap", true);
        var inventorySystem = Assembly.Load("Timberborn.InventorySystem");
        var inventoriesType = inventorySystem.GetType("Timberborn.InventorySystem.Inventories", true);
        var inventoryType = inventorySystem.GetType("Timberborn.InventorySystem.Inventory", true);
        var goods = Assembly.Load("Timberborn.Goods");
        var goodRegistryType = goods.GetType("Timberborn.Goods.GoodRegistry", true);
        var goodAmountType = goods.GetType("Timberborn.Goods.GoodAmount", true);
        var multiplierType = Assembly.Load("Timberborn.RecoverableGoodSystem").GetType("Timberborn.RecoverableGoodSystem.IRecoverableGoodMultiplier", true);
        var readOnlyListType = Assembly.Load("Timberborn.Common").GetType("Timberborn.Common.ReadOnlyList`1", true);
        var vectorType = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Vector3Int", true);
        var eventIo = mod.GetType("BeaverBuddies.IO.EventIO", true);
        var session = eventIo.GetField("instance", All);
        void Require(bool value, string message) { if (!value) throw new Exception(message); }

        // The game's own names for the two keys.
        string placeFinishedKey = (string)placerType.GetField("PlaceFinishedKey", All).GetValue(null);
        string dontRecoverKey = (string)recoveryType.GetField("DontRecoverGoodsKey", All).GetValue(null);
        var shouldBePlacedFinished = placerType.GetMethod("ShouldBePlacedFinished", All);
        var onBuildingDeconstructed = recoveryType.GetMethod("OnBuildingDeconstructed", All);

        // The game's input service on a keyboard where only the named keys are held.
        object Keyboard(params string[] held)
        {
            var registry = Activator.CreateInstance(registryType, DispatchProxy.Create(blockerType, typeof(DevKeyUnblockedProxy)), null, null);
            var byId = (IDictionary)registryType.GetField("_keyBindingsById", All).GetValue(registry);
            foreach (string id in new[] { placeFinishedKey, dontRecoverKey })
            {
                var binding = Activator.CreateInstance(bindingType, id, null, false);
                bindingType.GetField("<IsHeld>k__BackingField", All).SetValue(binding, held.Contains(id));
                byId[id] = binding;
            }
            var input = RuntimeHelpers.GetUninitializedObject(inputType);
            inputType.GetField("_keyBindingRegistry", All).SetValue(input, registry);
            return input;
        }
        T InGame<T>(bool coop, Func<T> run)
        {
            object previous = session.GetValue(null);
            session.SetValue(null, coop ? DispatchProxy.Create(eventIo, typeof(DevKeySessionProxy)) : null);
            try { return run(); }
            finally { session.SetValue(null, previous); }
        }

        bool PlacedFinished(bool coop, bool held, bool specPlaceFinished)
        {
            var placer = RuntimeHelpers.GetUninitializedObject(placerType);
            placerType.GetField("_inputService", All).SetValue(placer, held ? Keyboard(placeFinishedKey) : Keyboard());
            var spec = Activator.CreateInstance(specType);
            specType.GetProperty("PlaceFinished").SetValue(spec, specPlaceFinished);
            return InGame(coop, () => (bool)CallPatched(mod, shouldBePlacedFinished, placer, spec));
        }

        // Deconstructs a building holding 6 logs whose only foundation block is at (4, 5, 6), and returns the goods
        // the game queued to be dropped there as recovered goods.
        int GoodsRecovered(bool coop, bool held)
        {
            var storage = Activator.CreateInstance(goodRegistryType);
            goodRegistryType.GetMethod("Add").Invoke(storage, new[] { Activator.CreateInstance(goodAmountType, "Log", 6) });
            var inventory = RuntimeHelpers.GetUninitializedObject(inventoryType);
            inventoryType.GetField("_storage", All).SetValue(inventory, storage);
            var inventories = RuntimeHelpers.GetUninitializedObject(inventoriesType);
            var inventoryList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(inventoryType));
            inventoryList.Add(inventory);
            inventoriesType.GetField("_inventories", All).SetValue(inventories, inventoryList);
            var provider = RuntimeHelpers.GetUninitializedObject(providerType);
            providerType.GetField("_inventories", All).SetValue(provider, inventories);
            providerType.GetField("_recoverableGoodMultipliers", All).SetValue(provider, Activator.CreateInstance(typeof(List<>).MakeGenericType(multiplierType)));
            // The building's components, as the game caches them: only its RecoverableGoodProvider.
            var componentList = new List<object> { provider };
            var indexMap = Activator.CreateInstance(indexMapType, true);
            indexMapType.GetMethod("CacheType").MakeGenericMethod(providerType)
                .Invoke(indexMap, new[] { ReadOnly(readOnlyListType, typeof(object), componentList) });
            var cache = RuntimeHelpers.GetUninitializedObject(cacheType);
            cacheType.GetField("_components", All).SetValue(cache, componentList);
            cacheType.GetField("_typeIndexMap", All).SetValue(cache, indexMap);
            var deconstructible = RuntimeHelpers.GetUninitializedObject(deconstructibleType);
            baseComponentType.GetField("_componentCache", All).SetValue(deconstructible, cache);
            var coordinates = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vectorType));
            coordinates.Add(Activator.CreateInstance(vectorType, 4, 5, 6));
            var deconstructed = Activator.CreateInstance(deconstructedType, deconstructible, ReadOnly(readOnlyListType, vectorType, coordinates));

            var spawner = Activator.CreateInstance(spawnerType, null, null, null);
            var service = Activator.CreateInstance(recoveryType, null, held ? Keyboard(dontRecoverKey) : Keyboard(), spawner);
            InGame(coop, () => CallPatched(mod, onBuildingDeconstructed, service, deconstructed));
            int amount = 0;
            foreach (DictionaryEntry awaiting in (IDictionary)spawnerType.GetField("_awaitingGoods", All).GetValue(spawner))
                foreach (object good in (IEnumerable)awaiting.Value)
                    amount += (int)goodAmountType.GetProperty("Amount").GetValue(good);
            return amount;
        }

        test("Dev keys: in a co-op game a held 'place finished' key does not finish a placed building", () =>
        {
            Require(!PlacedFinished(coop: true, held: true, specPlaceFinished: false),
                "a held Ctrl made a building finished on this computer only (BuildingPlacer.ShouldBePlacedFinished reads the keyboard)");
            Require(PlacedFinished(coop: true, held: true, specPlaceFinished: true), "a building that is always placed finished was not");
            Require(!PlacedFinished(coop: true, held: false, specPlaceFinished: false), "a building was placed finished with no key held");
        });
        test("Dev keys: in a co-op game a held 'don't recover goods' key does not stop goods recovery", () =>
        {
            int recovered = GoodsRecovered(coop: true, held: true);
            Require(recovered == 6, $"a held Ctrl left {recovered} of 6 logs to recover on this computer only (BuildingGoodsRecoveryService reads the keyboard)");
            Require(GoodsRecovered(coop: true, held: false) == 6, "goods were not recovered with no key held");
        });
        test("Dev keys: single player still reads both keys", () =>
        {
            Require(PlacedFinished(coop: false, held: true, specPlaceFinished: false), "'place finished' no longer works in single player");
            Require(!PlacedFinished(coop: false, held: false, specPlaceFinished: false), "single player placed a building finished with no key held");
            Require(GoodsRecovered(coop: false, held: true) == 0, "'don't recover goods' no longer works in single player");
            Require(GoodsRecovered(coop: false, held: false) == 6, "single player lost recovered goods with no key held");
        });
        test("Dev keys: the prefixes that skip the game's key readers carry Priority.Last", () =>
        {
            foreach (var original in new[] { shouldBePlacedFinished, onBuildingDeconstructed })
            {
                var prefixes = Prefixes(mod, original);
                Require(prefixes.Count > 0, $"the mod does not patch {original.DeclaringType.Name}.{original.Name}");
                foreach (var prefix in prefixes.Where(p => p.ReturnType == typeof(bool)))
                    // HarmonyLib.Priority.Last
                    Require(Priority(prefix) == 0, $"{prefix.DeclaringType.Name}.Prefix can skip the game's method but is not [HarmonyPriority(Priority.Last)]");
            }
        });
        test("Dev keys: every key reader in the placing, demolishing, deconstruction and planting assemblies is reviewed", () =>
        {
            // InputService.IsKeyHeld or IsKeyDown read inside code that runs on every computer during a replay or a tick
            // (placing a building, deconstructing one) reads that computer's keyboard. The two dev keys above did. Every
            // reader in these assemblies, the tools' own input handling included, must be reviewed and either patched
            // or listed below with the reason it is safe or a known local-only dev mode tool.
            var readers = new List<string>();
            foreach (string assemblyName in new[] { "Timberborn.BuildingTools", "Timberborn.RecoveredGoodSystem", "Timberborn.Demolishing",
                "Timberborn.DemolishingUI", "Timberborn.ConstructionSites", "Timberborn.BlockSystem", "Timberborn.EntitySystem",
                "Timberborn.PlantingUI", "Timberborn.Forestry" })
            {
                foreach (Type type in LoadableTypes(Assembly.Load(assemblyName)))
                {
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(All | BindingFlags.DeclaredOnly); }
                    catch (Exception) { continue; }
                    foreach (MethodInfo method in methods)
                    {
                        List<MethodBase> calls;
                        // A method whose body references a native Unity module cannot be decoded here; it is not one of these.
                        try { calls = MethodsCalled(method); }
                        catch (Exception) { continue; }
                        if (calls.Any(m => m.DeclaringType?.Name == "InputService" && (m.Name == "IsKeyHeld" || m.Name == "IsKeyDown")))
                            readers.Add(type.Name + "." + method.Name);
                    }
                }
            }
            // Patched: the two dev keys, not read in a co-op game (Fixes/DevKeysCoopFix).
            var patched = new[] { "BuildingGoodsRecoveryService.OnBuildingDeconstructed", "BuildingPlacer.ShouldBePlacedFinished" };
            var reviewed = patched.Concat(new[]
            {
                // Dev mode's plant spawner. Only the planting tool calls it, on the player's own computer: the replayed
                // planting event marks the area and nothing else, so plants spawned by dev mode exist on that computer
                // alone. A known local-only dev mode tool; the co-op notice (Fixes/DevModeCoopWarning) warns about it.
                "DevModePlantableSpawner.SpawnPlantables",
                // Dev mode's instant unlock (Ctrl-click on a locked building). The building tool calls it on the player's
                // own computer, and it unlocks through BuildingUnlockingService.UnlockIgnoringCost, which this fork does
                // not share (it shares Unlock): a known local-only dev mode tool the co-op notice warns about.
                "BuildingToolLocker.TryToUnlock",
                // The same key on a locked planting tool only lets that player open the tool on their own computer;
                // nothing is unlocked, and the planting it leads to is recorded and played everywhere.
                "PlantableToolLocker.TryToUnlock",
                // The building panel's demolish shortcut: an input processor on the player's own computer. The
                // ChangeDemolishState it calls is recorded and played everywhere (DemolishButtonClickedEvent).
                "DemolishableFragment.ProcessInput",
            });
            var unreviewed = readers.Except(reviewed).ToList();
            Require(unreviewed.Count == 0, "review these key readers: " + string.Join(", ", unreviewed));
            foreach (string needed in patched)
                Require(readers.Contains(needed), "the game no longer reads a key in " + needed + "; the patch in DevKeysCoopFix may be stale");
        });
    }

    private static object ReadOnly(Type openType, Type element, IList list) =>
        Activator.CreateInstance(openType.MakeGenericType(element), All, null, new object[] { list }, null);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    // The mod's Harmony prefixes on a game method, found by their [HarmonyPatch] target, in the order Harmony runs
    // them (highest priority first).
    private static List<MethodInfo> Prefixes(Assembly mod, MethodInfo original)
    {
        bool Targets(CustomAttributeData a) => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"
            && a.ConstructorArguments.Any(arg => Equals(arg.Value, original.DeclaringType))
            && a.ConstructorArguments.Any(arg => Equals(arg.Value, original.Name));
        return LoadableTypes(mod)
            .Where(t => t.GetCustomAttributesData().Any(Targets))
            .Select(t => t.GetMethod("Prefix", All))
            .Where(p => p != null)
            .OrderByDescending(Priority)
            .ToList();
    }

    // HarmonyLib.Priority.Normal unless the prefix says otherwise.
    private static int Priority(MethodInfo prefix) => prefix.GetCustomAttributesData()
        .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority")
        .Select(a => (int)a.ConstructorArguments[0].Value).DefaultIfEmpty(400).First();

    // Calls a game method as it runs with the mod loaded: the mod's prefixes first, then the method itself unless a
    // prefix returned false. Prefix parameters are bound the way Harmony binds them: __instance, a by-ref __result,
    // and the original's parameters by name.
    private static object CallPatched(Assembly mod, MethodInfo original, object instance, params object[] args)
    {
        var parameters = original.GetParameters();
        object result = original.ReturnType == typeof(void) || !original.ReturnType.IsValueType ? null : Activator.CreateInstance(original.ReturnType);
        foreach (var prefix in Prefixes(mod, original))
        {
            var prefixParameters = prefix.GetParameters();
            var values = new object[prefixParameters.Length];
            for (int i = 0; i < prefixParameters.Length; i++)
            {
                string name = prefixParameters[i].Name;
                if (name == "__instance") values[i] = instance;
                else if (name == "__result") values[i] = result;
                else
                {
                    int index = Array.FindIndex(parameters, p => p.Name == name);
                    if (index < 0) throw new Exception($"{prefix.DeclaringType.Name}.Prefix asks for '{name}', which this harness cannot supply");
                    values[i] = args[index];
                }
            }
            object keepGoing = prefix.Invoke(null, values);
            int resultIndex = Array.FindIndex(prefixParameters, p => p.Name == "__result");
            if (resultIndex >= 0) result = values[resultIndex];
            if (keepGoing is false) return result;
        }
        return original.Invoke(instance, args);
    }

    /// <summary>The methods a method calls (call, callvirt), decoded from its IL, in order.</summary>
    private static List<MethodBase> MethodsCalled(MethodBase method)
    {
        var methods = new List<MethodBase>();
        byte[] body = method.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return methods;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
        int position = 0;
        while (position < body.Length)
        {
            ushort code = body[position++];
            if (code == 0xfe) code = (ushort)(0xfe00 | body[position++]);
            OpCode op = opcodes[code];
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: position += 1; break;
                case OperandType.InlineVar: position += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: position += 8; break;
                case OperandType.InlineSwitch: position += 4 + 4 * BitConverter.ToInt32(body, position); break;
                case OperandType.InlineMethod:
                    int token = BitConverter.ToInt32(body, position);
                    if (op == OpCodes.Call || op == OpCodes.Callvirt) methods.Add(method.Module.ResolveMethod(token));
                    position += 4;
                    break;
                default: position += 4; break;
            }
        }
        return methods;
    }
}

public class DevKeyUnblockedProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args) =>
        targetMethod.Name == "IsKeyBlocked" ? false : throw new NotSupportedException(targetMethod.Name);
}

// A co-op session: only its presence matters to the code under test.
public class DevKeySessionProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args) => throw new NotSupportedException(targetMethod.Name);
}
