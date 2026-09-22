using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

// Which types a multiplayer frame may create. Frames are read with TypeNameHandling.All, so a "$type" at any depth
// names a type to create; only actions (ReplayEvent types) and what they carry may be created, from BeaverBuddies or
// from another mod, and the JSON that is sent must stay exactly what it was.
internal static class FrameTypeChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var eventType = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var groupedType = mod.GetType("BeaverBuddies.GroupedEvent", true);
        var automationType = mod.GetType("BeaverBuddies.Events.AutomationEvent", true);
        var jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true);
        var newtonsoft = jsonType.BaseType.Assembly;
        var convert = newtonsoft.GetType("Newtonsoft.Json.JsonConvert", true);
        var settingsBase = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings", true);
        var serializeWith = convert.GetMethod("SerializeObject", new[] { typeof(object), settingsBase });
        var deserializeWith = convert.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type), settingsBase });
        var unity = Assembly.Load("UnityEngine.CoreModule");
        var vector3 = unity.GetType("UnityEngine.Vector3", true);
        var vector3Int = unity.GetType("UnityEngine.Vector3Int", true);
        var ray = unity.GetType("UnityEngine.Ray", true);
        var unityObject = unity.GetType("UnityEngine.Object", true);
        var traceType = mod.GetType("BeaverBuddies.DesyncDetecter.Trace", true);
        // Looked up when a check needs it, so a build without the binder fails those checks instead of all of them.
        Type Binder() => mod.GetType("BeaverBuddies.IO.ReplayEventBinder", false)
            ?? throw new Exception("BeaverBuddies.IO.ReplayEventBinder is not in this build: frames are read with no binder");

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        void Quietly(Action run)
        {
            object previous = pluginLogger.GetValue(null);
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); } finally { pluginLogger.SetValue(null, previous); }
        }

        // JsonSettings.Default reads every frame; a fresh copy has a binder that has seen nothing yet.
        object Fresh() => Activator.CreateInstance(jsonType, true);
        object WithoutBinder()
        {
            object settings = Fresh();
            settingsBase.GetProperty("SerializationBinder").SetValue(settings, null);
            return settings;
        }
        string Write(object replayEvent, object settings = null) => settings == null
            ? (string)jsonType.GetMethod("Serialize").MakeGenericMethod(eventType).Invoke(null, new[] { replayEvent })
            : (string)serializeWith.Invoke(null, new[] { replayEvent, settings });
        object Read(string json, object settings = null)
        {
            try
            {
                return settings == null
                    ? jsonType.GetMethod("Deserialize").MakeGenericMethod(eventType).Invoke(null, new object[] { json })
                    : deserializeWith.Invoke(null, new object[] { json, eventType, settings });
            }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }
        // The binder's refusal, wherever Newtonsoft wrapped it; null if the frame was read.
        Exception Refusal(string json, object settings = null)
        {
            try { Read(json, settings); return null; }
            catch (Exception e)
            {
                for (Exception inner = e; inner != null; inner = inner.InnerException)
                    if (inner.GetType().Name == "RefusedTypeException") return inner;
                throw new Exception("The frame failed, but not because the binder refused a type: " + e.Message, e);
            }
        }

        object Grouped(params object[] events)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(eventType));
            foreach (object e in events) list.Add(e);
            return Activator.CreateInstance(groupedType, list);
        }
        object Automation(params object[] arguments)
        {
            object e = Activator.CreateInstance(automationType);
            automationType.GetField("entityID").SetValue(e, Guid.Empty.ToString());
            automationType.GetField("methodKey").SetValue(e, "Timberborn.AutomationBuildings.Lever.SwitchState");
            automationType.GetField("arguments").SetValue(e, arguments);
            return e;
        }

        // ---- a peer may not create any other type ----

        test("A frame from the other player cannot create a type that is not an action or carried by one", () => Quietly(() =>
        {
            // What an attacker sends: a harmless sentinel standing in for a dangerous type, nested where an action
            // carries untyped values.
            string json = Write(Grouped(Automation(new FrameSentinel())));
            int before = FrameSentinel.Created;
            Exception refusal = Refusal(json);
            if (refusal == null) throw new Exception($"The frame was read and created {FrameSentinel.Created - before} {nameof(FrameSentinel)}");
            if (FrameSentinel.Created != before) throw new Exception("The sentinel was created before it was refused");
            if (!refusal.Message.Contains(nameof(FrameSentinel)) || !refusal.Message.Contains("RuntimeChecks"))
                throw new Exception("The refusal does not name the type and its assembly: " + refusal.Message);
            // The same type named by the frame itself rather than by something inside it.
            string root = Write(new FrameSentinel(), Fresh());
            before = FrameSentinel.Created;
            if (Refusal(root) == null || FrameSentinel.Created != before) throw new Exception("A frame that is a sentinel was read");
        }));

        test("A list, array or map of a refused type is refused as well, and so is a map no action declares", () => Quietly(() =>
        {
            var carriedValues = new object[]
            {
                new List<FrameSentinel> { new() }, new[] { new FrameSentinel() },
                new Dictionary<string, FrameSentinel> { ["key"] = new() }, new Dictionary<string, int> { ["key"] = 1 },
            };
            // Written honestly, each element names its own type too, and that alone is refused. A frame can leave it
            // out: Newtonsoft then makes each element the collection's declared element type without asking the
            // binder, so the collection's own type must be refused for what it holds. JsonSettings writes indented
            // JSON, so the element's "$type" is removed with the white space after it.
            var elementType = new Regex($"\"\\$type\":\\s*\"{Regex.Escape($"{typeof(FrameSentinel).FullName}, {typeof(FrameSentinel).Assembly.GetName().Name}")}\",\\s*");
            int untyped = 0;
            foreach (object carried in carriedValues)
            {
                string typed = Write(Grouped(Automation(carried)));
                var frames = new List<(string How, string Json)> { ("", typed) };
                if (elementType.IsMatch(typed)) { frames.Add((" with untyped elements", elementType.Replace(typed, ""))); untyped++; }
                foreach (var (how, json) in frames)
                {
                    int before = FrameSentinel.Created;
                    if (Refusal(json) == null)
                        throw new Exception($"A frame carrying {carried.GetType()}{how} was read and created {FrameSentinel.Created - before} sentinels");
                    if (FrameSentinel.Created != before) throw new Exception($"A sentinel was created before {carried.GetType()}{how} was refused");
                }
            }
            if (untyped != 3) throw new Exception($"Only {untyped} of the list, array and map could be sent with untyped elements");
        }));

        test("A type given to the binder as an extra payload type passes; without it the same frame is refused", () => Quietly(() =>
        {
            string json = Write(Grouped(Automation(new FrameSentinel())));
            object settings = Fresh();
            settingsBase.GetProperty("SerializationBinder").SetValue(settings,
                Activator.CreateInstance(Binder(), new object[] { new[] { typeof(FrameSentinel) } }));
            if (Write(Read(json, settings)) != json) throw new Exception("The extra payload type did not read back");
            if (Refusal(json, Fresh()) == null) throw new Exception("A fresh binder without the extra type read it");
        }));

        test("What this mod's actions declare only lets in its own types, structs, enums, strings and lists of them", () =>
        {
            // The types found by following the members of every action. A class from the game, the framework or
            // another library (one that can do something when it is created or filled in) must never be reached,
            // which would happen, for example, if an action declared a field of a game service.
            Type binderType = Binder();
            object binder = Activator.CreateInstance(binderType, new object[] { Type.EmptyTypes });
            var found = ((IEnumerable)binderType.GetField("payloadTypes", all).GetValue(binder)).Cast<Type>().ToList();
            var unexpected = found.Where(t => t.Assembly != mod && !t.IsValueType && t != typeof(string) && !t.IsArray &&
                !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))).Select(t => t.FullName).ToList();
            if (unexpected.Count > 0) throw new Exception("Unexpected types are allowed: " + string.Join(", ", unexpected));
            // Not even one of this mod's own: a Unity object, a delegate or a reflection type does something when made.
            var never = found.Where(t => unityObject.IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t) ||
                typeof(MemberInfo).IsAssignableFrom(t) || typeof(Assembly).IsAssignableFrom(t)).Select(t => t.FullName).ToList();
            if (never.Count > 0) throw new Exception("Types that must never be created are allowed: " + string.Join(", ", never));
            foreach (Type expected in new[] { traceType, ray, vector3Int, typeof(object[]), typeof(List<>).MakeGenericType(vector3Int) })
                if (!found.Contains(expected)) throw new Exception($"{expected} was not found in the actions' members");
        });

        // Resolves a type by the name a frame would give it, as Newtonsoft does. True if the binder let it through.
        bool Binds(object binder, Type type)
        {
            var name = new object[] { type, null, null };
            binder.GetType().GetMethod("BindToName").Invoke(binder, name);
            try { binder.GetType().GetMethod("BindToType").Invoke(binder, new[] { name[1], name[2] }); return true; }
            catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "RefusedTypeException") { return false; }
        }

        test("A Unity object, a delegate or a reflection type is refused even when a build lists it as an extra payload type", () => Quietly(() =>
        {
            var never = new[] { unity.GetType("UnityEngine.GameObject", true), unity.GetType("UnityEngine.ScriptableObject", true),
                typeof(Action), typeof(MethodInfo), typeof(Assembly) };
            object binder = Activator.CreateInstance(Binder(), new object[] { never });
            foreach (Type type in never)
                if (Binds(binder, type)) throw new Exception($"{type} was allowed");
        }));

        test("A generic action passes only if its type arguments do", () => Quietly(() =>
        {
            // This mod's generic actions (BuildingDropdownEvent<T>, say) are closed over a game component by each
            // concrete action. A frame names the concrete action, which passes; the generic one closed over that game
            // class, which no action carries, must not.
            object binder = Activator.CreateInstance(Binder(), new object[] { Type.EmptyTypes });
            var concrete = mod.GetTypes()
                .Where(t => eventType.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters &&
                    t.BaseType is { IsGenericType: true } b && eventType.IsAssignableFrom(b))
                .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
            if (concrete.Count == 0) throw new Exception("No action derives from a generic action");
            foreach (Type type in concrete)
            {
                if (!Binds(binder, type)) throw new Exception($"{type} was refused");
                if (Binds(binder, type.BaseType)) throw new Exception($"{type.BaseType} was allowed, although {type.BaseType.GetGenericArguments()[0]} is carried by no action");
            }
        }));

        // ---- every real action still reads back ----

        var samples = new Samples(mod, eventType, vector3, vector3Int, ray, traceType, Automation);
        List<object> Everything() => mod.GetTypes()
            .Where(t => eventType.IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(samples.Event).ToList();

        test("Every action this mod sends reads back the same, fields filled in", () => Quietly(() =>
        {
            var events = Everything();
            if (events.Count < 40) throw new Exception($"Only {events.Count} action types were found");
            foreach (object e in events)
            {
                string json = Write(e);
                string again = Write(Read(json));
                if (again != json) throw new Exception($"{e.GetType().Name} changed on the way back:\n{json}\n{again}");
            }
            // And all of them in one tick's group, as they travel.
            string group = Write(Grouped(events.Where(e => e.GetType() != groupedType).ToArray()));
            if (Write(Read(group)) != group) throw new Exception("A group of every action changed on the way back");
        }));

        test("The binder leaves the JSON sent for every action unchanged, so the event hash is unchanged too", () => Quietly(() =>
        {
            foreach (object e in Everything())
            {
                string with = Write(e), without = Write(e, WithoutBinder());
                if (with != without) throw new Exception($"{e.GetType().Name} is written differently:\n{without}\n{with}");
            }
        }));

        // ---- another mod's actions, from an assembly loaded from bytes ----

        StubAssembly.Field F(string name, string kind) => new(name, kind);
        // MixedStorage's bridge: the same assembly, namespace, type and fields as
        // MixedStorage.Multiplayer.StorageAllocationEvent, which is compiled against this mod.
        var bridge = Load(StubAssembly.Build("MixedStorage.MultiplayerBridge", mod,
            new StubAssembly.Class("MixedStorage.Multiplayer", "StorageAllocationEvent", true,
                F("entityID", "string"), F("allocation", "string"), F("senderID", "string"))));
        // A mod whose action carries its own payload classes, and a class that no action carries.
        var other = Load(StubAssembly.Build("OtherMod.Actions", mod,
            new StubAssembly.Class("OtherMod", "PayloadEvent", true, F("payload", "Payload"), F("payloads", "List:Payload")),
            new StubAssembly.Class("OtherMod", "Payload", false, F("amount", "int"), F("good", "string")),
            new StubAssembly.Class("OtherMod", "Unreferenced", false, F("amount", "int"))));
        object Allocation()
        {
            var type = bridge.GetType("MixedStorage.Multiplayer.StorageAllocationEvent", true);
            object e = Activator.CreateInstance(type);
            type.GetField("entityID").SetValue(e, Guid.Empty.ToString());
            type.GetField("allocation").SetValue(e, "Log=40;Plank=60");
            type.GetField("senderID").SetValue(e, "player");
            return e;
        }
        object Payload(int amount)
        {
            object payload = Activator.CreateInstance(other.GetType("OtherMod.Payload", true));
            payload.GetType().GetField("amount").SetValue(payload, amount);
            payload.GetType().GetField("good").SetValue(payload, "Water");
            return payload;
        }
        object PayloadEvent()
        {
            var type = other.GetType("OtherMod.PayloadEvent", true);
            object e = Activator.CreateInstance(type);
            type.GetField("payload").SetValue(e, Payload(1));
            var list = (IList)Activator.CreateInstance(type.GetField("payloads").FieldType);
            list.Add(Payload(2)); list.Add(Payload(3));
            type.GetField("payloads").SetValue(e, list);
            return e;
        }

        test("MixedStorage's allocation action, loaded from bytes, reads back the same and is written as before", () => Quietly(() =>
        {
            object group = Grouped(Allocation(), Activator.CreateInstance(mod.GetType("BeaverBuddies.HeartbeatEvent", true), true));
            string json = Write(group);
            if (!json.Contains("\"MixedStorage.Multiplayer.StorageAllocationEvent, MixedStorage.MultiplayerBridge\""))
                throw new Exception("The action is not named the way MixedStorage's is:\n" + json);
            // A fresh binder too: the first thing it meets from the bridge is the action itself.
            foreach (object settings in new[] { null, Fresh() })
            {
                object back = Read(json, settings);
                if (Write(back) != json) throw new Exception("The allocation changed on the way back");
            }
            if (Write(group, WithoutBinder()) != json) throw new Exception("The allocation is written differently with the binder");
        }));

        test("Another mod's action and the payload classes it declares pass; a class of that mod no action carries does not", () => Quietly(() =>
        {
            string json = Write(Grouped(PayloadEvent()));
            if (Write(Read(json, Fresh())) != json) throw new Exception("The payload action changed on the way back");
            if (Write(Grouped(PayloadEvent()), WithoutBinder()) != json) throw new Exception("The payload action is written differently with the binder");
            object unreferenced = Activator.CreateInstance(other.GetType("OtherMod.Unreferenced", true));
            if (Refusal(Write(Grouped(Automation(unreferenced))), Fresh()) == null) throw new Exception("A class no action carries was created");
        }));

        test("A payload class passes even when it is the first thing a binder meets from its mod", () => Quietly(() =>
        {
            // The same answer whatever arrived first: the payload's assembly is scanned for the actions that carry it.
            string json = Write(Grouped(Automation(Payload(4))));
            object back = Read(json, Fresh());
            if (Write(back) != json) throw new Exception("The payload changed on the way back");
        }));
    }

    static Assembly Load(byte[] image)
    {
        var assembly = Assembly.Load(image);
        // Like MixedStorage: an assembly loaded from bytes is found by name only through this hook.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            new AssemblyName(args.Name).Name == assembly.GetName().Name ? assembly : null;
        return assembly;
    }

    // Fills every public field of an action with a value of its type. A field type with no sample fails the check,
    // so a new kind of field on an action gets a round trip before it ships.
    sealed class Samples
    {
        readonly Assembly mod;
        readonly Type eventType, vector3, vector3Int, ray, traceType;
        readonly Func<object[], object> automation;

        public Samples(Assembly mod, Type eventType, Type vector3, Type vector3Int, Type ray, Type traceType, Func<object[], object> automation)
        {
            this.mod = mod; this.eventType = eventType; this.vector3 = vector3; this.vector3Int = vector3Int;
            this.ray = ray; this.traceType = traceType; this.automation = automation;
        }

        public object Event(Type type)
        {
            object e = RuntimeHelpers.GetUninitializedObject(type);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsInitOnly) continue;
                field.SetValue(e, Value(field.FieldType, $"{type.Name}.{field.Name}"));
            }
            return e;
        }

        object Value(Type type, string where)
        {
            if (type == typeof(string)) return where;
            if (type == typeof(int)) return 7;
            if (type == typeof(int?)) return 11;
            if (type == typeof(long)) return 9L;
            if (type == typeof(float)) return 1.5f;
            if (type == typeof(double)) return 2.25;
            if (type == typeof(bool)) return true;
            if (type == typeof(Guid)) return new Guid("0b7e1c55-5f4e-4a5e-9d2c-3a1f6e0d9b21");
            if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Last();
            if (type == vector3Int) return Activator.CreateInstance(vector3Int, 1, -2, 3);
            if (type == vector3) return Activator.CreateInstance(vector3, 1.5f, 2f, -3.25f);
            if (type == ray) return Activator.CreateInstance(ray, Value(vector3, where), Activator.CreateInstance(vector3, 0f, 0f, 1f));
            if (type == traceType)
            {
                object trace = Activator.CreateInstance(traceType);
                traceType.GetField("message").SetValue(trace, where);
                return trace;
            }
            // Automation arguments as they arrive: whole numbers are long, fractions double, entities their id.
            if (type == typeof(object[])) return new object[] { 42L, 2.5, "text", true, null };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                if (element == eventType)
                {
                    // A group of a few actions, as each tick is sent.
                    list.Add(Activator.CreateInstance(mod.GetType("BeaverBuddies.HeartbeatEvent", true), true));
                    list.Add(automation(new object[] { 3L, "id" }));
                    list.Add(Event(mod.GetType("BeaverBuddies.Events.PlantingAreaMarkedEvent", true)));
                    return list;
                }
                list.Add(Value(element, where + "[0]"));
                list.Add(Value(element, where + "[1]"));
                return list;
            }
            // A game value such as UnlockableWorkerType: built through its fullest public constructor.
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            if (constructor != null && constructor.GetParameters().Length > 0 && type.Namespace?.StartsWith("System") != true)
                return constructor.Invoke(constructor.GetParameters().Select(p => Value(p.ParameterType, $"{where}.{p.Name}")).ToArray());
            throw new Exception($"No sample value for {where} of type {type}; add one to FrameTypeChecks");
        }
    }
}

// A harmless stand-in for a type the other player should never be able to create. It only counts its instances.
public class FrameSentinel
{
    public static int Created;
    public int value = 1;
    public FrameSentinel() { Interlocked.Increment(ref Created); }
}
