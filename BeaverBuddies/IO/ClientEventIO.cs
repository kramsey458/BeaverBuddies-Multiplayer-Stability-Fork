using System;
using BeaverBuddies.Connect;
using TimberNet;
using static TimberNet.TimberNetBase;

namespace BeaverBuddies.IO
{
    public class ClientEventIO : NetIOBase<TimberClient>
    {
        // If the client receives an event to replay, no matter where it
        // originated, it shouldn't send it *back* to the server, since the
        // server is what sent the event.
        public override bool RecordReplayedEvents => false;

        // Clients don't need to send heartbeats
        public override bool ShouldSendHeartbeat => false;

        // The client doesn't get to do anything from the user directly.
        // The client should send user-initiated events to the server.
        // It has to wait until an event is received from the server.
        public override UserEventBehavior UserEventBehavior => UserEventBehavior.Send;

        private MapReceived mapReceivedCallback;
        private bool FailedToConnect = false;
        // True once the host's save arrived and the game was told to load it. Before that this is only a join
        // attempt; after it, a game exists that belongs to this session.
        private bool mapDelivered = false;

        private ClientEventIO(ISocketStream socket, MapReceived mapReceivedCallback,
            Action<string> onError)
        {
            this.mapReceivedCallback = mapReceivedCallback;

            NetBase = new TimberClient(socket)
            {
                CompatibilityIdentity = BuildCompatibility.CreateIdentity(),
                // Mod lists are swapped with the host and compared; a difference is only a warning.
                CompatibilityAdvisory = ModCompatibility.CreateAdvisory(),
            };
            NetBase.DetailedLoggingEnabled = () => Settings.Debug && Settings.VerboseLogging;
            NetBase.OnSessionFault += reason => SingletonManager.GetSingleton<ReplayService>()?.AbortReplay(reason);
            NetBase.OnMapReceived += OnMapReceivedByNet;
            ModWarnings.Clear();
            NetBase.OnPeerAdvisory += ModCompatibility.OnPeerAdvisory;
            NetBase.OnLog += Plugin.Log;
            NetBase.OnError += (error) => OnConnectionError(error, onError);
            try
            {
                NetBase.Start();
            }
            catch (Exception ex)
            {
                onError(ex.Message);
                Plugin.LogError(ex.ToString());
                CleanUp();
                FailedToConnect = true;
            }
        }

        // Everything a guest plays comes from the host, which has already played it, so an action this game cannot
        // read leaves it behind the host's for good. The session stops the way it does when an action fails, and
        // nothing more of that tick is played.
        protected override bool HandleUnreadableFrame(string problem)
        {
            Plugin.LogError("Could not read an action from the host: " + problem);
            NetBase?.RaiseSessionFault("An action from the host could not be read, so this game would no longer " +
                "match the host's. " + problem);
            return false;
        }

        // Only once the game has been told to load the save: if that throws, this stays a join attempt that failed,
        // and the error that follows is reported to whoever is joining.
        private void OnMapReceivedByNet(byte[] mapBytes)
        {
            mapReceivedCallback(mapBytes);
            mapDelivered = true;
        }

        /// <summary>
        /// The connection failed or dropped. What that means depends on how far the join got (see
        /// ConnectionErrorPlanner); this is reached on the update thread, from wherever the session is being updated.
        /// </summary>
        private void OnConnectionError(string error, Action<string> onError)
        {
            Plugin.LogError(error);
            // One failure is one report: a second error queued behind the first (a save that then failed to load, say)
            // must not show a second dialog.
            if (FailedToConnect) return;
            CleanUp();
            FailedToConnect = true;

            bool isCurrent = ReferenceEquals(EventIO.Get(), this);
            switch (ConnectionErrorPlanner.Decide(isCurrent, mapDelivered, ReplayService.HasReplayFailure))
            {
                case ConnectionErrorPlan.ReportWhileJoining:
                    // Nothing came of this join, so nothing should stay installed: a session that is over but still
                    // installed turns the next game loaded from this menu into one that is paused for good.
                    EventIO.ResetIf(this);
                    onError(error);
                    break;
                case ConnectionErrorPlan.EndRunningGame:
                    // The game's own dialog: the join attempt's belongs to a menu that no longer exists. With no
                    // game yet (the save is still loading) the new game finds the session over and reports it itself,
                    // see ReplayService.UpdateSingleton.
                    SingletonManager.GetSingleton<ReplayService>()?.EndSession(SessionEndMessages.ConnectionLost(error));
                    break;
            }
        }

        private void CleanUp()
        {
            if (NetBase == null) return;
            NetBase.Close();
            NetBase.OnMapReceived -= OnMapReceivedByNet;
            NetBase.OnLog -= Plugin.Log;
            NetBase.OnPeerAdvisory -= ModCompatibility.OnPeerAdvisory;
            NetBase = null;
        }

        public static ClientEventIO Create(ISocketStream socket, MapReceived mapReceivedCallback, Action<string> onError)
        {
            ClientEventIO eventIO = new ClientEventIO(socket, mapReceivedCallback, onError);
            if (eventIO.FailedToConnect) return null;
            return eventIO;
        }
    }
}
