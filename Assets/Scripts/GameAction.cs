using System;
using UnityEngine;
using UnityEngine.InputSystem;

// One rebindable action, wrapping a real Unity InputAction so callers keep the shape
// they already use - input.jump.Pressed, input.slide.Held, input.axePickup.Label -
// while getting gamepad support, multiple bindings, composites and runtime rebinding.
//
// Supply the action inline (edited in the inspector, default) or via an
// InputActionReference from an asset, which wins over the inline one if set.
//
// Named GameAction rather than InputBinding since UnityEngine.InputSystem already
// owns that name.
[Serializable]
public class GameAction
{
    [Tooltip("Optional. If set, the action comes from an InputActionAsset and the inline action below is ignored.")]
    [SerializeField] InputActionReference reference;

    [SerializeField] InputAction action = new InputAction();

    int consumedFrame = -1;

    public InputAction Action =>
        reference != null && reference.action != null ? reference.action : action;

    public bool FromAsset => reference != null && reference.action != null;
    public bool Valid => Action != null;
    public int BindingCount => Action != null ? Action.bindings.Count : 0;
    public string Name => Action != null ? Action.name : string.Empty;

    // ---------------------------------------------------------------- construction

    public GameAction() { }

    GameAction(string name, InputActionType type, string expectedControlType)
    {
        action = new InputAction(name, type, expectedControlType: expectedControlType);
    }

    /// Press / release action. Triggers, sticks-as-buttons and keys all work.
    public static GameAction Button(string name, params string[] paths)
    {
        GameAction ga = new GameAction(name, InputActionType.Button, null);
        foreach (string p in paths) ga.Bind(p);
        return ga;
    }

    /// Continuous single axis, e.g. mouse wheel.
    public static GameAction Axis(string name, params string[] paths)
    {
        GameAction ga = new GameAction(name, InputActionType.Value, "Axis");
        foreach (string p in paths) ga.Bind(p);
        return ga;
    }

    /// Continuous 2D value, e.g. move or look.
    public static GameAction Vector(string name)
    {
        return new GameAction(name, InputActionType.Value, "Vector2");
    }

    /// Fluent single binding. Processors are the Input System's own, e.g.
    /// "stickDeadzone(min=0.15,max=0.95)" or "invertVector2(invertX=false)".
    public GameAction Bind(string path, string processors = null)
    {
        if (action != null && !string.IsNullOrEmpty(path))
            action.AddBinding(path, processors: processors);
        return this;
    }

    /// Fluent WASD-style composite. Digital normalized, so diagonals are not faster.
    public GameAction Composite2D(string up, string down, string left, string right)
    {
        if (action == null) return this;

        action.AddCompositeBinding("2DVector(mode=2)")
            .With("Up", up)
            .With("Down", down)
            .With("Left", left)
            .With("Right", right);

        return this;
    }

    // ---------------------------------------------------------------- lifecycle

    public void Enable()
    {
        InputAction a = Action;
        if (a != null && !a.enabled) a.Enable();
    }

    public void Disable()
    {
        InputAction a = Action;
        if (a != null && a.enabled) a.Disable();
    }

    // ---------------------------------------------------------------- reading

    // Held is safe to read from FixedUpdate. Pressed/Released are frame edges - read
    // those from Update, or use ConsumePressed from FixedUpdate.
    public bool Held
    {
        get { InputAction a = Action; return a != null && a.IsPressed(); }
    }

    public bool Pressed
    {
        get { InputAction a = Action; return a != null && a.WasPressedThisFrame(); }
    }

    public bool Released
    {
        get { InputAction a = Action; return a != null && a.WasReleasedThisFrame(); }
    }

    /// Hands the press to exactly one caller per frame, so a jump polled from
    /// FixedUpdate (which can run several times per rendered frame) can't double-fire.
    public bool ConsumePressed()
    {
        if (!Pressed) return false;

        int frame = Time.frameCount;
        if (consumedFrame == frame) return false;

        consumedFrame = frame;
        return true;
    }

    /// Only valid on actions built with Axis().
    public float ReadAxis()
    {
        InputAction a = Action;
        return a != null ? a.ReadValue<float>() : 0f;
    }

    /// Only valid on actions built with Vector().
    public Vector2 ReadVector()
    {
        InputAction a = Action;
        return a != null ? a.ReadValue<Vector2>() : Vector2.zero;
    }

    // ---------------------------------------------------------------- display

    /// Prompt text for the device currently in use.
    public string Label
    {
        get
        {
            bool pad = PlayerInputRouter.Instance != null && PlayerInputRouter.Instance.IsGamepad;
            return DisplayFor(pad);
        }
    }

    public string DisplayFor(bool gamepad, string fallback = "-")
    {
        int index = FindBindingIndex(gamepad);
        return index < 0 ? fallback : DisplayAt(index, fallback);
    }

    public string DisplayAt(int bindingIndex, string fallback = "-")
    {
        InputAction a = Action;
        if (a == null || bindingIndex < 0 || bindingIndex >= a.bindings.Count) return fallback;

        string path = a.bindings[bindingIndex].effectivePath;
        if (string.IsNullOrEmpty(path)) return fallback;

        // Ask the live device first, so "<Gamepad>/buttonSouth" becomes "A" on an
        // Xbox pad or "Cross" on a DualSense instead of "Button South".
        if (IsGamepadPath(path) && Gamepad.current != null)
        {
            InputControl control = InputControlPath.TryFindControl(Gamepad.current, path);
            if (control != null && !string.IsNullOrEmpty(control.displayName))
                return control.displayName;
        }

        string display = a.GetBindingDisplayString(bindingIndex,
            UnityEngine.InputSystem.InputBinding.DisplayStringOptions.DontIncludeInteractions);

        return string.IsNullOrEmpty(display) ? fallback : display;
    }

    // ---------------------------------------------------------------- binding lookup

    /// First non-composite binding that belongs to the given device class, or -1.
    public int FindBindingIndex(bool gamepad)
    {
        InputAction a = Action;
        if (a == null) return -1;

        for (int i = 0; i < a.bindings.Count; i++)
        {
            UnityEngine.InputSystem.InputBinding b = a.bindings[i];
            if (b.isComposite) continue;
            if (IsGamepadPath(b.effectivePath) == gamepad) return i;
        }

        return -1;
    }

    public bool IsGamepadBinding(int index)
    {
        InputAction a = Action;
        if (a == null || index < 0 || index >= a.bindings.Count) return false;
        return IsGamepadPath(a.bindings[index].effectivePath);
    }

    public static bool IsGamepadPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string layout = InputControlPath.TryGetDeviceLayout(path);
        if (string.IsNullOrEmpty(layout) || layout == "*") return false;

        layout = layout.Trim('<', '>');
        return InputSystem.IsFirstLayoutBasedOnSecond(layout, "Gamepad");
    }

    // ---------------------------------------------------------------- overrides

    public string SaveOverrides()
    {
        InputAction a = Action;
        return a != null ? a.SaveBindingOverridesAsJson() : string.Empty;
    }

    public void LoadOverrides(string json)
    {
        InputAction a = Action;
        if (a == null || string.IsNullOrEmpty(json)) return;

        bool wasEnabled = a.enabled;
        if (wasEnabled) a.Disable();
        a.LoadBindingOverridesFromJson(json);
        if (wasEnabled) a.Enable();
    }

    public void ClearOverrides()
    {
        InputAction a = Action;
        if (a == null) return;

        bool wasEnabled = a.enabled;
        if (wasEnabled) a.Disable();
        a.RemoveAllBindingOverrides();
        if (wasEnabled) a.Enable();
    }

    /// Path this binding currently resolves to, override included.
    public string EffectivePath(int index)
    {
        InputAction a = Action;
        if (a == null || index < 0 || index >= a.bindings.Count) return string.Empty;
        return a.bindings[index].effectivePath;
    }
}