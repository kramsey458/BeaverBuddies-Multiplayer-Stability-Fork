using BeaverBuddies.IO;
using HarmonyLib;
using System;
using System.Collections.Generic;
using Timberborn.Options;
using Timberborn.OptionsGame;
using Timberborn.TimeSpeedButtonSystem;
using Timberborn.TimeSystem;
using Timberborn.TimeSystemUI;
using Timberborn.UILayoutSystem;
using static BeaverBuddies.SingletonManager;

namespace BeaverBuddies.Events
{
    [Serializable]
    public class SpeedSetEvent : ReplayEvent
    {
        // Unpausing starts the first tick, which closes joining itself (ReplayService.DoTick).
        public override bool ChangesGame() => false;

        // The speed the player picked (the game's buttons: 1, 3, 7; 0 paused). The game runs at it plus the
        // session's boost (SpeedBoost), so with a boost of +0.5 a pick of 3 runs at 3.5.
        public float speed;

        public override void Replay(IReplayContext context)
        {
            SpeedManager sm = context.GetSingleton<SpeedManager>();
            ReplayService replayService = context.GetSingleton<ReplayService>();
            float target = SpeedBoost.Apply(speed, replayService.Boost);
            Plugin.Log($"Event: Changing speed from {sm.CurrentSpeed} to {target}"
                + (target != speed ? $" (speed {speed} with a boost of {SpeedBoost.Format(replayService.Boost)})" : ""));
            if (sm.CurrentSpeed != target) SpeedChangePatcher.SetSpeedSilentlyNow(sm, target);

            if (speed != replayService.ChosenSpeed || target != replayService.TargetSpeed)
            {
                Plugin.Log($"Event: Changing target speed from {replayService.TargetSpeed} to {target}");
                replayService.SetChosenSpeed(speed);
            }
        }
    }

    /// <summary>
    /// The session's speed boost changed (SpeedBoost): from now on the game runs at the picked speed plus this. Any
    /// player may ask, from the chat box; everyone plays the answer, the asker included, like a speed change.
    /// </summary>
    [Serializable]
    public class SpeedBoostEvent : ReplayEvent
    {
        // Like a speed change: it changes how fast ticks are worked through, not what is in them.
        public override bool ChangesGame() => false;

        public float boost;

        public override void Replay(IReplayContext context)
        {
            ReplayService replayService = context.GetSingleton<ReplayService>();
            float before = replayService.Boost;
            replayService.SetBoost(boost);
            Plugin.Log($"Event: Speed boost {SpeedBoost.Format(before)} -> {SpeedBoost.Format(replayService.Boost)}: " +
                       $"speed {replayService.ChosenSpeed} runs at {replayService.TargetSpeed}");
        }
    }

    /// <summary>Asks the session for a boost, from the chat box.</summary>
    public static class SpeedBoostRequest
    {
        /// <summary>Returns false if there is no session to ask, or it has failed; the box then shows the old value again.</summary>
        public static bool Send(float boost)
        {
            ReplayService replayService = ReplayEvent.GetReplayServiceIfReady();
            if (replayService == null || ReplayService.HasReplayFailure) return false;
            float wanted = SpeedBoost.Clamp(boost);
            if (wanted == replayService.Boost) return true;
            Plugin.Log($"Speed boost asked: {SpeedBoost.Format(wanted)} (was {SpeedBoost.Format(replayService.Boost)})");
            ReplayEvent.DoPrefix(() => new SpeedBoostEvent { boost = wanted });
            return true;
        }
    }

    [ManualMethodOverwrite]
    /*
        02/22/2026
        if (!_isLocked)
        {
            _nextSpeed = speed;
        }
     */
    [HarmonyPatch(typeof(SpeedManager), nameof(SpeedManager.ChangeSpeed), typeof(float))]
    public class SpeedChangePatcher
    {
        private static bool silently = false;

        /**
         * Sets the CurrentSpeed immediately without triggering and event.
         */
        public static void SetSpeedSilentlyNow(SpeedManager speedManager, float speed)
        {
            silently = true;
            speedManager.ChangeSpeed(speed);
            silently = false;

            // Have to call ChangeSpeed again to immediate update it.
            speedManager.ChangeSpeed();
        }

        // Records the speed itself rather than through ReplayEvent.DoPrefix, and runs first like every recording prefix.
        [HarmonyPriority(Priority.First)]
        static bool Prefix(SpeedManager __instance, ref float speed)
        {
            if (!ReplayService.IsLoaded) return true;
            // No need to log speed changes to current speed
            if (__instance.CurrentSpeed == speed) return true;
            // Also don't log if we're silent
            if (silently) return true;

            var replayService = ReplayEvent.GetReplayServiceIfReady();
            if (replayService == null) return true;

            // A request for the speed the players already picked changes nothing. With a boost the game runs at that
            // speed plus the boost (and while catching up, above it), so the check against the current speed above
            // does not catch it; without this the game would run at the bare speed for a frame and record a no-op.
            if (speed == replayService.ChosenSpeed) return false;

            replayService.RecordEvent(new SpeedSetEvent()
            {
                speed = speed
            });

            if (EventIO.ShouldPlayPatchedEvents)
            {
                // If this will actually change the speed, make sure
                // we shouldn't pause instead.
                if (EventIO.ShouldPauseTicking) speed = 0;
                return true;
            }
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
        04/19/2025
    	if (!_isLocked)
		{
			_speedBefore = CurrentSpeed;
			ChangeSpeed(value);
			_isLocked = true;
			_eventBus.Post(new SpeedLockChangedEvent(_isLocked));
		}
     */
    [HarmonyPatch(typeof(SpeedManager), nameof(SpeedManager.ChangeAndLockSpeed))]
    public class SpeedLockPatcher
    {
        static bool Prefix(SpeedManager __instance, float value)
        {
            // Clients should never freeze for dialogs. Main menu will be
            // handled separately.
            if (EventIO.Get()?.UserEventBehavior == UserEventBehavior.Send)
            {
                return false;
            }

            if (!__instance._isLocked)
            {
                __instance._speedBefore = __instance.CurrentSpeed;
                SpeedChangePatcher.SetSpeedSilentlyNow(__instance, value);
                __instance._isLocked = true;
                __instance._eventBus.Post(new SpeedLockChangedEvent(__instance._isLocked));
            }
            return false;
        }
    }

    [ManualMethodOverwrite]
    /*
     	04/19/2025
        if (_isLocked)
		{
			_isLocked = false;
			ChangeSpeed(_speedBefore);
			_eventBus.Post(new SpeedLockChangedEvent(_isLocked));
		}
     */
    [HarmonyPatch(typeof(SpeedManager), nameof(SpeedManager.UnlockSpeed))]
    public class SpeedUnlockPatcher
    {
        static bool Prefix(SpeedManager __instance)
        {
            // Clients should never unfreeze for dialogs. See above.
            if (EventIO.Get()?.UserEventBehavior == UserEventBehavior.Send)
            {
                return false;
            }

            if (__instance._isLocked)
            {
                __instance._isLocked = false;
                SpeedChangePatcher.SetSpeedSilentlyNow(__instance, __instance._speedBefore);
                __instance._eventBus.Post(new SpeedLockChangedEvent(__instance._isLocked));
            }
            return false;
        }
    }

    [Serializable]
    class ShowOptionsMenuEvent : SpeedSetEvent
    {
        public ShowOptionsMenuEvent()
        {
            speed = 0;
        }

        public override void Replay(IReplayContext context)
        {
            base.Replay(context);
            context.GetSingleton<IOptionsBox>().Show();
        }
    }

    // By default, we make showing the options menu a synced game event, rather than
    // a non-synced UI action, for two reasons:
    // 1) This ensures that the Options menu is always shown when a full
    //    tick has been completed.
    // 2) This will give other players a visual clue about why the game has
    //    paused.
    // However, only the host will be able to unpause, and only by manually
    // setting the game speed, since they won't process any events by clients
    // while they have a panel (including this one) up (I think...).
    [HarmonyPatch(typeof(GameOptionsBox), nameof(GameOptionsBox.Show))]
    public class GameOptionsBoxShowPatcher
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix()
        {
            // After a failed multiplayer action everything else is blocked on purpose (see ReplayEvent.DoPrefix),
            // but never the menu: it is the only way out. The message the player just closed tells them to return
            // to the main menu, and with the menu blocked too the game could only be killed.
            if (ReplayService.HasReplayFailure) return true;

            // This would make options menu unsynced and non-pausing,
            // but I think it's dangerous to open the menu outside of a synced pause.
            // So we will only do this if the user explicitly opts into it
            if (Settings.PauseReductionSetting == PauseReductionLevel.NeverAutoPause) return true;

            return ReplayEvent.DoPrefix(() => new ShowOptionsMenuEvent());
        }
    }

    // OverlayPanelSpeedLocker is triggering ChangeAndLockSpeed via OnPanelShown
    // This is now configurable via Settings.PauseReduction. If we don't freeze, it could in theory
    // cause invalid operations (e.g. deleting a building that's not there anymore).
    // Event creation could crash if it expects state that has changed, or it could
    // send an invalid event to the server. The server is robust to invalid actions
    // (they're always a possibility)e. For clients, stale-state
    // issues are inherent regardless of freezing, since actions always happen at a delay.
    [HarmonyPatch(typeof(OverlayPanelSpeedLocker), nameof(OverlayPanelSpeedLocker.OnPanelShown))]
    public class OverlayPanelSpeedLockerShowPatcher
    {
        public static bool Prefix()
        {
            return Settings.PauseReductionSetting == PauseReductionLevel.Off;
        }
    }

    [ManualMethodOverwrite]
    /*
        2026-09-22 (Timberborn 1.1.2.4, SpeedControlPanel.SetSpeed)
        if (timeSpeed == 0f)
        {
            float currentSpeed = _speedManager.CurrentSpeed;
            if (currentSpeed == 0f) { _speedManager.ChangeSpeed(_speedBeforePause); return; }
            _speedBeforePause = currentSpeed;
            _speedManager.ChangeSpeed(0f);
        }
        else _speedManager.ChangeSpeed(timeSpeed);
     */
    // The game returns from a pause to the speed it was running at when paused. In a session that is the picked
    // speed plus the boost (or a catch-up speed), and returning to it would pick that as the new speed and add the
    // boost again: pause at 3 + 0.5, unpause, and the game would run at 4. The speed to return to is the picked one.
    [HarmonyPatch(typeof(SpeedControlPanel), "SetSpeed")]
    static class SpeedControlPanelSetSpeedPatcher
    {
        static void Postfix(SpeedControlPanel __instance, float timeSpeed)
        {
            if (timeSpeed != 0) return;
            ReplayService replayService = ReplayEvent.GetReplayServiceIfReady();
            if (replayService != null && replayService.ChosenSpeed > 0) __instance._speedBeforePause = replayService.ChosenSpeed;
        }
    }

    [ManualMethodOverwrite]
    /*
        2026-09-22 (Timberborn 1.1.2.4, TimeSpeedButtonGroup.GetCurrentButton)
        float currentSpeed = _currentSpeedGetter();
        return _buttons.SingleOrDefault((TimeSpeedButton button) => (float)button.TimeSpeed == currentSpeed);
     */
    // The game's keys for the next and previous speed look for the button of the current speed, and find none while
    // the game runs at a boosted (or catch-up) speed, so the keys did nothing. In a session the button is the picked
    // speed's. The buttons' highlight is left to the game: at a speed no button has, it writes the speed on the last
    // one ("x3.5"), which is how the game itself shows a custom speed.
    [HarmonyPatch(typeof(TimeSpeedButtonGroup), "GetCurrentButton")]
    static class TimeSpeedButtonGroupCurrentButtonPatcher
    {
        static bool Prefix(TimeSpeedButtonGroup __instance, ref TimeSpeedButton __result)
        {
            ReplayService replayService = ReplayEvent.GetReplayServiceIfReady();
            if (replayService == null) return true;
            float chosen = replayService.ChosenSpeed;
            __result = null;
            foreach (TimeSpeedButton button in __instance._buttons)
            {
                if (button.TimeSpeed == chosen) { __result = button; break; }
            }
            return false;
        }
    }
}
