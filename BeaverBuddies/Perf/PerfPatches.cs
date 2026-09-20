using HarmonyLib;
using Timberborn.TickSystem;

namespace BeaverBuddies.Perf
{
    /*
     * The frame boundary.
     *
     * Ticker.Update is the game's per-frame entry into the simulation, and it runs every frame whatever the
     * speed, including while paused and while a player waits for the other. Measuring from one of these to
     * the next therefore gives the real frame period, drawing included.
     *
     * A patch of its own rather than a line added to the determinism patcher, so the whole of the logging
     * can be removed by deleting this folder and the paired Begin/End calls. It runs once per frame, so the
     * second detour on this method costs nothing worth counting.
     *
     * The Finalizer runs even if the frame threw, so a bad frame closes rather than swallowing the next one.
     */
    [HarmonyPatch(typeof(Ticker), nameof(Ticker.Update))]
    internal static class PerfFramePatcher
    {
        static void Prefix()
        {
            if (!PerfProbe.Enabled) return;
            PerfSession.BeginFrame();
        }

        static void Finalizer()
        {
            if (!PerfProbe.Enabled) return;
            PerfSession.EndFrame();
        }
    }
}
