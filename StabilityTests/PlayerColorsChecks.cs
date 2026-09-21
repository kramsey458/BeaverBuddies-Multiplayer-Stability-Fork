using System.Globalization;
using BeaverBuddies.Activity;
using BeaverBuddies.Panel;

// The color a player gets when they have not chosen one: the production PlayerColors source, compiled directly.
static class PlayerColorsChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    static double Brightness(string hex)
    {
        int rgb = int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return .2126 * ((rgb >> 16) & 255) / 255.0 + .7152 * ((rgb >> 8) & 255) / 255.0 + .0722 * (rgb & 255) / 255.0;
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Every player number up to the size of the palette gets a different color, and none is the yellow everyone starts with", () =>
        {
            var seen = new HashSet<string>();
            for (int id = 0; id < PlayerColors.Count; id++)
            {
                string hex = PlayerColors.ForPlayer(id);
                Check(hex.Length == 6 && hex.All(Uri.IsHexDigit), "not six hex digits: " + hex);
                Check(seen.Add(hex), "player " + id + " repeats " + hex);
                Check(!PlayerColors.IsDefault(hex), "player " + id + " got the default yellow");
            }
            Check(PlayerColors.Count >= 8, "room for fewer than eight players before colors repeat");
            Check(PlayerColors.ForPlayer(0) != PlayerColors.ForPlayer(1), "the host and the first guest look alike");
        });
        yield return ("The colors are light enough to read on the dark panel without being pushed toward white", () =>
        {
            for (int id = 0; id < PlayerColors.Count; id++)
            {
                string hex = PlayerColors.ForPlayer(id);
                Check(Brightness(hex) >= .4, hex + " is too dark");
                // A name is drawn lightened if it is too dark: these should need little or none of that.
                double lifted = Brightness(ChatFormat.ReadableHex(hex)) - Brightness(hex);
                Check(lifted < .06, hex + " would be changed too much to be read: " + lifted);
            }
        });
        yield return ("A player's own choice is kept, and only the default yellow is replaced", () =>
        {
            Equal("FF00FF", PlayerColors.Effective("FF00FF", 3));
            Equal("00FF00", PlayerColors.Effective("00FF00", 0));
            Equal(PlayerColors.ForPlayer(2), PlayerColors.Effective("FFFF00", 2));
            Equal(PlayerColors.ForPlayer(2), PlayerColors.Effective("ffff00", 2));
            // A yellow that is not exactly the default is a choice.
            Equal("FFFF01", PlayerColors.Effective("FFFF01", 2));
            Equal("FFE000", PlayerColors.Effective("FFE000", 2));
            Check(PlayerColors.IsDefault("FFFF00") && PlayerColors.IsDefault("ffff00") && !PlayerColors.IsDefault("FFFF01") && !PlayerColors.IsDefault(null!));
        });
        yield return ("Two players who both left the default get different colors, and every machine works out the same ones", () =>
        {
            string host = PlayerColors.Effective("FFFF00", 0), guest = PlayerColors.Effective("FFFF00", 1);
            Check(host != guest, "host and guest look alike");
            Equal(host, PlayerColors.Effective("FFFF00", 0));
            Equal(guest, PlayerColors.Effective("FFFF00", 1));
            // One of them chose a color and the other did not: the chooser keeps theirs, the other gets a color of their own.
            Equal("FF00FF", PlayerColors.Effective("FF00FF", 0));
            Equal(guest, PlayerColors.Effective("FFFF00", 1));
        });
        yield return ("Player numbers past the palette start over, and an unknown or odd number never goes out of range", () =>
        {
            Equal(PlayerColors.ForPlayer(0), PlayerColors.ForPlayer(PlayerColors.Count));
            Equal(PlayerColors.ForPlayer(3), PlayerColors.ForPlayer(3 + 5 * PlayerColors.Count));
            foreach (int id in new[] { 63, 64, 1000, int.MaxValue, -1, -8, -9, int.MinValue })
                Check(!PlayerColors.IsDefault(PlayerColors.ForPlayer(id)) && PlayerColors.ForPlayer(id).Length == 6, "player " + id);
            // A player number that is not known yet leaves the color alone, so it is not guessed wrongly.
            Equal("FFFF00", PlayerColors.Effective("FFFF00", -1));
            Equal("FF00FF", PlayerColors.Effective("FF00FF", -1));
        });
    }
}
