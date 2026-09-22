using BeaverBuddies.Activity;

// Per-player cursor styles: the production PlayerCursorPreferences source, compiled directly.
static class CursorPreferencesChecks
{
    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    sealed class TempFile : IDisposable
    {
        public readonly string Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bb-cursor-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "styles.json");
        public void Dispose() { try { System.IO.Directory.Delete(Directory, true); } catch { } }
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("Cursor style defaults match the documented look", () =>
        {
            var style = new PlayerCursorStyle();
            Equal(1f, style.Size); Equal(.5f, style.Opacity); Check(style.ColorHex == null && style.IsDefault);
            Check(ReferenceEquals(new PlayerCursorPreferences(null).Get("nobody"), PlayerCursorStyle.Default));
        });
        yield return ("Out-of-range and invalid style values are forced valid", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            prefs.Set("a", new PlayerCursorStyle { Size = 99, Opacity = -4, ColorHex = "zzzzzz" });
            var a = prefs.Get("a");
            Equal(PlayerCursorStyle.MaxSize, a.Size); Equal(PlayerCursorStyle.MinOpacity, a.Opacity); Check(a.ColorHex == null);
            prefs.Set("b", new PlayerCursorStyle { Size = float.NaN, Opacity = float.PositiveInfinity, ColorHex = "  #ff8800 " });
            var b = prefs.Get("b");
            Equal(PlayerCursorStyle.DefaultSize, b.Size); Equal(PlayerCursorStyle.DefaultOpacity, b.Opacity); Equal("FF8800", b.ColorHex);
            prefs.Set("c", new PlayerCursorStyle { Size = .01f, Opacity = 5 });
            Equal(PlayerCursorStyle.MinSize, prefs.Get("c").Size); Equal(PlayerCursorStyle.MaxOpacity, prefs.Get("c").Opacity);
        });
        yield return ("Styles equal to the default are not stored", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            prefs.Set("a", new PlayerCursorStyle { Size = 2 });
            Equal(1, prefs.Count);
            prefs.Set("a", new PlayerCursorStyle());
            Equal(0, prefs.Count);
            prefs.Set("b", new PlayerCursorStyle { ColorHex = "112233" });
            prefs.Reset("b"); Equal(0, prefs.Count);
        });
        yield return ("Stored styles are copies, so later edits never leak in", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            var style = new PlayerCursorStyle { Size = 2 };
            prefs.Set("a", style);
            style.Size = 3;
            Equal(2f, prefs.Get("a").Size);
        });
        yield return ("Styles survive a save and reload, keyed case-insensitively", () =>
        {
            using var file = new TempFile();
            var prefs = new PlayerCursorPreferences(file.Path);
            prefs.Set("sarah", new PlayerCursorStyle { ColorHex = "112233", Size = 1.5f, Opacity = .8f });
            Check(prefs.Save());
            Check(!File.Exists(file.Path + ".tmp"), "temp file left behind");
            var reloaded = new PlayerCursorPreferences(file.Path).Get("SARAH");
            Equal("112233", reloaded.ColorHex); Equal(1.5f, reloaded.Size); Equal(.8f, reloaded.Opacity);
            // Saving again replaces the existing file.
            prefs.Set("sarah", new PlayerCursorStyle { Size = 2 });
            Check(prefs.Save());
            Equal(2f, new PlayerCursorPreferences(file.Path).Get("sarah").Size);
        });
        yield return ("A damaged or hostile file falls back to defaults and is normalized", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            File.WriteAllText(file.Path, "{ not json");
            Equal(0, new PlayerCursorPreferences(file.Path).Count);
            File.WriteAllText(file.Path, "null");
            Equal(0, new PlayerCursorPreferences(file.Path).Count);
            File.WriteAllText(file.Path, "{\"x\":{\"ColorHex\":\"zz\",\"Size\":1e9,\"Opacity\":-1},\"y\":null,\"\":{\"Size\":2}}");
            var prefs = new PlayerCursorPreferences(file.Path);
            Equal(1, prefs.Count);
            Equal(PlayerCursorStyle.MaxSize, prefs.Get("x").Size); Check(prefs.Get("x").ColorHex == null);
        });
        yield return ("A file with a huge number of entries is capped", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            var entries = string.Join(",", Enumerable.Range(0, 1000).Select(i => $"\"p{i}\":{{\"Size\":2}}"));
            File.WriteAllText(file.Path, "{" + entries + "}");
            Equal(PlayerCursorPreferences.MaxEntries, new PlayerCursorPreferences(file.Path).Count);
        });
        yield return ("An unwritable location reports failure instead of throwing", () =>
        {
            using var file = new TempFile();
            Directory.CreateDirectory(file.Directory);
            string blocker = System.IO.Path.Combine(file.Directory, "blocker");
            File.WriteAllText(blocker, "a file, not a folder");
            var prefs = new PlayerCursorPreferences(System.IO.Path.Combine(blocker, "styles.json"));
            prefs.Set("a", new PlayerCursorStyle { Size = 2 });
            Check(!prefs.Save());
            Equal(2f, prefs.Get("a").Size); // still applies for this session
        });
        yield return ("Style keys use the name, plus the player number only when names collide", () =>
        {
            Equal("sarah", PlayerCursorPreferences.KeyFor("  Sarah ", 3, false));
            Equal("sarah#3", PlayerCursorPreferences.KeyFor("Sarah", 3, true));
            Equal("player", PlayerCursorPreferences.KeyFor("", 2, false));
            Equal("player", PlayerCursorPreferences.KeyFor(null, 2, false));
        });
        yield return ("Your own chat color is kept with the styles, under a key no player's name can take", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            Check(prefs.OwnChatColor == null, "nothing chosen yet");
            prefs.SetOwnChatColor(" #4d96ff ");
            Equal("4D96FF", prefs.OwnChatColor);
            Equal(1, prefs.Count);
            // Back to the default: the entry goes, as a player's default style does.
            prefs.SetOwnChatColor(null);
            Check(prefs.OwnChatColor == null && prefs.Count == 0, "default not dropped");
            prefs.SetOwnChatColor("not a color");
            Check(prefs.OwnChatColor == null && prefs.Count == 0, "an invalid color is the default");
            // A player who calls themselves by the reserved key is kept apart from it, connected or not.
            prefs.SetOwnChatColor("FF4D4D");
            Check(PlayerCursorPreferences.KeyFor(PlayerCursorPreferences.SelfKey, 2, false) != PlayerCursorPreferences.SelfKey, "name lands on the key");
            Check(PlayerCursorPreferences.KeyFor(" #YOU ", 2, false) != PlayerCursorPreferences.SelfKey, "name lands on the key (case)");
            Check(prefs.SavedColorFor(PlayerCursorPreferences.SelfKey, 2) == null, "a player named like the key gets your color");
            prefs.Set(PlayerCursorPreferences.KeyFor(PlayerCursorPreferences.SelfKey, 2, false), new PlayerCursorStyle { ColorHex = "6BCB77" });
            Equal("6BCB77", prefs.SavedColorFor(PlayerCursorPreferences.SelfKey, 2));
            Equal("FF4D4D", prefs.OwnChatColor);
        });
        yield return ("Your own chat color survives a save and reload, and an older file without it reads as before", () =>
        {
            using var file = new TempFile();
            var prefs = new PlayerCursorPreferences(file.Path);
            prefs.Set("sarah", new PlayerCursorStyle { ColorHex = "112233" });
            prefs.SetOwnChatColor("9B5DE5");
            Check(prefs.Save());
            var reloaded = new PlayerCursorPreferences(file.Path);
            Equal("9B5DE5", reloaded.OwnChatColor);
            Equal("112233", reloaded.Get("sarah").ColorHex);
            // The file is still one flat map of styles, so a build without this feature reads it as before.
            Check(File.ReadAllText(file.Path).Contains("\"" + PlayerCursorPreferences.SelfKey + "\""), "not in the same map");
            File.WriteAllText(file.Path, "{\"sarah\":{\"ColorHex\":\"112233\"}}");
            var older = new PlayerCursorPreferences(file.Path);
            Check(older.OwnChatColor == null); Equal("112233", older.Get("sarah").ColorHex);
        });
        yield return ("Every string the Player cursors dialog asks for exists in the English file", () =>
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(System.IO.Path.Combine(root, "BeaverBuddies.sln"))) root = System.IO.Path.GetDirectoryName(root)!;
            Check(root != null, "could not find the repository root");
            string csv = File.ReadAllText(System.IO.Path.Combine(root!, "BeaverBuddies", "Localizations", "enUS_BeaverBuddie.csv"));
            var defined = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(csv, "^([A-Za-z0-9.]+),", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value));
            string source = File.ReadAllText(System.IO.Path.Combine(root!, "BeaverBuddies", "Activity", "PlayerCursorSettingsUI.cs"));
            var used = System.Text.RegularExpressions.Regex.Matches(source, "\"(BeaverBuddies\\.Cursors\\.[A-Za-z0-9.]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
            Check(used.Count >= 12, "found only " + used.Count + " keys; the check is not looking in the right place");
            var missing = used.Where(k => !defined.Contains(k)).ToList();
            Check(missing.Count == 0, "missing from enUS_BeaverBuddie.csv: " + string.Join(", ", missing));
            foreach (string key in new[] { "BeaverBuddies.Cursors.You", "BeaverBuddies.Cursors.YouNote", "BeaverBuddies.Cursors.Default" })
                Check(used.Contains(key), "the dialog never asks for " + key);
        });
        yield return ("A color saved for a player is found again from a chat message, even when they are not connected", () =>
        {
            var prefs = new PlayerCursorPreferences(null);
            Check(prefs.SavedColorFor("Sarah", 3) == null, "nothing saved yet");
            prefs.Set("sarah", new PlayerCursorStyle { ColorHex = "F15BB5" });
            Equal("F15BB5", prefs.SavedColorFor("Sarah", 3));           // by name, whatever the case
            Equal("F15BB5", prefs.SavedColorFor("  SARAH ", 7));        // and whatever number they have this time
            Check(prefs.SavedColorFor("Bob", 3) == null, "another player has no color");
            // Two connected players with one name are saved under their numbers: that one wins for that player.
            prefs.Set("sarah#5", new PlayerCursorStyle { ColorHex = "2EC4B6" });
            Equal("2EC4B6", prefs.SavedColorFor("Sarah", 5));
            Equal("F15BB5", prefs.SavedColorFor("Sarah", 6));
            // A saved size or transparency alone is not a color.
            prefs.Set("bob", new PlayerCursorStyle { Size = 2 });
            Check(prefs.SavedColorFor("Bob", 1) == null, "size only");
            Check(prefs.SavedColorFor(null, 1) == null, "no name");
        });
    }
}
