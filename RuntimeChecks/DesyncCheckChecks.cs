using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

// The always-on desync check in the compiled mod: what the heartbeat carries on the wire, and where the tick
// hashes start when a multiplayer game loads. StabilityTests checks the comparison itself over the transport.
internal static class DesyncCheckChecks
{
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

        // The constructor clears them as well, but it cannot run here: it also seeds Unity's random numbers.
        test("Leaving a multiplayer game clears the tick hashes, so the next one starts from zero", () =>
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
        });

        // StabilityTests runs the comparison; this is that the compiled ReplayService feeds it and acts on it. Unity's
        // random state cannot be read outside the game, so the check is read from the instructions instead of run.
        test("The always-on check reads all four random words and both tick hashes, and stops the session on a mismatch", () =>
        {
            var replayService = mod.GetType("BeaverBuddies.ReplayService", true)!;
            var members = new[] { replayService }.Concat(replayService.GetNestedTypes(all))
                .SelectMany(type => type.GetMethods(all | BindingFlags.DeclaredOnly))
                .Where(method => method.GetMethodBody() != null)
                .ToDictionary(method => (MethodBase)method, Members);
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

            // The replay of each event asks the check and stops at a mismatch.
            var replay = Naming(check[0].DeclaringType!.FullName!, check[0].Name);
            if (!replay.Any(method => Names(method, "BeaverBuddies.ReplayService", "HandleDesync")))
                throw new Exception($"Nothing that calls {check[0].Name} goes on to HandleDesync");

            // And the host fills in what the guests compare.
            foreach (var (type, field) in new[] { ("BeaverBuddies.Events.ReplayEvent", "randomStateHashBefore"),
                ("BeaverBuddies.HeartbeatEvent", "entityOrderHash"), ("BeaverBuddies.HeartbeatEvent", "walkerPositionHash") })
                if (!members.Values.Any(list => list.Any(m => m is FieldInfo f && f.DeclaringType?.FullName == type && f.Name == field)))
                    throw new Exception($"ReplayService never sets {field}, so the host sends nothing to compare");
        });
    }

    private static readonly Dictionary<ushort, OpCode> opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => (ushort)o.Value);

    // The methods and fields a method's instructions name: what it calls, and what fields it reads and writes.
    private static List<MemberInfo> Members(MethodBase method)
    {
        byte[] body = method.GetMethodBody()!.GetILAsByteArray()!;
        Type[]? typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var members = new List<MemberInfo>();
        for (int at = 0; at < body.Length;)
        {
            ushort value = body[at++];
            if (value == 0xfe) value = (ushort)(0xfe00 | body[at++]);
            OpCode op = opcodes[value];
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: at += 1; break;
                case OperandType.InlineVar: at += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: at += 8; break;
                case OperandType.InlineSwitch: at += 4 + 4 * BitConverter.ToInt32(body, at); break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                    int token = BitConverter.ToInt32(body, at); at += 4;
                    try
                    {
                        members.Add(op.OperandType == OperandType.InlineMethod
                            ? method.Module.ResolveMethod(token, typeArguments, methodArguments)!
                            : method.Module.ResolveField(token, typeArguments, methodArguments)!);
                    }
                    // Something in an assembly this program does not load: not what is looked for here.
                    catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is ArgumentException) { }
                    break;
                default: at += 4; break;
            }
        }
        return members;
    }
}
