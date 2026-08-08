using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Single source of truth for player input. Put this on the Player root.
// Every other script asks this for input instead of touching Keyboard/Mouse directly,
// so rebinding happens in one inspector instead of five scripts.
public enum InputSourceType { None, Key, LeftMouse, RightMouse, MiddleMouse }

[System.Serializable]
public class InputBinding
{
    public InputSourceType source = InputSourceType.Key;
    public Key key = Key.None;

    public InputBinding() { }

    public InputBinding(Key k)
    {
        source = InputSourceType.Key;
        key = k;
    }

    public InputBinding(InputSourceType s)
    {
        source = s;
        key = Key.None;
    }

    ButtonControl Control
    {
        get
        {
            switch (source)
            {
                case InputSourceType.Key:
                    if (key == Key.None || Keyboard.current == null) return null;
                    return Keyboard.current[key];
                case InputSourceType.LeftMouse:
                    return Mouse.current != null ? Mouse.current.leftButton : null;
                case InputSourceType.RightMouse:
                    return Mouse.current != null ? Mouse.current.rightButton : null;
                case InputSourceType.MiddleMouse:
                    return Mouse.current != null ? Mouse.current.middleButton : null;
            }
            return null;
        }
    }

    // Held is safe to read from FixedUpdate. Pressed / Released are frame edges,
    // so only read those from Update.
    public bool Held
    {
        get { ButtonControl c = Control; return c != null && c.isPressed; }
    }

    public bool Pressed
    {
        get { ButtonControl c = Control; return c != null && c.wasPressedThisFrame; }
    }

    public bool Released
    {
        get { ButtonControl c = Control; return c != null && c.wasReleasedThisFrame; }
    }

    public string Label
    {
        get
        {
            switch (source)
            {
                case InputSourceType.Key: return key.ToString();
                case InputSourceType.LeftMouse: return "LMB";
                case InputSourceType.RightMouse: return "RMB";
                case InputSourceType.MiddleMouse: return "MMB";
            }
            return "-";
        }
    }
}

[DefaultExecutionOrder(-500)]
public class PlayerInputRouter : MonoBehaviour
{
    public static PlayerInputRouter Instance { get; private set; }

    [Header("Move")]
    public InputBinding moveForward = new InputBinding(Key.W);
    public InputBinding moveBack = new InputBinding(Key.S);
    public InputBinding moveLeft = new InputBinding(Key.A);
    public InputBinding moveRight = new InputBinding(Key.D);
    public bool arrowKeysAlsoMove = true;

    [Header("Movement Actions")]
    public InputBinding jump = new InputBinding(Key.Space);
    public InputBinding slide = new InputBinding(Key.LeftShift);
    public InputBinding dart = new InputBinding(Key.LeftShift);

    [Header("Grapple")]
    public InputBinding grappleSwing = new InputBinding(Key.E);
    public InputBinding grapplePull = new InputBinding(Key.F);

    [Header("Battle Axe")]
    public InputBinding axeSwing = new InputBinding(InputSourceType.LeftMouse);
    public InputBinding axeThrow = new InputBinding(InputSourceType.RightMouse);
    public InputBinding axePickup = new InputBinding(Key.G);
    public InputBinding axeRecall = new InputBinding(Key.R);

    [Header("Look")]
    public bool invertLookY = false;
    public bool blockLookWhenCursorFree = true;

    [Header("Global")]
    public bool inputEnabled = true;

    public Vector2 Move
    {
        get
        {
            if (!inputEnabled) return Vector2.zero;

            Vector2 m = Vector2.zero;
            if (moveForward.Held) m.y += 1f;
            if (moveBack.Held) m.y -= 1f;
            if (moveRight.Held) m.x += 1f;
            if (moveLeft.Held) m.x -= 1f;

            Keyboard k = Keyboard.current;
            if (arrowKeysAlsoMove && k != null)
            {
                if (k.upArrowKey.isPressed) m.y += 1f;
                if (k.downArrowKey.isPressed) m.y -= 1f;
                if (k.rightArrowKey.isPressed) m.x += 1f;
                if (k.leftArrowKey.isPressed) m.x -= 1f;
            }

            m.x = Mathf.Clamp(m.x, -1f, 1f);
            m.y = Mathf.Clamp(m.y, -1f, 1f);
            return m;
        }
    }

    public Vector2 LookDelta
    {
        get
        {
            if (!inputEnabled || Mouse.current == null) return Vector2.zero;
            if (blockLookWhenCursorFree && Cursor.lockState != CursorLockMode.Locked)
                return Vector2.zero;

            Vector2 d = Mouse.current.delta.ReadValue();
            if (invertLookY) d.y = -d.y;
            return d;
        }
    }

    public float ScrollY
    {
        get
        {
            if (!inputEnabled || Mouse.current == null) return 0f;
            return Mouse.current.scroll.ReadValue().y;
        }
    }

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

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