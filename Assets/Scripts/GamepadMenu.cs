using UnityEngine;
using UnityEngine.InputSystem;

// Gamepad-and-keyboard navigation helper for the project's IMGUI popups. IMGUI only
// reacts to real mouse clicks, so every button menu (PauseMenu, LevelCompleteMenu)
// needs a manual "selected index + confirm" loop - centralized here so they all
// navigate and highlight the same way.
public static class GamepadMenu
{
    // OnGUI fires several times per rendered frame (Layout, Repaint, input events), so
    // reading wasPressedThisFrame on every call would apply one press multiple times
    // within what is visually a single frame. Poll and Confirm each only act on the
    // first call per frame.
    static int pollFrame = -1;
    static int confirmFrame = -1;

    /// Call once per OnGUI before drawing any buttons. Moves `selected` with the dpad
    /// or arrow keys - up/right one way, down/left the other, regardless of whether
    /// the menu lays its buttons out vertically or horizontally.
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

    /// True on the first call this frame if confirm (gamepad South / A / Cross, or
    /// keyboard Enter) is pressed.
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
