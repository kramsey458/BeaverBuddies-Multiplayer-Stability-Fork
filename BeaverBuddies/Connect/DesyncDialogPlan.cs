namespace BeaverBuddies.Connect
{
    /// <summary>
    /// How a guest joined its last session: through a Steam lobby (to a host's Steam ID) or directly (to an address it
    /// typed). Kept for the desync dialog's "Reconnect (wait for Rehost)", which joins the same way again.
    /// </summary>
    public sealed class JoinRoute
    {
        /// <summary>The host's Steam ID, when the guest joined through the host's Steam lobby.</summary>
        public ulong? SteamHost { get; }

        /// <summary>The address as the player typed it (host, host:port or [IPv6]:port), when it joined directly.</summary>
        public string Address { get; }

        private JoinRoute(ulong? steamHost, string address)
        {
            SteamHost = steamHost;
            Address = address;
        }

        public static JoinRoute ViaSteam(ulong host) => new JoinRoute(host, null);

        public static JoinRoute ViaAddress(string address) => new JoinRoute(null, address);

        public override string ToString() => SteamHost.HasValue ? $"Steam (host {SteamHost.Value})" : $"address {Address}";
    }

    public enum ReconnectStep
    {
        /// <summary>Connect directly to <see cref="ReconnectPlan.Address"/>.</summary>
        DialAddress,
        /// <summary>
        /// Join the host's Steam lobby, <see cref="ReconnectPlan.Lobby"/>. Entering it connects to the host exactly as
        /// accepting an invite does (SteamOverlayConnectionService.OnLobbyEntered): the host only accepts Steam
        /// connections from members of its current lobby, and a rehost opens a new one.
        /// </summary>
        JoinSteamLobby,
        /// <summary>
        /// A Steam guest whose host shows no lobby it can join (an invite-only lobby, or the host has not rehosted
        /// yet): the guest has to accept the host's new invite.
        /// </summary>
        WaitForSteamInvite,
    }

    public readonly struct ReconnectPlan
    {
        public readonly ReconnectStep Step;
        public readonly string Address;
        public readonly ulong Lobby;

        public ReconnectPlan(ReconnectStep step, string address, ulong lobby)
        {
            Step = step;
            Address = address;
            Lobby = lobby;
        }
    }

    /// <summary>
    /// The decisions behind the desync dialog, with no game or Steam types so they can be checked outside the game.
    /// ClientDesyncedEvent shows the dialog; ClientConnectionService carries out a guest's reconnect.
    /// </summary>
    public static class DesyncDialogPlan
    {
        public const string PostBugReportKey = "BeaverBuddies.ClientDesynced.PostBugReportButton";
        public const string EnableLoggingKey = "BeaverBuddies.ClientDesynced.EnableTracing";

        /// <summary>
        /// The key of the report button's label, or null when there is no such button. Only a build with an upload
        /// token can post a report, and public builds have none. In debug mode the button posts the report; otherwise
        /// it turns detailed logging on ("Enable Logging"), so that the next desync can be reported.
        /// </summary>
        public static string ReportButtonKey(bool debug, bool canPostReports)
        {
            if (!canPostReports) return null;
            return debug ? PostBugReportKey : EnableLoggingKey;
        }

        /// <summary>
        /// Whether the message adds the sentence that asks every player to press Enable Logging, below: only when
        /// that button is there to press.
        /// </summary>
        public static bool AsksToEnableLogging(bool debug, bool canPostReports)
        {
            return ReportButtonKey(debug, canPostReports) == EnableLoggingKey;
        }

        /// <summary>
        /// What a guest's "Reconnect (wait for Rehost)" does. <paramref name="lastJoin"/> is how it joined (null if
        /// not known), <paramref name="savedAddress"/> the address in the settings, and
        /// <paramref name="hostLobby"/> the host's joinable Steam lobby, if Steam shows one.
        /// </summary>
        public static ReconnectPlan Reconnect(JoinRoute lastJoin, string savedAddress, System.Func<ulong, ulong?> hostLobby)
        {
            if (lastJoin?.SteamHost is ulong host)
            {
                // Never the saved address: it has nothing to do with this host (by default it is 127.0.0.1).
                ulong? lobby = hostLobby(host);
                return lobby.HasValue
                    ? new ReconnectPlan(ReconnectStep.JoinSteamLobby, null, lobby.Value)
                    : new ReconnectPlan(ReconnectStep.WaitForSteamInvite, null, 0);
            }
            // The address the guest typed when it joined. Without a record (which a guest always has, since joining
            // makes one), the one in the settings, as before.
            return new ReconnectPlan(ReconnectStep.DialAddress, lastJoin?.Address ?? savedAddress, 0);
        }
    }
}
