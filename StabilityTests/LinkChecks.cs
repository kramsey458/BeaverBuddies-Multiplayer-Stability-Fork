using System.Reflection;
using BeaverBuddies.Util;

// The links the mod opens (the first-time guide and the troubleshooting guide after a failed join) and its bug-report
// address. They must lead to this fork, never to the original project, and each site page must exist in docs/.
static class LinkChecks
{
    const string Site = "https://timbermods.github.io/BeaverBuddies-Stability-Fork/";
    const string Repo = "https://github.com/timbermods/BeaverBuddies-Stability-Fork/";

    static void Check(bool value, string message = "assertion failed") { if (!value) throw new Exception(message); }

    static IEnumerable<(string Name, string Url)> Links() => typeof(LinkHelper)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (f.Name, (string)f.GetRawConstantValue()!));

    public static IEnumerable<(string Name, Action Run)> Tests()
    {
        yield return ("In-game links lead to this fork, not the original project", () =>
        {
            var links = Links().ToList();
            Check(links.Count == 3, $"expected 3 links, found {links.Count}");
            foreach (var (name, url) in links)
            {
                Check(url.StartsWith(Site) || url.StartsWith(Repo), $"{name} leads elsewhere: {url}");
                Check(!url.Contains("thomaswp", StringComparison.OrdinalIgnoreCase), $"{name} leads to the original: {url}");
            }
            Check(LinkHelper.BugReportURL == Repo + "issues", LinkHelper.BugReportURL);
        });
        yield return ("Each in-game site link names a page the site has", () =>
        {
            string root = typeof(LinkChecks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "SourceRoot")?.Value;
            Check(root != null && Directory.Exists(Path.Combine(root, "docs")), $"no docs/ under the source root '{root}'");
            foreach (var (name, url) in Links().Where(l => l.Url.StartsWith(Site)))
            {
                string page = url.Substring(Site.Length).Split('#')[0];
                Check(page.EndsWith(".html") && File.Exists(Path.Combine(root!, "docs", page)), $"{name}: docs/{page} is missing");
            }
        });
    }
}
