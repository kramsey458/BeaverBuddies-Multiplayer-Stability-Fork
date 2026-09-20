namespace BeaverBuddies.Connect
{
    /// <summary>
    /// What a player is told when the multiplayer session ends under a game that is still running. Plain text and no
    /// game types, so it can be checked outside the game.
    /// </summary>
    internal static class SessionEndMessages
    {
        /// <summary>
        /// The connection to the host dropped. <paramref name="reason"/> is the transport's own explanation, if it
        /// gave one.
        /// </summary>
        public static string ConnectionLost(string reason)
        {
            string text = "The multiplayer connection was lost.";
            if (!string.IsNullOrWhiteSpace(reason)) text += "\n\"" + reason.Trim() + "\"";
            return text + "\n\nMultiplayer has ended for this game and it is paused. " +
                "Open the menu to save it, or to return to the main menu and join again.";
        }
    }
}
