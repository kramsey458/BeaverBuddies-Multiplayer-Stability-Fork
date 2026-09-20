using System.Text.RegularExpressions;
using BeaverBuddies.Panel;

// How wide the connection panel is and how tall its chat is, and that its words are short enough to keep it that
// way. The measuring itself needs the game; the decisions and the strings do not.
static class PanelLayoutChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}], got [{actual}]");

    static string EnglishFile()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "BeaverBuddies.sln"))) root = Path.GetDirectoryName(root)!;
        Check(root != null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(root!, "BeaverBuddies", "Localizations", "enUS_BeaverBuddie.csv"));
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("The panel takes the population panel's width when it is in the corner", () =>
        {
            Equal<float?>(300f, PanelLayout.ChooseWidth(300f, new[] { 250f, 260f }));
            // Even when a nearer panel is a different width.
            Equal<float?>(280f, PanelLayout.ChooseWidth(280f, new[] { 400f }));
        });
        yield return ("Without the population panel it follows the nearest usable panel above", () =>
        {
            Equal<float?>(250f, PanelLayout.ChooseWidth(null, new[] { 250f, 260f }));
            // A panel that is not laid out yet (NaN) or has no width is skipped, and the next one is used.
            Equal<float?>(260f, PanelLayout.ChooseWidth(null, new[] { float.NaN, 0f, 260f }));
            Equal<float?>(260f, PanelLayout.ChooseWidth(float.NaN, new[] { 260f }));
        });
        yield return ("A width that is not believable is never followed", () =>
        {
            foreach (float bad in new[] { float.NaN, 0f, 40f, PanelLayout.MinMatchedWidth - 1, PanelLayout.MaxMatchedWidth + 1, 1920f, float.PositiveInfinity })
                Check(PanelLayout.ChooseWidth(bad, new float[0]) == null, "followed " + bad);
            // The limits themselves are allowed.
            Equal<float?>(PanelLayout.MinMatchedWidth, PanelLayout.ChooseWidth(PanelLayout.MinMatchedWidth, new float[0]));
            Equal<float?>(PanelLayout.MaxMatchedWidth, PanelLayout.ChooseWidth(PanelLayout.MaxMatchedWidth, new float[0]));
        });
        yield return ("With nothing to follow the panel sizes to its own content", () =>
        {
            Check(PanelLayout.ChooseWidth(null, new float[0]) == null);
            Check(PanelLayout.ChooseWidth(null, new[] { 0f, float.NaN }) == null);
        });
        yield return ("The chat is a compact box", () =>
        {
            // Small enough that the panel, tall as it is with everything the host sees, stays clear of the game's
            // alerts at the bottom of the screen; large enough for a few lines and the box to type in.
            Check(PanelLayout.ChatHeight >= 110 && PanelLayout.ChatHeight <= 200, "chat height " + PanelLayout.ChatHeight);
        });
        yield return ("Every panel label fits its column and the pacing text stays short", () =>
        {
            string csv = EnglishFile();
            // The labels share one 92-unit column, about 14 letters; a longer one wraps and makes the panel taller.
            var labels = Regex.Matches(csv, "^(BeaverBuddies\\.Panel\\.Label[A-Za-z]+),\"([^\"]*)\"", RegexOptions.Multiline)
                .Select(m => (Key: m.Groups[1].Value, Text: m.Groups[2].Value)).ToList();
            Check(labels.Count >= 7, "found only " + labels.Count + " labels; the check is not looking in the right place");
            var tooLong = labels.Where(l => l.Text.Length > 14).Select(l => $"{l.Key} = \"{l.Text}\"").ToList();
            Check(tooLong.Count == 0, "labels too long for their column: " + string.Join("; ", tooLong));
            // The values beside them share the rest of a panel as narrow as the game's own; "{0}" stands for a number.
            var values = Regex.Matches(csv, "^(BeaverBuddies\\.Panel\\.Pacing[A-Za-z]+),\"([^\"]*)\"", RegexOptions.Multiline)
                .Select(m => (Key: m.Groups[1].Value, Text: m.Groups[2].Value.Replace("{0}", "100"))).ToList();
            Check(values.Count == 3, "expected 3 pacing strings, found " + values.Count);
            var tooWide = values.Where(v => v.Text.Length > 20).Select(v => $"{v.Key} = \"{v.Text}\"").ToList();
            Check(tooWide.Count == 0, "pacing text too long: " + string.Join("; ", tooWide));
        });
    }
}
