#nullable enable
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

// 1.1.14's fixes (from the review of MultiColony 1.4.0-beta11, whose shared-colony code is this fork's), against the
// compiled mod: saves wait for the water and soil threads, a stop that is not a pause finishes its tick before saving, the
// multiplayer clock starts at tick 0 as a game loads, loading keeps the non-game random marks, desync reports draw no game
// random numbers, and a replay whose building or entity this game lacks neither throws for everyone nor plays on one
// computer.
internal static class ReviewFixChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        MethodInfo Method(string type, string name) =>
            mod.GetType(type, true)!.GetMethod(name, all) ?? throw new Exception($"{type}.{name} is gone");
        bool Calls(MethodBase method, string type, string name) =>
            IlScan.Instructions(method).Any(i => i.Calls && i.Is(type, name));

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true)!.GetField("logger", all)!;
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true)!;
        void Quietly(Action run)
        {
            object previous = pluginLogger.GetValue(null)!;
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previous); }
        }
        var registryType = Assembly.Load("Timberborn.EntitySystem").GetType("Timberborn.EntitySystem.EntityRegistry", true)!;
        var contextType = mod.GetType("BeaverBuddies.Events.IReplayContext", true)!;
        object EmptyWorld()
        {
            var context = (ReplayContextProxy)DispatchProxy.Create(contextType, typeof(ReplayContextProxy));
            context.Registry = Activator.CreateInstance(registryType)!; context.RegistryType = registryType;
            return context;
        }


        test("Saving: a save at the end of a tick waits for the water and soil threads first, without their early catch-up", () =>
        {
            MethodInfo completed = Method("BeaverBuddies.TickingService", "OnTickingCompleted");
            var code = IlScan.Instructions(completed);
            int wait = code.FindIndex(i => i.Calls && i.Is("BeaverBuddies.TickingService", "FinishParallelTickBeforeSaving"));
            int run = code.FindIndex(i => i.Calls && i.Is("System.Action", "Invoke"));
            if (wait < 0) throw new Exception("OnTickingCompleted no longer waits for the parallel tick");
            if (run < 0) throw new Exception("OnTickingCompleted no longer runs its callbacks");
            if (wait > run) throw new Exception("the callbacks (the save) run before the parallel tick is waited for");
            MethodInfo helper = Method("BeaverBuddies.TickingService", "FinishParallelTickBeforeSaving");
            if (!Calls(helper, "Timberborn.TickSystem.TickableSingletonService", "FinishParallelTick"))
                throw new Exception("the wait does not call TickableSingletonService.FinishParallelTick");
            // ForceFinishParallelTick also raises ForcedParallelTickFinished, whose listeners (water, soil, terrain) would
            // catch up early on the saving computer alone.
            if (Calls(helper, "Timberborn.TickSystem.TickableSingletonService", "ForceFinishParallelTick"))
                throw new Exception("the wait forces the parallel tick, which lets its listeners run on one computer only");
        });

        test("Saving: a save with nothing waiting on a game's bucket service still runs, once", () => Quietly(() =>
        {
            Type ticking = mod.GetType("BeaverBuddies.TickingService", true)!;
            object instance = RuntimeHelpers.GetUninitializedObject(ticking);
            int calls = 0;
            var callbacks = new List<Action> { () => calls++ };
            ticking.GetField("onCompletedFullTick", all)!.SetValue(instance, callbacks);
            ticking.GetField("<ShouldCompleteFullTick>k__BackingField", all)!.SetValue(instance, true);
            ticking.GetMethod("OnTickingCompleted", all)!.Invoke(instance, null);
            if (calls != 1 || callbacks.Count != 0) throw new Exception($"the save ran {calls} times");
        }));

        test("Saving: only a pause by the players saves at once; any other stop finishes its tick first", () =>
        {
            MethodInfo finish = Method("BeaverBuddies.ReplayService", "FinishFullTickIfNeededAndThen");
            if (!Calls(finish, "BeaverBuddies.ReplayService", "get_TargetSpeed"))
                throw new Exception("FinishFullTickIfNeededAndThen does not ask whether the players paused");
            if (Calls(finish, "Timberborn.TimeSystem.SpeedManager", "get_CurrentSpeed"))
                throw new Exception("FinishFullTickIfNeededAndThen still takes any stop (a guest waiting, the host easing off) for a pause");
        });

        test("Loading: the multiplayer clock starts at tick 0 as a game loads, wherever the last game left it", () =>
        {
            Type determinism = mod.GetType("BeaverBuddies.DeterminismService", true)!;
            if (!determinism.GetConstructors(all).Any(ctor => IlScan.Instructions(ctor).Any(i => i.Calls && i.Is("BeaverBuddies.TimeTimePatcher", "ResetForLoad"))))
                throw new Exception("DeterminismService's constructor does not reset the multiplayer clock");
            Type clock = mod.GetType("BeaverBuddies.TimeTimePatcher", true)!;
            FieldInfo time = clock.GetField("time", all)!;
            object before = time.GetValue(null)!;
            try
            {
                time.SetValue(null, 12345.6f);
                clock.GetMethod("ResetForLoad", all)!.Invoke(null, null);
                if ((float)clock.GetProperty("SimulationTime", all)!.GetValue(null)! != 0f) throw new Exception("the clock kept the last game's time");
            }
            finally { time.SetValue(null, before); }
        });

        test("Random numbers: DeterminismService asks RandomSourceRules, so loading keeps the non-game marks", () =>
        {
            MethodInfo getter = mod.GetType("BeaverBuddies.DeterminismService", true)!.GetProperty("ShouldFreezeSeed", all)!.GetMethod!;
            if (!Calls(getter, "BeaverBuddies.RandomSourceRules", "Choose"))
                throw new Exception("ShouldFreezeSeed decides without RandomSourceRules.Choose");
        });

        test("Random numbers: a real GUID asked for on one thread leaves the others' entity IDs to the game's random numbers", () =>
        {
            FieldInfo flag = mod.GetType("BeaverBuddies.GuidPatcher", true)!.GetField("makeRealGuid", all)
                ?? throw new Exception("GuidPatcher.makeRealGuid is gone");
            if (!flag.IsDefined(typeof(ThreadStaticAttribute))) throw new Exception("GuidPatcher.makeRealGuid is shared by every thread");
        });

        test("Desync reports name their files without the game's random numbers", () =>
        {
            foreach (string name in new[] { "BeaverBuddies.DesyncDetecter.WalkerDiagnostics", "BeaverBuddies.DesyncDetecter.WaterDiagnostics" })
            {
                // Written as a ClientDesyncedEvent plays, only on computers with detailed logging data: a Guid.NewGuid there
                // drew 16 game random numbers on some computers and not others.
                var calls = IlScan.Of(mod.GetType(name, true)!).Where(pair => IlScan.Names(pair.Value, "System.Guid", "NewGuid")).ToList();
                if (calls.Count > 0) throw new Exception($"{name}.{calls[0].Key.Name} calls Guid.NewGuid");
            }
        });

        test("Buildings: the host refuses an action naming a building its game does not have, before playing anything", () =>
        {
            MethodInfo names = Method("BeaverBuddies.ReplayService", "NamesMissingBuilding");
            if (!Calls(names, "Timberborn.Buildings.BuildingService", "GetBuildingTemplate"))
                throw new Exception("NamesMissingBuilding does not ask the game whether it has the building");
            // The replay loop (a lambda in ReplayService.ReplayEvents) asks before it records the random state and plays.
            var loops = IlScan.Of(mod.GetType("BeaverBuddies.ReplayService", true)!)
                .Where(pair => IlScan.Names(pair.Value, "BeaverBuddies.ReplayService", "NamesMissingBuilding")).ToList();
            if (loops.Count == 0) throw new Exception("nothing in the replay loop calls NamesMissingBuilding");
            foreach (var pair in loops)
            {
                var code = IlScan.Instructions(pair.Key);
                int ask = code.FindIndex(i => i.Calls && i.Is("BeaverBuddies.ReplayService", "NamesMissingBuilding"));
                int play = code.FindIndex(i => i.Calls && i.Member?.Name == "Replay");
                if (play >= 0 && ask > play) throw new Exception($"{pair.Key.Name} plays the action before it checks the building");
            }
        });

        test("Buildings: a guest meets a building its game lacks as missing content, before anything is played", () => Quietly(() =>
        {
            Type placed = mod.GetType("BeaverBuddies.Events.BuildingPlacedEvent", true)!;
            object replayEvent = Activator.CreateInstance(placed, true)!;
            placed.GetField("prefabName")!.SetValue(replayEvent, "SomeoneElsesMod.Folktails");
            try
            {
                placed.GetMethod("Replay")!.Invoke(replayEvent, new[] { EmptyWorld() });
                throw new Exception("a placement of a building this game lacks was played");
            }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "MissingContentException")
            {
                if (!e.InnerException!.Message.Contains("SomeoneElsesMod.Folktails")) throw new Exception("the message does not name the building");
            }
        }));

        test("Buildings: a guest that lacks a building the host used leaves quietly; anything else still stops the session", () =>
        {
            // The replay loop's failure handler (a lambda in ReplayService.ReplayEvents) tells the two apart: it tests the
            // exception's type, and leaves quietly (AbortReplay(reason, leaveQuietly)) with the exception's own message.
            bool found = IlScan.Of(mod.GetType("BeaverBuddies.ReplayService", true)!).Any(pair =>
                pair.Key.Name != "AbortReplay"
                && IlScan.Instructions(pair.Key).Any(i => i.Op == System.Reflection.Emit.OpCodes.Isinst)
                && pair.Value.OfType<MethodInfo>().Any(m => m.Name == "AbortReplay" && m.GetParameters().Length == 2)
                && IlScan.Names(pair.Value, "System.Exception", "get_Message"));
            if (!found) throw new Exception("the replay failure handler does not single out missing content");
        });

        Type tickingType = mod.GetType("BeaverBuddies.TickingService", true)!;
        var tickSystem = Assembly.Load("Timberborn.TickSystem");
        Type tickerType = tickSystem.GetType("Timberborn.TickSystem.Ticker", true)!;
        Type bucketType = tickSystem.GetType("Timberborn.TickSystem.TickableBucketService", true)!;

        test("Deletions: an entity deleted in a tick or a replayed action ends the frame's ticking on every computer", () =>
        {
            Type patcher = mod.GetType("BeaverBuddies.Fixes.EntityDeletionEndsFramePatcher", true)!;
            var target = patcher.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch")
                ?? throw new Exception("the deletion patch has no target");
            if (!target.ConstructorArguments.Any(a => a.Value is Type t && t.FullName == "Timberborn.EntitySystem.EntityService")
                || !target.ConstructorArguments.Any(a => (a.Value as string) == "Delete"))
                throw new Exception("the deletion patch is not on EntityService.Delete");
            MethodInfo postfix = patcher.GetMethod("Postfix", all) ?? throw new Exception("the deletion patch has no postfix");
            if (!Calls(postfix, "BeaverBuddies.TickingService", "set_ShouldInterruptTicking"))
                throw new Exception("a deletion does not interrupt ticking");
            if (!Calls(postfix, "BeaverBuddies.ReplayService", "get_IsReplayingEvents"))
                throw new Exception("a deletion in a paused replay (the unpause may follow in the same frame) is not covered");
        });

        test("Deletions: an interrupted frame gives its unticked buckets back to the ticker, at most a tick's worth", () =>
        {
            object ticking = RuntimeHelpers.GetUninitializedObject(tickingType);
            object ticker = RuntimeHelpers.GetUninitializedObject(tickerType);
            float perBucket = 0.6f / 129;
            tickerType.GetField("_secondsPerBucket", all)!.SetValue(ticker, perBucket);
            FieldInfo clock = tickerType.GetField("_accumulatedDeltaTime", all)!;
            clock.SetValue(ticker, 0f);
            tickingType.GetField("ticker", all)!.SetValue(ticking, ticker);
            object buckets = RuntimeHelpers.GetUninitializedObject(bucketType);
            MethodInfo give = tickingType.GetMethod("GiveBackBuckets", all)!;
            give.Invoke(ticking, new[] { buckets, (object)10 });
            if (Math.Abs((float)clock.GetValue(ticker)! - 10 * perBucket) > 1e-6f) throw new Exception("10 unticked buckets were not given back");
            give.Invoke(ticking, new[] { buckets, (object)0 });
            if (Math.Abs((float)clock.GetValue(ticker)! - 10 * perBucket) > 1e-6f) throw new Exception("nothing to give back changed the clock");
            give.Invoke(ticking, new[] { buckets, (object)100000 });
            if (Math.Abs((float)clock.GetValue(ticker)! - 129 * perBucket) > 1e-5f) throw new Exception("more than one tick's worth was saved up");
            if (!Calls(Method("BeaverBuddies.TickingService", "TickBuckets"), "BeaverBuddies.TickingService", "GiveBackBuckets"))
                throw new Exception("TickBuckets never gives buckets back");
        });

        test("Saving: an interruption before a tick's end keeps the save for that end, the next frame", () => Quietly(() =>
        {
            object ticking = RuntimeHelpers.GetUninitializedObject(tickingType);
            object buckets = RuntimeHelpers.GetUninitializedObject(bucketType);
            FieldInfo next = bucketType.GetField("_nextBucketIndex", all)!;
            next.SetValue(buckets, 5);
            tickingType.GetField("bucketService", all)!.SetValue(ticking, buckets);
            int calls = 0;
            tickingType.GetField("onCompletedFullTick", all)!.SetValue(ticking, new List<Action> { () => calls++ });
            FieldInfo complete = tickingType.GetField("<ShouldCompleteFullTick>k__BackingField", all)!;
            complete.SetValue(ticking, true);
            MethodInfo completed = tickingType.GetMethod("OnTickingCompleted", all)!;
            completed.Invoke(ticking, null);
            if (calls != 0 || !(bool)complete.GetValue(ticking)!) throw new Exception("the save ran in the middle of a tick");
            next.SetValue(buckets, 0);
            completed.Invoke(ticking, null);
            if (calls != 1 || (bool)complete.GetValue(ticking)!) throw new Exception("the save did not run at the tick's end");
        }));

        test("Simulation calls: the tick's own call passes every recording prefix, and the count survives a throw", () =>
        {
            Type replayEvent = mod.GetType("BeaverBuddies.Events.ReplayEvent", true)!;
            MethodInfo doPrefix = replayEvent.GetMethod("DoPrefix", all)!;
            MethodInfo runAs = replayEvent.GetMethod("RunAsSimulation", all)!;
            FieldInfo count = replayEvent.GetField("simulationCalls", all)!;
            Type eventFactory = typeof(Func<>).MakeGenericType(replayEvent);
            // A factory that fails the check if DoPrefix ever asks it for an event (that is, records the call).
            Delegate never = Expression.Lambda(eventFactory, Expression.Throw(Expression.Constant(new Exception("recorded")), replayEvent)).Compile();
            bool result = false;
            runAs.Invoke(null, new object[] { (Action)(() => result = (bool)doPrefix.Invoke(null, new object[] { never })!) });
            if (!result) throw new Exception("a simulation call was not let through");
            if ((int)count.GetValue(null)! != 0) throw new Exception("the count of simulation calls was left raised");
            try { runAs.Invoke(null, new object[] { (Action)(() => throw new InvalidOperationException()) }); } catch (TargetInvocationException) { }
            if ((int)count.GetValue(null)! != 0) throw new Exception("a throw inside a simulation call left the count raised");
        });

        test("Levers and paused buildings: the tick's own switch runs as a simulation call, not a click", () =>
        {
            Type lever = mod.GetType("BeaverBuddies.Fixes.LeverSpringReturnCoopPatcher", true)!;
            MethodInfo prefix = lever.GetMethod("Prefix", all)!;
            if (!Calls(prefix, "BeaverBuddies.Events.ReplayEvent", "RunAsSimulation"))
                throw new Exception("the spring-return is not run as a simulation call");
            if (IlScan.Instructions(prefix).Any(i => i.Member?.Name == "_isPressed"))
                throw new Exception("the spring-return still reads _isPressed, which only the pressing computer sets");
            var priority = prefix.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
            if (priority == null || (int)priority.ConstructorArguments[0].Value! != 0)
                throw new Exception("the spring-return replaces the game's method but does not run last (Priority.Last)");
            Type paused = mod.GetType("BeaverBuddies.Fixes.PausableBuildingStateExitPatcher", true)!;
            if (!Calls(paused.GetMethod("Prefix", all)!, "BeaverBuddies.Events.ReplayEvent", "EnterSimulationCall")
                || !Calls(paused.GetMethod("Finalizer", all)!, "BeaverBuddies.Events.ReplayEvent", "ExitSimulationCall"))
                throw new Exception("a building's state exit is not a simulation call, or does not end one");
            Type debugger = mod.GetType("BeaverBuddies.Fixes.WalkerDebuggerRandomPatcher", true)!;
            if (!Calls(debugger.GetMethod("Finalizer", all)!, "UnityEngine.Random", "set_state"))
                throw new Exception("the walker debugger's random draws are not put back");
        });

        test("Pace: the host's heartbeat carries its eased pace, and a guest's speed follows it", () =>
        {
            if (!Calls(Method("BeaverBuddies.ReplayService", "DoTick"), "BeaverBuddies.ReplayService", "PaceForGuests"))
                throw new Exception("the host's heartbeat does not carry its pace");
            if (!Calls(Method("BeaverBuddies.HeartbeatEvent", "Replay"), "BeaverBuddies.ReplayService", "SetHostPace"))
                throw new Exception("a guest does not take the host's pace from the heartbeat");
            if (!Calls(Method("BeaverBuddies.ReplayService", "UpdateSpeed"), "BeaverBuddies.CatchUpSpeed", "PaceFor"))
                throw new Exception("a guest's speed does not follow the host's pace");
            if (!IlScan.Instructions(Method("BeaverBuddies.ReplayService", "SampleHostPacing"))
                    .Any(i => i.Calls && i.Member is MethodInfo m && m.Name == "Sample" && m.GetParameters().Length == 3))
                throw new Exception("the host's pacing thresholds are not told the speed");
        });

        test("Pace: the heartbeat leaves the pace out at full speed, and a guest reads it back when it is there", () =>
        {
            Type heartbeat = mod.GetType("BeaverBuddies.HeartbeatEvent", true)!;
            Type settings = mod.GetType("BeaverBuddies.IO.JsonSettings", true)!;
            MethodInfo serialize = settings.GetMethod("Serialize", all)!.MakeGenericMethod(heartbeat);
            MethodInfo deserialize = settings.GetMethod("Deserialize", all)!.MakeGenericMethod(mod.GetType("BeaverBuddies.Events.ReplayEvent", true)!);
            FieldInfo pace = heartbeat.GetField("hostSpeed", all)!;
            object full = Activator.CreateInstance(heartbeat, true)!;
            string fullJson = (string)serialize.Invoke(null, new[] { full })!;
            if (fullJson.Contains("hostSpeed")) throw new Exception("a heartbeat at full speed carries the pace: " + fullJson);
            object eased = Activator.CreateInstance(heartbeat, true)!;
            pace.SetValue(eased, (float?)4.9f);
            string easedJson = (string)serialize.Invoke(null, new[] { eased })!;
            object read = deserialize.Invoke(null, new object[] { easedJson })!;
            if (!Equals(pace.GetValue(read), (float?)4.9f)) throw new Exception("a guest did not read the pace back: " + easedJson);
        });

        test("Steam: what is sent outside the tick loop is handed to Steam at once", () =>
        {
            if (!Calls(Method("BeaverBuddies.ReplayService", "FlushSteam"), "BeaverBuddies.Steam.SteamNet", "PumpBetweenTicks"))
                throw new Exception("FlushSteam does not pump Steam");
            // HandleDesync has two overloads here; the one that sends is the one to check.
            Type replayService = mod.GetType("BeaverBuddies.ReplayService", true)!;
            foreach (string sender in new[] { "UpdateSingleton", "HandleDesync" })
                if (!replayService.GetMethods(all).Where(m => m.Name == sender).Any(m => Calls(m, "BeaverBuddies.ReplayService", "FlushSteam")))
                    throw new Exception($"ReplayService.{sender} sends without handing it to Steam");
        });

        test("Wonders: whether a wonder may be activated is the host's answer, written into the action and followed by guests", () =>
        {
            Type activated = mod.GetType("BeaverBuddies.Events.WonderActivatedEvent", true)!;
            if (activated.GetField("activated", all)?.FieldType != typeof(bool?)) throw new Exception("the action carries no host's answer");
            MethodInfo replay = activated.GetMethod("Replay", all)!;
            if (!Calls(replay, "Timberborn.Wonders.Wonder", "CanBeActivated")) throw new Exception("the host does not ask the game");
            if (!IlScan.Instructions(replay).Any(i => i.Stores && i.Member?.Name == "HostSaidYes"))
                throw new Exception("a guest does not follow the host's yes");
            Type follows = mod.GetType("BeaverBuddies.Events.WonderActivationFollowsHostPatcher", true)!;
            var priority = follows.GetMethod("Prefix", all)!.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "HarmonyPriority");
            if (priority == null || (int)priority.ConstructorArguments[0].Value! != 0)
                throw new Exception("the answer replaces the game's check but does not run last (Priority.Last)");
        });

        test("Walker hash: a walker switched off (a pilot riding its plane) is not counted", () =>
        {
            MethodInfo prefix = mod.GetType("BeaverBuddies.TEBPatcher", true)!.GetMethod("Prefix", all)!;
            var code = IlScan.Instructions(prefix);
            int enabled = code.FindIndex(i => i.Calls && i.Member?.Name == "get_Enabled" && i.Member.DeclaringType?.Name == "BaseComponent");
            int add = code.FindIndex(i => i.Calls && i.Member?.Name == "AddWalker");
            if (enabled < 0 || add < 0 || enabled > add) throw new Exception("the walker hash counts walkers that do not move in the simulation");
        });

        test("Automation: a setting for a building that is gone is skipped, not thrown for everyone", () => Quietly(() =>
        {
            Type automation = mod.GetType("BeaverBuddies.Events.AutomationEvent", true)!;
            var cache = (System.Collections.IDictionary)automation.GetField("methodCache", all)!.GetValue(null)!;
            Type mover = Assembly.Load("Timberborn.WaterBuildings").GetType("Timberborn.WaterBuildings.WaterMover", true)!;
            MethodInfo setter = mover.GetProperty("CleanWaterMovement", all)!.SetMethod!;
            const string key = "ReviewFixChecks.WaterMover.set_CleanWaterMovement";
            cache[key] = setter;
            try
            {
                object replayEvent = Activator.CreateInstance(automation, true)!;
                automation.GetField("entityID")!.SetValue(replayEvent, Guid.NewGuid().ToString());
                automation.GetField("methodKey")!.SetValue(replayEvent, key);
                automation.GetField("arguments")!.SetValue(replayEvent, new object[] { true });
                automation.GetMethod("Replay")!.Invoke(replayEvent, new[] { EmptyWorld() });
                // And an event that carries no arguments at all is refused the same way, not thrown.
                automation.GetField("arguments")!.SetValue(replayEvent, null);
                automation.GetMethod("Replay")!.Invoke(replayEvent, new[] { EmptyWorld() });
            }
            finally { cache.Remove(key); }
        }));
    }
}
