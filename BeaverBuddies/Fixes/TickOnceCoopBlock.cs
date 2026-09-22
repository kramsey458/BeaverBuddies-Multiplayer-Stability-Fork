using BeaverBuddies.IO;
using BeaverBuddies.Util;
using HarmonyLib;
using System;
using Timberborn.QuickNotificationSystem;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace BeaverBuddies.Fixes
{
    /// <summary>
    /// The speed panel's "pause or tick once" key (period by default, not dev mode only) calls Ticker.TickOnce when
    /// the game is paused. That runs a whole tick through TickableBucketService.TickOnce, which calls TickNextBucket
    /// and FinishFullTick itself and never goes through TickBuckets, where the ReplayService ticks
    /// (TickableBucketServiceTickUpdatePatcher). So the tick counter and the shared actions were skipped and the tick
    /// ran on that computer only, desyncing the game. The heartbeat's random-state check did not see it either: outside
    /// Ticker.Update and outside a replay, the tick's random draws come from the non-game generator. In a co-op game it
    /// is refused with a notice; in single player the game's own method runs.
    /// </summary>
    public class TickOnceCoopNotice : RegisteredSingleton, ILoadableSingleton
    {
        private readonly QuickNotificationService _quickNotificationService;

        public TickOnceCoopNotice(QuickNotificationService quickNotificationService)
        {
            _quickNotificationService = quickNotificationService;
        }

        // Loadable only so the game creates it with the map, and the patch below can find it.
        public void Load() { }

        public void Show()
        {
            try
            {
                _quickNotificationService.SendWarningNotification(RegisteredLocalizationService.T("BeaverBuddies.TickOnce.CoopBlocked"));
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not show the tick once notice: " + error.Message);
            }
        }
    }

    // Priority.Last, the rule for a prefix that replaces the original: another mod's prefix on Ticker.TickOnce runs
    // first. If that prefix skips the original itself, Harmony skips this one too.
    [HarmonyPatch(typeof(Ticker), nameof(Ticker.TickOnce))]
    static class TickerTickOncePatcher
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix()
        {
            // The same hard stop as TickBuckets: after a failed multiplayer action the session is gone (EventIO is
            // null) but the game must not tick on. The dialog already said why, so no notice.
            if (ReplayService.HasReplayFailure) return false;
            if (EventIO.IsNull) return true;
            Plugin.Log("Refused tick once in a co-op game: it would tick this computer only");
            SingletonManager.GetSingleton<TickOnceCoopNotice>()?.Show();
            return false;
        }
    }
}
