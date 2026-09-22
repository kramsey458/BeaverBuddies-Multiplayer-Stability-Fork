#nullable enable
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

// Planting marks are levelled by the game's terrain picker, which stops at the layer the local player has sliced the
// view to. The planting event carries the tiles the marker levelled, so no computer levels them again with its own view.
// Runs the game's real levelling (TerrainAreaService, TerrainPicker, GridTraversal) over a small made-up terrain, and the
// mod's own planting prefixes, wire format and replay.
internal static class PlantingLevelChecks
{
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        var unity = Assembly.Load("UnityEngine.CoreModule");
        Type vector3Int = unity.GetType("UnityEngine.Vector3Int", true)!;
        Type vector3 = unity.GetType("UnityEngine.Vector3", true)!;
        Type rayType = unity.GetType("UnityEngine.Ray", true)!;
        Type terrainServiceType = Assembly.Load("Timberborn.TerrainSystem").GetType("Timberborn.TerrainSystem.ITerrainService", true)!;
        Type visibilityType = Assembly.Load("Timberborn.LevelVisibilitySystem").GetType("Timberborn.LevelVisibilitySystem.ILevelVisibilityService", true)!;
        Type mapSizeType = Assembly.Load("Timberborn.MapStateSystem").GetType("Timberborn.MapStateSystem.MapSize", true)!;
        Type traversalType = Assembly.Load("Timberborn.GridTraversing").GetType("Timberborn.GridTraversing.GridTraversal", true)!;
        var querying = Assembly.Load("Timberborn.TerrainQueryingSystem");
        Type pickerType = querying.GetType("Timberborn.TerrainQueryingSystem.TerrainPicker", true)!;
        Type areaServiceType = querying.GetType("Timberborn.TerrainQueryingSystem.TerrainAreaService", true)!;
        Type selectionType = Assembly.Load("Timberborn.PlantingUI").GetType("Timberborn.PlantingUI.PlantingSelectionService", true)!;
        var selectionSystem = Assembly.Load("Timberborn.SelectionSystem");
        Type highlighterType = selectionSystem.GetType("Timberborn.SelectionSystem.Highlighter", true)!;
        Type rollingHighlighterType = selectionSystem.GetType("Timberborn.SelectionSystem.RollingHighlighter", true)!;
        Type areaHighlightingType = selectionSystem.GetType("Timberborn.SelectionSystem.AreaHighlightingService", true)!;
        Type eventType = mod.GetType("BeaverBuddies.Events.PlantingAreaMarkedEvent", true)!;
        Type replayEventType = mod.GetType("BeaverBuddies.Events.ReplayEvent", true)!;
        Type contextType = mod.GetType("BeaverBuddies.Events.IReplayContext", true)!;
        Type replayServiceType = mod.GetType("BeaverBuddies.ReplayService", true)!;
        Type singletonsType = mod.GetType("BeaverBuddies.SingletonManager", true)!;
        Type eventIoType = mod.GetType("BeaverBuddies.IO.EventIO", true)!;
        Type jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true)!;
        Type listType = typeof(List<>).MakeGenericType(vector3Int);
        MethodInfo levelMethod = areaServiceType.GetMethod("InMapLeveledCoordinates")!;
        string unmark = (string)eventType.GetField("UNMARK")!.GetValue(null)!;

        // What the fix adds, looked up by each check: a build without it fails those checks, not the whole run.
        Type OverrideType() => mod.GetType("BeaverBuddies.Events.PlantingLeveledCoordinatesPatcher")
            ?? throw new Exception("nothing replaces the game's levelling while an event is played: each computer levels the area with its own view");
        FieldInfo Recorded() => OverrideType().GetField("Recorded", all) ?? throw new Exception("the levelling override has no recorded tiles");
        FieldInfo Coordinates() => eventType.GetField("coordinates")
            ?? throw new Exception("the event carries no levelled tiles, only the dragged blocks and the camera ray");

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true)!.GetField("logger", all)!;
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true)!;
        object? previousLogger = pluginLogger.GetValue(null);

        object V(int x, int y, int z) => Activator.CreateInstance(vector3Int, x, y, z)!;
        (int x, int y, int z) Xyz(object v) => ((int)vector3Int.GetProperty("x")!.GetValue(v)!,
            (int)vector3Int.GetProperty("y")!.GetValue(v)!, (int)vector3Int.GetProperty("z")!.GetValue(v)!);
        IList NewList(IEnumerable<(int x, int y, int z)> tiles)
        {
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var t in tiles) list.Add(V(t.x, t.y, t.z));
            return list;
        }
        List<(int, int, int)> Tiles(IEnumerable items) => items.Cast<object>().Select(Xyz).ToList();
        string Show(IEnumerable<(int, int, int)> tiles) => "[" + string.Join(" ", tiles) + "]";

        // An 8x8 map: ground at height 3, and a raised block at height 6 where x < 4.
        static int Height(int x, int y) => x < 4 ? 6 : 3;
        var terrain = (TerrainProxy)DispatchProxy.Create(terrainServiceType, typeof(TerrainProxy));
        terrain.Height = Height;

        object MapSize()
        {
            object size = RuntimeHelpers.GetUninitializedObject(mapSizeType);
            mapSizeType.GetProperty("TotalSize")!.GetSetMethod(true)!.Invoke(size, new[] { V(8, 8, 12) });
            return size;
        }
        // The game's levelling as one player's view sees it: sliced to show levels below maxVisibleLevel.
        object AreaService(int maxVisibleLevel)
        {
            var visibility = DispatchProxy.Create(visibilityType, typeof(VisibilityProxy));
            ((VisibilityProxy)visibility).MaxVisibleLevel = maxVisibleLevel;
            object traversal = Activator.CreateInstance(traversalType, MapSize())!;
            object picker = Activator.CreateInstance(pickerType, terrain, traversal, visibility)!;
            return Activator.CreateInstance(areaServiceType, terrain, picker)!;
        }
        // Looking almost straight down on the raised block.
        object ray = Activator.CreateInstance(rayType,
            Activator.CreateInstance(vector3, 2.5f, 2.5f, 20f)!, Activator.CreateInstance(vector3, 0.01f, 0.02f, -1f)!)!;
        List<(int, int, int)> Leveled(object areaService, IList blocks) =>
            Tiles((IEnumerable)levelMethod.Invoke(areaService, new[] { blocks, ray })!);
        // The planting tool's drag: a rectangle on the level of the terrain the ray picked first (AreaPicker).
        IList Dragged(object areaService)
        {
            object picker = areaServiceType.GetField("_terrainPicker", all)!.GetValue(areaService)!;
            object? picked = pickerType.GetMethod("PickTerrainCoordinates", new[] { rayType })!.Invoke(picker, new[] { ray });
            if (picked == null) throw new Exception("the ray picked no terrain");
            int z = Xyz(picked.GetType().GetProperty("Coordinates")!.GetValue(picked)!).z;
            return NewList(from x in Enumerable.Range(1, 5) from y in Enumerable.Range(1, 3) select (x, y, z));
        }

        // The mod's prefixes on the game's levelling, run as Harmony runs them (this harness cannot install Harmony):
        // highest priority first, and one that returns false skips the rest and the game's own levelling.
        List<Type> ModTypes()
        {
            try { return mod.GetTypes().ToList(); }
            catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToList(); }
        }
        bool Targets(CustomAttributeData a, string method) => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"
            && a.ConstructorArguments.Count == 2
            && Equals(a.ConstructorArguments[0].Value, areaServiceType) && Equals(a.ConstructorArguments[1].Value, method);
        int? DeclaredPriority(MemberInfo member) => member.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority")
            .Select(a => (int?)(int)a.ConstructorArguments[0].Value!).FirstOrDefault();
        int Priority(MethodInfo prefix) => DeclaredPriority(prefix) ?? DeclaredPriority(prefix.DeclaringType!) ?? 400;
        List<MethodInfo> LevellingPrefixes() => ModTypes()
            .Where(t => t.GetCustomAttributesData().Any(a => Targets(a, "InMapLeveledCoordinates")))
            .Select(t => t.GetMethod("Prefix", all))
            .OfType<MethodInfo>()
            .OrderByDescending(Priority)
            .ToList();
        List<(int, int, int)> PatchedLevel(object areaService, object? inputBlocks, object ray)
        {
            foreach (MethodInfo prefix in LevellingPrefixes())
            {
                ParameterInfo[] parameters = prefix.GetParameters();
                object?[] args = parameters.Select(p => p.Name switch
                {
                    "__result" => null,
                    "__instance" => areaService,
                    "inputBlocks" => inputBlocks,
                    "ray" => ray,
                    _ => throw new NotSupportedException("a levelling prefix takes " + p.Name),
                }).ToArray();
                if (prefix.Invoke(null, args) is false)
                {
                    int result = Array.FindIndex(parameters, p => p.Name == "__result");
                    if (result < 0) throw new Exception("a levelling prefix skipped the game's levelling and gave no tiles");
                    return Tiles((IEnumerable)args[result]!);
                }
            }
            return Tiles((IEnumerable)levelMethod.Invoke(areaService, new[] { inputBlocks, ray })!);
        }

        // The marking player drags an area: the mod's own MarkArea / UnmarkArea prefix records the event for every
        // computer to play instead of marking now (as a host does, queued for its next tick).
        object Mark(object markerAreaService, IList blocks, string prefab)
        {
            object service = RuntimeHelpers.GetUninitializedObject(selectionType);
            selectionType.GetField("_terrainAreaService", all)!.SetValue(service, markerAreaService);
            string patcher = prefab == unmark ? "PlantingAreaUnmarkedPatcher" : "PlantingAreaMarkedPatcher";
            MethodInfo prefix = mod.GetType("BeaverBuddies.Events." + patcher, true)!.GetMethod("Prefix", all)!;
            object?[] args = prefix.GetParameters().Select(p => p.Name switch
            {
                "__instance" => service,
                "inputBlocks" => blocks,
                "ray" => ray,
                "templateName" => (object?)prefab,
                _ => throw new NotSupportedException("the planting prefix takes " + p.Name),
            }).ToArray();

            object replayService = RuntimeHelpers.GetUninitializedObject(replayServiceType);
            Type queueType = typeof(ConcurrentQueue<>).MakeGenericType(replayEventType);
            object queued = Activator.CreateInstance(queueType)!;
            replayServiceType.GetField("eventsToPlay", all)!.SetValue(replayService, queued);
            replayServiceType.GetField("eventsToSend", all)!.SetValue(replayService, Activator.CreateInstance(queueType));
            var singletons = (IDictionary)singletonsType.GetField("map", all)!.GetValue(null)!;
            FieldInfo isLoaded = replayServiceType.GetField("<IsLoaded>k__BackingField", all)!;
            object? previousService = singletons.Contains(replayServiceType) ? singletons[replayServiceType] : null;
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            singletons[replayServiceType] = replayService;
            isLoaded.SetValue(null, true);
            eventIoType.GetMethod("Set")!.Invoke(null, new[] { DispatchProxy.Create(eventIoType, typeof(HostIoProxy)) });
            try
            {
                if ((bool)prefix.Invoke(null, args)!) throw new Exception("the marking player's game marked at once instead of recording");
                var events = ((IEnumerable)queued).Cast<object>().ToList();
                if (events.Count != 1) throw new Exception($"{events.Count} events were recorded for one drag");
                return events[0];
            }
            finally
            {
                eventIoType.GetMethod("Reset")!.Invoke(null, null);
                isLoaded.SetValue(null, false);
                if (previousService != null) singletons[replayServiceType] = previousService;
                else singletons.Remove(replayServiceType);
                pluginLogger.SetValue(null, previousLogger);
            }
        }
        // What every other computer reads off the network.
        object Received(object replayEvent)
        {
            string json = (string)jsonType.GetMethod("Serialize")!.MakeGenericMethod(replayEventType).Invoke(null, new[] { replayEvent })!;
            return jsonType.GetMethod("Deserialize")!.MakeGenericMethod(replayEventType).Invoke(null, new object[] { json })!;
        }
        // Plays a received event on a computer whose view is sliced at `view`: the event's own Replay, then the game's
        // MarkArea / UnmarkArea, up to the game's levelling of the area. It returns the tiles that levelling gives there
        // with the mod's prefixes on it, and stops the replay at that point. The game is handed the event's own blocks
        // and ray (MarkArea / UnmarkArea(inputBlocks, ray)).
        List<(int, int, int)> Played(object replayEvent, int view)
        {
            object areaService = AreaService(view);
            object context = PlayingContext(areaService);
            List<(int, int, int)>? played = null;
            terrain.Probe = () =>
            {
                played = PatchedLevel(areaService, eventType.GetField("inputBlocks")!.GetValue(replayEvent), eventType.GetField("ray")!.GetValue(replayEvent)!);
                throw new ReachedLevelling();
            };
            try { eventType.GetMethod("Replay")!.Invoke(replayEvent, new object[] { context }); }
            catch (TargetInvocationException e) when (e.InnerException is ReachedLevelling) { }
            finally { terrain.Probe = null; }
            return played ?? throw new Exception("the replay never reached the game's levelling");
        }
        // The planting service of a computer that plays events, levelling with `areaService`.
        object PlayingContext(object areaService)
        {
            object service = RuntimeHelpers.GetUninitializedObject(selectionType);
            selectionType.GetField("_terrainAreaService", all)!.SetValue(service, areaService);
            // UnmarkArea clears the highlighted objects first.
            object highlighting = Activator.CreateInstance(areaHighlightingType,
                Activator.CreateInstance(rollingHighlighterType, Activator.CreateInstance(highlighterType)), null, null)!;
            selectionType.GetField("_areaHighlightingService", all)!.SetValue(service, highlighting);
            var context = (SingletonContextProxy)DispatchProxy.Create(contextType, typeof(SingletonContextProxy));
            context.Singleton = service;
            return context;
        }

        const int FullView = 100, Sliced = 3;

        test("Planting: the game levels the same drag differently in a sliced view (why events carry the tiles)", () =>
        {
            IList blocks = Dragged(AreaService(FullView));
            var full = Leveled(AreaService(FullView), blocks);
            var sliced = Leveled(AreaService(Sliced), blocks);
            if (full.Count == 0) throw new Exception("the full view marked nothing");
            if (full.SequenceEqual(sliced))
                throw new Exception($"both views gave {Show(full)}: the game no longer levels by view; the recorded tiles may be unneeded");
        });

        // Regression: the event carried the dragged blocks and the camera ray, and every computer levelled them again
        // with its own view, so a player with a sliced view marked (or unmarked) other tiles than the marker: a desync.
        foreach (bool unmarking in new[] { false, true })
            test($"Planting: {(unmarking ? "an unmark" : "a mark")} made in one layer view is played on the marker's tiles in another", () =>
            {
                foreach (var (markerView, playerView) in new[] { (FullView, Sliced), (Sliced, FullView) })
                {
                    object marker = AreaService(markerView);
                    IList blocks = Dragged(marker);
                    // What the marking player saw highlighted (HighlightMarkableArea levels the same way).
                    var expected = Leveled(marker, blocks);
                    if (expected.Count == 0) throw new Exception($"view {markerView} marked nothing");
                    object received = Received(Mark(marker, blocks, unmarking ? unmark : "Carrot"));
                    var played = Played(received, playerView);
                    if (!played.SequenceEqual(expected))
                        throw new Exception($"marked in view {markerView} on {Show(expected)}, played in view {playerView} on {Show(played)}");
                    // Outside a replay the game levels as it always did.
                    var own = Leveled(AreaService(playerView), blocks);
                    var after = PatchedLevel(AreaService(playerView), blocks, ray);
                    if (!after.SequenceEqual(own)) throw new Exception($"after the replay the game's levelling gave {Show(after)}, not {Show(own)}");
                }
            });

        test("Planting: the event sends the levelled tiles, not the dragged blocks", () =>
        {
            object marker = AreaService(FullView);
            IList blocks = Dragged(marker);
            object received = Received(Mark(marker, blocks, "Carrot"));
            var coordinates = Tiles((IEnumerable?)Coordinates().GetValue(received) ?? throw new Exception("the tiles were lost in transit"));
            if (!coordinates.SequenceEqual(Leveled(marker, blocks))) throw new Exception($"the event carries {Show(coordinates)}");
            int sent = ((IList?)eventType.GetField("inputBlocks")!.GetValue(received))?.Count ?? 0;
            if (sent != 0) throw new Exception($"the {sent} dragged blocks were sent as well");
            string action = (string)eventType.GetMethod("ToActionString")!.Invoke(received, null)!;
            if (action != $"Planting {coordinates.Count} of Carrot") throw new Exception($"the log line reads \"{action}\"");
        });

        // An event without levelled tiles (as an earlier build sent it: the dragged blocks and the ray) is levelled by the
        // event's Replay from the blocks alone, not with the view of the computer that plays it.
        foreach (int view in new[] { FullView, Sliced })
            test($"Planting: an older event without tiles is played on the tiles the marker's own game levelled (marked in view {view})", () =>
            {
                object marker = AreaService(view);
                IList blocks = Dragged(marker);
                var expected = Leveled(marker, blocks);
                if (expected.Count == 0) throw new Exception($"view {view} marked nothing");
                object older = Activator.CreateInstance(eventType, true)!;
                eventType.GetField("prefabName")!.SetValue(older, "Carrot");
                eventType.GetField("inputBlocks")!.SetValue(older, blocks);
                eventType.GetField("ray")!.SetValue(older, ray);
                int other = view == FullView ? Sliced : FullView;
                var played = Played(Received(older), other);
                if (!played.SequenceEqual(expected))
                    throw new Exception($"marked in view {view} on {Show(expected)}, played in view {other} on {Show(played)}");
            });

        test("Planting: while an event is played, levelling gives the recorded tiles", () =>
        {
            var tiles = NewList(new[] { (1, 2, 6), (5, 5, 3) });
            FieldInfo recorded = Recorded();
            var prefix = OverrideType().GetMethod("Prefix", all)!;
            try
            {
                recorded.SetValue(null, tiles);
                object?[] args = { null };
                if ((bool)prefix.Invoke(null, args)!) throw new Exception("the game's levelling ran");
                if (!Tiles((IEnumerable)args[0]!).SequenceEqual(Tiles(tiles))) throw new Exception("other tiles were given");
                if (ReferenceEquals(args[0], tiles)) throw new Exception("the event's own list was handed to the game");
                recorded.SetValue(null, null);
                args[0] = null;
                if (!(bool)prefix.Invoke(null, args)!) throw new Exception("the game's levelling was skipped outside a replay");
            }
            finally { recorded.SetValue(null, null); }
        });

        test("Planting: the levelling override lets other mods' prefixes run first (Priority.Last)", () =>
        {
            var prefix = OverrideType().GetMethod("Prefix", all)!;
            if (!OverrideType().GetCustomAttributesData().Any(a => Targets(a, "InMapLeveledCoordinates")))
                throw new Exception("the override does not patch TerrainAreaService.InMapLeveledCoordinates");
            if (Priority(prefix) != 0) throw new Exception($"the override's priority is {Priority(prefix)}, not Priority.Last (0)");
        });

        test("Planting: a replay that fails leaves no recorded tiles behind", () =>
        {
            FieldInfo recorded = Recorded();
            object service = RuntimeHelpers.GetUninitializedObject(selectionType);
            // Unset picker: if the game levelled again here it would throw at once.
            selectionType.GetField("_terrainAreaService", all)!.SetValue(service, RuntimeHelpers.GetUninitializedObject(areaServiceType));
            var context = (SingletonContextProxy)DispatchProxy.Create(contextType, typeof(SingletonContextProxy));
            context.Singleton = service;
            object e = Activator.CreateInstance(eventType, true)!;
            eventType.GetField("prefabName")!.SetValue(e, "Carrot");
            eventType.GetField("inputBlocks")!.SetValue(e, NewList(new[] { (1, 1, 2) }));
            Coordinates().SetValue(e, NewList(new[] { (1, 1, 3) }));
            try { eventType.GetMethod("Replay")!.Invoke(e, new object[] { context }); }
            catch (TargetInvocationException) { /* the unset game services: expected here */ }
            if (recorded.GetValue(null) != null) throw new Exception("recorded tiles leaked out of the replay");
        });

        // Every real replay returns normally. Tiles left recorded after it would be what this computer's levelling
        // gives from then on: the tool's highlighting and the next mark it records.
        foreach (bool unmarking in new[] { false, true })
            test($"Planting: {(unmarking ? "an unmark" : "a mark")} that plays to the end leaves no recorded tiles behind", () =>
            {
                FieldInfo recorded = Recorded();
                object e = Activator.CreateInstance(eventType, true)!;
                eventType.GetField("prefabName")!.SetValue(e, unmarking ? unmark : "Carrot");
                eventType.GetField("inputBlocks")!.SetValue(e, NewList(Array.Empty<(int, int, int)>()));
                eventType.GetField("ray")!.SetValue(e, ray);
                Coordinates().SetValue(e, NewList(new[] { (1, 1, 6) }));
                try
                {
                    // Without Harmony the game levels the event's empty blocks itself, finds nothing to act on and returns.
                    eventType.GetMethod("Replay")!.Invoke(e, new object[] { PlayingContext(AreaService(FullView)) });
                    if (recorded.GetValue(null) != null) throw new Exception("recorded tiles stayed set after a replay that finished");
                }
                finally { recorded.SetValue(null, null); }
            });

        test("Planting: the game members the fix relies on are there", () =>
        {
            Type blocks = typeof(IEnumerable<>).MakeGenericType(vector3Int);
            var missing = new List<string>();
            if (levelMethod.ReturnType != blocks || !levelMethod.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { blocks, rayType }))
                missing.Add("TerrainAreaService.InMapLeveledCoordinates(IEnumerable<Vector3Int>, Ray)");
            if (selectionType.GetMethod("MarkArea", new[] { blocks, rayType, typeof(string) }) == null) missing.Add("PlantingSelectionService.MarkArea");
            if (selectionType.GetMethod("UnmarkArea", new[] { blocks, rayType }) == null) missing.Add("PlantingSelectionService.UnmarkArea");
            if (selectionType.GetField("_terrainAreaService", all)?.FieldType != areaServiceType) missing.Add("PlantingSelectionService._terrainAreaService");
            if (areaServiceType.GetField("_terrainService", all)?.FieldType != terrainServiceType) missing.Add("TerrainAreaService._terrainService");
            if (missing.Count > 0) throw new Exception("missing: " + string.Join(", ", missing));
        });
    }
}

public class TerrainProxy : DispatchProxy
{
    public Func<int, int, int> Height = (_, _) => 0;
    // Called once, the next time the terrain picker asks about a voxel.
    public Action? Probe;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "Underground" && Probe is Action probe)
        {
            Probe = null;
            probe();
        }
        object tile = args![0]!;
        Type type = tile.GetType();
        int x = (int)type.GetProperty("x")!.GetValue(tile)!, y = (int)type.GetProperty("y")!.GetValue(tile)!, z = (int)type.GetProperty("z")!.GetValue(tile)!;
        bool inside = x >= 0 && y >= 0 && x < 8 && y < 8;
        return targetMethod.Name switch
        {
            "Underground" => inside && z < Height(x, y),
            "OnGround" => inside && z == Height(x, y),
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}

public class VisibilityProxy : DispatchProxy
{
    public int MaxVisibleLevel;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod!.Name == "get_MaxVisibleLevel" ? MaxVisibleLevel : throw new NotSupportedException(targetMethod.Name);
}

public class SingletonContextProxy : DispatchProxy
{
    public object? Singleton;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Singleton;
}

// A host's session: actions are queued for its next tick.
public class HostIoProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Type type = targetMethod!.ReturnType;
        if (targetMethod.Name == "get_UserEventBehavior") return Enum.Parse(type, "QueuePlay");
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

public class ReachedLevelling : Exception { }
