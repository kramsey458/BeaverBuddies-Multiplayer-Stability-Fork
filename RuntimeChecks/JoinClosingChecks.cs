using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;

// A player who joins loads the save the host started from and is sent only what is played after it connected.
// So the first action played before the first tick that changes the game closes joining, as the first tick does.
internal static class JoinClosingChecks
{
    // Events a player who joins later can do without. Each overrides ReplayEvent.ChangesGame() to return false.
    static readonly string[] NotGameChanging =
    {
        "BeaverBuddies.HeartbeatEvent",                         // a no-op that tells guests the host ticked
        "BeaverBuddies.Events.SpeedSetEvent",                   // unpausing runs the first tick, which closes joining
        "BeaverBuddies.Events.ShowOptionsMenuEvent",            // a SpeedSetEvent: a pause and the options menu
        "BeaverBuddies.Events.InitializeClientEvent",           // the host's greeting, sent to every guest as it joins
        "BeaverBuddies.Events.ClientDesyncedEvent",             // a desync report: the session is over
        "BeaverBuddies.DesyncDetecter.TraceLoggedForTickEvent", // compares debug traces, changes nothing
        "BeaverBuddies.Ping.PingEvent",                         // a marker on the screen
    };

    // Everything else keeps the default (true). A type missing from both lists fails the check, so a new event is a
    // decision, not an accident. An event from another mod is not listed and keeps the default too.
    static readonly string[] GameChanging =
    {
        "BeaverBuddies.GroupedEvent", // never played itself: ReplayService plays the events inside it one by one
        "BeaverBuddies.Events.AutosaveEvent", // nothing records it today; saving runs game code, so not exempted
        "BeaverBuddies.Events.AutomationEvent",
        "BeaverBuddies.Events.SetAutomatableInputEvent",
        "BeaverBuddies.Events.SetTimerIntervalEvent",
        "BeaverBuddies.Events.ResetTransmitterEvent",
        "BeaverBuddies.Events.WeatherStationSetActivateEarlyEvent",
        "BeaverBuddies.Events.ManualMigrationEvent",
        "BeaverBuddies.Events.SetDistrictMinimumPopulationEvent",
        "BeaverBuddies.Events.SetDistrictMigrationToggledEvent",
        "BeaverBuddies.Events.GoodDistributionSettingChangedEvent",
        "BeaverBuddies.Events.GatheringPrioritizedEvent",
        "BeaverBuddies.Events.ManufactoryRecipeSelectedEvent",
        "BeaverBuddies.Events.PlantablePrioritizedEvent",
        "BeaverBuddies.Events.FarmHousePrioritizePlantingChangedEvent",
        "BeaverBuddies.Events.SingleGoodAllowedEvent",
        "BeaverBuddies.Events.BuildingPausedChangedEvent",
        "BeaverBuddies.Events.ConstructionPriorityChangedEvent",
        "BeaverBuddies.Events.WorkplacePriorityChangedEvent",
        "BeaverBuddies.Events.WorkplaceDesiredWorkersChangedEvent",
        "BeaverBuddies.Events.FloodgateHeightChangedEvent",
        "BeaverBuddies.Events.FloodgateSynchronizedChangedEvent",
        "BeaverBuddies.Events.StockpilePriorityChangedEvent",
        "BeaverBuddies.Events.DemolishButtonClickedEvent",
        "BeaverBuddies.Events.DynamiteTriggeredEvent",
        "BeaverBuddies.Events.GoodStackDeletedEvent",
        "BeaverBuddies.Events.EntityRenamedEvent",
        "BeaverBuddies.Events.WorkerTypeUnlockedEvent",
        "BeaverBuddies.Events.WorkerTypeSetEvent",
        "BeaverBuddies.Events.HaulPrioritizablePrioritizedEvent",
        "BeaverBuddies.Events.ToggleForresterReplantDeadTreesEvent",
        "BeaverBuddies.Events.WaterMoverModeChangedEvent",
        "BeaverBuddies.Events.ZiplineConnectionChangedEvent",
        "BeaverBuddies.Events.WonderActivatedEvent",
        "BeaverBuddies.Events.DefaultWorkerTypeChangedEvent",
        "BeaverBuddies.Events.BuildingPlacedEvent",
        "BeaverBuddies.Events.BuildingsDeconstructedEvent",
        "BeaverBuddies.Events.PlantingAreaMarkedEvent",
        "BeaverBuddies.Events.ClearResourcesMarkedEvent",
        "BeaverBuddies.Events.TreeCuttingAreaEvent",
        "BeaverBuddies.Events.BuildingUnlockedEvent",
        "BeaverBuddies.Events.WorkingHoursChangedEvent",
        "BeaverBuddies.Events.DuplicationEvent",
    };

    const string ChangedMessage = "changed the game";

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var eventType = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var replayServiceType = mod.GetType("BeaverBuddies.ReplayService", true);
        var hostIoType = mod.GetType("BeaverBuddies.IO.ServerEventIO", true);
        var guestIoType = mod.GetType("BeaverBuddies.IO.ClientEventIO", true);
        var jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true);
        var timberNet = Assembly.Load("TimberNet");
        var serverType = timberNet.GetType("TimberNet.TimberServer", true);
        var listenerType = timberNet.GetType("TimberNet.ISocketListener", true);

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        object previousLogger = pluginLogger.GetValue(null);
        void Quietly(Action run)
        {
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previousLogger); }
        }

        List<Type> EventTypes()
        {
            Type[] types;
            try { types = mod.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            return types.Where(t => eventType.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        }
        MethodInfo ChangesGameMethod() =>
            eventType.GetMethod("ChangesGame", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)
            ?? throw new Exception("ReplayEvent has no ChangesGame(): nothing says which actions a player joining later would miss");
        object Event(string name) => RuntimeHelpers.GetUninitializedObject(mod.GetType(name, true));

        // A host whose server was never started: only its joining state is looked at.
        (object Io, object Server) Host()
        {
            object io = RuntimeHelpers.GetUninitializedObject(hostIoType);
            object server = Activator.CreateInstance(serverType,
                DispatchProxy.Create(listenerType, typeof(EmptyEventProxy)),
                (Func<Task<byte[]>>)(() => Task.FromResult(Array.Empty<byte>())), null);
            hostIoType.GetProperty("NetBase").SetValue(io, server);
            return (io, server);
        }
        bool Accepting(object server) => (bool)serverType.GetProperty("IsAcceptingClients").GetValue(server);
        string Refusal(object server) => (string)serverType.GetField("errorMessage", all).GetValue(server);
        MethodInfo CloseMethod() => replayServiceType.GetMethod("CloseJoiningIfGameChanged", all)
            ?? throw new Exception("ReplayService has no CloseJoiningIfGameChanged: an action played before the first tick never closes joining");
        void Play(object io, int tick, object replayEvent) => CloseMethod().Invoke(null, new[] { io, tick, replayEvent });

        test("Every ReplayEvent type says whether a player joining later could do without it", () =>
        {
            var changesGame = ChangesGameMethod();
            if (!changesGame.IsVirtual || changesGame.ReturnType != typeof(bool)) throw new Exception("ChangesGame() is not a virtual bool method");
            var problems = new List<string>();
            var types = EventTypes();
            foreach (var type in types)
            {
                bool expected;
                if (NotGameChanging.Contains(type.FullName)) expected = false;
                else if (GameChanging.Contains(type.FullName)) expected = true;
                else { problems.Add($"{type.FullName} is in neither list: decide whether a player who joins without it would be missing something"); continue; }
                bool actual = (bool)changesGame.Invoke(RuntimeHelpers.GetUninitializedObject(type), null);
                if (actual != expected) problems.Add($"{type.FullName}.ChangesGame() is {actual}, expected {expected}");
            }
            foreach (string name in NotGameChanging.Concat(GameChanging).Where(n => types.All(t => t.FullName != n)))
                problems.Add($"{name} is listed but is no longer an event");
            if (problems.Count > 0) throw new Exception(string.Join("; ", problems));
        });

        test("An event type nobody classified, such as another mod's, changes the game", () =>
        {
            var changesGame = ChangesGameMethod();
            // Stands in for an event compiled elsewhere (MixedStorage's allocation event is one).
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("JoinClosingStub"), AssemblyBuilderAccess.Run).DefineDynamicModule("JoinClosingStub");
            var builder = module.DefineType("OtherMod.SomeEvent", TypeAttributes.Public | TypeAttributes.Class, eventType);
            var abstractReplay = eventType.GetMethod("Replay");
            var replay = builder.DefineMethod("Replay", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                typeof(void), abstractReplay.GetParameters().Select(p => p.ParameterType).ToArray());
            replay.GetILGenerator().Emit(OpCodes.Ret);
            builder.DefineMethodOverride(replay, abstractReplay);
            var stub = RuntimeHelpers.GetUninitializedObject(builder.CreateType());
            if (!(bool)changesGame.Invoke(stub, null)) throw new Exception("An unclassified event was treated as not changing the game");
        });

        test("A game-changing action played by the host before the first tick closes joining", () => Quietly(() =>
        {
            foreach (string name in new[] { "BeaverBuddies.Events.BuildingPlacedEvent", "BeaverBuddies.Events.WorkplacePriorityChangedEvent", "BeaverBuddies.Events.PlantingAreaMarkedEvent" })
            {
                var (io, server) = Host();
                Play(io, 0, Event(name));
                if (Accepting(server)) throw new Exception($"{name} at tick 0 left joining open: a guest joining now would never get it");
                if (!Refusal(server).Contains(ChangedMessage)) throw new Exception("The refusal does not say the host changed the game: " + Refusal(server));
            }
        }));

        test("Heartbeats, speed changes, pings and the other non-game events leave joining open", () => Quietly(() =>
        {
            foreach (string name in NotGameChanging)
            {
                var (io, server) = Host();
                Play(io, 0, Event(name));
                if (!Accepting(server)) throw new Exception($"{name} at tick 0 closed joining");
            }
        }));

        test("Only the host closes joining for an action, and only at tick 0 (the first tick closes it itself)", () => Quietly(() =>
        {
            var (io, server) = Host();
            // ReplayService.DoTick closes joining at the end of tick 1 with its own reason.
            Play(io, 1, Event("BeaverBuddies.Events.BuildingPlacedEvent"));
            if (!Accepting(server)) throw new Exception("An action at tick 1 closed joining with the tick-0 reason");
            // A guest has no one to refuse.
            Play(RuntimeHelpers.GetUninitializedObject(guestIoType), 0, Event("BeaverBuddies.Events.BuildingPlacedEvent"));
        }));

        test("The first tick keeps the reason when an action already closed joining", () => Quietly(() =>
        {
            var (io, server) = Host();
            Play(io, 0, Event("BeaverBuddies.Events.BuildingPlacedEvent"));
            string reason = Refusal(server);
            // What ReplayService.DoTick calls at tick 1.
            var stop = hostIoType.GetMethod("StopAcceptingClients");
            stop.Invoke(io, stop.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
            if (Refusal(server) != reason) throw new Exception("The first tick replaced the reason with: " + Refusal(server));
        }));

        test("ReplayEvents closes joining after playing an action and before queueing it to be sent", () =>
        {
            // The loop body is a lambda, so look in ReplayService and the classes the compiler makes for it.
            var candidates = new[] { replayServiceType }.Concat(replayServiceType.GetNestedTypes(all))
                .SelectMany(t => t.GetMethods(all | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.Contains("ReplayEvents") && m.GetMethodBody() != null);
            foreach (var method in candidates)
            {
                var calls = Calls(method);
                int replay = calls.FindIndex(c => c.Name == "Replay" && c.DeclaringType == eventType);
                int send = calls.FindIndex(c => c.Name == "EnqueueEventForSending");
                if (replay < 0 || send < 0) continue;
                int close = calls.FindIndex(c => c.Name == "CloseJoiningIfGameChanged");
                if (close < 0) throw new Exception("The replay loop never closes joining");
                if (!(replay < close && close < send)) throw new Exception($"Joining closes out of order (replay {replay}, close {close}, send {send})");
                return;
            }
            throw new Exception("The replay loop was not found");
        });

        test("ChangesGame is a method, so it adds nothing to an event's JSON, its hash or what the binder scans", () =>
        {
            var named = EventTypes().Append(eventType)
                .SelectMany(t => t.GetMembers(all).Where(m => m is FieldInfo || m is PropertyInfo))
                .Where(m => m.Name.Contains("ChangesGame", StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0) throw new Exception("A field or property is named after ChangesGame: " + named[0].DeclaringType + "." + named[0].Name);
            var problems = new List<string>();
            string Show(IEnumerable<string> names) => "[" + string.Join(",", names) + "]";
            // ReplayEvent's own members are in every event: one more changes every event on the wire.
            var baseMembers = Members(eventType, eventType.BaseType);
            if (!baseMembers.SequenceEqual(Sorted(BaseMembers)))
                problems.Add($"ReplayEvent's fields and properties are {Show(baseMembers)}, not {Show(Sorted(BaseMembers))}");
            foreach (var (name, own) in OwnMembers)
            {
                var type = mod.GetType(name, true);
                var members = Members(type, eventType);
                if (!members.SequenceEqual(Sorted(own)))
                    problems.Add($"{name}'s own fields and properties are {Show(members)}, not {Show(Sorted(own))}");
                object replayEvent = Activator.CreateInstance(type, true);
                string json = (string)jsonType.GetMethod("Serialize").MakeGenericMethod(type).Invoke(null, new[] { replayEvent });
                var keys = Sorted(JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
                var expected = Sorted(own.Concat(BaseMembers).Append("$type"));
                if (!keys.SequenceEqual(expected)) problems.Add($"{name}'s JSON keys are {Show(keys)}, not {Show(expected)}");
            }
            if (problems.Count > 0) throw new Exception(string.Join("; ", problems));
        });
    }

    // What an event carries, as it was before ChangesGame() existed: its JSON keys (less "$type") and every instance
    // field and property that SF1's binder scans. ReplayEvent's members go into every event. The own members are
    // pinned for each event that overrides ChangesGame(), and for one ordinary action. A change here is a wire change
    // (the JSON is hashed, and the binder checks the members), so it needs its own decision, not a side effect.
    static readonly string[] BaseMembers = { "randomS0Before", "ticksSinceLoad", "type" };
    static readonly (string Name, string[] Own)[] OwnMembers =
    {
        ("BeaverBuddies.HeartbeatEvent", new string[0]),
        ("BeaverBuddies.Events.SpeedSetEvent", new[] { "speed" }),
        ("BeaverBuddies.Events.ShowOptionsMenuEvent", new[] { "speed" }),
        ("BeaverBuddies.Events.InitializeClientEvent", new[] { "isDebugMode", "removeLargeColonySpeedLimit", "serverGameVersion", "serverModVersion" }),
        ("BeaverBuddies.Events.ClientDesyncedEvent", new[] { "desyncID", "desyncTrace" }),
        ("BeaverBuddies.DesyncDetecter.TraceLoggedForTickEvent", new[] { "tick", "traces" }),
        ("BeaverBuddies.Ping.PingEvent", new[] { "CreatorID", "WorldPosition", "colorHex", "senderName", "worldX", "worldY", "worldZ" }),
        ("BeaverBuddies.Events.BuildingPausedChangedEvent", new[] { "entityID", "wasPaused" }),
    };

    static List<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal).ToList();

    // Every instance field and property, of any visibility, declared from type up to (not including) stop.
    static List<string> Members(Type type, Type stop)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var names = new List<string>();
        for (Type t = type; t != null && t != stop; t = t.BaseType)
            names.AddRange(t.GetFields(declared).Select(f => f.Name).Concat(t.GetProperties(declared).Select(p => p.Name)));
        return Sorted(names);
    }

    // The methods a method body calls, in IL order.
    static List<MethodBase> Calls(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => (ushort)o.Value);
        byte[] body = method.GetMethodBody().GetILAsByteArray();
        var calls = new List<MethodBase>();
        int position = 0;
        while (position < body.Length)
        {
            ushort code = body[position++];
            if (code == 0xfe) code = (ushort)(0xfe00 | body[position++]);
            OpCode op = opcodes[code];
            int size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(body, position),
                _ => 4,
            };
            if (op.OperandType == OperandType.InlineMethod && (op == OpCodes.Call || op == OpCodes.Callvirt))
            {
                try
                {
                    calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(body, position),
                        method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null));
                }
                catch (Exception) { }
            }
            position += size;
        }
        return calls;
    }
}
