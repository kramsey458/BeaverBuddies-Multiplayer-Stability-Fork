using System.Reflection;
using System.Text.RegularExpressions;

// The always-on desync check in the compiled mod: what the heartbeat carries on the wire, and where the tick
// hashes start when a multiplayer game loads. StabilityTests checks the comparison itself over the transport.
internal static class DesyncCheckChecks
{
    // The same method, whichever way it was found (a call's operand, or reflection).
    static bool Same(MemberInfo found, MemberInfo method) =>
        found != null && found.Module == method.Module && found.MetadataToken == method.MetadataToken;

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var replayEventType = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var heartbeatType = mod.GetType("BeaverBuddies.HeartbeatEvent", true);
        var jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true);
        var patcherType = mod.GetType("BeaverBuddies.TEBPatcher", true);
        var serviceType = mod.GetType("BeaverBuddies.DeterminismService", true);

        // These names are the ones the StabilityTests transport check sends; the guest reads them back by name.
        test("A heartbeat carries the whole random state and both tick hashes on the wire", () =>
        {
            FieldInfo Field(Type type, string name) =>
                type.GetField(name) ?? throw new Exception($"{type.Name} has no field {name}, so it is not sent");
            object heartbeat = Activator.CreateInstance(heartbeatType, true)!;
            Field(replayEventType, "randomS0Before").SetValue(heartbeat, 0x11111111);
            Field(replayEventType, "randomStateHashBefore").SetValue(heartbeat, 0x22222222);
            Field(heartbeatType, "entityOrderHash").SetValue(heartbeat, 0x33333333);
            Field(heartbeatType, "walkerPositionHash").SetValue(heartbeat, 0x44444444);
            string json = (string)jsonType.GetMethod("Serialize")!.MakeGenericMethod(replayEventType).Invoke(null, new[] { heartbeat })!;
            foreach (var (field, value) in new[] { ("randomS0Before", 0x11111111), ("randomStateHashBefore", 0x22222222),
                ("entityOrderHash", 0x33333333), ("walkerPositionHash", 0x44444444) })
            {
                if (!Regex.IsMatch(json, $"\"{field}\":\\s*{value}\\b")) throw new Exception($"{field} is not on the wire:\n{json}");
            }
            object back = jsonType.GetMethod("Deserialize")!.MakeGenericMethod(replayEventType).Invoke(null, new object[] { json })!;
            if (back.GetType() != heartbeatType) throw new Exception("The heartbeat came back as " + back.GetType());
            if ((int?)heartbeatType.GetField("walkerPositionHash")!.GetValue(back) != 0x44444444 ||
                (int?)replayEventType.GetField("randomStateHashBefore")!.GetValue(back) != 0x22222222)
                throw new Exception("A hash was lost in transit");
        });

        // The constructor clears them as well, but it cannot run here (it also seeds Unity's random numbers), so that
        // part is read from its instructions.
        test("Loading or leaving a multiplayer game clears the tick hashes, so each game starts from zero", () =>
        {
            // Whatever the game being left accumulated.
            object hashes = patcherType.GetField("hashes", all)?.GetValue(null)
                ?? throw new Exception("TEBPatcher keeps no tick hashes outside detailed logging");
            hashes.GetType().GetMethod("StartTick")!.Invoke(hashes, new object[] { 3 });
            hashes.GetType().GetMethod("AddBucket")!.MakeGenericMethod(typeof(Guid))
                .Invoke(hashes, new object[] { new List<Guid> { Guid.NewGuid() }, (Func<Guid, Guid>)(id => id) });
            hashes.GetType().GetMethod("AddWalker")!.Invoke(hashes, new object[] { 1f, 2f, 3f });
            int Order() => (int)patcherType.GetProperty("EntityUpdateHash")!.GetValue(null)!;
            int Walkers() => (int)patcherType.GetProperty("PositionHash")!.GetValue(null)!;
            if (Order() == 0 || Walkers() == 0) throw new Exception("The hashes did not move");
            // What the next scene's configurator calls on this game's singletons.
            object service = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(serviceType);
            serviceType.GetMethod("Reset")!.Invoke(service, null);
            if (Order() != 0 || Walkers() != 0) throw new Exception($"The hashes survived: {Order():X8} {Walkers():X8}");
            // And the constructor, which every player runs as a multiplayer game loads.
            if (!serviceType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(ctor => IlScan.Instructions(ctor).Any(i => i.Calls && i.Is("BeaverBuddies.TEBPatcher", "ResetHashes"))))
                throw new Exception("Loading a multiplayer game (the DeterminismService constructor) does not clear the tick hashes");
        });

        // StabilityTests runs the comparison; this is that the compiled ReplayService feeds it and acts on it. Unity's
        // random state cannot be read outside the game, so the check is read from the instructions instead of run.
        test("The always-on check reads all four random words and both tick hashes, and stops the session on a mismatch", () =>
        {
            var replayService = mod.GetType("BeaverBuddies.ReplayService", true)!;
            var members = new[] { replayService }.Concat(replayService.GetNestedTypes(all))
                .SelectMany(type => type.GetMethods(all | BindingFlags.DeclaredOnly))
                .Where(method => method.GetMethodBody() != null)
                .ToDictionary(method => (MethodBase)method, IlScan.Members);
            bool Names(MethodBase method, string declaringType, string name) =>
                members[method].Any(m => m.DeclaringType?.FullName == declaringType && m.Name == name);
            MethodBase[] Naming(string declaringType, string name) =>
                members.Keys.Where(method => Names(method, declaringType, name)).ToArray();

            // Earlier builds read only s0, so a game whose other 96 bits differed passed.
            foreach (string word in new[] { "s1", "s2", "s3" })
                if (Naming("UnityEngine.Random+State", word).Length == 0)
                    throw new Exception($"ReplayService never reads {word} of Unity's random state: only part of it is compared");

            var check = Naming("BeaverBuddies.DesyncDetecter.DesyncCheck", "Mismatch");
            if (check.Length != 1) throw new Exception($"{check.Length} methods of ReplayService call DesyncCheck.Mismatch, expected one");
            foreach (string read in new[] { "s1", "s2", "s3" })
                if (!Names(check[0], "UnityEngine.Random+State", read))
                    throw new Exception($"{check[0].Name} compares without reading {read} of this game's random state");
            foreach (string hash in new[] { "get_EntityUpdateHash", "get_PositionHash" })
                if (!Names(check[0], "BeaverBuddies.TEBPatcher", hash))
                    throw new Exception($"{check[0].Name} compares without reading this game's {hash.Substring(4)}");

            // The replay of each event asks the check and stops at a mismatch, passing on what differed (the host's
            // log and a report name it; without detailed logging there is no other trace).
            var replay = Naming(check[0].DeclaringType!.FullName!, check[0].Name);
            if (!replay.Any(method => members[method].Any(m => m is MethodBase handle && handle.Name == "HandleDesync" &&
                    handle.DeclaringType?.FullName == "BeaverBuddies.ReplayService" && handle.GetParameters().Length == 1)))
                throw new Exception($"Nothing that calls {check[0].Name} goes on to HandleDesync with what differed");
            // Only a random-state difference stops the session. An entity or walker difference alone is logged once
            // per game and the game goes on (DesyncCheck.TickMismatch says why).
            if (!Names(check[0], "BeaverBuddies.DesyncDetecter.DesyncCheck", "RandomMismatch"))
                throw new Exception($"{check[0].Name} does not ask DesyncCheck.RandomMismatch whether to stop, so an entity or walker difference would end the session");
            if (!replay.Any(method => Names(method, "BeaverBuddies.TEBPatcher", "FirstTickDifference")))
                throw new Exception("An entity or walker difference is not logged once per game (TEBPatcher.FirstTickDifference)");

            // And the host fills in what the guests compare. A guest compares only what the host sent, so without
            // these stores every guest would compare nothing new and still pass: the check would be off.
            const string replayEvent = "BeaverBuddies.Events.ReplayEvent", heartbeat = "BeaverBuddies.HeartbeatEvent";
            var code = members.Keys.ToDictionary(method => method, IlScan.Instructions);
            var stamping = code.Keys.Where(method => code[method].Any(i => i.Stores && i.Is(replayEvent, "randomStateHashBefore"))).ToArray();
            if (stamping.Length == 0)
                throw new Exception("ReplayService never stores randomStateHashBefore, so the host sends no whole random state to compare");
            foreach (var method in stamping)
                foreach (string word in new[] { "s0", "s1", "s2", "s3" })
                    if (!code[method].Any(i => i.Loads && i.Is("UnityEngine.Random+State", word)))
                        throw new Exception($"{method.Name} stores randomStateHashBefore without reading {word} of this game's random state");
            // Every event the host plays and sends goes through EnqueueEventForSending, the heartbeat included.
            var send = code.Keys.SingleOrDefault(method => method.Name == "EnqueueEventForSending")
                ?? throw new Exception("ReplayService has no EnqueueEventForSending");
            if (!code[send].Any(i => (i.Stores && i.Is(replayEvent, "randomStateHashBefore")) ||
                    (i.Calls && stamping.Any(method => Same(i.Member, method)))))
                throw new Exception("EnqueueEventForSending does not record the whole random state, so the host sends none");
            foreach (var (field, getter) in new[] { ("entityOrderHash", "get_EntityUpdateHash"), ("walkerPositionHash", "get_PositionHash") })
            {
                if (!code[send].Any(i => i.Stores && i.Is(heartbeat, field)))
                    throw new Exception($"EnqueueEventForSending never stores the heartbeat's {field}, so the host sends none");
                if (!code[send].Any(i => i.Calls && i.Is("BeaverBuddies.TEBPatcher", getter)))
                    throw new Exception($"EnqueueEventForSending stores {field} without reading TEBPatcher.{getter.Substring(4)}");
            }
            var beats = code.Keys.Where(method => code[method].Any(i => i.Op == System.Reflection.Emit.OpCodes.Newobj &&
                i.Member?.DeclaringType?.FullName == heartbeat)).ToArray();
            if (beats.Length == 0 || !beats.All(method => code[method].Any(i => i.Calls && Same(i.Member, send))))
                throw new Exception("A heartbeat is sent without going through EnqueueEventForSending, so it carries no hashes");
        });

        // The comparison is only as good as the hashes: they used to be kept only with detailed logging on, and a
        // public game would then send and compare 0 == 0 on every tick. The pass needs the game's buckets, so this
        // reads its instructions.
        test("Every bucket pass adds to the tick hashes whatever the logging settings, and each tick moves the sampled IDs", () =>
        {
            var prefix = patcherType.GetMethod("Prefix", all) ?? throw new Exception("TEBPatcher has no Prefix");
            var code = IlScan.Instructions(prefix);
            const string hashes = "BeaverBuddies.DesyncDetecter.TickHashes";
            foreach (string add in new[] { "AddBucket", "AddWalker" })
                if (!code.Any(i => i.Calls && i.Is(hashes, add)))
                    throw new Exception($"TEBPatcher.Prefix never calls TickHashes.{add}, so that hash never changes");
            var gate = code.FirstOrDefault(i => i.Member?.DeclaringType?.FullName == "BeaverBuddies.Settings" &&
                (i.Member.Name.Contains("Debug") || i.Member.Name.Contains("VerboseLogging")));
            if (gate != null)
                throw new Exception($"TEBPatcher.Prefix reads Settings.{gate.Member!.Name}: the hashes must not depend on a player's logging settings");

            // Without this the same eighth of each bucket's IDs is hashed on every tick, and the other seven never are.
            var replayService = mod.GetType("BeaverBuddies.ReplayService", true)!;
            var tickSetter = replayService.GetProperty("ticksSinceLoad", all)?.SetMethod
                ?? throw new Exception("ReplayService has no ticksSinceLoad setter");
            if (!IlScan.Instructions(tickSetter).Any(i => i.Calls && i.Is("BeaverBuddies.TEBPatcher", "StartTick")))
                throw new Exception("Setting ReplayService.ticksSinceLoad does not call TEBPatcher.StartTick, so the sampled IDs never rotate");
        });
    }
}
