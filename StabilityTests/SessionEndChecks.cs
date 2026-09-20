using BeaverBuddies.Connect;
using BeaverBuddies.Steam;

// Controls that stop answering after a message is closed: what a guest's connection error means at each stage of
// a join, what the player is told, and what to do when Steam's overlay closes under a dialog. The wiring around
// these decisions needs the game; the decisions do not.
static class SessionEndChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}], got [{actual}]");

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        // ---- what a connection error means ----

        yield return ("Connection error while joining is reported to whoever is joining", () =>
        {
            // The save has not arrived, so no game exists and the join dialog is the only explanation there will be.
            Equal(ConnectionErrorPlan.ReportWhileJoining, ConnectionErrorPlanner.Decide(true, false, false));
        });
        yield return ("A failed join is reported even after an earlier failed action", () =>
        {
            // The flag belongs to a game that was already stopped; a new join attempt from it must still say why it failed.
            Equal(ConnectionErrorPlan.ReportWhileJoining, ConnectionErrorPlanner.Decide(true, false, true));
        });
        yield return ("Connection lost after the game loaded ends multiplayer in that game", () =>
        {
            Equal(ConnectionErrorPlan.EndRunningGame, ConnectionErrorPlanner.Decide(true, true, false));
        });
        yield return ("A connection that drops after a failed action adds nothing", () =>
        {
            Equal(ConnectionErrorPlan.Ignore, ConnectionErrorPlanner.Decide(true, true, true));
        });
        yield return ("A session that a rejoin replaced never tears down the newer one", () =>
        {
            foreach (bool mapDelivered in new[] { false, true })
            foreach (bool failure in new[] { false, true })
                Equal(ConnectionErrorPlan.Ignore, ConnectionErrorPlanner.Decide(false, mapDelivered, failure));
        });

        // ---- what the player is told ----

        yield return ("Connection lost message names the reason and the way out", () =>
        {
            string text = SessionEndMessages.ConnectionLost("Error receiving data: boom");
            Check(text.Contains("connection was lost"));
            Check(text.Contains("\"Error receiving data: boom\""));
            // The game is playable again, so the way out has to be the menu, which now opens.
            Check(text.Contains("menu"));
            Check(text.Contains("paused"));
        });
        yield return ("Connection lost message copes with no reason and with a padded one", () =>
        {
            foreach (string reason in new string[] { null, "", "   " })
            {
                string text = SessionEndMessages.ConnectionLost(reason);
                Check(text.StartsWith("The multiplayer connection was lost."));
                Check(!text.Contains("\"\""), "an empty reason must not print an empty quotation");
            }
            Check(SessionEndMessages.ConnectionLost("  reset by peer\n").Contains("\"reset by peer\""));
        });

        // ---- Steam's overlay closing under a dialog ----

        yield return ("Overlay opening or closing normally is left to the game", () =>
        {
            // Blocker on top (or not present) and something in the stack: the game's own push and pop are right.
            Equal(OverlayBlockerAction.RunGameCode, OverlayBlockerPolicy.Decide(true, false, false));
            Equal(OverlayBlockerAction.RunGameCode, OverlayBlockerPolicy.Decide(false, false, false));
            Equal(OverlayBlockerAction.RunGameCode, OverlayBlockerPolicy.Decide(true, false, true));
        });
        yield return ("Overlay closing over an empty stack is skipped, as before", () =>
        {
            // A game just loaded and cleared the stack while the overlay was open: there is nothing to pop.
            Equal(OverlayBlockerAction.Skip, OverlayBlockerPolicy.Decide(false, false, true));
        });
        yield return ("Overlay closing under a dialog waits for the blocker to surface", () =>
        {
            // The game would leave the blocker in the stack for good, and it would swallow every key once the
            // dialog is closed.
            Equal(OverlayBlockerAction.PopWhenOnTop, OverlayBlockerPolicy.Decide(false, true, false));
        });
        yield return ("Overlay opening again under a dialog does not push a second blocker", () =>
        {
            Equal(OverlayBlockerAction.Skip, OverlayBlockerPolicy.Decide(true, true, false));
        });
    }
}
