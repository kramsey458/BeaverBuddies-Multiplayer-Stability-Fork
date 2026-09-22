#nullable enable
using BeaverBuddies.Connect;

// The desync dialog's decisions (DesyncDialogPlan): what it asks of the players, and how a guest's
// "Reconnect (wait for Rehost)" joins again.
static class DesyncDialogChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    const ulong Host = 76561198000000001, Lobby = 109775240000000001;
    static readonly Func<ulong, ulong?> NoLobby = _ => null;
    static Func<ulong, ulong?> LobbyOf(ulong host, ulong lobby) => id => id == host ? lobby : null;

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Desync dialog: a public build (no upload token) neither shows the report button nor asks to press Enable Logging", () =>
        {
            Check(!DesyncDialogPlan.AsksToEnableLogging(debug: false, canPostReports: false),
                "the message asks every player to press Enable Logging, and there is no such button");
            Check(DesyncDialogPlan.ReportButtonKey(debug: false, canPostReports: false) == null, "a report button with nothing to post to");
        });
        yield return ("Desync dialog: a build that can post reports shows Enable Logging and says to press it", () =>
        {
            Equal(DesyncDialogPlan.EnableLoggingKey, DesyncDialogPlan.ReportButtonKey(debug: false, canPostReports: true));
            Check(DesyncDialogPlan.AsksToEnableLogging(debug: false, canPostReports: true));
        });
        yield return ("Desync dialog: in debug mode the button posts the report and nothing asks for logging", () =>
        {
            Equal(DesyncDialogPlan.PostBugReportKey, DesyncDialogPlan.ReportButtonKey(debug: true, canPostReports: true));
            Check(!DesyncDialogPlan.AsksToEnableLogging(debug: true, canPostReports: true));
            Check(DesyncDialogPlan.ReportButtonKey(debug: true, canPostReports: false) == null, "a report button with nothing to post to");
        });
        yield return ("Desync dialog: the Enable Logging sentence only ever appears with the Enable Logging button", () =>
        {
            foreach (bool debug in new[] { false, true })
                foreach (bool canPost in new[] { false, true })
                    if (DesyncDialogPlan.AsksToEnableLogging(debug, canPost))
                        Equal(DesyncDialogPlan.EnableLoggingKey, DesyncDialogPlan.ReportButtonKey(debug, canPost));
        });
        yield return ("Reconnect: a guest that joined over Steam rejoins through the host's lobby, not the saved address", () =>
        {
            var plan = DesyncDialogPlan.Reconnect(JoinRoute.ViaSteam(Host), "127.0.0.1", LobbyOf(Host, Lobby));
            Check(plan.Step == ReconnectStep.JoinSteamLobby, $"a Steam guest would {plan.Step} {plan.Address}");
            Equal(Lobby, plan.Lobby);
        });
        yield return ("Reconnect: a Steam guest whose host shows no lobby it can join waits for a new invite, and dials nothing", () =>
        {
            var plan = DesyncDialogPlan.Reconnect(JoinRoute.ViaSteam(Host), "127.0.0.1", NoLobby);
            Check(plan.Step == ReconnectStep.WaitForSteamInvite, $"a Steam guest would {plan.Step} {plan.Address}");
            // Another friend's lobby is not the host's.
            plan = DesyncDialogPlan.Reconnect(JoinRoute.ViaSteam(Host), "127.0.0.1", LobbyOf(Host + 1, Lobby));
            Equal(ReconnectStep.WaitForSteamInvite, plan.Step);
        });
        yield return ("Reconnect: a direct guest redials the address it joined with", () =>
        {
            var plan = DesyncDialogPlan.Reconnect(JoinRoute.ViaAddress("[2001:db8::1]:25566"), "127.0.0.1", LobbyOf(Host, Lobby));
            Equal(ReconnectStep.DialAddress, plan.Step);
            Equal("[2001:db8::1]:25566", plan.Address);
        });
        yield return ("Reconnect: with no record of the join, the saved address is dialled, as before", () =>
        {
            var plan = DesyncDialogPlan.Reconnect(null, "192.168.1.20", LobbyOf(Host, Lobby));
            Equal(ReconnectStep.DialAddress, plan.Step);
            Equal("192.168.1.20", plan.Address);
        });
    }
}
