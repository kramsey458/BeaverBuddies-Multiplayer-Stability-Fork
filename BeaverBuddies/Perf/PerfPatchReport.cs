using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// Reads Harmony's records of who has patched what, for the header of the frame rate log. It only reads. What is
    /// listed, and how, is decided in <see cref="PerfPatchBuilder"/>. If reading fails the header says so and the rest of
    /// the log is unaffected. This is the one part of the log that only runs for real inside the game.
    /// </summary>
    internal static class PerfPatchReport
    {
        internal static void Append(List<string> lines)
        {
            try
            {
                var methods = new List<PatchedMethodRecord>();
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    var record = new PatchedMethodRecord
                    {
                        TypeName = method.DeclaringType?.Name,
                        MethodName = method.Name,
                        FullName = (method.DeclaringType?.FullName ?? "?") + "." + method.Name,
                    };
                    AddPatches(record, "prefix", info.Prefixes);
                    AddPatches(record, "postfix", info.Postfixes);
                    AddPatches(record, "transpiler", info.Transpilers);
                    AddPatches(record, "finalizer", info.Finalizers);
                    methods.Add(record);
                }
                // Built aside, so a failure part way leaves the header without a half-written report.
                var report = new List<string>();
                PerfPatchBuilder.Build(methods, Plugin.ID, report);
                lines.AddRange(report);
            }
            catch (Exception error)
            {
                lines.Add("# patches-unavailable: " + PerfPatchFormat.Clean(error.GetType().Name + " " + error.Message));
            }
        }

        static void AddPatches(PatchedMethodRecord record, string kind, IEnumerable<Patch> patches)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                MethodInfo patchMethod = patch.PatchMethod;
                record.Patches.Add(new PatchRecord
                {
                    Kind = kind,
                    Owner = patch.owner,
                    Priority = patch.priority,
                    Index = patch.index,
                    Before = patch.before,
                    After = patch.after,
                    Assembly = patchMethod?.DeclaringType?.Assembly.GetName().Name,
                    PatchMethod = patchMethod == null ? null : patchMethod.DeclaringType?.FullName + "." + patchMethod.Name,
                });
            }
        }
    }
}
