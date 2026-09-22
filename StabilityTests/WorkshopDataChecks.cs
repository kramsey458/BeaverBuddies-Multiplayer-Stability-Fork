using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;

// What a build leaves in the mod folder for Timberborn's Workshop uploader. The uploader reads workshop_data.json
// beside the mod and updates the Workshop item it names, so a build must never leave the original project's item
// there. These run the project's real PostBuild step into a scratch Documents folder: they need the .NET SDK, not
// the game.
static class WorkshopDataChecks
{
    // thomaswp's "BeaverBuddies - Multiplayer Co-Op" Workshop item. This fork is not published on the Workshop.
    const string UpstreamItemId = "3293380223";

    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }

    // A Documents folder of its own, so a check never touches the one in env.props.
    sealed class ScratchDocuments : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "bb-workshop-" + Guid.NewGuid().ToString("N"));
        public string ModFolder => Path.Combine(Root, "Timberborn", "Mods", "BeaverBuddies");
        public string WorkshopData => Path.Combine(ModFolder, "workshop_data.json");
        public ScratchDocuments() { Directory.CreateDirectory(Root); }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }

        // Written the way the game's uploader writes it after it creates an item.
        public string Write(string itemId, string name = "BeaverBuddies")
        {
            Directory.CreateDirectory(ModFolder);
            string text = new JObject
            {
                ["ItemId"] = itemId, ["Name"] = name, ["Visibility"] = "Public", ["UpdateDescription"] = true,
                ["UpdateVisibility"] = true, ["UpdatePreview"] = true, ["UpdateTags"] = true, ["Tags"] = new JArray("Mod"),
            }.ToString();
            File.WriteAllText(WorkshopData, text);
            return text;
        }

        public string ItemId => File.Exists(WorkshopData) ? (string)JObject.Parse(File.ReadAllText(WorkshopData))["ItemId"] : null;
    }

    // Runs only the PostBuild target, which copies the mod into $(DocumentsPath)Timberborn\Mods\BeaverBuddies. The
    // configuration has no build output, so nothing but the root files is copied.
    static void PostBuild(ScratchDocuments docs)
    {
        // The checkout the tests were built against (StabilityTests.csproj records it), so that
        // `-p:SourceRoot=<another checkout>` runs that checkout's PostBuild step rather than this one's.
        string root = typeof(WorkshopDataChecks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceRoot")?.Value;
        Check(root != null && File.Exists(Path.Combine(root, "BeaverBuddies", "BeaverBuddies.csproj")),
            $"could not find BeaverBuddies/BeaverBuddies.csproj under the source root '{root}'");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Both paths are set outright, and end in '/' because a trailing backslash would escape a closing quote.
        foreach (string arg in new[]
        {
            "msbuild", Path.Combine(root!, "BeaverBuddies", "BeaverBuddies.csproj"), "-t:PostBuild",
            "-p:Configuration=WorkshopDataCheck", "-p:DocumentsPath=" + docs.Root + "/",
            "-p:BeaverBuddiesModsPath=" + docs.ModFolder + "/", "-nologo", "-v:q", "-nodeReuse:false",
        })
            start.ArgumentList.Add(arg);
        // `dotnet run` can hand its own MSBuild settings down; the child finds its own.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0, "the PostBuild step failed: " + output + errors.Result);
        // Otherwise a check could pass on a mod folder the build never wrote to.
        Check(File.Exists(Path.Combine(docs.ModFolder, "thumbnail.png")), "the PostBuild step did not copy the mod into the scratch folder");
    }

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("A build does not point the mod folder at the original project's Workshop item", () =>
        {
            using var docs = new ScratchDocuments();
            PostBuild(docs);
            Check(docs.ItemId != UpstreamItemId,
                $"the built mod folder's workshop_data.json has ItemId {docs.ItemId}, the original project's Workshop item");
        });
        yield return ("A build removes the original project's Workshop item left there by an earlier build", () =>
        {
            // Builds before this change copied the original project's workshop_data.json into the mod folder, and
            // nothing else ever removes it.
            using var docs = new ScratchDocuments();
            docs.Write(UpstreamItemId);
            PostBuild(docs);
            Check(docs.ItemId != UpstreamItemId,
                $"the built mod folder's workshop_data.json still has ItemId {docs.ItemId}, the original project's Workshop item");
        });
        yield return ("A build keeps a Workshop item the uploader created for this mod folder", () =>
        {
            // With no workshop_data.json the uploader creates a new item and records it beside the mod; rebuilding
            // must not lose or replace that record.
            using var docs = new ScratchDocuments();
            string own = docs.Write("1234567890");
            PostBuild(docs);
            Check(File.Exists(docs.WorkshopData) && File.ReadAllText(docs.WorkshopData) == own,
                $"the uploader's workshop_data.json was replaced or removed (ItemId now {docs.ItemId ?? "missing"})");
        });
        yield return ("A build keeps a Workshop item of its own that only mentions the original project's id", () =>
        {
            // Only the ItemId decides which item the uploader updates: a longer id that contains the original one, or
            // a name that mentions it, is someone's own item.
            foreach (var (itemId, name) in new[]
            {
                ("13293380223", "BeaverBuddies"),
                ("3512345678", $"BeaverBuddies fork (based on {UpstreamItemId})"),
            })
            {
                using var docs = new ScratchDocuments();
                string own = docs.Write(itemId, name);
                PostBuild(docs);
                Check(File.Exists(docs.WorkshopData) && File.ReadAllText(docs.WorkshopData) == own,
                    $"the workshop_data.json for item {itemId} named \"{name}\" was replaced or removed");
            }
        });
    }
}
