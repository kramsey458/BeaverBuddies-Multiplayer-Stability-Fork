using System;
using System.Collections.Generic;
using Timberborn.CoreUI;
using Timberborn.Localization;
using TimberNet;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// The chat half of the connection panel: the messages, and the box to type in. It fills the space the panel
    /// gives it and never asks for any: it is laid out on top of that space, so a long message wraps inside the
    /// panel's width instead of making the panel wider. Inline styles only, like the rest of the panel.
    /// </summary>
    internal sealed class ChatView
    {
        // A backlog (a long history arriving) is drawn over a few frames rather than in one.
        const int MaxLinesPerSync = 100;

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

            Root.Add(log);
            Root.Add(input);

            // The game's own setup for these: its scroll bar look and the wheel speed the player chose, and, for
            // the text box, the part that switches the game's hotkeys off while a player types in it.
            initializer.InitializeVisualElement(log);
            initializer.InitializeVisualElement(input);

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
            AddEmptyHint();
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
        // wrong, the game's hotkeys would stay switched off with nothing here able to notice.
        VisualElement FocusedInside()
        {
            var current = input.panel?.focusController?.focusedElement as VisualElement;
            return current != null && (current == input || input.Contains(current)) ? current : null;
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
