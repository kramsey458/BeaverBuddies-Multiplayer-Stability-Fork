using BeaverBuddies;
using static BeaverBuddies.RandomSourceRules;

// Which random generator a draw uses in a multiplayer game (1.1.14): RandomSourceRules, which DeterminismService
// feeds with what is going on (a session, the thread, marked methods, loading, the tick, a replay).
static class DeterminismRuleChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    // Everything off: a session, on the main thread, after loading, between ticks, in no replay and no marked method.
    static Source Pick(bool inSession = true, bool nonGameplay = false, bool otherThread = false, bool gameMarker = false,
        bool nonGameMarker = false, bool loaded = true, bool ticking = false, bool replaying = false) =>
        Choose(inSession, nonGameplay, otherThread, gameMarker, nonGameMarker, loaded, ticking, replaying);

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Random source: outside a session every draw is the game's, as in single player", () =>
        {
            Equal(Source.Game, Pick(inSession: false));
            Equal(Source.Game, Pick(inSession: false, nonGameplay: true, otherThread: true, nonGameMarker: true, loaded: false));
        });

        yield return ("Random source: while a game loads, a method marked as not simulation keeps its own generator", () =>
        {
            // Until 1.1.14 loading came first: a sound or input marked as not simulation drew the game's random
            // numbers for the first frames of a game, and one computer's sound could move its random state alone.
            Equal(Source.NonGame, Pick(loaded: false, nonGameMarker: true));
            Equal(Source.NonGame, Pick(loaded: false, nonGameplay: true));
            // Everything else while loading is the simulation setting itself up, the same on every computer.
            Equal(Source.Game, Pick(loaded: false));
            Equal(Source.Game, Pick(loaded: false, gameMarker: true));
            Equal(Source.Game, Pick(loaded: false, gameMarker: true, nonGameMarker: true));
        });

        yield return ("Random source: in the tick or a replay the game's, unless marked; anywhere else unknown", () =>
        {
            Equal(Source.Game, Pick(ticking: true));
            Equal(Source.Game, Pick(replaying: true));
            Equal(Source.NonGame, Pick(ticking: true, nonGameMarker: true));
            Equal(Source.NonGame, Pick(replaying: true, nonGameplay: true));
            Equal(Source.Game, Pick(gameMarker: true));
            Equal(Source.Unknown, Pick());
        });

        yield return ("Random source: another thread never draws the game's, loading or not", () =>
        {
            Equal(Source.Unknown, Pick(otherThread: true));
            Equal(Source.Unknown, Pick(otherThread: true, loaded: false));
            Equal(Source.Unknown, Pick(otherThread: true, ticking: true, gameMarker: true));
            Equal(Source.NonGame, Pick(otherThread: true, nonGameplay: true));
        });
    }
}
