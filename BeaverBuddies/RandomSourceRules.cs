namespace BeaverBuddies
{
    /// <summary>
    /// Which random number generator a call to the game's RandomNumberGenerator draws from in a multiplayer game: the
    /// game's (Unity's shared random state, which every computer keeps in step and the heartbeat compares), or a
    /// separate one of this computer's own. Kept free of the game for the checks; DeterminismService supplies the facts.
    /// </summary>
    public static class RandomSourceRules
    {
        public enum Source
        {
            /// <summary>The game's shared random state: the draw is part of the simulation.</summary>
            Game,
            /// <summary>This computer's own generator: known not to be simulation (a sound, a model's look, input).</summary>
            NonGame,
            /// <summary>This computer's own generator, for a caller nobody has classified: worth a line in the log.</summary>
            Unknown,
        }

        /// <param name="inSession">A multiplayer game is running (outside one every draw is the game's, as in single player).</param>
        /// <param name="nonGameplay">The caller asked for this computer's own generator (the RNG injected into listed classes).</param>
        /// <param name="otherThread">Not Unity's main thread, where the game's random state lives.</param>
        /// <param name="gameMarker">Inside a method marked as simulation (a beaver's name, say).</param>
        /// <param name="nonGameMarker">Inside a method marked as not simulation (sounds, input, model variations).</param>
        /// <param name="loaded">The game has finished loading (ReplayService.IsLoaded).</param>
        /// <param name="ticking">Inside the tick.</param>
        /// <param name="replaying">Inside a replayed player action.</param>
        public static Source Choose(bool inSession, bool nonGameplay, bool otherThread, bool gameMarker, bool nonGameMarker,
            bool loaded, bool ticking, bool replaying)
        {
            if (!inSession) return Source.Game;
            if (nonGameplay) return Source.NonGame;
            // Off the main thread nothing may touch the game's random state.
            if (otherThread) return Source.Unknown;
            // A marked method means the same while the game loads as after. Until 1.1.14 the loading rule below came
            // first, so for the first frames of a game every draw was the game's, the sounds and input marked as not
            // simulation included: a sound one player's game played and another's did not moved one random state only.
            if (gameMarker) return Source.Game;
            if (nonGameMarker) return Source.NonGame;
            // While loading, almost every draw is the simulation setting itself up (when trees die, which beaver goes
            // in first), the same on every computer, since each loads the same save with the same seed.
            if (!loaded) return Source.Game;
            if (ticking || replaying) return Source.Game;
            return Source.Unknown;
        }
    }
}
