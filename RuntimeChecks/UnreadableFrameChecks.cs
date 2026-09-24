using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

// What a player does with a received frame it cannot read: one whose "$type" the binder refuses (see
// FrameTypeChecks), one from a mod this game does not have, one with no type at all, a group of actions that holds
// an empty entry or another group, or an action with a value of the wrong kind. The frames are read by the
// real ClientEventIO and ServerEventIO over a real TimberClient and TimberServer, from ReadEvents, which runs inside a
// tick with nothing to catch an exception, so it must never throw.
internal static class UnreadableFrameChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var eventType = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var groupedType = mod.GetType("BeaverBuddies.GroupedEvent", true);
        var heartbeatType = mod.GetType("BeaverBuddies.HeartbeatEvent", true);
        var automationType = mod.GetType("BeaverBuddies.Events.AutomationEvent", true);
        var jsonType = mod.GetType("BeaverBuddies.IO.JsonSettings", true);
        var clientIOType = mod.GetType("BeaverBuddies.IO.ClientEventIO", true);
        var serverIOType = mod.GetType("BeaverBuddies.IO.ServerEventIO", true);
        var net = Assembly.Load("TimberNet");
        var netBase = net.GetType("TimberNet.TimberNetBase", true);
        var jObject = jsonType.BaseType.Assembly.GetType("Newtonsoft.Json.Linq.JObject", true);

        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        // Runs with a logger that keeps what is logged, instead of Unity's native one.
        List<string> Logged(Action run)
        {
            object previous = pluginLogger.GetValue(null);
            object logger = DispatchProxy.Create(loggerType, typeof(RecordingLoggerProxy));
            pluginLogger.SetValue(null, logger);
            try { run(); } finally { pluginLogger.SetValue(null, previous); }
            return ((RecordingLoggerProxy)logger).Lines;
        }

        string Write(object replayEvent) =>
            (string)jsonType.GetMethod("Serialize").MakeGenericMethod(eventType).Invoke(null, new[] { replayEvent });
        object Frame(string json) => jObject.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { json });
        object Heartbeat(int? randomS0Before = null)
        {
            object e = Activator.CreateInstance(heartbeatType, true);
            eventType.GetField("randomS0Before").SetValue(e, randomS0Before);
            return e;
        }
        object Automation(params object[] arguments)
        {
            object e = Activator.CreateInstance(automationType);
            automationType.GetField("entityID").SetValue(e, Guid.Empty.ToString());
            automationType.GetField("methodKey").SetValue(e, "Timberborn.AutomationBuildings.Lever.SwitchState");
            automationType.GetField("arguments").SetValue(e, arguments);
            return e;
        }
        // One tick's actions, as the host sends them.
        object GroupOf(int tick, params object[] events)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(eventType));
            foreach (object e in events) list.Add(e);
            object group = Activator.CreateInstance(groupedType, list);
            eventType.GetField("ticksSinceLoad").SetValue(group, tick);
            return group;
        }
        string Group(int tick, params object[] events) => Write(GroupOf(tick, events));

        string Renamed(string json, string from, string to) =>
            Replaced(json, $"\"{from}\"", $"\"{to}\"");
        string Replaced(string json, string from, string to) =>
            json.Contains(from) ? json.Replace(from, to) : throw new Exception($"{from} is not in {json}");

        const int Tick = 3;
        object Readable() => Frame(Group(Tick, Heartbeat()));
        // Unreadable frames, with a piece of text the reason for dropping each must contain.
        var unreadable = new List<(string Kind, Func<object> Make, string[] Named)>
        {
            ("a type the binder refuses", () => Frame(Group(Tick, Heartbeat(), Automation(new FrameSentinel()))),
                new[] { nameof(FrameSentinel), "RuntimeChecks" }),
            ("an action from a mod this game does not have", () => Frame(Renamed(Group(Tick, Heartbeat()),
                    heartbeatType.FullName + ", " + heartbeatType.Assembly.GetName().Name, "MissingMod.Actions.MissingEvent, MissingMod.Actions")),
                new[] { "MissingMod.Actions.MissingEvent", "MissingMod.Actions" }),
            ("a frame with no type", () => Frame($"{{\"ticksSinceLoad\": {Tick}}}"), Array.Empty<string>()),
            // Both are read fine, but no action in them can be played: replaying them would fail and stop the session.
            ("a group holding an empty entry", () => Frame(Group(Tick, Heartbeat(), null)), new[] { "group of actions" }),
            ("a group inside a group", () => Frame(Group(Tick, Heartbeat(), GroupOf(Tick, Heartbeat()))), new[] { "group of actions" }),
            // randomS0Before is a number (int?). Only the heartbeat's is set, so only it is given text; the group's is null.
            ("an action with a value of the wrong kind", () => Frame(Replaced(Group(Tick, Heartbeat(12345)),
                    "\"randomS0Before\": 12345", "\"randomS0Before\": \"x\"")),
                new[] { "randomS0Before" }),
        };

        // The real event IO around a real TimberClient or TimberServer that has received these frames. Nothing is
        // connected: the frames are put where the receive thread would have put them.
        (object IO, object Net, List<string> Faults) Received(bool asGuest, params object[] frames)
        {
            object netObject = asGuest
                ? Activator.CreateInstance(net.GetType("TimberNet.TimberClient", true),
                    DispatchProxy.Create(net.GetType("TimberNet.ISocketStream", true), typeof(InertProxy)))
                : Activator.CreateInstance(net.GetType("TimberNet.TimberServer", true),
                    DispatchProxy.Create(net.GetType("TimberNet.ISocketListener", true), typeof(InertProxy)), null, null);
            netBase.GetProperty("Started").SetValue(netObject, true);
            var received = (IList)netBase.GetField("receivedEvents", all).GetValue(netObject);
            foreach (object frame in frames) received.Add(frame);
            var faults = new FaultRecorder();
            var onFault = netBase.GetEvent("OnSessionFault");
            onFault.AddEventHandler(netObject, Delegate.CreateDelegate(onFault.EventHandlerType, faults, nameof(FaultRecorder.Record)));
            object io = RuntimeHelpers.GetUninitializedObject(asGuest ? clientIOType : serverIOType);
            io.GetType().GetProperty("NetBase").SetValue(io, netObject);
            return (io, netObject, faults.Reasons);
        }
        List<object> Read(object io)
        {
            try { return ((IEnumerable)io.GetType().GetMethod("ReadEvents").Invoke(io, new object[] { Tick })).Cast<object>().ToList(); }
            catch (TargetInvocationException e) { throw new Exception("ReadEvents threw: " + e.InnerException?.Message, e.InnerException); }
        }

        test("A guest stops the session when the host sends a frame it cannot read, and says which type", () =>
        {
            // Control: frames that can be read are read, and nothing stops.
            Logged(() =>
            {
                var (io, _, faults) = Received(true, Readable(), Readable());
                if (Read(io).Count != 2 || faults.Count != 0) throw new Exception("Readable frames were not read as before");
            });
            foreach (var bad in unreadable)
            {
                List<object> events = null;
                List<string> faults = null;
                List<string> log = Logged(() =>
                {
                    var (io, _, recorded) = Received(true, Readable(), bad.Make(), Readable());
                    faults = recorded;
                    events = Read(io);
                });
                // The host has played everything it sent, so this game can no longer keep up with it.
                if (faults.Count != 1) throw new Exception($"With {bad.Kind}, the session fault was raised {faults.Count} times");
                // Nothing of that tick is played: the session is over and part of the tick would be worse.
                if (events.Count != 0) throw new Exception($"With {bad.Kind}, {events.Count} actions of the tick were still played");
                // The same words as BeaverBuddies-MultiColony, then why. The whole shared sentence is checked, so the
                // two forks cannot drift apart in its second half without this failing.
                if (!faults[0].StartsWith("An action from the host could not be read, so this game would no longer match the host's. "))
                    throw new Exception($"With {bad.Kind}, the reason does not say what happened: {faults[0]}");
                foreach (string name in bad.Named)
                    if (!faults[0].Contains(name)) throw new Exception($"With {bad.Kind}, the reason does not name {name}: {faults[0]}");
                if (!log.Any(line => line.StartsWith("LogError"))) throw new Exception($"With {bad.Kind}, no error was logged");
            }
            // It leaves as if it had quit (ReplayService.AbortReplay, leaveQuietly): the host and the others play on, since
            // nothing went wrong for them. A guest that read everything has not left.
            PropertyInfo left = clientIOType.GetProperty("LeftOverUnreadableAction")
                ?? throw new Exception("A guest does not say it left over an action it could not read");
            Logged(() =>
            {
                var (readIO, _, _) = Received(true, Readable());
                Read(readIO);
                if ((bool)left.GetValue(readIO)) throw new Exception("A guest that read every frame says it left over one");
                var (badIO, _, _) = Received(true, unreadable[0].Make());
                Read(badIO);
                if (!(bool)left.GetValue(badIO)) throw new Exception("A guest that could not read a frame does not say it left over it");
            });
            // The guest's fault handler passes that on, and a quiet leave closes the connection: only a real failure sends
            // the host the fault that stops everyone (AbortSession).
            List<MethodBase> Calls(MethodBase method) =>
                IlScan.Instructions(method).Where(i => i.Calls && i.Member is MethodBase).Select(i => (MethodBase)i.Member).ToList();
            const BindingFlags declared = all | BindingFlags.DeclaredOnly;
            var handlers = new[] { clientIOType }.Concat(clientIOType.GetNestedTypes(all))
                .SelectMany(t => t.GetMethods(declared))
                .Where(m => m.GetMethodBody() != null && Calls(m).Any(c => c.Name == "AbortReplay"))
                .ToList();
            if (handlers.Count != 1 || !Calls(handlers[0]).Any(c => c.Name == "get_LeftOverUnreadableAction")
                || Calls(handlers[0]).Single(c => c.Name == "AbortReplay").GetParameters().Length != 2)
                throw new Exception("The guest's fault handler no longer says whether it left over an unreadable action");
            var abort = mod.GetType("BeaverBuddies.ReplayService", true).GetMethod("AbortReplay", all, new[] { typeof(string), typeof(bool) })
                ?? throw new Exception("ReplayService.AbortReplay(string, bool) is gone");
            var abortCalls = Calls(abort).Select(c => c.Name).ToList();
            if (!abortCalls.Contains("Close") || abortCalls.Count(n => n == "AbortSession") != 2)
                throw new Exception("AbortReplay no longer closes quietly for a guest that left: " + string.Join(", ", abortCalls));
        });

        test("A host ignores a guest's frame it cannot read, keeps the rest and logs which type", () =>
        {
            object[] frames = new[] { Readable() }.Concat(unreadable.Select(bad => bad.Make())).Append(Readable()).ToArray();
            List<object> events = null;
            List<string> faults = null;
            object netObject = null;
            List<string> log = Logged(() =>
            {
                var (io, received, recorded) = Received(false, frames);
                faults = recorded;
                netObject = received;
                events = Read(io);
            });
            // A guest's action is played only once the host has read it, so no player went out of step. Ending the
            // session here would let any guest end it.
            if (faults.Count != 0) throw new Exception("The host raised a session fault: " + faults[0]);
            if ((bool)netBase.GetProperty("IsStopped").GetValue(netObject)) throw new Exception("The host's session was stopped");
            if (events.Count != 2) throw new Exception($"{events.Count} actions were read; the 2 readable ones should be");
            string warnings = string.Join("\n", log.Where(line => line.StartsWith("LogWarning")));
            foreach (string name in unreadable.SelectMany(bad => bad.Named))
                if (!warnings.Contains(name)) throw new Exception($"The log does not name {name}:\n{warnings}");
        });
    }
}

public class RecordingLoggerProxy : DispatchProxy
{
    public readonly List<string> Lines = new();
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        Lines.Add(targetMethod.Name + ": " + args?.FirstOrDefault());
        return null;
    }
}

// An interface whose members do nothing and return defaults.
public class InertProxy : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args) =>
        targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
}

public class FaultRecorder
{
    public readonly List<string> Reasons = new();
    public void Record(string reason) => Reasons.Add(reason);
}
