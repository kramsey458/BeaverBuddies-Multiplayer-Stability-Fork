using System.Reflection;

// The desync dialog in the compiled mod: that it follows DesyncDialogPlan, and that a guest's reconnect joins the way it
// joined. StabilityTests checks the decisions themselves; the dialog and Steam cannot run here, so this reads the
// instructions of the code that uses them.
internal static class DesyncDialogChecks
{
    public static void Run(Assembly mod, Action<string, Action> test)
    {
        const string plan = "BeaverBuddies.Connect.DesyncDialogPlan";
        const string service = "BeaverBuddies.Connect.ClientConnectionService";

        test("The desync dialog asks DesyncDialogPlan for its report button and the Enable Logging sentence", () =>
        {
            var dialog = IlScan.Of(mod.GetType("BeaverBuddies.Events.ClientDesyncedEvent", true)!);
            foreach (string decision in new[] { "ReportButtonKey", "AsksToEnableLogging" })
                if (!dialog.Values.Any(members => IlScan.Names(members, plan, decision)))
                    throw new Exception($"The dialog decides for itself instead of asking DesyncDialogPlan.{decision}");
            // A guest's reconnect goes through the service's Reconnect, which knows how this guest joined; never the
            // saved address directly.
            if (!dialog.Values.Any(members => IlScan.Names(members, service, "Reconnect")))
                throw new Exception("A guest's Reconnect does not go through ClientConnectionService.Reconnect");
            if (dialog.Values.Any(members => members.Any(m => m is MethodBase method && method.DeclaringType?.FullName == service &&
                    method.Name == "ConnectOrShowFailureMessage" && method.GetParameters().Length == 0)))
                throw new Exception("A guest's Reconnect still dials the saved address whatever the route");
        });

        test("Joining records how the guest joined, and Reconnect follows DesyncDialogPlan", () =>
        {
            var type = mod.GetType(service, true)!;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            MethodBase Method(string name, string parameterType) => type.GetMethods(all)
                .Single(m => m.Name == name && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == parameterType);
            if (!IlScan.Names(IlScan.Members(Method("TryToConnect", "CSteamID")), "BeaverBuddies.Connect.JoinRoute", "ViaSteam"))
                throw new Exception("Joining over Steam is not remembered");
            if (!IlScan.Names(IlScan.Members(Method("TryToConnect", "String")), "BeaverBuddies.Connect.JoinRoute", "ViaAddress"))
                throw new Exception("Joining by address is not remembered");
            var reconnect = IlScan.Members(type.GetMethod("Reconnect", all, Type.EmptyTypes)
                ?? throw new Exception("ClientConnectionService has no Reconnect"));
            if (!IlScan.Names(reconnect, plan, "Reconnect")) throw new Exception("Reconnect does not ask DesyncDialogPlan.Reconnect");
            if (!IlScan.Names(reconnect, "Steamworks.SteamMatchmaking", "JoinLobby")) throw new Exception("Reconnect never joins the host's Steam lobby");
        });
    }
}
