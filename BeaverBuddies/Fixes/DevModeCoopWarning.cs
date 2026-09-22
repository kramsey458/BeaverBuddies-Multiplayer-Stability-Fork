using BeaverBuddies.IO;
using BeaverBuddies.Util;
using System;
using Timberborn.Debugging;
using Timberborn.QuickNotificationSystem;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Fixes
{
    /// <summary>
    /// Dev mode's tools change the game on the computer they are used on, and the mod does not play them on the other
    /// computers: in a co-op game its instant unlock (Ctrl-click on a locked building, which goes around the unlock the
    /// mod shares), a construction site's "Finish now", deleting a beaver (CharacterKiller) or any object (the dev
    /// deletion tool), the debug panels and spawning plants with the planting tool desync the game. Its two keys that
    /// the game reads while placing and deleting are off in co-op (DevKeysCoopFix). This says so when dev mode is turned
    /// on in a co-op game. Every scene starts with dev mode off, so PostLoad only matters if another mod turns it on
    /// while loading.
    /// </summary>
    public class DevModeCoopWarning : ILoadableSingleton, IPostLoadableSingleton
    {
        private readonly EventBus _eventBus;
        private readonly DevModeManager _devModeManager;
        private readonly QuickNotificationService _quickNotificationService;

        public DevModeCoopWarning(EventBus eventBus, DevModeManager devModeManager, QuickNotificationService quickNotificationService)
        {
            _eventBus = eventBus;
            _devModeManager = devModeManager;
            _quickNotificationService = quickNotificationService;
        }

        public void Load()
        {
            _eventBus.Register(this);
        }

        public void PostLoad()
        {
            if (_devModeManager.Enabled) Warn();
        }

        [OnEvent]
        public void OnDevModeToggled(DevModeToggledEvent devModeToggledEvent)
        {
            if (devModeToggledEvent.Enabled) Warn();
        }

        private void Warn()
        {
            if (EventIO.IsNull) return;
            Plugin.LogWarning("Dev mode is on in a co-op game: its tools change this computer only and desync the game");
            try
            {
                _quickNotificationService.SendWarningNotification(RegisteredLocalizationService.T("BeaverBuddies.DevMode.CoopWarning"));
            }
            catch (Exception error)
            {
                // A notice that cannot be shown must never disturb the game.
                Plugin.LogWarning("Could not show the dev mode notice: " + error.Message);
            }
        }
    }
}
