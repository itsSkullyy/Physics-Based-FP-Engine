using UnityEngine;

// Level-complete popup. LevelGoal calls Show() with the final time and rank; this owns
// freezing the game and offering Try Again / Close Game. IMGUI, matching every other menu
// in this project - no canvas, no prefab, nothing to import.
public class LevelCompleteMenu : MonoBehaviour
{
    public static LevelCompleteMenu Instance { get; private set; }

    [Header("Layout")]
    public int width = 480;
    public int height = 340;

    [Header("Rank Colours")]
    public Color sColor = new Color(1f, 0.85f, 0.2f, 1f);
    public Color aColor = new Color(0.4f, 1f, 0.5f, 1f);
    public Color bColor = new Color(0.4f, 0.75f, 1f, 1f);
    public Color cColor = new Color(1f, 0.85f, 0.3f, 1f);
    public Color dColor = new Color(1f, 0.55f, 0.25f, 1f);
    public Color fColor = new Color(1f, 0.3f, 0.3f, 1f);

    public bool Showing { get; private set; }

    float finalTime;
    char rank;
    bool isNewBest;
    float savedTimeScale;
    PlayerInputRouter router;

    int selected;
    static Texture2D whiteTex;
    static Texture2D blackTex;
    GUIStyle titleStyle;
    GUIStyle timeStyle;
    GUIStyle rankStyle;
    GUIStyle buttonStyle;
    GUIStyle selectedButtonStyle;

    public static LevelCompleteMenu Get()
    {
        if (Instance != null) return Instance;

        LevelCompleteMenu found = FindFirstObjectByType<LevelCompleteMenu>();
        if (found != null) { Instance = found; return Instance; }

        GameObject go = new GameObject("LevelCompleteMenu");
        Instance = go.AddComponent<LevelCompleteMenu>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void Show(float time, char rankLetter, bool newBest)
    {
        if (Showing) return;

        finalTime = time;
        rank = rankLetter;
        isNewBest = newBest;
        Showing = true;
        selected = 0;

        router = PlayerInputRouter.Instance;
        if (router != null) router.inputEnabled = false;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        savedTimeScale = Time.timeScale;
        Time.timeScale = 0f;
    }

    void OnGUI()
    {
        if (!Showing) return;

        EnsureStyles();

        Rect area = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        GUI.color = Color.black;
        GUI.DrawTexture(area, whiteTex);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(area.x + 20f, area.y + 18f, area.width - 40f, area.height - 36f));

        GUILayout.Label("LEVEL COMPLETE", titleStyle, GUILayout.Width(area.width - 40f));

        GUILayout.Space(10f);

        string timeLabel = "Time  " + CourseTimer.Format(finalTime);
        if (isNewBest) timeLabel += "   (New Best!)";
        GUILayout.Label(timeLabel, timeStyle, GUILayout.Width(area.width - 40f));

        GUILayout.Space(4f);

        Color old = GUI.color;
        GUI.color = RankColor(rank);
        GUILayout.Label(rank.ToString(), rankStyle, GUILayout.Width(area.width - 40f));
        GUI.color = old;

        GUILayout.Space(16f);

        GamepadMenu.Poll(ref selected, 2);
        bool confirm = GamepadMenu.Confirm();

        GUILayout.BeginHorizontal();
        if (GamepadMenu.Button("Try Again", 0, selected, confirm, buttonStyle, selectedButtonStyle, GUILayout.Height(34f)))
            Restart();
        if (GamepadMenu.Button("Close Game", 1, selected, confirm, buttonStyle, selectedButtonStyle, GUILayout.Height(34f)))
            QuitGame();
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }

    // Same flat black-panel / white-text look as PauseMenu, plus a colour-flipped button
    // twin so the gamepad's current selection has something to show for itself without a
    // mouse hover to lean on. The rank letter is the one deliberate spot of colour - that
    // is the actual information this screen exists to show.
    void EnsureStyles()
    {
        if (buttonStyle != null) return;

        whiteTex = MakeTex(Color.white);
        blackTex = MakeTex(Color.black);

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 26,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        titleStyle.normal.textColor = Color.white;

        timeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            alignment = TextAnchor.UpperCenter
        };
        timeStyle.normal.textColor = Color.white;

        rankStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 60,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };

        buttonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
        buttonStyle.normal.background = whiteTex;
        buttonStyle.hover.background = whiteTex;
        buttonStyle.active.background = blackTex;
        buttonStyle.normal.textColor = Color.black;
        buttonStyle.hover.textColor = Color.black;
        buttonStyle.active.textColor = Color.white;

        selectedButtonStyle = new GUIStyle(buttonStyle);
        selectedButtonStyle.normal.background = blackTex;
        selectedButtonStyle.normal.textColor = Color.white;
        selectedButtonStyle.hover.background = blackTex;
        selectedButtonStyle.hover.textColor = Color.white;
    }

    static Texture2D MakeTex(Color c)
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, c);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    Color RankColor(char r)
    {
        switch (r)
        {
            case 'S': return sColor;
            case 'A': return aColor;
            case 'B': return bColor;
            case 'C': return cColor;
            case 'D': return dColor;
            default: return fColor;
        }
    }

    void Restart()
    {
        Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
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
}
