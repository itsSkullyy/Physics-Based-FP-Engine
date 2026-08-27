using UnityEngine;
using UnityEngine.InputSystem;

// Tiny gamepad-and-keyboard navigation helper for the project's IMGUI popups. IMGUI only
// reacts to real mouse clicks - a gamepad press or an arrow key never becomes a
// GUI.Button click on its own - so every button menu (PauseMenu, LevelCompleteMenu)
// needs a manual "selected index + confirm" loop. Centralized here so they all navigate
// and highlight the same way instead of each reimplementing it slightly differently.
public static class GamepadMenu
{
    // OnGUI fires several times per rendered frame (a Layout pass, a Repaint pass, and
    // more for input events) - reading wasPressedThisFrame on every one of those calls
    // would apply the same key press several times in what is visually a single frame.
    // With an even button count that cancels out completely (advance twice on a 2-button
    // row lands back where it started, which is exactly why the rank screen looked dead),
    // and with an odd count it skips unpredictably. Poll ignores every call within a frame
    // after its first; Confirm answers true on only the first of those calls, so a menu
    // action tied to it (Restart, QuitGame, ...) fires exactly once per press.
    static int pollFrame = -1;
    static int confirmFrame = -1;

    /// Call once per OnGUI before drawing any buttons. Moves `selected` with the dpad or
    /// the arrow keys - up/right for a vertical stack (PauseMenu) or a horizontal row
    /// (LevelCompleteMenu's Try Again / Close Game), down/left for the other way. All
    /// four directions work regardless of a given menu's layout, since responding to an
    /// axis it doesn't use is harmless and some players will reach for either.
    //
    // dpad.up / dpad.down read backwards from their physical buttons on at least one
    // controller tested with this project - swapped here rather than trusting the InputSystem
    // labels, so "press down, selection moves down" actually holds on real hardware.
    public static void Poll(ref int selected, int count)
    {
        if (count <= 0) return;
        if (pollFrame == Time.frameCount) return;
        pollFrame = Time.frameCount;

        Gamepad pad = Gamepad.current;
        bool forward = (pad != null && (pad.dpad.up.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame));
        bool backward = (pad != null && (pad.dpad.down.wasPressedThisFrame || pad.dpad.left.wasPressedThisFrame));

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            forward |= kb[Key.UpArrow].wasPressedThisFrame || kb[Key.RightArrow].wasPressedThisFrame;
            backward |= kb[Key.DownArrow].wasPressedThisFrame || kb[Key.LeftArrow].wasPressedThisFrame;
        }

        if (forward) selected = (selected + 1) % count;
        else if (backward) selected = (selected - 1 + count) % count;
    }

    /// True on the first call this frame if the confirm input (gamepad South / A / Cross,
    /// or keyboard Enter) is pressed - false on every later call in the same frame, so the
    /// button action it triggers can't fire more than once per press.
    public static bool Confirm()
    {
        if (confirmFrame == Time.frameCount) return false;
        confirmFrame = Time.frameCount;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.buttonSouth.wasPressedThisFrame) return true;

        Keyboard kb = Keyboard.current;
        return kb != null && (kb[Key.Enter].wasPressedThisFrame || kb[Key.NumpadEnter].wasPressedThisFrame);
    }

    /// Draws one menu button, highlighted with `selectedStyle` while it holds the
    /// gamepad selection. Still clickable by mouse regardless of selection.
    public static bool Button(string label, int index, int selected, bool confirmPressed,
        GUIStyle style, GUIStyle selectedStyle, params GUILayoutOption[] options)
    {
        bool isSelected = index == selected;
        bool clicked = GUILayout.Button(label, isSelected ? selectedStyle : style, options);
        return clicked || (confirmPressed && isSelected);
    }
}
