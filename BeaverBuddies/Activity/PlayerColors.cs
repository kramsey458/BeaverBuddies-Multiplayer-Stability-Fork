using System;

namespace BeaverBuddies.Activity
{
    /// <summary>
    /// The color a player gets when they have not chosen one. Every player's Ping Color starts as the same yellow, so
    /// left alone they would all look alike on the cursors and in the chat. A player who has not changed it gets a
    /// color of their own instead, picked by player number (the host is 0, guests count up from 1). It is worked out
    /// by whoever is looking, from the number alone, so it costs nothing on the network and every machine picks the
    /// same one. Plain code with no game types, so it is checked without the game.
    /// </summary>
    public static class PlayerColors
    {
        /// <summary>The Ping Color setting's default. A player still on it has not chosen a color.</summary>
        public const string DefaultPingHex = "FFFF00";

        // Well apart in hue, none of them yellow, and light enough to read on the dark panel. The host comes first.
        static readonly string[] Palette =
        {
            "FF9F1C", // orange
            "4D96FF", // blue
            "6BCB77", // green
            "F15BB5", // pink
            "9B5DE5", // purple
            "2EC4B6", // teal
            "FF4D4D", // red
            "A3D94D", // lime
        };

        public static int Count => Palette.Length;

        /// <summary>The color for this player number. Numbers past the end of the palette start over.</summary>
        public static string ForPlayer(int playerId) =>
            Palette[((playerId % Palette.Length) + Palette.Length) % Palette.Length];

        /// <summary>Whether this is the default Ping Color, meaning the player has not chosen one.</summary>
        public static bool IsDefault(string hex) => string.Equals(hex, DefaultPingHex, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The color to show for a player who advertises this one (six hex digits): their own choice, or, if it is
        /// still the default, the color for their player number. A number below zero means it is not known yet, and
        /// the color is left as it is.
        /// </summary>
        public static string Effective(string chosenHex, int playerId) =>
            playerId >= 0 && IsDefault(chosenHex) ? ForPlayer(playerId) : chosenHex;
    }
}
