using System.Collections.Generic;
using UnityEngine.UIElements;

namespace BeaverBuddies.Panel
{
    /// <summary>
    /// Keeps the panel in front of the rest of the game's interface while the chat box has the cursor, so the alerts
    /// at the bottom of the screen cannot draw over what is being typed, and puts everything back afterwards.
    /// </summary>
    /// <remarks>
    /// The game's interface is a set of corner containers ("Top-left", "Bottom-left", and so on) that are drawn in the
    /// order they were defined; the alerts live in "Bottom-left", which comes after "Top-left", so they cover a tall
    /// panel in the top-left corner. Nothing here can change what is clickable: the game defines every container as
    /// ignoring the pointer, and only what is inside them receives it.
    /// </remarks>
    internal sealed class CornerLift
    {
        static readonly HashSet<string> Corners = new HashSet<string>
        {
            "Top-left", "Top-right", "Top-bar", "Bottom-left", "Bottom-right", "Bottom-bar", "Absolute-items"
        };

        VisualElement lifted, wasBehind;
        bool warned;

        public bool IsLifted => lifted != null;

        /// <summary>Moves the panel's corner in front of the other corners. Does nothing if it already is, or if it is not safe.</summary>
        public void Lift(VisualElement panel)
        {
            if (lifted != null) return;
            VisualElement corner = panel.parent, holder = corner?.parent;
            if (corner == null || holder == null || !Corners.Contains(corner.name)) return;
            // A corner that is laid out on its own can change places without moving anything else. If the game ever
            // changes that, leave the order alone rather than shuffle the whole interface.
            if (corner.resolvedStyle.position != Position.Absolute)
            {
                if (!warned) { warned = true; Plugin.Log($"Chat: '{corner.name}' is not positioned on its own, so it was not moved in front of the alerts."); }
                return;
            }
            int mine = holder.IndexOf(corner);
            VisualElement front = null;
            for (int i = 0; i < holder.childCount; i++)
                if (holder[i] != corner && Corners.Contains(holder[i].name)) front = holder[i];
            // Already drawn after every other corner.
            if (front == null || holder.IndexOf(front) < mine) return;

            wasBehind = holder[mine + 1];
            corner.PlaceInFront(front);
            lifted = corner;
            if (!warned) { warned = true; Plugin.Log($"Chat: '{corner.name}' is drawn in front of '{front.name}' while the chat box has the cursor."); }
        }

        /// <summary>Puts the corner back exactly where it was in the drawing order. Safe to call at any time.</summary>
        public void Restore()
        {
            VisualElement corner = lifted, next = wasBehind;
            lifted = null; wasBehind = null;
            if (corner == null) return;
            // If either has been moved or removed since, there is nothing sensible to restore to.
            if (next != null && next.parent != null && next.parent == corner.parent) corner.PlaceBehind(next);
        }
    }
}
