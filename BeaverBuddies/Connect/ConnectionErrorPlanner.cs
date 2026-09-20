namespace BeaverBuddies.Connect
{
    internal enum ConnectionErrorPlan
    {
        /// <summary>Nothing to do: something newer replaced this session, or a failed action already explained.</summary>
        Ignore,
        /// <summary>Still joining and never got a game: clear the session away and tell whoever is joining.</summary>
        ReportWhileJoining,
        /// <summary>A game belongs to this session: leave multiplayer in it, and say so there.</summary>
        EndRunningGame,
    }

    /// <summary>
    /// Decides what a guest's connection error means. It depends on how far the join got, because that decides
    /// which screen the player is looking at and which dialog can still be shown: the menu's dialogs stop working
    /// the moment the game scene loads.
    /// </summary>
    internal static class ConnectionErrorPlanner
    {
        /// <param name="isCurrentSession">This is still the session the game is using, not one a rejoin replaced.</param>
        /// <param name="mapDelivered">The host's save arrived and the game was told to load it.</param>
        /// <param name="hasReplayFailure">A failed action already stopped multiplayer and said so.</param>
        public static ConnectionErrorPlan Decide(bool isCurrentSession, bool mapDelivered, bool hasReplayFailure)
        {
            if (!isCurrentSession) return ConnectionErrorPlan.Ignore;
            // Joining never produced a game, so this error is the only explanation the player will get.
            if (!mapDelivered) return ConnectionErrorPlan.ReportWhileJoining;
            return hasReplayFailure ? ConnectionErrorPlan.Ignore : ConnectionErrorPlan.EndRunningGame;
        }
    }
}
