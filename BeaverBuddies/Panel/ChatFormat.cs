using System;
using System.Globalization;
using System.Linq;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// How a chat line is written on the panel. Plain code with no game types, so it is checked without the game.
    /// </summary>
    internal static class ChatFormat
    {
        // The panel is dark, so a name must not be: a color darker than this is lightened toward white.
        const double MinBrightness = .5;
        // The panel's ordinary text color, used when a color is not six hex digits.
        const string FallbackHex = "F2E8D0";

        /// <summary>"Name: message" with the name in the sender's color and the message in the panel's own text color. Only the color is markup.</summary>
        public static string Line(string name, string colorHex, string text) =>
            "<color=#" + ReadableHex(colorHex) + ">" + Plain(name) + "</color>: " + Plain(text);

        // Rich text is on so the name can be colored, so nothing a player types may carry a tag of its own.
        // (Chat is already cleaned when it arrives; this keeps the line safe whatever calls it.)
        static string Plain(string value) => (value ?? "").Replace("<", "").Replace(">", "");

        /// <summary>The color as six hex digits, lightened if it would be hard to read on the panel's dark background.</summary>
        public static string ReadableHex(string hex)
        {
            if (hex == null || hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return FallbackHex;
            int rgb = int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
            double brightness = .2126 * r + .7152 * g + .0722 * b;
            if (brightness < MinBrightness)
            {
                double toward = (MinBrightness - brightness) / (1 - brightness);
                r += (1 - r) * toward; g += (1 - g) * toward; b += (1 - b) * toward;
            }
            return Hex2(r) + Hex2(g) + Hex2(b);
        }

        static string Hex2(double channel) =>
            ((int)Math.Round(Math.Min(1, Math.Max(0, channel)) * 255)).ToString("X2", CultureInfo.InvariantCulture);
    }
}
