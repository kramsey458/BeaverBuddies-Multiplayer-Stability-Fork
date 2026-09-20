using System.Reflection;

// Controls that stop answering after a message is closed. Runs the real compiled mod's decisions: whether the
// options menu can still be opened once multiplayer has stopped, and which installed session a late reset may end.
internal static class MenuRecoveryChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var replayService = mod.GetType("BeaverBuddies.ReplayService", true);
        var replayEvent = mod.GetType("BeaverBuddies.Events.ReplayEvent", true);
        var optionsPatcher = mod.GetType("BeaverBuddies.Events.GameOptionsBoxShowPatcher", true);
        var eventIo = mod.GetType("BeaverBuddies.IO.EventIO", true);
        var failure = replayService.GetField("<HasReplayFailure>k__BackingField", all);

        // Plugin.Log* would otherwise reach Unity's native logger.
        var pluginLogger = mod.GetType("BeaverBuddies.Plugin", true).GetField("logger", all);
        var loggerType = mod.GetType("BeaverBuddies.Util.Logging.ILogger", true);
        object previousLogger = pluginLogger.GetValue(null);

        void Quiet(Action run)
        {
            pluginLogger.SetValue(null, DispatchProxy.Create(loggerType, typeof(QuietLoggerProxy)));
            try { run(); }
            finally
            {
                pluginLogger.SetValue(null, previousLogger);
                failure.SetValue(null, false);
            }
        }
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        bool MenuMayOpen() => (bool)optionsPatcher.GetMethod("Prefix", all).Invoke(null, null);
        bool ActionMayRun() => (bool)replayEvent.GetMethod("DoPrefix", all).Invoke(null, new object[] { null });

        // Regression: after "Multiplayer has stopped" the message told the player to return to the main menu, but
        // the hard stop also blocked the menu, so Escape and the options button did nothing and the game could only
        // be killed.
        test("After a failed multiplayer action the options menu still opens", () => Quiet(() =>
        {
            failure.SetValue(null, true);
            Require(MenuMayOpen(), "the options menu was blocked by the hard stop");
        }));
        test("After a failed multiplayer action gameplay actions stay blocked", () => Quiet(() =>
        {
            failure.SetValue(null, true);
            Require(!ActionMayRun(), "an action ran after multiplayer had stopped");
            failure.SetValue(null, false);
            Require(ActionMayRun(), "with no failure the game's own behaviour must apply");
        }));

        // A session that ends late (the receive thread's error, dispatched a frame after a rejoin) must never end
        // the session that replaced it. Also: a reset of a session that is no longer installed does nothing.
        object Proxy() => DispatchProxy.Create(eventIo, typeof(EventIoProxy));
        void Set(object io) => eventIo.GetMethod("Set").Invoke(null, new[] { io });
        void ResetIf(object io) => eventIo.GetMethod("ResetIf").Invoke(null, new[] { io });
        bool IsNull() => (bool)eventIo.GetProperty("IsNull").GetValue(null);

        test("ResetIf ends the installed session and closes it", () => Quiet(() =>
        {
            var installed = Proxy();
            Set(installed);
            try
            {
                ResetIf(installed);
                Require(IsNull(), "the installed session was not removed");
                Require(((EventIoProxy)installed).Closed == 1, "the removed session was not closed exactly once");
            }
            finally { eventIo.GetMethod("Reset").Invoke(null, null); }
        }));
        test("ResetIf leaves a newer session alone", () => Quiet(() =>
        {
            var older = Proxy(); var newer = Proxy();
            Set(newer);
            try
            {
                ResetIf(older);
                Require(!IsNull(), "an older session tore down the newer one");
                Require(((EventIoProxy)newer).Closed == 0 && ((EventIoProxy)older).Closed == 0, "a session was closed by mistake");
                ResetIf(null);
                Require(!IsNull(), "a null reset removed the installed session");
            }
            finally { eventIo.GetMethod("Reset").Invoke(null, null); }
        }));

        // A session with no network behind it is over; one that is still running is not. Left installed, a dead one
        // queues every action for nobody and pauses the game for good.
        test("A host that never started listening is a session that is over", () =>
        {
            var host = Activator.CreateInstance(mod.GetType("BeaverBuddies.IO.ServerEventIO", true));
            Require((bool)eventIo.GetProperty("IsSessionOver").GetValue(host), "no network but not over");
        });
    }
}

public class EventIoProxy : DispatchProxy
{
    public int Closed;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "Close") Closed++;
        return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
