using System;
using System.Reflection;
using HarmonyLib;
using TimberNet.Perf;
using Timberborn.TickSystem;

namespace BeaverBuddies.Perf
{
    /// <summary>
    /// The patches that exist only for the frame rate log: they time and count each singleton's tick. They are made on a Harmony
    /// instance of their own when a log starts and removed when it ends, so with the log off the game does not run them at all.
    /// They only read the clock and the allocation counter.
    /// </summary>
    internal static class PerfPatches
    {
        const string Id = "beaverbuddies.perf";
        static Harmony harmony;

        internal static int Installed { get; private set; }

        internal static void Install()
        {
            Uninstall();
            try
            {
                harmony = new Harmony(Id);
                MethodInfo tick = typeof(TickableSingletonService.MeteredSingleton).GetMethod("Tick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (tick == null) { Plugin.LogWarning("The frame rate log could not find the singleton tick to time."); return; }
                harmony.Patch(tick,
                    prefix: new HarmonyMethod(typeof(PerfPatches).GetMethod(nameof(SingletonPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(PerfPatches).GetMethod(nameof(SingletonPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = 1;
            }
            catch (Exception error)
            {
                Plugin.LogWarning("The frame rate log could not time the singletons: " + error.Message);
                Uninstall();
            }
        }

        internal static void Uninstall()
        {
            try { harmony?.UnpatchAll(Id); }
            catch (Exception error) { Plugin.LogWarning("Could not remove the frame rate log's patches: " + error.Message); }
            harmony = null;
            Installed = 0;
        }

        static void SingletonPrefix(out PerfSample __state) => __state = PerfProfile.BeginSingleton();

        static void SingletonPostfix(ref TickableSingletonService.MeteredSingleton __instance, PerfSample __state)
        {
            if (__state.On) PerfProfile.EndSingleton(__instance._tickableSingleton?.GetType(), __state);
        }
    }
}
