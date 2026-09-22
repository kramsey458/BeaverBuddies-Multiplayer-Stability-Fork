using System.Reflection;

// Tick once: the speed panel's pause key (period by default, not dev mode only) calls Ticker.TickOnce when the game
// is paused. That runs a whole tick through TickableBucketService.TickOnce, which never reaches TickBuckets, where
// the ReplayService ticks, so in a co-op game the tick ran on the pressing computer only. Runs the real compiled
// mod's prefix as Harmony would (this harness cannot install Harmony): with a co-op session installed and without.
internal static class TickOnceChecks
{
    const string NoticeKey = "BeaverBuddies.TickOnce.CoopBlocked";

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var tickerType = Assembly.Load("Timberborn.TickSystem").GetType("Timberborn.TickSystem.Ticker", true)!;
        var panelType = Assembly.Load("Timberborn.TimeSystemUI").GetType("Timberborn.TimeSystemUI.SpeedControlPanel", true)!;
        var notificationsType = Assembly.Load("Timberborn.QuickNotificationSystem")
            .GetType("Timberborn.QuickNotificationSystem.QuickNotificationService", true)!;
        var locType = Assembly.Load("Timberborn.Localization").GetType("Timberborn.Localization.ILoc", true)!;
        var eventIo = mod.GetType("BeaverBuddies.IO.EventIO", true)!;
        var singletons = mod.GetType("BeaverBuddies.SingletonManager", true)!;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true)!.GetField("logger", all)!;
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true)!;
        void Quiet(Action run)
        {
            object previousLogger = pluginLogger.GetValue(null)!;
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            singletons.GetMethod("Reset")!.Invoke(null, null);
            try { run(); }
            finally
            {
                eventIo.GetMethod("Reset")!.Invoke(null, null);
                singletons.GetMethod("Reset")!.Invoke(null, null);
                pluginLogger.SetValue(null, previousLogger);
            }
        }

        test("Tick once: the game's pause-or-tick-once key still reaches Ticker.TickOnce", () =>
        {
            var tickOnce = tickerType.GetMethod("TickOnce", all, Type.EmptyTypes);
            Require(tickOnce != null, "Ticker.TickOnce is gone");
            var pauseOrTickOnce = panelType.GetMethod("PauseOrTickOnce", all, Type.EmptyTypes);
            Require(pauseOrTickOnce != null, "SpeedControlPanel.PauseOrTickOnce is gone");
            // If a game update routes the key somewhere else, the mod's patch no longer covers it.
            Require(Calls(pauseOrTickOnce!, tickOnce!),
                "SpeedControlPanel.PauseOrTickOnce no longer calls Ticker.TickOnce: check what the key runs now");
        });

        // The mod's prefix on Ticker.TickOnce, found by its Harmony target rather than by name.
        List<Type> ModTypes()
        {
            try { return mod.GetTypes().ToList(); }
            catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToList(); }
        }
        bool TargetsTickOnce(CustomAttributeData a) => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"
            && a.ConstructorArguments.Count == 2
            && Equals(a.ConstructorArguments[0].Value, tickerType) && Equals(a.ConstructorArguments[1].Value, "TickOnce");
        MethodInfo Prefix()
        {
            var patchers = ModTypes().Where(t => t.GetCustomAttributesData().Any(TargetsTickOnce)).ToList();
            Require(patchers.Count == 1, $"expected one mod patch on Ticker.TickOnce, found {patchers.Count}");
            var prefix = patchers[0].GetMethod("Prefix", all);
            Require(prefix != null && prefix.ReturnType == typeof(bool) && prefix.GetParameters().Length == 0,
                "the Ticker.TickOnce patch has no parameterless bool Prefix");
            return prefix!;
        }
        bool RunPrefix() => (bool)Prefix().Invoke(null, null)!;

        // It replaces the method in a co-op game but also records the shared pause, so it is a recording prefix and
        // runs first (see RecordingPriorityChecks): no other mod's prefix runs on the pressing computer alone.
        test("Tick once: the mod's prefix on Ticker.TickOnce runs first (Priority.First)", () =>
        {
            var priority = Prefix().GetCustomAttributesData()
                .Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPriority")
                .Select(a => (int?)(int)a.ConstructorArguments[0].Value!).FirstOrDefault();
            // HarmonyLib.Priority.First
            Require(priority == 800, "the prefix that skips Ticker.TickOnce and records the pause is not [HarmonyPriority(Priority.First)]");
        });

        // Stands in for the game's objects the notice uses: the localization service and the notification service.
        List<(string text, bool warning)> alerts = new();
        Action<object, object> onAlert = (sender, args) => alerts.Add(
            ((string)args.GetType().GetProperty("Text")!.GetValue(args)!, (bool)args.GetType().GetProperty("IsWarning")!.GetValue(args)!));
        object InstallNotice()
        {
            var notifications = Activator.CreateInstance(notificationsType)!;
            var alertSent = notificationsType.GetEvent("AlertSent")!;
            alertSent.AddEventHandler(notifications, Delegate.CreateDelegate(alertSent.EventHandlerType!, onAlert.Target, onAlert.Method));
            return Activator.CreateInstance(mod.GetType("BeaverBuddies.Fixes.TickOnceCoopNotice", true)!, notifications)!;
        }
        void InstallLocalization()
        {
            var loc = DispatchProxy.Create(locType, typeof(KeyEchoLocProxy));
            Activator.CreateInstance(mod.GetType("BeaverBuddies.Util.RegisteredLocalizationService", true)!, loc);
        }
        void InstallSession() => eventIo.GetMethod("Set")!.Invoke(null, new[] { DispatchProxy.Create(eventIo, typeof(EventIoProxy)) });

        test("Tick once: refused in a co-op game, with one warning notice", () => Quiet(() =>
        {
            alerts.Clear();
            InstallSession(); InstallLocalization(); InstallNotice();
            Require(!RunPrefix(), "tick once ran in a co-op game: it ticks this computer only");
            Require(alerts.Count == 1, $"expected one notice, got {alerts.Count}");
            Require(alerts[0].warning, "the notice is not a warning");
            Require(alerts[0].text == KeyEchoLocProxy.Echo(NoticeKey), "the notice shows " + alerts[0].text);
        }));
        test("Tick once: refused in a co-op game even when the notice or its text is missing", () => Quiet(() =>
        {
            alerts.Clear();
            InstallSession();
            Require(!RunPrefix(), "tick once ran in a co-op game with no notice");
            InstallNotice();
            Require(!RunPrefix(), "tick once ran in a co-op game with no localization");
            Require(alerts.Count == 0, "a notice was shown with no text");
        }));
        test("Tick once: runs the game's own method in single player", () => Quiet(() =>
        {
            alerts.Clear();
            InstallLocalization(); InstallNotice();
            Require((bool)eventIo.GetProperty("IsNull")!.GetValue(null)!, "a session was left installed");
            Require(RunPrefix(), "tick once was refused in single player");
            Require(alerts.Count == 0, "single player showed the co-op notice");
        }));
        // In co-op this computer's speed is often 0 while the shared game runs (a guest waiting for the host's next
        // tick, a host waiting for a slow guest), and then the panel calls TickOnce for a key pressed to pause. A loaded
        // ReplayService with the given shared (target) speed, and a session that records like a guest or a host.
        var replayType = mod.GetType("BeaverBuddies.ReplayService", true)!;
        var replayLoaded = replayType.GetField("<IsLoaded>k__BackingField", all)!;
        var behaviorType = mod.GetType("BeaverBuddies.IO.UserEventBehavior", true)!;
        object InstallReplay(float sharedSpeed)
        {
            object replay = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(replayType);
            foreach (string queue in new[] { "eventsToSend", "eventsToPlay" })
            {
                var field = replayType.GetField(queue, all)!;
                field.SetValue(replay, Activator.CreateInstance(field.FieldType));
            }
            replayType.GetField("<TargetSpeed>k__BackingField", all)!.SetValue(replay, sharedSpeed);
            singletons.GetMethod("RegisterSingleton")!.MakeGenericMethod(replayType).Invoke(null, new[] { replay });
            replayLoaded.SetValue(null, true);
            return replay;
        }
        List<object> Queued(object replay, string queue) =>
            ((System.Collections.IEnumerable)replayType.GetField(queue, all)!.GetValue(replay)!).Cast<object>().ToList();
        void InstallSessionAs(string behavior)
        {
            var io = DispatchProxy.Create(eventIo, typeof(BehaviorEventIoProxy));
            ((BehaviorEventIoProxy)io).Behavior = Enum.Parse(behaviorType, behavior);
            eventIo.GetMethod("Set")!.Invoke(null, new[] { io });
        }
        void RequirePauseRequest(List<object> queued, string who)
        {
            Require(queued.Count == 1, $"{who}: expected one recorded pause, got {queued.Count} events");
            Require(queued[0].GetType().FullName == "BeaverBuddies.Events.SpeedSetEvent",
                $"{who}: recorded {queued[0].GetType().FullName}, not a SpeedSetEvent");
            float speed = (float)queued[0].GetType().GetField("speed")!.GetValue(queued[0])!;
            Require(speed == 0, $"{who}: the recorded speed is {speed}, not a pause");
        }

        test("Tick once: while the shared co-op game runs and this computer waits, the key asks everyone to pause", () => Quiet(() =>
        {
            try
            {
                // A guest: the pause goes to the host, which plays it for everyone.
                alerts.Clear();
                InstallSessionAs("Send"); InstallLocalization(); InstallNotice();
                object guest = InstallReplay(2);
                Require(!RunPrefix(), "a guest waiting for the host ran tick once: it ticks this computer only");
                Require(alerts.Count == 0, "a guest that asked to pause was shown the tick once notice");
                RequirePauseRequest(Queued(guest, "eventsToSend"), "guest");
                Require(Queued(guest, "eventsToPlay").Count == 0, "guest: an event was queued to play locally");

                // A host holding for a slow guest: the pause is queued like the host's own pause button.
                eventIo.GetMethod("Reset")!.Invoke(null, null);
                singletons.GetMethod("Reset")!.Invoke(null, null);
                alerts.Clear();
                InstallSessionAs("QueuePlay"); InstallLocalization(); InstallNotice();
                object host = InstallReplay(1);
                Require(!RunPrefix(), "a host holding for a guest ran tick once: it ticks this computer only");
                Require(alerts.Count == 0, "a host that asked to pause was shown the tick once notice");
                RequirePauseRequest(Queued(host, "eventsToPlay"), "host");
                Require(Queued(host, "eventsToSend").Count == 0, "host: the pause skipped the host's own queue");
            }
            finally { replayLoaded.SetValue(null, false); }
        }));
        test("Tick once: while the shared co-op game is paused, the key shows the notice and records nothing", () => Quiet(() =>
        {
            try
            {
                alerts.Clear();
                InstallSessionAs("Send"); InstallLocalization(); InstallNotice();
                object replay = InstallReplay(0);
                Require(!RunPrefix(), "tick once ran in a paused co-op game: it ticks this computer only");
                Require(alerts.Count == 1, $"expected one notice, got {alerts.Count}");
                Require(Queued(replay, "eventsToSend").Count == 0 && Queued(replay, "eventsToPlay").Count == 0,
                    "an event was recorded for a key pressed in a paused game");
            }
            finally { replayLoaded.SetValue(null, false); }
        }));

        // After a failed multiplayer action the session is removed but the game is held still (TickBuckets is
        // blocked too); tick once must not get around that.
        var failure = replayType.GetField("<HasReplayFailure>k__BackingField", all)!;
        test("Tick once: refused after a failed multiplayer action, with no co-op notice", () => Quiet(() =>
        {
            alerts.Clear();
            InstallLocalization(); InstallNotice();
            failure.SetValue(null, true);
            try
            {
                Require(!RunPrefix(), "tick once ran after multiplayer had stopped");
                Require(alerts.Count == 0, "the co-op notice was shown after multiplayer had stopped");
            }
            finally { failure.SetValue(null, false); }
        }));

        test("Tick once: the notice is bound in the game scene", () =>
        {
            var configure = mod.GetType("BeaverBuddies.ReplayConfigurator", true)!.GetMethod("Configure")!;
            Require(CallsGeneric(configure, "Bind", "BeaverBuddies.Fixes.TickOnceCoopNotice"),
                "ReplayConfigurator does not bind TickOnceCoopNotice, so the notice never shows");
        });
        test("Tick once: the notice text is in the built English localization", () =>
        {
            string file = Path.Combine(Path.GetDirectoryName(mod.Location)!, "Localizations", "enUS_BeaverBuddie.csv");
            Require(File.Exists(file), "no localization file at " + file);
            string line = File.ReadLines(file).FirstOrDefault(l => l.StartsWith(NoticeKey + ","));
            Require(line != null, NoticeKey + " is missing from " + file);
            string text = line!.Substring(NoticeKey.Length + 1);
            Require(text.StartsWith("\"") && text.IndexOf("\",", 1) > 1, NoticeKey + " has no quoted text");
        });
    }

    // True if the method's body calls this method (call or callvirt).
    static bool Calls(MethodInfo method, MethodInfo target)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            MethodBase called;
            try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); }
            catch (Exception) { continue; }
            if (called.Module == target.Module && called.MetadataToken == target.MetadataToken) return true;
        }
        return false;
    }

    // True if the method's body calls a generic method with this name and type argument (call or callvirt).
    static bool CallsGeneric(MethodInfo method, string name, string typeArgument)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) continue;
            MethodBase called;
            try { called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); }
            catch (Exception) { continue; }
            if (called is MethodInfo m && m.IsGenericMethod && m.Name == name
                && m.GetGenericArguments().Any(t => t.FullName == typeArgument)) return true;
        }
        return false;
    }
}

// A co-op session that records the player's actions the way a guest (Send) or a host (QueuePlay) does.
public class BehaviorEventIoProxy : DispatchProxy
{
    public object Behavior;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "get_UserEventBehavior") return Behavior;
        return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

public class KeyEchoLocProxy : DispatchProxy
{
    public static string Echo(string key) => "loc:" + key;
    protected override object Invoke(MethodInfo method, object[] args) =>
        method.Name == "T" && args.Length == 1 && args[0] is string key ? Echo(key) : throw new NotSupportedException(method.Name);
}
