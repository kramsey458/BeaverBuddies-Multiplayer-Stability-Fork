using System;
using System.Globalization;
using Timberborn.CoreUI;
using Timberborn.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// The connection panel's on-screen elements. Built only from inline styles so it stays readable
    /// whatever the game's stylesheet does, and it draws no special characters, only shapes and plain
    /// text, so it cannot depend on a glyph the game's font might lack.
    /// </summary>
    internal sealed class ConnectionPanelView
    {
        internal static readonly Color Ink = new Color(.95f, .91f, .82f);
        internal static readonly Color Muted = new Color(.72f, .68f, .60f);
        internal static readonly Color Rule = new Color(1f, 1f, 1f, .12f);
        static readonly Color Good = new Color(.42f, .80f, .47f);
        static readonly Color Fair = new Color(.96f, .76f, .26f);
        static readonly Color Bad = new Color(.93f, .36f, .32f);
        static readonly Color Unknown = new Color(.62f, .60f, .56f);

        readonly ILoc loc;
        readonly VisualElement topSection, header, headerDot, body, statusDot, rows, facts, chatArea;
        readonly Label title, role, chevron, statusText, unreadBadge;
        int shownUnread;
        bool chatDisabled;

        public VisualElement Root { get; }

        /// <summary>The chat half, or null if it could not be built (the rest of the panel still works).</summary>
        public ChatView Chat { get; }

        /// <summary>Raised when the header is clicked: the player wants to collapse or expand the panel.</summary>
        public event Action HeaderClicked;

        /// <summary>Raised when the host clicks the guest frame rate floor: pick the next one.</summary>
        public event Action FpsFloorClicked;

        public ConnectionPanelView(ILoc loc, VisualElementInitializer initializer)
        {
            this.loc = loc;
            Root = new VisualElement { name = "BeaverBuddiesConnectionPanel" };
            var s = Root.style;
            s.minWidth = 210; s.maxWidth = 300;
            s.marginTop = 6; s.marginBottom = 6; s.marginLeft = 6; s.marginRight = 6;
            s.paddingTop = 6; s.paddingBottom = 6; s.paddingLeft = 10; s.paddingRight = 10;
            s.backgroundColor = new Color(.09f, .08f, .06f, .86f);
            Border(Root, 1, Rule, 6);

            header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row; header.style.alignItems = Align.Center;
            headerDot = Dot(10);
            title = Text("", 14, Ink, bold: true); title.style.flexGrow = 1; title.style.flexShrink = 1;
            role = Text("", 11, Muted); role.style.marginLeft = 8;
            chevron = Text("-", 16, Muted, bold: true); chevron.style.marginLeft = 8; chevron.style.width = 14;
            chevron.style.unityTextAlign = TextAnchor.MiddleCenter;
            // Shown only while the panel is collapsed, so new messages are not missed.
            unreadBadge = Text("", 11, Fair, bold: true); unreadBadge.style.marginLeft = 8;
            unreadBadge.style.display = DisplayStyle.None;
            header.Add(headerDot); header.Add(title); header.Add(unreadBadge); header.Add(role); header.Add(chevron);
            header.RegisterCallback<ClickEvent>(_ => HeaderClicked?.Invoke());
            // Everything that was the panel before chat is the top section: chat is laid out to match its size.
            topSection = new VisualElement { name = "BeaverBuddiesConnectionPanelTop" };
            topSection.Add(header);

            body = new VisualElement();
            body.style.marginTop = 6;
            var statusRow = Horizontal(); statusRow.style.alignItems = Align.Center;
            statusDot = Dot(8);
            statusText = Text("", 13, Ink); statusText.style.flexGrow = 1;
            statusRow.Add(statusDot); statusRow.Add(statusText);
            rows = new VisualElement(); facts = new VisualElement();
            body.Add(statusRow); body.Add(Separator()); body.Add(rows); body.Add(Separator()); body.Add(facts);
            topSection.Add(body);
            Root.Add(topSection);

            // The chat sits below, in the same rectangle, and is exactly as tall as the section above it. It is laid
            // out over its own area rather than inside the panel's flow, so the panel's width stays whatever the
            // top section makes it. Chat is optional: if it cannot be built the panel is just what it was.
            chatArea = new VisualElement { name = "BeaverBuddiesChatArea" };
            chatArea.style.marginTop = 6;
            try
            {
                Chat = new ChatView(loc, initializer);
                chatArea.Add(Chat.Root);
                topSection.RegisterCallback<GeometryChangedEvent>(e => chatArea.style.height = e.newRect.height);
            }
            catch (Exception error)
            {
                Chat = null; chatDisabled = true;
                Plugin.LogWarning("The chat could not be created and is disabled: " + error.Message);
            }
            chatArea.style.display = DisplayStyle.None;
            Root.Add(chatArea);
        }

        /// <summary>Removes chat for the rest of the scene, after it failed. The rest of the panel carries on.</summary>
        public void DisableChat()
        {
            chatDisabled = true;
            try { Chat?.ReleaseFocus(); } catch (Exception) { }
            chatArea.style.display = DisplayStyle.None;
            SetUnread(0);
        }

        /// <summary>How many messages from others arrived while the panel was collapsed; 0 hides the badge.</summary>
        public void SetUnread(int count)
        {
            if (count == shownUnread) return;
            shownUnread = count;
            unreadBadge.text = count > 0 ? string.Format(CultureInfo.InvariantCulture, loc.T("BeaverBuddies.Chat.Unread"), count) : "";
            unreadBadge.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void SetVisible(bool visible)
        {
            // Hiding a text box that has the cursor would leave the game's hotkeys switched off.
            if (!visible) Chat?.ReleaseFocus();
            Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Keeps the panel to the side of the slot it sits in.</summary>
        public void SetAlignment(bool rightSide) =>
            Root.style.alignSelf = rightSide ? Align.FlexEnd : Align.FlexStart;

        public void Show(PanelModel model, bool expanded)
        {
            Color headline = ColorOf(model);
            headerDot.style.backgroundColor = headline;
            title.text = expanded ? loc.T("BeaverBuddies.Panel.Title") : model.Summary;
            role.text = expanded ? model.Role : "";
            role.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            chevron.text = expanded ? "-" : "+";
            body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (!expanded) Chat?.ReleaseFocus();
            chatArea.style.display = expanded && !chatDisabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (!expanded) return;

            statusDot.style.backgroundColor = StatusColor(model.Status, headline);
            statusText.text = model.StatusText;
            statusText.style.color = model.Status == StatusKind.InSync ? Ink : StatusColor(model.Status, headline);

            rows.Clear();
            foreach (var row in model.Rows) rows.Add(PlayerRow(row));

            facts.Clear();
            facts.Add(Fact("BeaverBuddies.Panel.LabelTickRate", model.TickRateText));
            facts.Add(Fact("BeaverBuddies.Panel.LabelSpeed", model.SpeedText));
            if (model.BehindText != null) facts.Add(Fact("BeaverBuddies.Panel.LabelBehind", model.BehindText));
            if (model.GuestsBehindText != null) facts.Add(Fact("BeaverBuddies.Panel.LabelGuestsBehind", model.GuestsBehindText));
            if (model.PacingText != null) facts.Add(Fact("BeaverBuddies.Panel.LabelPacing", model.PacingText));
            if (model.GuestFpsText != null) facts.Add(Fact("BeaverBuddies.Panel.LabelGuestFps", model.GuestFpsText));
            if (model.FpsFloorText != null) facts.Add(Choice("BeaverBuddies.Panel.LabelFpsFloor", model.FpsFloorText, () => FpsFloorClicked?.Invoke()));
            if (model.LinkText != null) facts.Add(Fact("BeaverBuddies.Panel.LabelLink", model.LinkText));
        }

        VisualElement PlayerRow(PanelRow row)
        {
            var line = Horizontal(); line.style.alignItems = Align.Center; line.style.marginTop = 3;
            var dot = Dot(8); dot.style.backgroundColor = QualityColor(row.Quality);
            var name = Text(row.Name, 13, Ink); name.style.flexGrow = 1; name.style.flexShrink = 1;
            var tag = Text(row.Tag, 11, Muted); tag.style.marginLeft = 6;
            var ping = Text(row.PingText, 13, PingColor(row.Quality));
            ping.style.marginLeft = 10; ping.style.minWidth = 52; ping.style.unityTextAlign = TextAnchor.MiddleRight;
            line.Add(dot); line.Add(name); line.Add(tag); line.Add(ping);
            return line;
        }

        // A fact the player can change: the value is underlined by a rule and clicking the line picks the next choice.
        VisualElement Choice(string labelKey, string value, Action clicked)
        {
            var line = Fact(labelKey, value + "  >");
            line.tooltip = loc.T(labelKey + ".Tooltip");
            Border(line, 1, Rule, 3);
            line.style.paddingLeft = 3; line.style.paddingRight = 3; line.style.marginLeft = -4;
            line.RegisterCallback<ClickEvent>(e => { clicked(); e.StopPropagation(); });
            return line;
        }

        VisualElement Fact(string labelKey, string value)
        {
            var line = Horizontal(); line.style.marginTop = 2;
            var label = Text(loc.T(labelKey), 12, Muted); label.style.width = 92;
            var text = Text(value, 12, Ink); text.style.flexGrow = 1; text.style.flexShrink = 1;
            line.Add(label); line.Add(text);
            return line;
        }

        // ---- colours: green is fine, yellow needs attention, red is a problem, grey is not measured yet ----

        static Color QualityColor(Quality quality)
        {
            switch (quality)
            {
                case Quality.Good: return Good;
                case Quality.Fair: return Fair;
                case Quality.Poor: case Quality.Silent: return Bad;
                default: return Unknown;
            }
        }

        // A good ping reads as normal text; only a warning colours the number.
        static Color PingColor(Quality quality) => quality == Quality.Good || quality == Quality.Unknown ? Ink : QualityColor(quality);

        static Color ColorOf(PanelModel model)
        {
            switch (model.Status)
            {
                case StatusKind.Disconnected: case StatusKind.Desynced: case StatusKind.Unstable: return Bad;
                case StatusKind.WaitingForHost: case StatusKind.CatchingUp: return Fair;
                default: return QualityColor(model.SummaryQuality);
            }
        }

        static Color StatusColor(StatusKind status, Color fallback)
        {
            switch (status)
            {
                case StatusKind.InSync: return Good;
                case StatusKind.WaitingForHost: case StatusKind.CatchingUp: return Fair;
                case StatusKind.Disconnected: case StatusKind.Desynced: case StatusKind.Unstable: return Bad;
                default: return fallback;
            }
        }

        // ---- element helpers ----

        static VisualElement Horizontal()
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Row;
            return element;
        }

        static VisualElement Dot(float size)
        {
            var dot = new VisualElement();
            dot.style.width = size; dot.style.height = size; dot.style.marginRight = 8; dot.style.flexShrink = 0;
            dot.style.borderTopLeftRadius = size / 2; dot.style.borderTopRightRadius = size / 2;
            dot.style.borderBottomLeftRadius = size / 2; dot.style.borderBottomRightRadius = size / 2;
            dot.style.backgroundColor = Unknown;
            return dot;
        }

        static VisualElement Separator()
        {
            var line = new VisualElement();
            line.style.height = 1; line.style.marginTop = 6; line.style.marginBottom = 4;
            line.style.backgroundColor = Rule;
            return line;
        }

        internal static Label Text(string text, int size, Color color, bool bold = false)
        {
            var label = new Label(text);
            label.style.color = color; label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        internal static void Border(VisualElement element, float width, Color color, float radius)
        {
            var style = element.style;
            style.borderTopWidth = width; style.borderBottomWidth = width; style.borderLeftWidth = width; style.borderRightWidth = width;
            style.borderTopColor = color; style.borderBottomColor = color; style.borderLeftColor = color; style.borderRightColor = color;
            style.borderTopLeftRadius = radius; style.borderTopRightRadius = radius;
            style.borderBottomLeftRadius = radius; style.borderBottomRightRadius = radius;
        }
    }
}
