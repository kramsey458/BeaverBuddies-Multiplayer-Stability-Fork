using System;
using System.Collections.Generic;
using System.Linq;

// Decides what goes into the frame rate log's patch report, from plain records: no Unity, Timberborn or Harmony types, so
// it can be tested on its own. PerfPatchReport reads Harmony and hands the records here.
namespace BeaverBuddies.Perf
{
    public sealed class PatchRecord
    {
        public string Kind, Owner, Assembly, PatchMethod;
        public int Priority, Index;
        public string[] Before, After;
    }

    public sealed class PatchedMethodRecord
    {
        /// <summary>The declaring type's short name, for the hot list.</summary>
        public string TypeName;
        public string MethodName;
        /// <summary>The full name shown in the log.</summary>
        public string FullName;
        /// <summary>Every patch on the method, in the order they run: prefixes, postfixes, transpilers, finalizers.</summary>
        public List<PatchRecord> Patches = new List<PatchRecord>();
    }

    public static class PerfPatchBuilder
    {
        public const int MaxDetailLines = 600;

        /// <summary>
        /// Lists the methods that run every tick or frame and are patched (whoever patched them), every method more than one
        /// owner patches, and every method patched only by other mods. Methods only <paramref name="ownOwner"/> patches, and
        /// that are not hot, are counted but not listed.
        /// </summary>
        public static void Build(IEnumerable<PatchedMethodRecord> methods, string ownOwner, List<string> lines)
        {
            var detail = new List<string>();
            var methodsPerOwner = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int total = 0, hot = 0, shared = 0, other = 0, oursOnly = 0;
            foreach (PatchedMethodRecord method in methods.OrderBy(m => m.FullName, StringComparer.Ordinal))
            {
                if (method.Patches.Count == 0) continue;
                total++;
                List<string> owners = method.Patches.Select(p => p.Owner ?? "").Distinct(StringComparer.Ordinal).ToList();
                foreach (string owner in owners)
                    methodsPerOwner[owner] = methodsPerOwner.TryGetValue(owner, out int n) ? n + 1 : 1;

                string tag;
                if (PerfPatchFormat.IsHot(method.TypeName, method.MethodName)) { tag = "hot"; hot++; }
                else if (owners.Count > 1) { tag = "shared"; shared++; }
                else if (owners.Count == 1 && owners[0] == ownOwner) { oursOnly++; continue; }
                else { tag = "other"; other++; }

                foreach (PatchRecord p in method.Patches)
                    detail.Add(PerfPatchFormat.PatchLine(tag, method.FullName, p.Kind, p.Owner, p.Priority, p.Index,
                        p.Before, p.After, p.Assembly, p.PatchMethod));
            }

            lines.Add($"# patches: methods={total} hot={hot} shared={shared} other={other} ownedOnlyByThisMod={oursOnly}");
            lines.Add("# patches-note: hot = runs every tick or frame and this mod patches it; shared = patched by more than one owner; " +
                      "other = patched only by other mods. Methods only this mod patches, and are not hot, are not listed.");
            lines.Add("# patches-note: the owner is the Harmony id the mod chose; the assembly is the DLL the patch code lives in, which names the mod. " +
                      "Within a kind, patches run in the order listed (higher priority first).");
            lines.Add("# patches-note: Harmony reports only patches made through the Harmony library the game loads. MonoMod hooks and native detours " +
                      "are not visible to it; this mod installs two (GameSaver.Save and UnityEngine.Time.time), and another mod could install more.");
            foreach (var pair in methodsPerOwner)
                lines.Add("# patch-owner|" + PerfPatchFormat.Clean(pair.Key) + "|methods=" + pair.Value);
            lines.AddRange(detail.Take(MaxDetailLines));
            if (detail.Count > MaxDetailLines)
                lines.Add($"# patches-truncated: {detail.Count - MaxDetailLines} more patch lines not shown");
        }
    }
}
