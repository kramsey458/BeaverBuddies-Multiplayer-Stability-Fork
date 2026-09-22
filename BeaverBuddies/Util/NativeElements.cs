using System;
using UnityEngine.UIElements;

namespace BeaverBuddies.Util
{
    /// <summary>
    /// Elements drawn with the game's own style sheets and art, so a mod's panel looks like one of the game's. The
    /// classes are the ones the game's own panels use (read from Timberborn 1.1.2.4's UXML and USS). Run the result
    /// through the game's VisualElementInitializer (click sounds, and switching hotkeys off while a player types).
    /// </summary>
    internal static class NativeElements
    {
        /// <summary>
        /// A square - or + like the Workplace panel's worker buttons. The handler gets the click, for its modifier keys.
        /// </summary>
        public static Button SquareButton(bool plus, Action<ClickEvent> onClick)
        {
            var button = new Button();
            button.AddToClassList("button-square");
            button.AddToClassList("button-square--large");
            button.AddToClassList(plus ? "button-plus" : "button-minus");
            button.RegisterCallback<ClickEvent>(click => onClick(click));
            button.style.flexShrink = 0;
            return button;
        }
    }
}
