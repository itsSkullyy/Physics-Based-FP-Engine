using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Drop-in controls menu. Put it on the Player root next to PlayerInputRouter.
//
// IMGUI on purpose: it matches the pickup prompt and grapple reticle already in this
// project, needs no canvas, no prefab and no TextMeshPro, and works the moment the
// component is added. When you build a real menu, keep this as the reference for the
// call order - it is only ~15 lines of actual rebinding logic.
[DefaultExecutionOrder(-400)]
public class RebindMenu : MonoBehaviour
{
    [Header("Refs")]
    public PlayerInputRouter router;

    [Header("Open / Close")]
    public Key toggleKey = Key.F1;
    public bool gamepadStartAlsoToggles = true;

    [Header("Layout")]
    public int width = 620;
    public int height = 520;

    class Row
    {
        public GameAction action;
        public int index;
        public string label;
        public bool gamepad;
    }

    readonly List<Row> rows = new List<Row>();

    bool open;
    Row listening;
    string status = "";
    Vector2 scroll;
    CursorLockMode restoreLock;
    bool restoreVisible;
    bool restoreInput;
    InputActionRebindingExtensions.RebindingOperation activeOp;

    void Awake()
    {
        if (router == null) router = PlayerInputRouter.Resolve(this);
    }

    void Start()
    {
        BuildRows();
    }

    void BuildRows()
    {
        rows.Clear();
        if (router == null) return;

        foreach (PlayerInputRouter.Entry entry in router.Registry)
        {
            InputAction action = entry.action?.Action;
            if (action == null) continue;

            for (int i = 0; i < action.bindings.Count; i++)
            {
                UnityEngine.InputSystem.InputBinding b = action.bindings[i];
                if (b.isComposite) continue;   // the parts underneath are the bindable bits

                string label = entry.label;
                if (b.isPartOfComposite && !string.IsNullOrEmpty(b.name))
                    label += "  ›  " + char.ToUpper(b.name[0]) + b.name.Substring(1);

                rows.Add(new Row
                {
                    action = entry.action,
                    index = i,
                    label = label,
                    gamepad = GameAction.IsGamepadPath(b.effectivePath)
                });
            }
        }
    }

    void Update()
    {
        if (listening != null) return;   // swallow the toggle while waiting for a control

        bool pressed =
            (Keyboard.current != null && toggleKey != Key.None && Keyboard.current[toggleKey].wasPressedThisFrame) ||
            (gamepadStartAlsoToggles && Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        if (pressed) Toggle();
    }

    public void Toggle()
    {
        if (open) Close();
        else Open();
    }

    public void Open()
    {
        if (open || router == null) return;

        open = true;
        status = "";

        restoreLock = Cursor.lockState;
        restoreVisible = Cursor.visible;
        restoreInput = router.inputEnabled;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        router.inputEnabled = false;
    }

    public void Close()
    {
        if (!open) return;

        CancelListening();
        open = false;

        Cursor.lockState = restoreLock;
        Cursor.visible = restoreVisible;
        router.inputEnabled = restoreInput;
    }

    void OnDisable() => CancelListening();

    void CancelListening()
    {
        activeOp?.Cancel();
        activeOp = null;
        listening = null;
    }

    // ---------------------------------------------------------------- rebinding

    void StartRebind(Row row)
    {
        listening = row;
        status = "Press a " + (row.gamepad ? "controller" : "keyboard or mouse") +
                 " control...   (Esc cancels)";

        activeOp = InputRebinding.Begin(row.action, row.index, row.gamepad, result =>
        {
            activeOp = null;
            listening = null;

            if (!result.success)
            {
                status = "Cancelled.";
                return;
            }

            status = row.label + " is now " + result.display;

            if (result.conflicts.Count > 0)
            {
                status += "   (also used by ";
                for (int i = 0; i < result.conflicts.Count; i++)
                    status += (i > 0 ? ", " : "") + result.conflicts[i].label;
                status += ")";
            }

            router.SaveBindings();
        }, router);
    }

    // ---------------------------------------------------------------- drawing

    void OnGUI()
    {
        if (!open || router == null) return;

        Rect area = new Rect(
            (Screen.width - width) * 0.5f,
            (Screen.height - height) * 0.5f,
            width, height);

        GUI.Box(area, GUIContent.none);
        GUILayout.BeginArea(new Rect(area.x + 14f, area.y + 12f, area.width - 28f, area.height - 24f));

        GUILayout.Label("Controls", new GUIStyle(GUI.skin.label) { fontSize = 20 });
        GUILayout.Label(status.Length > 0 ? status : "Click a binding to change it.");
        GUILayout.Space(6f);

        scroll = GUILayout.BeginScrollView(scroll);

        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];

            GUILayout.BeginHorizontal();
            GUILayout.Label(row.label, GUILayout.Width(220f));
            GUILayout.Label(row.gamepad ? "Pad" : "K&M", GUILayout.Width(42f));

            bool waiting = listening == row;
            string face = waiting ? "< press a control >" : row.action.DisplayAt(row.index);

            GUI.enabled = listening == null;
            if (GUILayout.Button(face, GUILayout.Width(190f)) && listening == null)
                StartRebind(row);

            if (GUILayout.Button("Reset", GUILayout.Width(60f)) && listening == null)
            {
                InputRebinding.ResetBinding(row.action, row.index);
                router.SaveBindings();
                status = row.label + " reset.";
            }
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();
        GUILayout.Space(6f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset All", GUILayout.Height(26f)))
        {
            router.ResetAllBindings();
            status = "All bindings back to defaults.";
        }
        if (GUILayout.Button("Save", GUILayout.Height(26f)))
        {
            router.SaveBindings();
            status = "Saved.";
        }
        if (GUILayout.Button("Close", GUILayout.Height(26f)))
        {
            Close();
        }
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
}
