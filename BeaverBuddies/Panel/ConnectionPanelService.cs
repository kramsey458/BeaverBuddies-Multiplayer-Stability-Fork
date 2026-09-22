using System;
using System.Collections.Generic;
using System.Globalization;
using BeaverBuddies.Activity;
using BeaverBuddies.IO;
using Timberborn.CoreUI;
using Timberborn.InputSystem;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using Timberborn.UILayoutSystem;
using TimberNet;
using UnityEngine;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// Shows connected players, ping, tick rate and sync state in a small HUD panel during multiplayer.
    /// It reads, and sends only chat and the speed boost (a speed change, which no more touches the
    /// simulation than the speed buttons do); any failure disables just the panel.
    /// </summary>
    public sealed class ConnectionPanelService : RegisteredSingleton, IPostLoadableSingleton, IUpdatableSingleton, IInputProcessor, IResettableSingleton
    {
        public const string ToggleKeyBindingId = "BeaverBuddies.KeyBind.TogglePanel";
        public const string FocusChatKeyBindingId = "BeaverBuddies.KeyBind.FocusChat";
        // Later than the game's own panels, so this one sits after them in its corner.
        const int LayoutOrder = 1000;
        const float RefreshSeconds = .5f, TickSampleSeconds = .25f;
        // A guest is briefly out of events between every tick; only a longer wait means it is waiting on the host.
        const float WaitingDebounceSeconds = 1.5f;
        // The host allows a short burst and then a steady rate; staying a little under it means an honest sender is
        // never the one it drops.
        const float MinChatIntervalSeconds = .4f;

        readonly UILayout layout;
        readonly VisualElementInitializer initializer;
        readonly InputService input;
        readonly ILoc loc;
        readonly SpeedManager speed;
        readonly TickRateMeter tickMeter = new TickRateMeter();
        ConnectionPanelView view;
        bool loaded, failed, chatFailed;
        // Chat: the session the chat belongs to, how far messages have been counted, and what is unread.
        TimberNetBase chatNet;
        int countedSequence, unread, myPlayerId;
        // Whether myPlayerId has been read from the connection yet (a guest is told its number a moment after joining).
        bool myPlayerIdKnown;
        float lastChatSend;
        PanelCorner placedIn = (PanelCorner)(-1);
        PanelDisplayMode lastVisibleMode = PanelDisplayMode.Expanded;
        float nextRefresh, nextTickSample, waitingSince = -1;
        double? tickRate;

        public ConnectionPanelService(UILayout layout, VisualElementInitializer initializer, InputService input, ILoc loc, SpeedManager speed)
        {
            this.layout = layout; this.initializer = initializer; this.input = input; this.loc = loc; this.speed = speed;
        }

        public void PostLoad()
        {
            try
            {
                view = new ConnectionPanelView(loc, initializer);
                view.HeaderClicked += OnHeaderClicked;
                view.FpsFloorClicked += OnFpsFloorClicked;
                if (view.Chat != null)
                {
                    view.Chat.Submit = OnChatSubmit; view.Chat.ColorOf = ChatColorOf;
                    view.Chat.BoostRequested = OnBoostRequested;
                }
                view.SetVisible(false);
                input.AddInputProcessor(this);
                loaded = true;
            }
            catch (Exception error) { Disable("could not be created", error); }
        }

        public void Reset()
        {
            loaded = false;
            // Before the chat goes away: a text box that still has the cursor keeps the game's hotkeys switched off.
            try { view?.Chat?.ReleaseFocus(); } catch (Exception) { }
            try { view?.SetLifted(false); } catch (Exception) { }
            try { view?.Root.RemoveFromHierarchy(); } catch (Exception) { }
            try { input.RemoveInputProcessor(this); } catch (Exception) { }
            placedIn = (PanelCorner)(-1);
        }

        void Disable(string what, Exception error)
        {
            failed = true;
            Plugin.LogWarning($"The connection panel {what} and is disabled for this scene: {error.Message}");
            Reset();
        }

        // ---- input: optional keys to show or hide the panel, and to start typing in the chat ----

        public bool ProcessInput()
        {
            if (!loaded || failed) return false;
            if (input.IsKeyDown(ToggleKeyBindingId))
            {
                var mode = Settings.ConnectionPanelDisplayMode;
                if (mode == PanelDisplayMode.Hidden) Settings.SetConnectionPanelDisplayMode(lastVisibleMode);
                else { lastVisibleMode = mode; Settings.SetConnectionPanelDisplayMode(PanelDisplayMode.Hidden); }
                nextRefresh = 0;
            }
            else if (input.IsKeyDown(FocusChatKeyBindingId)) FocusChat();
            // Never swallow the key press for anyone else.
            return false;
        }

        void FocusChat()
        {
            if (view.Chat == null || chatFailed || CurrentNetwork() == null) return;
            // Asking for the chat shows it, whatever state the panel was in.
            if (Settings.ConnectionPanelDisplayMode != PanelDisplayMode.Expanded)
                Settings.SetConnectionPanelDisplayMode(PanelDisplayMode.Expanded);
            view.Chat.RequestFocus();
            nextRefresh = 0;
        }

        // Only the host is shown this choice, and only the host's value is ever used.
        void OnFpsFloorClicked()
        {
            Settings.SetGuestFpsFloor(FrameRatePacing.NextFloor(Settings.GuestFpsFloorValue));
            nextRefresh = 0;
        }

        void OnHeaderClicked()
        {
            var mode = Settings.ConnectionPanelDisplayMode;
            Settings.SetConnectionPanelDisplayMode(mode == PanelDisplayMode.Expanded ? PanelDisplayMode.Collapsed : PanelDisplayMode.Expanded);
            nextRefresh = 0;
        }

        // ---- per frame ----

        public void UpdateSingleton()
        {
            if (!loaded || failed) return;
            try { Tick(); }
            catch (Exception error) { Disable("stopped working", error); }
        }

        void Tick()
        {
            var mode = Settings.ConnectionPanelDisplayMode;
            var net = CurrentNetwork();
            if (!ReferenceEquals(net, chatNet)) StartChatSession(net);
            if (mode == PanelDisplayMode.Hidden || net == null)
            {
                view.SetVisible(false);
                tickMeter.Reset(); tickRate = null; waitingSince = -1;
                return;
            }
            PlaceIfNeeded();
            // Every frame, not just at each refresh: typing and new messages must not wait half a second.
            UpdateChat(net, mode == PanelDisplayMode.Expanded);

            float now = Time.unscaledTime;
            var replay = SingletonManager.GetSingleton<ReplayService>();
            if (now >= nextTickSample && replay != null)
            {
                nextTickSample = now + TickSampleSeconds;
                tickRate = tickMeter.Sample(replay.TicksSinceLoad, now);
            }
            if (now < nextRefresh) return;
            nextRefresh = now + RefreshSeconds;

            // Line up with the game's own panel above this one (measured, so it follows the UI scale and any change).
            view.SetWidth(view.MeasureMatchedWidth());
            var model = PanelModelBuilder.Build(Collect(net, replay, now), Translate);
            view.Show(model, mode == PanelDisplayMode.Expanded);
            view.SetVisible(true);
            if (mode == PanelDisplayMode.Expanded) { RefreshChatColors(); ShowBoost(replay); }
        }

        // ---- chat ----

        // A new session (or none) starts an empty chat; the messages themselves live with the network session.
        void StartChatSession(TimberNetBase net)
        {
            chatNet = net; countedSequence = 0; unread = 0; lastChatSend = -100; myPlayerIdKnown = false;
            if (view.Chat == null || chatFailed) return;
            try { view.Chat.ReleaseFocus(); view.Chat.Clear(); view.SetUnread(0); }
            catch (Exception error) { DisableChat(error); }
        }

        void UpdateChat(TimberNetBase net, bool expanded)
        {
            if (view.Chat == null || chatFailed) return;
            try
            {
                ChatLog log = net.Chat;
                if (expanded)
                {
                    view.Chat.Tick();
                    view.Chat.Sync(log);
                    // A click on the game itself, not on any interface, gives the keyboard back to the game.
                    if (view.Chat.IsFocused && input.MainMouseButtonDown && !input.MouseOverUI) view.Chat.ReleaseFocus();
                    // While the cursor is in the box, the panel is drawn in front of the game's alerts.
                    view.SetLifted(view.Chat.IsFocused);
                    countedSequence = log.LastSequence; unread = 0;
                }
                else
                {
                    // Collapsed: the box is off screen, so the keyboard goes back to the game at once (waiting for the
                    // next refresh would leave the hotkeys off for up to half a second).
                    view.Chat.ReleaseFocus();
                    view.SetLifted(false);
                    // And count what others say, so the header can say there is something to read.
                    if (log.LastSequence > countedSequence)
                    {
                        foreach (ChatMessage message in log.Since(countedSequence))
                            if (message.PlayerId != myPlayerId) unread++;
                        countedSequence = log.LastSequence;
                    }
                }
                view.SetUnread(expanded ? 0 : unread);
            }
            catch (Exception error) { DisableChat(error); }
        }

        // Chat follows the cursor colors: what you see on a player's cursor is the color of what they say. That is the
        // color they chose for themselves, or the one you set for them in the player cursors settings.
        string ChatColorOf(ChatMessage message)
        {
            // Your own name: the color you picked for it under Player cursors (only you see it), else what others
            // see by default: your Ping Color, or the color for your player number while it is still the default.
            if (myPlayerIdKnown && message.PlayerId == myPlayerId)
                return PlayerActivityService.Preferences.OwnChatColor
                    ?? PlayerColors.Effective(ColorUtility.ToHtmlStringRGB(Settings.PingColorValue), myPlayerId);
            var activity = SingletonManager.GetSingleton<PlayerActivityService>();
            if (activity != null && activity.TryGetCursorColor(message.PlayerId, out Color cursor)) return ColorUtility.ToHtmlStringRGB(cursor);
            // No cursor for them now (they left, or player activity is off): the color you saved for them, if any,
            // else the one they sent with the message (by player number if it is still the default).
            return PlayerActivityService.Preferences.SavedColorFor(message.Name, message.PlayerId)
                ?? PlayerColors.Effective(message.Color, message.PlayerId);
        }

        void RefreshChatColors()
        {
            if (view.Chat == null || chatFailed) return;
            try { view.Chat.RefreshColors(); }
            catch (Exception error) { DisableChat(error); }
        }

        // ---- the speed boost ----

        // The boost is the session's: the request is played by everyone as an event, like a speed change. It changes
        // how fast ticks are worked through, never what is in them (SpeedBoost).
        bool OnBoostRequested(float boost)
        {
            var net = CurrentNetwork();
            if (net == null || net.IsStopped) return false;
            return BeaverBuddies.Events.SpeedBoostRequest.Send(boost);
        }

        void ShowBoost(ReplayService replay)
        {
            if (view.Chat == null || chatFailed) return;
            try { view.Chat.ShowBoost(replay?.Boost ?? 0, replay?.TargetSpeed ?? 0); }
            catch (Exception error) { DisableChat(error); }
        }

        bool OnChatSubmit(string text)
        {
            var net = CurrentNetwork();
            float now = Time.unscaledTime;
            if (net == null || net.IsStopped || now - lastChatSend < MinChatIntervalSeconds) return false;
            if (!net.SendChat(Settings.PingDisplayName, ColorUtility.ToHtmlStringRGB(Settings.PingColorValue), text)) return false;
            lastChatSend = now;
            return true;
        }

        // Chat is optional: if it fails, the rest of the panel keeps working.
        void DisableChat(Exception error)
        {
            chatFailed = true;
            Plugin.LogWarning("The chat stopped working and is disabled for this scene: " + error.Message);
            try { view.DisableChat(); } catch (Exception) { }
        }

        void PlaceIfNeeded()
        {
            var corner = Settings.ConnectionPanelCornerValue;
            if (corner == placedIn) return;
            view.Root.RemoveFromHierarchy();
            switch (corner)
            {
                case PanelCorner.TopRight: layout.AddTopRight(view.Root, LayoutOrder); break;
                case PanelCorner.BottomLeft: layout.AddBottomLeft(view.Root, LayoutOrder); break;
                case PanelCorner.BottomRight: layout.AddBottomRight(view.Root, LayoutOrder); break;
                default: layout.AddTopLeft(view.Root, LayoutOrder); break;
            }
            view.SetAlignment(corner == PanelCorner.TopRight || corner == PanelCorner.BottomRight);
            placedIn = corner;
        }

        static TimberNetBase CurrentNetwork() => EventIO.Get() is ServerEventIO host ? host.NetBase :
            EventIO.Get() is ClientEventIO guest ? guest.NetBase : null;

        /// <summary>This player's number in the session (the host is 0), or -1 with no session or before the host has said.</summary>
        public static int LocalPlayerId()
        {
            var net = CurrentNetwork();
            if (net == null) return -1;
            NetworkStatus status = net.GetNetworkStatus();
            return status.IsHost ? 0 : status.YourPlayerId;
        }

        PanelInputs Collect(TimberNetBase net, ReplayService replay, float now)
        {
            var io = EventIO.Get();
            NetworkStatus status = net.GetNetworkStatus();
            myPlayerId = status.IsHost ? 0 : status.YourPlayerId;
            myPlayerIdKnown = myPlayerId >= 0;

            // A guest is only "waiting" if it has been out of events for a while, not between ticks.
            bool outOfEvents = !status.IsHost && io != null && io.IsOutOfEvents;
            if (!outOfEvents) waitingSince = -1;
            else if (waitingSince < 0) waitingSince = now;

            var result = new PanelInputs
            {
                IsHost = status.IsHost,
                Stopped = status.IsStopped,
                Desynced = replay?.IsDesynced == true || ReplayService.HasReplayFailure,
                WaitingForHost = waitingSince >= 0 && now - waitingSince >= WaitingDebounceSeconds,
                TicksBehind = io?.TicksBehind ?? 0,
                HostSilenceSeconds = status.HostSilenceSeconds,
                TickRate = tickRate,
                Speed = speed.CurrentSpeed,
                HostPacingPercent = replay?.HostPacingPercent ?? 100,
                HostPacingHolding = replay?.HostPacingHolding == true,
                GuestFpsFloor = Settings.GuestFpsFloorValue,
                FrameRatePacingPercent = replay?.FrameRatePacingPercent ?? 100,
            };

            // Names come from player activity (the same names other players chose for pings and cursors).
            var names = new Dictionary<int, string>();
            var activity = SingletonManager.GetSingleton<PlayerActivityService>();
            if (activity != null) foreach (var player in activity.RemotePlayers) names[player.PlayerId] = player.Name;
            string me = Settings.PingDisplayName;

            if (status.IsHost)
            {
                result.Players.Add(new PanelPlayer { Id = 0, Name = me, IsYou = true, IsHost = true });
                foreach (var peer in status.Peers) result.Players.Add(FromPeer(peer, NameOf(peer.PlayerId, names), false));
            }
            else
            {
                result.Players.Add(new PanelPlayer { Id = 0, Name = NameOf(0, names), IsHost = true });
                bool foundYou = false;
                foreach (var peer in status.Peers)
                {
                    bool isYou = peer.PlayerId == status.YourPlayerId;
                    foundYou |= isYou;
                    result.Players.Add(FromPeer(peer, isYou ? me : NameOf(peer.PlayerId, names), isYou));
                }
                // Before the host's first update arrives we still know we are here.
                if (!foundYou) result.Players.Add(new PanelPlayer { Id = status.YourPlayerId, Name = me, IsYou = true });
            }
            return result;
        }

        static PanelPlayer FromPeer(PeerStatus peer, string name, bool isYou) => new PanelPlayer
        {
            Id = peer.PlayerId, Name = name, IsYou = isYou,
            RttMs = peer.RttMs, SilenceSeconds = peer.SilenceSeconds, Transport = peer.Transport,
            TicksBehind = peer.TicksBehind,
            Fps = peer.Fps,
        };

        string NameOf(int id, Dictionary<int, string> names)
        {
            if (names.TryGetValue(id, out string name)) return name;
            return id == 0 ? Translate("BeaverBuddies.Panel.Host", Array.Empty<object>())
                : Translate("BeaverBuddies.Panel.PlayerNumber", new object[] { id });
        }

        // ILoc translates a key; the placeholders are filled in here, in a fixed culture.
        string Translate(string key, object[] args)
        {
            string text = loc.T(key);
            return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
        }
    }
}
