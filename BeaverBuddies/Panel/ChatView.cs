using System;
using System.Collections.Generic;
using System.Globalization;
using BeaverBuddies.Util;
using Timberborn.CoreUI;
using Timberborn.Localization;
using TimberNet;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// The chat half of the connection panel: the speed boost row, the messages, and the box to type in. It fills
    /// the space the panel gives it and never asks for any: it is laid out on top of that space, so a long message
    /// wraps inside the panel's width instead of making the panel wider. Inline styles only, like the rest of the
    /// panel, except the - and + of the boost row, which are the game's own buttons.
    /// </summary>
    internal sealed class ChatView
    {
        // A backlog (a long history arriving) is drawn over a few frames rather than in one.
        const int MaxLinesPerSync = 100;
        // How long an asked-for boost is shown before the session's answer is expected to have replaced it.
        const float PendingSeconds = 2f;

        // A drawn line and what it was drawn from, so it can be drawn again in another color.
        sealed class Line
        {
            public readonly Label Label;
            public readonly ChatMessage Message;
            public string Hex;
            public Line(Label label, ChatMessage message, string hex) { Label = label; Message = message; Hex = hex; }
        }

        readonly ILoc loc;
        readonly ScrollView log;
        readonly TextField input;
        readonly VisualElement boostRow;
        TextField boostBox;
        Label boostResult;
        // The boost the row shows; NaN until the session's has been shown once. While a request is out, the asked value.
        float shownBoost = float.NaN, pendingBoost, pendingUntil;
        readonly Queue<Line> lines = new Queue<Line>();
        // Who is which color, worked out once per refresh instead of once per line.
        readonly Dictionary<(int Player, string Name, string Sent), string> colors = new Dictionary<(int, string, string), string>();
        Label emptyHint;
        int renderedSequence, focusDelayFrames;
        bool stickToBottom = true, blurRequested;

        public VisualElement Root { get; }

        /// <summary>Whether the cursor is in the box right now, asked of the panel itself rather than remembered.</summary>
        public bool IsFocused => FocusedInside() != null;

        /// <summary>Asked to send what was typed. Returns true if it went out, and only then is the box cleared.</summary>
        public Func<string, bool> Submit;

        /// <summary>
        /// Asked to set the session's speed boost (SpeedBoost). Returns true if the request went out; the row then
        /// shows the asked value until the session answers, and the old one again if it did not go out.
        /// </summary>
        public Func<float, bool> BoostRequested;

        /// <summary>
        /// Asked for the color (six hex digits) a message is drawn in. It follows the sender's cursor color, which the
        /// player can change at any time, so it is asked again on every <see cref="RefreshColors"/>. Without it, or if
        /// it fails, a line uses the color that came with the message.
        /// </summary>
        public Func<ChatMessage, string> ColorOf;

        public ChatView(ILoc loc, VisualElementInitializer initializer)
        {
            this.loc = loc;

            Root = new VisualElement { name = "BeaverBuddiesChat" };
            var s = Root.style;
            s.position = Position.Absolute; s.left = 0; s.right = 0; s.top = 0; s.bottom = 0;
            s.paddingTop = 6;
            s.borderTopWidth = 1; s.borderTopColor = ConnectionPanelView.Rule;

            log = new ScrollView(ScrollViewMode.Vertical) { name = "BeaverBuddiesChatLog" };
            log.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            log.verticalScrollerVisibility = ScrollerVisibility.Auto;
            log.style.flexGrow = 1; log.style.flexShrink = 1;
            log.style.marginBottom = 6;
            AddEmptyHint();

            input = new TextField { name = "BeaverBuddiesChatInput", maxLength = ChatMessage.MaxTextLength };
            input.textEdition.placeholder = loc.T("BeaverBuddies.Chat.Placeholder");
            input.textEdition.hidePlaceholderOnFocus = true;
            input.style.flexShrink = 0;
            input.style.marginTop = 0; input.style.marginBottom = 0; input.style.marginLeft = 0; input.style.marginRight = 0;
            var box = input.Q<VisualElement>(TextField.textInputUssName);
            if (box != null)
            {
                var b = box.style;
                b.backgroundColor = new Color(.03f, .03f, .02f, .9f);
                b.color = ConnectionPanelView.Ink; b.fontSize = 13;
                b.unityTextAlign = TextAnchor.MiddleLeft;
                b.marginTop = 0; b.marginBottom = 0; b.marginLeft = 0; b.marginRight = 0;
                b.paddingTop = 3; b.paddingBottom = 3; b.paddingLeft = 6; b.paddingRight = 6;
                ConnectionPanelView.Border(box, 1, ConnectionPanelView.Rule, 4);
            }

            // The speed boost row sits above the messages, so it is in view however full the log is.
            boostRow = BuildBoostRow();

            Root.Add(boostRow);
            Root.Add(log);
            Root.Add(input);

            // The game's own setup for these: its scroll bar look and the wheel speed the player chose, and, for
            // the text boxes, the part that switches the game's hotkeys off while a player types in one (and the
            // click sound for the boost row's buttons).
            initializer.InitializeVisualElement(log);
            initializer.InitializeVisualElement(input);
            initializer.InitializeVisualElement(boostRow);

            input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // Follow new messages, unless the player has scrolled up to read older ones.
            log.verticalScroller.valueChanged += value => stickToBottom = value >= log.verticalScroller.highValue - 2f;
            log.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => ScrollIfFollowing());
            log.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => ScrollIfFollowing());
        }

        void ScrollIfFollowing()
        {
            if (stickToBottom) log.verticalScroller.value = log.verticalScroller.highValue;
        }

        // ---- messages ----

        /// <summary>Draws messages that arrived since last time (a batch at a time, oldest first).</summary>
        public void Sync(ChatLog chat)
        {
            var fresh = chat.Since(renderedSequence, MaxLinesPerSync);
            if (fresh.Length == 0) return;
            if (emptyHint != null) { emptyHint.RemoveFromHierarchy(); emptyHint = null; }
            foreach (var message in fresh)
            {
                string hex = Resolve(message);
                var label = ConnectionPanelView.Text(ChatFormat.Line(message.Name, hex, message.Text), 13, ConnectionPanelView.Ink);
                label.enableRichText = true;
                label.style.marginBottom = 3; label.style.flexShrink = 0;
                log.contentContainer.Add(label);
                lines.Enqueue(new Line(label, message, hex));
                renderedSequence = message.Sequence;
            }
            // Only the drawing is trimmed here; the log itself keeps its own cap.
            while (lines.Count > ChatLog.MaxMessages) lines.Dequeue().Label.RemoveFromHierarchy();
        }

        /// <summary>
        /// Draws again the lines whose color has changed since they were written: the player picked another color for
        /// someone's cursor, or someone changed their own.
        /// </summary>
        public void RefreshColors()
        {
            if (ColorOf == null || lines.Count == 0) return;
            colors.Clear();
            foreach (Line line in lines)
            {
                var key = (line.Message.PlayerId, line.Message.Name, line.Message.Color);
                if (!colors.TryGetValue(key, out string hex)) colors[key] = hex = Resolve(line.Message);
                if (hex == line.Hex) continue;
                line.Hex = hex;
                line.Label.text = ChatFormat.Line(line.Message.Name, hex, line.Message.Text);
            }
        }

        // The color is cosmetic: whatever goes wrong while working it out must not cost anyone the chat.
        string Resolve(ChatMessage message)
        {
            try { return ColorOf?.Invoke(message) ?? message.Color; }
            catch (Exception) { return message.Color; }
        }

        /// <summary>Starts over, for a new session.</summary>
        public void Clear()
        {
            log.contentContainer.Clear();
            lines.Clear();
            renderedSequence = 0; stickToBottom = true;
            shownBoost = float.NaN; pendingUntil = 0;
            AddEmptyHint();
        }

        // ---- the speed boost ----

        // "Speed boost  [-] [+0.5] [+]  = 3.5x": the session's boost (SpeedBoost), which any player may change. The
        // caption sits in the same column as the labels above it (Tick rate, Speed), so the row reads as one more
        // line of the panel, the one you can change.
        VisualElement BuildBoostRow()
        {
            var row = new VisualElement { name = "BeaverBuddiesSpeedBoost" };
            row.style.flexDirection = FlexDirection.Row; row.style.alignItems = Align.Center;
            row.style.flexShrink = 0; row.style.marginBottom = 6;
            row.tooltip = loc.T("BeaverBuddies.Chat.Boost.Tooltip");

            var caption = ConnectionPanelView.Text(loc.T("BeaverBuddies.Chat.Boost"), 12, ConnectionPanelView.Muted);
            caption.style.width = 92; caption.style.flexShrink = 0;
            row.Add(caption);

            // The game's own - and + (the Workplace panel's worker buttons), drawn small.
            row.Add(Small(NativeElements.SquareButton(plus: false, _ => StepBoost(up: false))));

            // The number, typed or stepped, drawn like the chat box.
            boostBox = new TextField { name = "BeaverBuddiesSpeedBoostBox", maxLength = 6 };
            boostBox.style.width = 48; boostBox.style.flexShrink = 0;
            boostBox.style.marginTop = 0; boostBox.style.marginBottom = 0; boostBox.style.marginLeft = 4; boostBox.style.marginRight = 4;
            var box = boostBox.Q<VisualElement>(TextField.textInputUssName);
            if (box != null)
            {
                var b = box.style;
                b.backgroundColor = new Color(.03f, .03f, .02f, .9f);
                b.color = ConnectionPanelView.Ink; b.fontSize = 12;
                b.unityTextAlign = TextAnchor.MiddleCenter;
                b.height = 22; b.minHeight = 22;
                b.marginTop = 0; b.marginBottom = 0; b.marginLeft = 0; b.marginRight = 0;
                b.paddingTop = 0; b.paddingBottom = 0; b.paddingLeft = 4; b.paddingRight = 4;
                ConnectionPanelView.Border(box, 1, ConnectionPanelView.Rule, 4);
            }
            boostBox.SetValueWithoutNotify(SpeedBoost.Format(0));
            boostBox.RegisterCallback<KeyDownEvent>(OnBoostKeyDown, TrickleDown.TrickleDown);
            // Leaving the box (a click elsewhere, Tab) applies what was typed, as the game's number boxes do.
            boostBox.RegisterCallback<FocusOutEvent>(_ => CommitBoost());
            row.Add(boostBox);

            row.Add(Small(NativeElements.SquareButton(plus: true, _ => StepBoost(up: true))));

            // What the boost makes of the picked speed, "= 3.5x". Empty without a boost, or while paused.
            boostResult = ConnectionPanelView.Text("", 12, ConnectionPanelView.Muted);
            boostResult.style.marginLeft = 8; boostResult.style.flexShrink = 1;
            row.Add(boostResult);
            return row;
        }

        static Button Small(Button button)
        {
            var s = button.style;
            s.width = 22; s.height = 22; s.minWidth = 22; s.minHeight = 22;
            s.marginTop = 0; s.marginBottom = 0; s.marginLeft = 0; s.marginRight = 0;
            return button;
        }

        float BoostShown => float.IsNaN(shownBoost) ? 0 : shownBoost;

        void StepBoost(bool up)
        {
            float wanted = SpeedBoost.Stepped(BoostShown, up);
            if (wanted != BoostShown) Ask(wanted);
        }

        void CommitBoost()
        {
            if (SpeedBoost.TryParse(boostBox.value, out float wanted) && wanted != BoostShown) Ask(wanted);
            boostBox.SetValueWithoutNotify(SpeedBoost.Format(BoostShown));
        }

        // Sends the request and shows the asked value until the session answers (a moment: everyone plays the
        // answer as an event, the asker included), or the old value again if it could not be sent.
        void Ask(float wanted)
        {
            if (BoostRequested == null || !BoostRequested(wanted)) return;
            shownBoost = wanted;
            pendingBoost = wanted; pendingUntil = Time.unscaledTime + PendingSeconds;
            boostBox.SetValueWithoutNotify(SpeedBoost.Format(wanted));
        }

        void OnBoostKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                e.StopImmediatePropagation();
                CommitBoost();
                RequestBlur();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                e.StopImmediatePropagation();
                // What was typed is dropped; leaving the box then applies the value it shows, which is the old one.
                boostBox.SetValueWithoutNotify(SpeedBoost.Format(BoostShown));
                RequestBlur();
            }
        }

        /// <summary>
        /// Called at each refresh with the session's boost and the speed the game is asked to run at (0 paused).
        /// The row follows the session, except while the player is typing in the box or a request is still out.
        /// </summary>
        public void ShowBoost(float boost, float targetSpeed)
        {
            bool answered = pendingUntil <= 0 || boost == pendingBoost || Time.unscaledTime >= pendingUntil;
            if (answered)
            {
                pendingUntil = 0;
                if (float.IsNaN(shownBoost) || boost != shownBoost)
                {
                    shownBoost = boost;
                    if (!IsBoostBoxFocused) boostBox.SetValueWithoutNotify(SpeedBoost.Format(boost));
                }
            }
            string result = answered && BoostShown != 0 && targetSpeed > 0
                ? "= " + string.Format(CultureInfo.InvariantCulture, loc.T("BeaverBuddies.Panel.SpeedValue"), targetSpeed.ToString("0.##", CultureInfo.InvariantCulture))
                : "";
            if (boostResult.text != result) boostResult.text = result;
        }

        bool IsBoostBoxFocused
        {
            get
            {
                var current = FocusedInside();
                return current != null && (current == boostBox || boostBox.Contains(current));
            }
        }

        void AddEmptyHint()
        {
            emptyHint = ConnectionPanelView.Text(loc.T("BeaverBuddies.Chat.Empty"), 12, ConnectionPanelView.Muted);
            log.contentContainer.Add(emptyHint);
        }

        // ---- the box ----

        /// <summary>Puts the cursor in the box a moment from now (it cannot take focus in the same frame it appears).</summary>
        public void RequestFocus() => focusDelayFrames = 2;

        /// <summary>Takes the cursor out of the box on the next frame, after the key that asked for it has been dealt with.</summary>
        public void RequestBlur() => blurRequested = true;

        /// <summary>Called every frame the chat is on screen.</summary>
        public void Tick()
        {
            if (blurRequested) { blurRequested = false; ReleaseFocus(); }
            if (focusDelayFrames > 0 && --focusDelayFrames == 0) input.Focus();
        }

        /// <summary>
        /// Takes the cursor out of the box now. Anything that hides or removes the chat calls this first: the game
        /// switches its hotkeys off while a text box has focus, and only losing focus switches them back on.
        /// </summary>
        public void ReleaseFocus()
        {
            focusDelayFrames = 0; blurRequested = false;
            FocusedInside()?.Blur();
        }

        // Asked of the panel every time instead of remembered from focus events: if a remembered flag were ever
        // wrong, the game's hotkeys would stay switched off with nothing here able to notice. Either box counts.
        VisualElement FocusedInside()
        {
            var current = input.panel?.focusController?.focusedElement as VisualElement;
            if (current == null) return null;
            if (current == input || input.Contains(current)) return current;
            if (current == boostBox || boostBox.Contains(current)) return current;
            return null;
        }

        void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                e.StopImmediatePropagation();
                string text = (input.value ?? "").Trim();
                // Enter on an empty box is how you leave the chat.
                if (text.Length == 0) RequestBlur();
                else if (Submit != null && Submit(text)) input.SetValueWithoutNotify("");
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                e.StopImmediatePropagation();
                // Not at once: the game reads the same key press for its own menu, and must still see input blocked.
                RequestBlur();
            }
        }
    }
}
