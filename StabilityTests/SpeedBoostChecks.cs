using System.Globalization;
using System.Text.RegularExpressions;
using BeaverBuddies;

// The speed boost: the constant added to the picked speed, as kept, applied, stepped, typed and shown.
static class SpeedBoostChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    static string EnglishFile()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "BeaverBuddies.sln"))) root = Path.GetDirectoryName(root)!;
        Check(root != null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(root!, "BeaverBuddies", "Localizations", "enUS_BeaverBuddie.csv"));
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Speed boost: kept to a hundredth and within its limits; anything that is not a number is 0", () =>
        {
            Equal(0.5f, SpeedBoost.Clamp(0.5f));
            Equal(1.25f, SpeedBoost.Clamp(1.254f));
            Equal(-0.5f, SpeedBoost.Clamp(-0.5f));
            Equal(SpeedBoost.Max, SpeedBoost.Clamp(100));
            Equal(SpeedBoost.Min, SpeedBoost.Clamp(-100));
            Equal(0f, SpeedBoost.Clamp(float.NaN));
            Equal(0f, SpeedBoost.Clamp(float.PositiveInfinity));
            Equal(0f, SpeedBoost.Clamp(float.NegativeInfinity));
            // The limits are the ones that take the fastest button (7) to the slowest and fastest speed allowed.
            Equal(SpeedBoost.MinSpeed, 7 + SpeedBoost.Min);
            Equal(SpeedBoost.MaxSpeed, 7 + SpeedBoost.Max);
            Equal(-6.5f, SpeedBoost.Min); Equal(23f, SpeedBoost.Max);
        });
        yield return ("Speed boost: the game runs at the picked speed plus the boost, paused stays paused", () =>
        {
            Equal(3.5f, SpeedBoost.Apply(3, 0.5f));
            Equal(7f, SpeedBoost.Apply(7, 0));
            Equal(1.5f, SpeedBoost.Apply(1, 0.5f));
            Equal(6f, SpeedBoost.Apply(7, -1));
            Equal(30f, SpeedBoost.Apply(7, 23));
            // A pick of 0 is a pause, boost or not; and a pause is never turned into a crawl.
            foreach (float boost in new[] { -6.5f, -1, 0, 0.5f, 23 }) Equal(0f, SpeedBoost.Apply(0, boost));
            Equal(0f, SpeedBoost.Apply(-1, 5));
        });
        yield return ("Speed boost: a running game stays between 0.5x and 30x whatever the pick and the boost", () =>
        {
            // Speed 1 with the boost that takes 7 down to 0.5 does not go below 0.5: the floor, not a pause.
            Equal(SpeedBoost.MinSpeed, SpeedBoost.Apply(1, SpeedBoost.Min));
            Equal(SpeedBoost.MinSpeed, SpeedBoost.Apply(3, -3));
            Equal(SpeedBoost.MaxSpeed, SpeedBoost.Apply(7, 100));
            for (float boost = -100; boost <= 100; boost += 0.7f)
                foreach (float picked in new float[] { 1, 3, 7 })
                {
                    float speed = SpeedBoost.Apply(picked, boost);
                    Check(speed >= SpeedBoost.MinSpeed && speed <= SpeedBoost.MaxSpeed, $"pick {picked} boost {boost}: {speed}");
                }
        });
        yield return ("Speed boost: - and + move by a half step, to the grid, and stop at the limits", () =>
        {
            Equal(0.5f, SpeedBoost.Stepped(0, up: true));
            Equal(1f, SpeedBoost.Stepped(0.5f, up: true));
            Equal(-0.5f, SpeedBoost.Stepped(0, up: false));
            Equal(0f, SpeedBoost.Stepped(0.5f, up: false));
            Equal(-1f, SpeedBoost.Stepped(-0.5f, up: false));
            // A typed value off the grid steps to the next grid value, not by a whole step.
            Equal(0.5f, SpeedBoost.Stepped(0.3f, up: true));
            Equal(0f, SpeedBoost.Stepped(0.3f, up: false));
            Equal(-0.5f, SpeedBoost.Stepped(-0.3f, up: false));
            Equal(0f, SpeedBoost.Stepped(-0.3f, up: true));
            Equal(1.5f, SpeedBoost.Stepped(1.25f, up: true));
            // The limits.
            Equal(SpeedBoost.Max, SpeedBoost.Stepped(SpeedBoost.Max, up: true));
            Equal(SpeedBoost.Min, SpeedBoost.Stepped(SpeedBoost.Min, up: false));
            Equal(SpeedBoost.Max, SpeedBoost.Stepped(22.75f, up: true));
            // From a grid value a step is exactly half, both ways, everywhere in the range.
            for (float boost = SpeedBoost.Min + SpeedBoost.Step; boost <= SpeedBoost.Max - SpeedBoost.Step; boost += SpeedBoost.Step)
            {
                Equal(boost + SpeedBoost.Step, SpeedBoost.Stepped(boost, up: true));
                Equal(boost - SpeedBoost.Step, SpeedBoost.Stepped(boost, up: false));
            }
        });
        yield return ("Speed boost: a typed value is read with a sign, a comma or spaces, and refused otherwise", () =>
        {
            Check(SpeedBoost.TryParse("1", out float b) && b == 1);
            Check(SpeedBoost.TryParse("+1.5", out b) && b == 1.5f);
            Check(SpeedBoost.TryParse(" -0.5 ", out b) && b == -0.5f);
            Check(SpeedBoost.TryParse("2,5", out b) && b == 2.5f);
            Check(SpeedBoost.TryParse("0", out b) && b == 0);
            Check(SpeedBoost.TryParse(".5", out b) && b == 0.5f);
            // Out of range reads as the limit, not as a refusal.
            Check(SpeedBoost.TryParse("100", out b) && b == SpeedBoost.Max);
            Check(SpeedBoost.TryParse("-100", out b) && b == SpeedBoost.Min);
            foreach (string bad in new[] { "", "   ", "abc", "1x", "3.5x", "NaN", "Infinity", "+", "-", "1.2.3", null! })
                Check(!SpeedBoost.TryParse(bad, out _), "read " + (bad ?? "null"));
        });
        yield return ("Speed boost: shown with its sign, the same in every culture, and readable back", () =>
        {
            Equal("+0.5", SpeedBoost.Format(0.5f));
            Equal("-0.5", SpeedBoost.Format(-0.5f));
            Equal("0", SpeedBoost.Format(0));
            Equal("0", SpeedBoost.Format(-0f));
            Equal("0", SpeedBoost.Format(0.001f));
            Equal("+1", SpeedBoost.Format(1));
            Equal("+1.25", SpeedBoost.Format(1.25f));
            Equal("+23", SpeedBoost.Format(SpeedBoost.Max));
            Equal("-6.5", SpeedBoost.Format(SpeedBoost.Min));
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Equal("+0.5", SpeedBoost.Format(0.5f));
                Check(SpeedBoost.TryParse("0.5", out float b) && b == 0.5f);
            }
            finally { CultureInfo.CurrentCulture = before; }
            for (float boost = SpeedBoost.Min; boost <= SpeedBoost.Max; boost += SpeedBoost.Step)
                Check(SpeedBoost.TryParse(SpeedBoost.Format(boost), out float back) && back == boost, "round trip " + boost);
        });
        yield return ("Speed boost: the row's English strings exist and say the step and the limits", () =>
        {
            string csv = EnglishFile();
            Check(Regex.IsMatch(csv, "^BeaverBuddies\\.Chat\\.Boost,\"[^\"]+\"", RegexOptions.Multiline), "no caption");
            var tooltip = Regex.Match(csv, "^BeaverBuddies\\.Chat\\.Boost\\.Tooltip,\"([^\"]+)\"", RegexOptions.Multiline);
            Check(tooltip.Success, "no tooltip");
            string text = tooltip.Groups[1].Value;
            Check(text.Contains("0.5") && text.Contains("30") && text.Contains("+23") && text.Contains("-6.5"), text);
            // The caption shares the labels' 92-unit column (see PanelLayoutChecks): about 14 letters.
            string caption = Regex.Match(csv, "^BeaverBuddies\\.Chat\\.Boost,\"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value;
            Check(caption.Length <= 14, "caption too long for its column: " + caption);
        });
    }
}
