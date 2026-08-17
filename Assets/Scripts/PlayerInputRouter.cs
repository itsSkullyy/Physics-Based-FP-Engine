using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

// Single source of truth for player input. Put this on the Player root.
//
// SETUP
//   1. Delete the old PlayerInputRouter.cs - this file replaces it, including the
//      old InputBinding class and InputSourceType enum.
//   2. Package Manager -> Input System installed, and Project Settings -> Player ->
//      Active Input Handling set to "Input System Package" or "Both".
//   3. Bindings on any existing Player object reset to the defaults below once, since
//      the serialized shape changed. The component reference itself survives, so
//      nothing else in the scene needs rewiring.
//
// Every caller keeps the API it already had: Move, LookDelta, ScrollY, and
// jump/slide/dart/grapple/axe actions with .Held, .Pressed, .Released and .Label.
// FirstPersonCharacterController, Grappling, BattleAxe and GunSway need zero edits.
[DefaultExecutionOrder(-500)]
public class PlayerInputRouter : MonoBehaviour
{
    public static PlayerInputRouter Instance { get; private set; }

    public enum Scheme { KeyboardMouse, Gamepad }

    // ---------------------------------------------------------------- actions

    [Header("Move")]
    public GameAction move = Default("move");

    [Header("Look")]
    public GameAction lookMouse = Default("lookMouse");
    public GameAction lookStick = Default("lookStick");
    public GameAction scroll = Default("scroll");

    [Header("Movement Actions")]
    public GameAction jump = Default("jump");
    public GameAction slide = Default("slide");
    public GameAction dart = Default("dart");

    [Header("Weapons (shared by every slot)")]
    public GameAction primary = Default("primary");
    public GameAction secondary = Default("secondary");

    [Header("Zip (works on every slot)")]
    public GameAction zip = Default("zip");

    [Header("Weapon Slots")]
    public GameAction slot1 = Default("slot1");
    public GameAction slot2 = Default("slot2");
    public GameAction slot3 = Default("slot3");
    public GameAction slotNext = Default("slotNext");
    public GameAction slotPrev = Default("slotPrev");
    
    [Header("Axe Pickup (works on every slot)")]
    public GameAction axePickup = Default("axePickup");

    // Aliases so BattleAxe and Grappling keep the exact call shape they already use.
    // Properties, not fields, so BuildRegistry's reflection pass never sees them and the
    // rebind menu still lists one row per real action.
    public GameAction grappleSwing => primary;
    public GameAction grapplePull  => zip;
    public GameAction axeSwing     => primary;
    public GameAction axeThrow     => secondary;
    public GameAction axeRecall    => axePickup;

    // ---------------------------------------------------------------- tuning

    [Header("Look Feel")]
    [Tooltip("Multiplies raw mouse delta. Leave at 1 and tune mouseSensitivity on the controller.")]
    public float mouseLookScale = 1f;
    [Tooltip("Stick look speed in mouse-delta units per second. 2600 lands near 260 deg/s " +
             "at the controller's default 0.1 sensitivity.")]
    public float gamepadLookSpeed = 2600f;
    [Range(0f, 0.5f)] public float gamepadLookDeadzone = 0.15f;
    [Tooltip("1 = linear. Higher gives finer aim near centre without losing top speed.")]
    [Range(1f, 4f)] public float gamepadLookExponent = 2f;
    public bool invertLookY = false;
    public bool blockLookWhenCursorFree = true;

    [Header("Device Switching")]
    [Tooltip("Stick or trigger movement past this counts as picking up the controller.")]
    [Range(0.05f, 0.9f)] public float gamepadWakeThreshold = 0.3f;
    public bool lockCursorOnGamepad = true;

    [Header("Rumble")]
    public bool enableRumble = true;
    [Range(0f, 1f)] public float rumbleScale = 1f;

    [Header("Global")]
    public bool inputEnabled = true;

    [Header("Saving")]
    [Tooltip("PlayerPrefs key the rebind menu writes to. Bump the suffix to invalidate old saves.")]
    public string saveKey = "input.bindings.v4";
    public bool loadSavedBindingsOnAwake = true;

    // ---------------------------------------------------------------- state

    public struct Entry
    {
        public string field;
        public string label;
        public GameAction action;
    }

    readonly List<Entry> registry = new List<Entry>();
    public IReadOnlyList<Entry> Registry => registry;

    Scheme active = Scheme.KeyboardMouse;
    public Scheme ActiveScheme => active;
    public bool IsGamepad => active == Scheme.Gamepad;
    public event Action<Scheme> SchemeChanged;

    Vector2 lookDelta;
    int lookFrame = -1;
    bool lastEnabled = true;
    IDisposable buttonWatch;
    Coroutine rumbleRoutine;

    // ---------------------------------------------------------------- defaults

    // One place that knows every default. Field initializers use it, and so does the
    // repair pass, so an action added in code later cannot end up silently unbound on
    // a prefab that was serialized before it existed.
    public static GameAction Default(string field)
    {
        switch (field)
        {
            case "move":
                return GameAction.Vector("Move")
                    .Composite2D("<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d")
                    .Composite2D("<Keyboard>/upArrow", "<Keyboard>/downArrow",
                                 "<Keyboard>/leftArrow", "<Keyboard>/rightArrow")
                    .Bind("<Gamepad>/leftStick", "stickDeadzone(min=0.15,max=0.95)");

            case "lookMouse":
                return GameAction.Vector("LookMouse").Bind("<Mouse>/delta");

            case "lookStick":
                return GameAction.Vector("LookStick")
                    .Bind("<Gamepad>/rightStick", "stickDeadzone(min=0.12,max=0.95)");

            case "scroll":
                return GameAction.Axis("Scroll", "<Mouse>/scroll/y", "<Gamepad>/dpad/y");

            case "jump":         return GameAction.Button("Jump", "<Keyboard>/space", "<Gamepad>/buttonSouth");
            case "slide":        return GameAction.Button("Slide", "<Keyboard>/leftShift", "<Gamepad>/buttonEast");
            case "dart":         return GameAction.Button("Dart", "<Keyboard>/leftShift", "<Gamepad>/buttonEast");
            case "primary":      return GameAction.Button("Primary", "<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            case "secondary":    return GameAction.Button("Secondary", "<Mouse>/rightButton", "<Gamepad>/leftTrigger");
            case "zip":          return GameAction.Button("Zip", "<Mouse>/rightButton", "<Gamepad>/leftShoulder");
            case "axePickup":    return GameAction.Button("Axe Pickup", "<Keyboard>/g", "<Gamepad>/buttonWest");
            
            // D-pad up/down is left alone: that is the scroll axis, which reels the rope.
            case "slot1":        return GameAction.Button("Slot 1", "<Keyboard>/1");
            case "slot2":        return GameAction.Button("Slot 2", "<Keyboard>/2");
            case "slot3":        return GameAction.Button("Slot 3", "<Keyboard>/3");
            
            case "slotNext":     return GameAction.Button("Slot Next", "<Gamepad>/dpad/right");
            case "slotPrev":     return GameAction.Button("Slot Prev", "<Gamepad>/dpad/left");
            
        }

        return null;
    }

    // ---------------------------------------------------------------- lifecycle

    void Awake()
    {
        Instance = this;

        BuildRegistry();

        if (loadSavedBindingsOnAwake)
            LoadBindings();
    }

    void OnEnable()
    {
        for (int i = 0; i < registry.Count; i++)
            registry[i].action.Enable();

        lastEnabled = inputEnabled;
        ApplyEnabledState();

        // Cheaper and more reliable than sweeping every control every frame.
        buttonWatch = InputSystem.onAnyButtonPress.Call(OnAnyButton);
    }

    void OnDisable()
    {
        buttonWatch?.Dispose();
        buttonWatch = null;

        for (int i = 0; i < registry.Count; i++)
            registry[i].action.Disable();

        StopRumble();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (inputEnabled != lastEnabled)
        {
            lastEnabled = inputEnabled;
            ApplyEnabledState();
        }

        DetectAnalogDevice();
        ComputeLook();
    }

    void ApplyEnabledState()
    {
        // Disabling the actions themselves means nothing leaks through, rather than
        // relying on every call site to remember to check a flag.
        for (int i = 0; i < registry.Count; i++)
        {
            if (inputEnabled) registry[i].action.Enable();
            else registry[i].action.Disable();
        }

        if (!inputEnabled)
        {
            lookDelta = Vector2.zero;
            StopRumble();
        }
    }

    void BuildRegistry()
    {
        registry.Clear();

        FieldInfo[] fields = GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);

        foreach (FieldInfo f in fields)
        {
            if (f.FieldType != typeof(GameAction)) continue;

            // Bindings live in Default(), not in the scene file. Unity rebuilds a
            // serialized GameAction with its own copy of the old binding array, which
            // leaves the singleton action and its hidden map holding two different
            // arrays - that is the "bindings array must match" assert, and it makes
            // the action resolve against stale controls. Building fresh sidesteps it
            // entirely, and an action added in code can never come back unbound on an
            // object that was serialized before it existed.
            GameAction fresh = Default(f.Name);
            if (fresh != null) f.SetValue(this, fresh);

            GameAction ga = f.GetValue(this) as GameAction;
            if (ga == null) continue;

            registry.Add(new Entry { field = f.Name, label = Prettify(f.Name), action = ga });
        }
    }

    static string Prettify(string field)
    {
        if (string.IsNullOrEmpty(field)) return field;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(char.ToUpper(field[0]));

        for (int i = 1; i < field.Length; i++)
        {
            if (char.IsUpper(field[i])) sb.Append(' ');
            sb.Append(field[i]);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- public reads

    public Vector2 Move
    {
        get
        {
            if (!inputEnabled) return Vector2.zero;

            Vector2 m = move.ReadVector();
            return m.sqrMagnitude > 1f ? m.normalized : m;
        }
    }

    /// Frame delta in mouse units. Stick input is converted into the same units, so
    /// the controller's mouseSensitivity keeps meaning the same thing for both.
    public Vector2 LookDelta
    {
        get
        {
            if (lookFrame != Time.frameCount) ComputeLook();
            return lookDelta;
        }
    }

    public float ScrollY => inputEnabled ? scroll.ReadAxis() : 0f;

    void ComputeLook()
    {
        lookFrame = Time.frameCount;
        lookDelta = Vector2.zero;

        if (!inputEnabled) return;
        if (blockLookWhenCursorFree && Cursor.lockState != CursorLockMode.Locked) return;

        Vector2 d = lookMouse.ReadVector() * mouseLookScale;

        Vector2 stick = ShapeStick(lookStick.ReadVector());
        if (stick != Vector2.zero)
        {
            // Unscaled on purpose. Hitstop drops timeScale to a crawl and stick aim
            // would crawl with it while mouse aim carried on at full speed.
            d += stick * gamepadLookSpeed * Time.unscaledDeltaTime;
        }

        if (invertLookY) d.y = -d.y;
        lookDelta = d;
    }

    Vector2 ShapeStick(Vector2 raw)
    {
        float mag = raw.magnitude;
        if (mag <= gamepadLookDeadzone) return Vector2.zero;

        float t = Mathf.InverseLerp(gamepadLookDeadzone, 1f, mag);
        t = Mathf.Pow(Mathf.Clamp01(t), gamepadLookExponent);

        return raw / mag * t;
    }

    // ---------------------------------------------------------------- device switching

    void OnAnyButton(InputControl control)
    {
        if (control?.device == null) return;

        if (control.device is Gamepad) SetScheme(Scheme.Gamepad);
        else if (control.device is Keyboard || control.device is Mouse) SetScheme(Scheme.KeyboardMouse);
    }

    void DetectAnalogDevice()
    {
        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            float t = gamepadWakeThreshold;
            bool moved =
                pad.leftStick.ReadValue().magnitude > t ||
                pad.rightStick.ReadValue().magnitude > t ||
                pad.leftTrigger.ReadValue() > t ||
                pad.rightTrigger.ReadValue() > t;

            if (moved)
            {
                SetScheme(Scheme.Gamepad);
                return;
            }
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.delta.ReadValue().sqrMagnitude > 4f)
            SetScheme(Scheme.KeyboardMouse);
    }

    void SetScheme(Scheme s)
    {
        if (active == s) return;

        active = s;

        if (s == Scheme.Gamepad && lockCursorOnGamepad && Cursor.lockState != CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        SchemeChanged?.Invoke(s);
    }

    // ---------------------------------------------------------------- rumble

    /// Low and high are the two motors, 0..1. Hook this into PlayerJuice next to the
    /// existing shaker calls - a landing or an axe hit is the obvious place.
    public void Rumble(float low, float high, float duration)
    {
        if (!enableRumble || !inputEnabled || duration <= 0f) return;
        if (Gamepad.current == null) return;

        StopRumble();
        rumbleRoutine = StartCoroutine(RumbleRoutine(
            Mathf.Clamp01(low) * rumbleScale,
            Mathf.Clamp01(high) * rumbleScale,
            duration));
    }

    IEnumerator RumbleRoutine(float low, float high, float duration)
    {
        Gamepad pad = Gamepad.current;
        pad.SetMotorSpeeds(low, high);

        float end = Time.unscaledTime + duration;
        while (Time.unscaledTime < end)
        {
            // Fades out, and survives the pad being yanked mid-shake.
            if (Gamepad.current == null) break;

            float k = Mathf.InverseLerp(end, end - duration, Time.unscaledTime);
            Gamepad.current.SetMotorSpeeds(low * k, high * k);
            yield return null;
        }

        InputSystem.ResetHaptics();
        rumbleRoutine = null;
    }

    public void StopRumble()
    {
        if (rumbleRoutine != null)
        {
            StopCoroutine(rumbleRoutine);
            rumbleRoutine = null;
        }

        InputSystem.ResetHaptics();
    }

    // ---------------------------------------------------------------- saving

    [Serializable]
    class SavedBinding
    {
        public string field;
        public string overrides;
    }

    [Serializable]
    class SavedBindings
    {
        public List<SavedBinding> entries = new List<SavedBinding>();
    }

    public void SaveBindings()
    {
        SavedBindings payload = new SavedBindings();

        for (int i = 0; i < registry.Count; i++)
        {
            string json = registry[i].action.SaveOverrides();
            if (string.IsNullOrEmpty(json)) continue;

            payload.entries.Add(new SavedBinding { field = registry[i].field, overrides = json });
        }

        PlayerPrefs.SetString(saveKey, JsonUtility.ToJson(payload));
        PlayerPrefs.Save();
    }

    public void LoadBindings()
    {
        if (!PlayerPrefs.HasKey(saveKey)) return;

        SavedBindings payload = JsonUtility.FromJson<SavedBindings>(PlayerPrefs.GetString(saveKey));
        if (payload?.entries == null) return;

        foreach (SavedBinding saved in payload.entries)
        {
            Entry entry = Find(saved.field);
            entry.action?.LoadOverrides(saved.overrides);
        }
    }

    [ContextMenu("Reset Bindings To Defaults")]
    public void ResetAllBindings()
    {
        for (int i = 0; i < registry.Count; i++)
            registry[i].action.ClearOverrides();

        PlayerPrefs.DeleteKey(saveKey);
        PlayerPrefs.Save();
    }

    public Entry Find(string field)
    {
        for (int i = 0; i < registry.Count; i++)
            if (registry[i].field == field) return registry[i];

        return default;
    }

    // ---------------------------------------------------------------- resolve

    // Called from other scripts' Awake. Finds the router, or drops one in with
    // default bindings so nothing silently dies when it is missing.
    public static PlayerInputRouter Resolve(Component owner)
    {
        if (Instance != null) return Instance;

        PlayerInputRouter router = owner != null
            ? owner.GetComponentInParent<PlayerInputRouter>()
            : null;

        if (router == null)
            router = FindFirstObjectByType<PlayerInputRouter>();

        if (router == null && owner != null)
        {
            GameObject host = owner.transform.root.gameObject;
            router = host.AddComponent<PlayerInputRouter>();
            Debug.LogWarning("PlayerInputRouter was missing. Added one to '" + host.name +
                             "' with default bindings.", host);
        }

        Instance = router;
        return router;
    }
}