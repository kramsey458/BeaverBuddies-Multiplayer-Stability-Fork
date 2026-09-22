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

            // Asking is not enough: what the plan answers has to decide. The sentence is added only on the branch
            // taken when AsksToEnableLogging is true, and the report button only after a test of the key it chose.
            var code = dialog.Keys.ToDictionary(method => method, IlScan.Instructions);
            const string sentence = "BeaverBuddies.ClientDesynced.NeedToEnableTracing";
            var sentences = code.SelectMany(pair => pair.Value.Where(i => i.Text == sentence).Select(i => (Method: pair.Key, At: i.Offset))).ToArray();
            if (sentences.Length != 1) throw new Exception($"The dialog names the Enable Logging sentence {sentences.Length} times, expected once");
            var body = code[sentences[0].Method];
            int ask = body.FindIndex(i => i.Calls && i.Is(plan, "AsksToEnableLogging"));
            // The Release Steam configuration is not optimized: there the answer goes through a local first
            // (stloc, ldloc) before the branch that tests it.
            int test = ask + 1;
            while (test < body.Count && (body[test].Op.Name!.StartsWith("stloc") || body[test].Op.Name!.StartsWith("ldloc"))) test++;
            if (ask < 0 || test >= body.Count || !body[test].BranchesIfFalse ||
                !(body[ask].Offset < sentences[0].At && sentences[0].At < body[test].Target))
                throw new Exception("The Enable Logging sentence is not added only when DesyncDialogPlan.AsksToEnableLogging is true");
            var reportButton = code.SelectMany(pair => pair.Value.Where(i => i.Calls && i.Member?.Name == "SetInfoButton")
                .Select(i => (Method: pair.Key, At: i.Offset))).ToArray();
            foreach (var (method, at) in reportButton)
            {
                int key = code[method].FindIndex(i => i.Calls && i.Is(plan, "ReportButtonKey"));
                if (key < 0 || !code[method].Skip(key).Any(i => i.BranchesIfFalse && i.Offset < at && at < i.Target))
                    throw new Exception("The report button is added whatever DesyncDialogPlan.ReportButtonKey says");
            }
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
            // A direct guest dials the address the plan chose (the one it typed), never the one in the settings.
            var steps = IlScan.Instructions(type.GetMethod("Reconnect", all, Type.EmptyTypes)!);
            if (steps.Any(i => i.Calls && i.Is(service, "ConnectOrShowFailureMessage") && ((MethodBase)i.Member!).GetParameters().Length == 0))
                throw new Exception("Reconnect dials the saved address instead of the one the guest joined with");
            int dial = steps.FindIndex(i => i.Calls && i.Is(service, "ConnectOrShowFailureMessage") &&
                ((MethodBase)i.Member!).GetParameters().Length == 1);
            if (dial < 1 || !(steps[dial - 1].Loads && steps[dial - 1].Is("BeaverBuddies.Connect.ReconnectPlan", "Address")))
                throw new Exception("Reconnect does not dial ReconnectPlan.Address");
        });
    }
}
