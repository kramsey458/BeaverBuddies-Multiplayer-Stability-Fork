using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

// Dev mode's tools change the game on the computer they are used on, and in co-op that desyncs it. The mod says so with
// a notice when dev mode is on in a co-op game. Runs the compiled notice against the game's own dev mode manager and
// notification service, with a co-op session installed and without.
internal static class DevModeWarningChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string NoticeKey = "BeaverBuddies.DevMode.CoopWarning";

    public static void Run(Assembly mod, Action<string, Action> test)
    {
        var debugging = Assembly.Load("Timberborn.Debugging");
        var managerType = debugging.GetType("Timberborn.Debugging.DevModeManager", true);
        var toggledType = debugging.GetType("Timberborn.Debugging.DevModeToggledEvent", true);
        var notificationsType = Assembly.Load("Timberborn.QuickNotificationSystem")
            .GetType("Timberborn.QuickNotificationSystem.QuickNotificationService", true);
        var locType = Assembly.Load("Timberborn.Localization").GetType("Timberborn.Localization.ILoc", true);
        var eventBusType = Assembly.Load("Timberborn.SingletonSystem").GetType("Timberborn.SingletonSystem.EventBus", true);
        var eventIo = mod.GetType("BeaverBuddies.IO.EventIO", true);
        var session = eventIo.GetField("instance", All);
        var singletons = (IDictionary)mod.GetType("BeaverBuddies.SingletonManager", true).GetField("map", All).GetValue(null);
        var localizationType = mod.GetType("BeaverBuddies.Util.RegisteredLocalizationService", true);
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", All);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        Type NoticeType() => mod.GetType("BeaverBuddies.Fixes.DevModeCoopWarning")
            ?? throw new Exception("the mod has no dev mode notice (BeaverBuddies.Fixes.DevModeCoopWarning)");

        // The notices the game's notification service was asked to show.
        var alerts = new List<(string text, bool warning)>();
        Action<object, object> onAlert = (sender, args) => alerts.Add(
            ((string)args.GetType().GetProperty("Text").GetValue(args), (bool)args.GetType().GetProperty("IsWarning").GetValue(args)));

        // The game's own event bus and dev mode manager of the game being run, wired to each other and to the notice.
        object bus = null, manager = null;

        // Builds the notice on a game with dev mode on or off, in a co-op game or single player, with or without the
        // mod's localization, and runs one step of its life on it.
        void Game(bool coop, bool devMode, bool localized, Action<object, Type> run)
        {
            object previousSession = session.GetValue(null);
            object previousLogger = pluginLogger.GetValue(null);
            bool hadLocalization = singletons.Contains(localizationType);
            object previousLocalization = hadLocalization ? singletons[localizationType] : null;
            alerts.Clear();
            try
            {
                // Plugin.Log* would otherwise reach Unity's native logger.
                pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
                session.SetValue(null, coop ? DispatchProxy.Create(eventIo, typeof(DevKeySessionProxy)) : null);
                singletons.Remove(localizationType);
                if (localized) Activator.CreateInstance(localizationType, DispatchProxy.Create(locType, typeof(DevModeLocProxy)));
                bus = Activator.CreateInstance(eventBusType);
                manager = Activator.CreateInstance(managerType, bus);
                managerType.GetField("<Enabled>k__BackingField", All).SetValue(manager, devMode);
                var notifications = Activator.CreateInstance(notificationsType);
                var alertSent = notificationsType.GetEvent("AlertSent");
                alertSent.AddEventHandler(notifications, Delegate.CreateDelegate(alertSent.EventHandlerType, onAlert.Target, onAlert.Method));
                var type = NoticeType();
                run(Activator.CreateInstance(type, bus, manager, notifications), type);
            }
            finally
            {
                singletons.Remove(localizationType);
                if (hadLocalization) singletons[localizationType] = previousLocalization;
                session.SetValue(null, previousSession);
                pluginLogger.SetValue(null, previousLogger);
                bus = manager = null;
            }
        }
        void PostLoad(object notice, Type type) => type.GetMethod("PostLoad").Invoke(notice, null);
        Action<object, Type> Toggled(bool enabled) => (notice, type) =>
            type.GetMethod("OnDevModeToggled").Invoke(notice, new[] { Activator.CreateInstance(toggledType, new object[] { enabled }) });
        void RequireOneNotice(string when)
        {
            Require(alerts.Count == 1, $"expected one notice {when}, got {alerts.Count}");
            Require(alerts[0].warning, $"the notice {when} is not a warning");
            Require(alerts[0].text == DevModeLocProxy.Echo(NoticeKey), $"the notice {when} shows '{alerts[0].text}'");
        }

        test("Dev mode notice: shown each time the game turns dev mode on in a co-op game, through its own event bus", () =>
        {
            // The only way the notice shows in the game today: every scene starts with dev mode off, and the player turns
            // it on with the ToggleDevMode key (DevModeManager posts DevModeToggledEvent on the scene's event bus).
            Game(coop: true, devMode: false, localized: true, (notice, type) =>
            {
                Require(type.GetInterfaces().Any(i => i.FullName == "Timberborn.SingletonSystem.ILoadableSingleton"),
                    "the notice is not an ILoadableSingleton, so the game never runs its Load and it never listens for dev mode");
                Require(type.GetInterfaces().Any(i => i.FullName == "Timberborn.SingletonSystem.IPostLoadableSingleton"),
                    "the notice is not an IPostLoadableSingleton");
                // The game's order: every singleton's Load, then every PostLoad (the event bus is one of them).
                type.GetMethod("Load").Invoke(notice, null);
                eventBusType.GetMethod("PostLoad").Invoke(bus, null);
                type.GetMethod("PostLoad").Invoke(notice, null);
                Require(alerts.Count == 0, "a co-op game that loaded with dev mode off showed the notice");
                // EnableSilently is Enable without its Unity log line.
                managerType.GetMethod("EnableSilently", All).Invoke(manager, null);
                RequireOneNotice("when the game turns dev mode on");
                managerType.GetMethod("Disable").Invoke(manager, null);
                Require(alerts.Count == 1, "turning dev mode off through the game showed the notice");
                managerType.GetMethod("EnableSilently", All).Invoke(manager, null);
                Require(alerts.Count == 2, $"expected a second notice when dev mode was turned on again, got {alerts.Count - 1}");
            });
        });
        test("Dev mode notice: a co-op game that loads with dev mode on shows one warning", () =>
        {
            // The game does not load a scene with dev mode on today (DevModeManager is made per scene, and only the
            // ToggleDevMode key turns it on). This guards the case in which another mod turns it on while loading.
            Game(coop: true, devMode: true, localized: true, PostLoad);
            RequireOneNotice("at load");
            Game(coop: true, devMode: false, localized: true, PostLoad);
            Require(alerts.Count == 0, "a co-op game with dev mode off showed the notice at load");
        });
        test("Dev mode notice: turning dev mode on in a co-op game shows it, turning it off does not", () =>
        {
            Game(coop: true, devMode: true, localized: true, Toggled(true));
            RequireOneNotice("when dev mode is turned on");
            Game(coop: true, devMode: false, localized: true, Toggled(false));
            Require(alerts.Count == 0, "turning dev mode off showed the notice");
        });
        test("Dev mode notice: never shown in single player", () =>
        {
            Game(coop: false, devMode: true, localized: true, PostLoad);
            Require(alerts.Count == 0, "single player showed the co-op dev mode notice at load");
            Game(coop: false, devMode: true, localized: true, Toggled(true));
            Require(alerts.Count == 0, "single player showed the co-op dev mode notice when dev mode was turned on");
        });
        test("Dev mode notice: missing text never stops the game from loading", () =>
        {
            Game(coop: true, devMode: true, localized: false, PostLoad);
            Require(alerts.Count == 0, "a notice was shown with no text");
        });
        test("Dev mode notice: its English text ships with the mod", () =>
        {
            string csv = Path.Combine(Path.GetDirectoryName(mod.Location), "Localizations", "enUS_BeaverBuddie.csv");
            var line = File.ReadLines(csv).FirstOrDefault(l => l.StartsWith(NoticeKey + ","));
            Require(line != null, "no " + NoticeKey + " line in " + csv);
            Require(line.Length > NoticeKey.Length + 10, "the notice's English text is empty");
        });
        test("Dev mode notice: loaded in a co-op game", () =>
        {
            // ReplayConfigurator binds the co-op services; the notice is one of them.
            var configure = mod.GetType("BeaverBuddies.ReplayConfigurator", true).GetMethod("Configure");
            var type = NoticeType();
            Require(Calls(configure).Any(m => m.Name == "Bind" && m.IsGenericMethod && m.GetGenericArguments()[0] == type),
                "ReplayConfigurator does not bind DevModeCoopWarning");
        });
    }

    /// <summary>The methods a method calls (call, callvirt), decoded from its IL, in order.</summary>
    private static List<MethodInfo> Calls(MethodInfo method)
    {
        var methods = new List<MethodInfo>();
        byte[] body = method.GetMethodBody().GetILAsByteArray();
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
                    if ((op == OpCodes.Call || op == OpCodes.Callvirt) && method.Module.ResolveMethod(BitConverter.ToInt32(body, position)) is MethodInfo called)
                        methods.Add(called);
                    position += 4;
                    break;
                default: position += 4; break;
            }
        }
        return methods;
    }
}

// The game's localization, answering each key with a text made from it.
public class DevModeLocProxy : DispatchProxy
{
    public static string Echo(string key) => "text of " + key;
    protected override object Invoke(MethodInfo targetMethod, object[] args) =>
        targetMethod.Name == "T" && args.Length == 1 && args[0] is string key ? Echo(key) : throw new NotSupportedException(targetMethod.Name);
}
