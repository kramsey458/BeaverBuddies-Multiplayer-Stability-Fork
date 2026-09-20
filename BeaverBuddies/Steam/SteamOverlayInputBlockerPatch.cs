# if IS_STEAM

using HarmonyLib;
using Steamworks;
using System.Linq;
using System.Runtime.CompilerServices;
using Timberborn.SteamOverlaySystem;
using Timberborn.SteamStoreSystem;

namespace BeaverBuddies.Steam
{
    // While Steam's overlay is open the game pushes a panel that blocks input, and it pops that panel when the overlay
    // closes, but only if the panel is on top. See OverlayBlockerPolicy for what happens when a dialog is on top of it.
    [HarmonyPatch(typeof(SteamOverlayInputBlocker), nameof(SteamOverlayInputBlocker.SteamOverlayActivated))]
    internal class SteamOverlayInputBlockerPatch
    {
        // Blockers whose overlay closed while a dialog was on top of them, waiting to surface. Keyed by the blocker
        // itself, so an entry goes away with the scene that owned it.
        private static readonly ConditionalWeakTable<SteamOverlayInputBlocker, object> waiting =
            new ConditionalWeakTable<SteamOverlayInputBlocker, object>();
        private static readonly object Marker = new object();

        static bool Prefix(SteamOverlayInputBlocker __instance, GameOverlayActivated_t callback)
        {
            if (callback.m_nAppID != SteamAppId.AppId) return true;

            var panelStack = __instance._panelStack;
            var stack = panelStack._stack;
            bool opened = callback.m_bActive == 1;
            // An open overlay is what the blocker is for, so nothing is waiting any more.
            if (opened) waiting.Remove(__instance);

            bool buried = !panelStack.IsPanelOnTop(__instance) && IsInStack(__instance);
            switch (OverlayBlockerPolicy.Decide(opened, buried, stack.Count == 0))
            {
                case OverlayBlockerAction.Skip:
                    return false;
                case OverlayBlockerAction.PopWhenOnTop:
                    waiting.AddOrUpdate(__instance, Marker);
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsInStack(SteamOverlayInputBlocker blocker) =>
            blocker._panelStack._stack.Any(panel => ReferenceEquals(panel.PanelController, blocker));

        /// <summary>
        /// Called every frame. Pops a blocker whose overlay closed under a dialog, now that the dialog is gone: left
        /// there, an empty panel that no key closes sits on top and swallows every key press.
        /// </summary>
        internal static void ReleaseSurfacedBlocker(SteamOverlayInputBlocker blocker)
        {
            if (!waiting.TryGetValue(blocker, out _)) return;
            var panelStack = blocker._panelStack;
            if (!panelStack.IsPanelOnTop(blocker))
            {
                // Still under a dialog, unless something else already removed it.
                if (!IsInStack(blocker)) waiting.Remove(blocker);
                return;
            }
            waiting.Remove(blocker);
            Plugin.Log("Releasing the Steam overlay's input blocker, which a dialog had covered.");
            panelStack.Pop(blocker);
            blocker._inputStateResetter.ResetInputState();
        }
    }
}

#endif
