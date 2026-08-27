using UnityEngine;
using UnityEngine.InputSystem;

// Escape (or gamepad Start) pause menu. Freeze input, free the cursor, restore both
// on close, and additionally freeze time itself via Time.timeScale - the same trick
// JuiceFX's hitstop uses - so physics and the course clock both actually stop while
// paused instead of just hiding behind a menu.
//
// The open/close toggle reads Keyboard/Gamepad directly rather than going through
// PlayerInputRouter, same as RebindMenu does - router.inputEnabled gets set false the
// moment the menu opens, which would disable a routed action too and make it impossible
// to ever press the button that closes the menu again.
[DefaultExecutionOrder(-400)]
public class PauseMenu : MonoBehaviour
{
    public static PauseMenu Instance { get; private set; }

    [Header("Refs")]
    public PlayerInputRouter router;

    [Header("Toggle")]
    public Key toggleKey = Key.Escape;

    [Header("Layout")]
    public int width = 340;
    public int height = 260;

    static Texture2D whiteTex;
    static Texture2D blackTex;
    GUIStyle titleStyle;
    GUIStyle buttonStyle;
    GUIStyle selectedButtonStyle;

    int selected;
    bool open;

    CursorLockMode restoreLock;
    bool restoreVisible;
    bool restoreInput;
    float savedTimeScale;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (router == null) router = PlayerInputRouter.Resolve(this);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (router == null) return;

        bool pressed =
            (Keyboard.current != null && toggleKey != Key.None && Keyboard.current[toggleKey].wasPressedThisFrame) ||
            (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        if (open)
        {
            if (pressed) Resume();
            return;
        }

        // Don't fight the level-complete menu for the cursor.
        if (LevelCompleteMenu.Instance != null && LevelCompleteMenu.Instance.Showing) return;

        if (pressed) Open();
    }

    public void Open()
    {
        if (open || router == null) return;
        open = true;
        selected = 0;

        restoreLock = Cursor.lockState;
        restoreVisible = Cursor.visible;
        restoreInput = router.inputEnabled;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        router.inputEnabled = false;

        savedTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        CourseTimer.Get().Paused = true;
    }

    public void Resume()
    {
        if (!open) return;
        open = false;

        Cursor.lockState = restoreLock;
        Cursor.visible = restoreVisible;
        router.inputEnabled = restoreInput;

        Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;

        CourseTimer.Get().Paused = false;
    }

    void Restart()
    {
        Time.timeScale = 1f;
        UnityEngine.SceneManagement.SceneManager.LoadScene(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnGUI()
    {
        if (!open) return;

        EnsureStyles();

        Rect area = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        GUI.color = Color.black;
        GUI.DrawTexture(area, whiteTex);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(area.x + 20f, area.y + 18f, area.width - 40f, area.height - 36f));

        GUILayout.Label("PAUSED", titleStyle);
        GUILayout.Space(20f);

        GamepadMenu.Poll(ref selected, 3);
        bool confirm = GamepadMenu.Confirm();

        if (GamepadMenu.Button("Resume", 0, selected, confirm, buttonStyle, selectedButtonStyle, GUILayout.Height(36f)))
            Resume();
        GUILayout.Space(6f);
        if (GamepadMenu.Button("Restart", 1, selected, confirm, buttonStyle, selectedButtonStyle, GUILayout.Height(36f)))
            Restart();
        GUILayout.Space(6f);
        if (GamepadMenu.Button("Close Game", 2, selected, confirm, buttonStyle, selectedButtonStyle, GUILayout.Height(36f)))
            QuitGame();

        GUILayout.EndArea();
    }

    // Solid black panel, white title, plain white buttons with black text - no gradients,
    // no transparency, deliberately as plain as a first pass gets.
    void EnsureStyles()
    {
        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }

        if (blackTex == null)
        {
            blackTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            blackTex.SetPixel(0, 0, Color.black);
            blackTex.Apply();
            blackTex.hideFlags = HideFlags.HideAndDontSave;
        }

        if (titleStyle != null) return;

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        titleStyle.normal.textColor = Color.white;

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold
        };
        buttonStyle.normal.background = whiteTex;
        buttonStyle.hover.background = whiteTex;
        buttonStyle.active.background = blackTex;
        buttonStyle.normal.textColor = Color.black;
        buttonStyle.hover.textColor = Color.black;
        buttonStyle.active.textColor = Color.white;

        // Same button, colours flipped - this is what marks the gamepad's current
        // selection, since there is no mouse hover to lean on for that.
        selectedButtonStyle = new GUIStyle(buttonStyle);
        selectedButtonStyle.normal.background = blackTex;
        selectedButtonStyle.normal.textColor = Color.white;
        selectedButtonStyle.hover.background = blackTex;
        selectedButtonStyle.hover.textColor = Color.white;
    }
}
