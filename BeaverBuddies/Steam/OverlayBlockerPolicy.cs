namespace BeaverBuddies.Steam
{
    internal enum OverlayBlockerAction
    {
        /// <summary>Let the game's own code run.</summary>
        RunGameCode,
        /// <summary>Skip the game's code: there is nothing for it to push or pop.</summary>
        Skip,
        /// <summary>Skip the game's code, and pop the blocker as soon as it is on top again.</summary>
        PopWhenOnTop,
    }

    /// <summary>
    /// The game pushes a panel that blocks input while Steam's overlay is open, and pops it when the overlay closes,
    /// but only if that panel is on top. A dialog can open over it in between (a connection error, an old invite
    /// that cannot be joined), and then the game leaves the blocker in the stack for good: once the dialog is
    /// dismissed, an empty panel that no key can close sits on top and swallows all input. No game types here, so
    /// it can be checked outside the game.
    /// </summary>
    internal static class OverlayBlockerPolicy
    {
        /// <param name="overlayOpened">The overlay just opened (true) or just closed (false).</param>
        /// <param name="blockerBuried">The blocker is in the stack but something is on top of it.</param>
        /// <param name="stackEmpty">The stack has no panels at all, for example because a game just loaded.</param>
        public static OverlayBlockerAction Decide(bool overlayOpened, bool blockerBuried, bool stackEmpty)
        {
            if (blockerBuried)
            {
                // Opening must not push a second copy. Closing cannot pop the buried one yet: wait until it surfaces.
                return overlayOpened ? OverlayBlockerAction.Skip : OverlayBlockerAction.PopWhenOnTop;
            }
            // Nothing to pop: a game just loaded and cleared the stack while the overlay was open.
            if (!overlayOpened && stackEmpty) return OverlayBlockerAction.Skip;
            return OverlayBlockerAction.RunGameCode;
        }
    }
}
