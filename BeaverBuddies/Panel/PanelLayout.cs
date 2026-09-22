using System.Collections.Generic;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// The panel's sizes that are decisions rather than measurements. Plain code with no game types, so it is
    /// checked without the game.
    /// </summary>
    internal static class PanelLayout
    {
        /// <summary>
        /// How tall the chat is, in the game's interface units. A compact box, not as tall as the section above it:
        /// with everything the host sees the panel is already tall, and the game's alerts sit at the bottom of the
        /// screen. 150 for the messages and the box to type in, plus the speed boost row above them.
        /// </summary>
        public const float ChatHeight = 150 + BoostRowHeight;

        /// <summary>The speed boost row: the game's small - and + buttons, and the margin under the row.</summary>
        public const float BoostRowHeight = 28;

        // A width taken from another panel is believed only within these limits, so a panel that is hidden, not laid
        // out yet or stretched across the screen can never make this one absurd.
        public const float MinMatchedWidth = 180, MaxMatchedWidth = 520;

        /// <summary>
        /// The width this panel should take, or null to size to its own content. The game's population panel (the
        /// beaver counters) is preferred; failing that, the nearest visible panel above this one.
        /// </summary>
        /// <param name="populationPanel">Its measured width, or null if it is not in this corner.</param>
        /// <param name="panelsAbove">Widths of the visible panels above this one, nearest first.</param>
        public static float? ChooseWidth(float? populationPanel, IEnumerable<float> panelsAbove)
        {
            if (Usable(populationPanel)) return populationPanel;
            foreach (float width in panelsAbove)
                if (Usable(width)) return width;
            return null;
        }

        static bool Usable(float? width) =>
            width.HasValue && !float.IsNaN(width.Value) && width.Value >= MinMatchedWidth && width.Value <= MaxMatchedWidth;
    }
}
