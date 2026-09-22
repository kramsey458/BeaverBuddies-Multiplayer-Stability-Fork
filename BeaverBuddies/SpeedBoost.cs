using System;
using System.Globalization;

namespace BeaverBuddies
{
    /// <summary>
    /// The speed boost: a constant added to the speed the players pick at the top right (the game's speed 1, 2 and 3
    /// run at 1, 3 and 7), the same for everyone in the session. Set from the chat box with - and +, in steps of 0.5,
    /// or typed. Like the speed buttons it only changes how fast ticks are worked through, never what happens in
    /// them. Plain code with no game types, so it is checked without the game.
    /// </summary>
    public static class SpeedBoost
    {
        /// <summary>What - and + take away or add.</summary>
        public const float Step = 0.5f;

        /// <summary>The slowest a running game goes: any slower and it might as well be paused.</summary>
        public const float MinSpeed = 0.5f;

        /// <summary>The fastest: the game's own developer speed "x30". Whether a computer keeps up is another matter.</summary>
        public const float MaxSpeed = 30f;

        /// <summary>The boost that takes the fastest button (7) down to MinSpeed, and the one that takes it up to MaxSpeed.</summary>
        public const float Min = MinSpeed - 7f, Max = MaxSpeed - 7f;

        /// <summary>A boost as kept: rounded to a hundredth and within the limits. A value that is not a number is 0.</summary>
        public static float Clamp(float boost)
        {
            if (float.IsNaN(boost) || float.IsInfinity(boost)) return 0;
            float rounded = (float)Math.Round(boost, 2);
            if (rounded == 0) return 0;
            return Math.Max(Min, Math.Min(Max, rounded));
        }

        /// <summary>The speed the game runs at: the picked speed plus the boost, within the limits. Paused stays paused.</summary>
        public static float Apply(float pickedSpeed, float boost)
        {
            if (pickedSpeed <= 0) return 0;
            float speed = pickedSpeed + Clamp(boost);
            return Math.Max(MinSpeed, Math.Min(MaxSpeed, speed));
        }

        /// <summary>The next boost on the half-step grid above (up) or below the current one, within the limits.</summary>
        public static float Stepped(float boost, bool up)
        {
            double steps = Clamp(boost) / Step;
            double next = up ? Math.Floor(steps + 1e-6) + 1 : Math.Ceiling(steps - 1e-6) - 1;
            return Clamp((float)(next * Step));
        }

        /// <summary>
        /// Reads a typed boost: "1", "+1.5", "-0.5", "2,5" (a comma as the decimal sign), with spaces around. Anything
        /// else is refused; a readable value comes back clamped.
        /// </summary>
        public static bool TryParse(string text, out float boost)
        {
            boost = 0;
            if (text == null) return false;
            string cleaned = text.Trim().Replace(',', '.');
            if (cleaned.Length == 0) return false;
            if (!float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return false;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            boost = Clamp(value);
            return true;
        }

        /// <summary>A boost as shown: "+0.5", "-1", "0". The same in every language.</summary>
        public static string Format(float boost)
        {
            float value = Clamp(boost);
            if (value == 0) return "0";
            string text = value.ToString("0.##", CultureInfo.InvariantCulture);
            return value > 0 ? "+" + text : text;
        }
    }
}
