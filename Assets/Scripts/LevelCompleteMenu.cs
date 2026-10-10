using UnityEngine;

// End-of-level results. LevelGoal calls Show() with the final time and rank. Nothing pauses:
// a panel slides in on the right while you keep moving, the rank stamps down a moment
// later, and a marker points at the exit portal until you go through it. Restarting is in
// the pause menu. IMGUI, matching the other menus.
public class LevelCompleteMenu : MonoBehaviour
{
    public static LevelCompleteMenu Instance { get; private set; }

    [Header("Panel")]
    public int width = 300;
    public int height = 250;
    [Tooltip("Top of the panel, as a fraction of screen height.")]
    public float screenY = 0.2f;
    public float margin = 24f;
    public Color panelColor = new Color(0f, 0f, 0f, 0.82f);
    public float slideTime = 0.45f;
    [Tooltip("Gap between the panel landing and the rank stamping down.")]
    public float rankDelay = 0.35f;
    public float rankStampTime = 0.28f;
    [Tooltip("Seconds the panel stays up after you go through the exit portal.")]
    public float lingerAfterExit = 5f;

    [Header("Exit Marker")]
    public bool showExitMarker = true;
    public Color markerColor = new Color(0.45f, 0.85f, 1f, 1f);
    public float markerEdgeMargin = 40f;

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
    float bestTime;
    Portal exit;

    float shownAt;
    bool stamped;
    bool wentThrough;
    float hideAt = -1f;
    bool hiding;
    float hideStart;

    static Texture2D whiteTex;
    GUIStyle titleStyle;
    GUIStyle timeStyle;
    GUIStyle smallStyle;
    GUIStyle rankStyle;
    GUIStyle markerStyle;

    public static LevelCompleteMenu Get()
    {
        if (Instance != null)
        {
            return Instance;
        }

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
        Portal.PlayerTravelled -= OnPlayerTravelled;
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void Show(float time, char rankLetter, bool newBest, Portal exitPortal)
    {
        finalTime = time;
        rank = rankLetter;
        isNewBest = newBest;
        bestTime = CourseTimer.Get().BestTime;
        exit = exitPortal;

        Showing = true;
        shownAt = Time.unscaledTime;
        stamped = false;
        wentThrough = false;
        hiding = false;
        hideAt = -1f;

        Portal.PlayerTravelled -= OnPlayerTravelled;
        Portal.PlayerTravelled += OnPlayerTravelled;
    }

    void OnPlayerTravelled(Portal entered, Portal exited)
    {
        if (exit == null || wentThrough)
        {
            return;
        }
        if (entered == exit || entered == exit.LinkedPortal)
        {
            wentThrough = true;
            hideAt = Time.unscaledTime + lingerAfterExit;
        }
    }

    void Update()
    {
        if (!Showing)
        {
            return;
        }

        float now = Time.unscaledTime;

        if (!stamped && now - shownAt >= slideTime + rankDelay + rankStampTime)
        {
            stamped = true;
            if (CameraShaker.Instance != null)
            {
                CameraShaker.Instance.AddTrauma(rank == 'S' ? 0.35f : 0.2f);
            }
        }

        if (!hiding && hideAt > 0f && now >= hideAt)
        {
            hiding = true;
            hideStart = now;
        }
        if (hiding && now - hideStart >= slideTime)
        {
            Showing = false;
            Portal.PlayerTravelled -= OnPlayerTravelled;
        }
    }

    // ---------------------------------------------------------------- drawing

    void OnGUI()
    {
        if (!Showing)
        {
            return;
        }
        EnsureStyles();

        float now = Time.unscaledTime;
        float slideIn = EaseOutBack(Mathf.Clamp01((now - shownAt) / slideTime));
        float slideOut = hiding ? EaseInCubic(Mathf.Clamp01((now - hideStart) / slideTime)) : 0f;
        float offscreen = width + margin;
        float x = Screen.width - width - margin + (1f - slideIn) * offscreen + slideOut * offscreen;

        Rect panel = new Rect(x, Screen.height * screenY, width, height);
        DrawPanel(panel, now);

        if (showExitMarker && !wentThrough && exit != null && exit.IsVisible)
        {
            DrawExitMarker();
        }
    }

    void DrawPanel(Rect panel, float now)
    {
        Color old = GUI.color;
        GUI.color = panelColor;
        GUI.DrawTexture(panel, whiteTex);

        // thin rank-coloured strip down the left edge
        GUI.color = RankColor(rank);
        GUI.DrawTexture(new Rect(panel.x, panel.y, 5f, panel.height), whiteTex);
        GUI.color = old;

        float pad = 18f;
        float innerX = panel.x + pad;
        float innerW = panel.width - pad * 2f;
        float y = panel.y + 14f;

        GUI.Label(new Rect(innerX, y, innerW, 30f), "LEVEL COMPLETE", titleStyle);
        y += 34f;

        GUI.Label(new Rect(innerX, y, innerW, 26f), "Time  " + CourseTimer.Format(finalTime), timeStyle);
        y += 26f;

        string bestLine;
        if (isNewBest)
        {
            // pulses so it reads as new without needing a popup
            float pulse = 0.65f + 0.35f * Mathf.Sin(now * 6f);
            GUI.color = new Color(sColor.r, sColor.g, sColor.b, pulse);
            bestLine = "New Best!";
        }
        else
        {
            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            bestLine = bestTime >= 0f ? "Best  " + CourseTimer.Format(bestTime) : "";
        }
        GUI.Label(new Rect(innerX, y, innerW, 20f), bestLine, smallStyle);
        GUI.color = old;
        y += 22f;

        DrawRank(new Rect(innerX, y, innerW, 90f), now);
        y += 96f;

        string hint = ExitHint();
        if (!string.IsNullOrEmpty(hint))
        {
            GUI.color = markerColor;
            GUI.Label(new Rect(innerX, y, innerW, 20f), hint, smallStyle);
            GUI.color = old;
        }
    }

    // the letter drops in big and lands, after the panel has settled
    void DrawRank(Rect area, float now)
    {
        float t = (now - shownAt - slideTime - rankDelay) / rankStampTime;
        if (t <= 0f)
        {
            return;
        }
        t = Mathf.Clamp01(t);

        float scale = Mathf.Lerp(2.8f, 1f, EaseInCubic(t));
        Color c = RankColor(rank);
        c.a = t;

        Matrix4x4 oldMatrix = GUI.matrix;
        Color old = GUI.color;
        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), area.center);
        GUI.color = c;
        GUI.Label(area, rank.ToString(), rankStyle);
        GUI.color = old;
        GUI.matrix = oldMatrix;
    }

    string ExitHint()
    {
        if (exit == null)
        {
            return "Esc > Restart to try again";
        }
        if (wentThrough)
        {
            return "";
        }
        if (!exit.IsOpen)
        {
            return "Exit portal opening...";
        }
        Camera cam = Camera.main;
        int metres = cam != null ? Mathf.RoundToInt(Vector3.Distance(cam.transform.position, exit.transform.position)) : 0;
        return "Exit portal open  " + metres + " m";
    }

    // Diamond over the exit portal, pinned to the screen edge when it's off screen or behind you.
    void DrawExitMarker()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        Vector3 sp = cam.WorldToScreenPoint(exit.transform.position);
        bool behind = sp.z < 0f;
        if (behind)
        {
            sp.x = Screen.width - sp.x;
            sp.y = Screen.height - sp.y;
        }

        Vector2 pos = new Vector2(sp.x, Screen.height - sp.y);
        float m = markerEdgeMargin;
        bool offScreen = behind || pos.x < m || pos.x > Screen.width - m || pos.y < m || pos.y > Screen.height - m;
        if (behind)
        {
            // straight behind would land mid-screen; push it out to whichever edge it leans to
            Vector2 centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 dir = pos - centre;
            if (dir.sqrMagnitude < 1f)
            {
                dir = Vector2.down;
            }
            pos = centre + dir.normalized * Mathf.Max(Screen.width, Screen.height);
        }
        pos.x = Mathf.Clamp(pos.x, m, Screen.width - m);
        pos.y = Mathf.Clamp(pos.y, m, Screen.height - m);

        float size = offScreen ? 12f : 16f;
        float bob = offScreen ? 0f : Mathf.Sin(Time.unscaledTime * 4f) * 3f;
        Rect diamond = new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f + bob, size, size);

        Matrix4x4 oldMatrix = GUI.matrix;
        Color old = GUI.color;
        GUIUtility.RotateAroundPivot(45f, diamond.center);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(diamond.x - 2f, diamond.y - 2f, diamond.width + 4f, diamond.height + 4f), whiteTex);
        GUI.color = markerColor;
        GUI.DrawTexture(diamond, whiteTex);
        GUI.matrix = oldMatrix;

        int metres = Mathf.RoundToInt(Vector3.Distance(cam.transform.position, exit.transform.position));
        string label = "EXIT  " + metres + " m";
        Vector2 labelSize = markerStyle.CalcSize(new GUIContent(label));
        float labelY = pos.y + size + 2f + bob;
        if (labelY + labelSize.y > Screen.height - 4f)
        {
            labelY = pos.y - size - labelSize.y - 2f;
        }
        float labelX = Mathf.Clamp(pos.x - labelSize.x * 0.5f, 4f, Screen.width - labelSize.x - 4f);
        Rect labelRect = new Rect(labelX, labelY, labelSize.x, labelSize.y);

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Label(new Rect(labelRect.x + 1f, labelRect.y + 1f, labelRect.width, labelRect.height), label, markerStyle);
        GUI.color = markerColor;
        GUI.Label(labelRect, label, markerStyle);
        GUI.color = old;
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.3f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    static float EaseInCubic(float t) => t * t * t;

    void EnsureStyles()
    {
        if (titleStyle != null)
        {
            return;
        }

        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        titleStyle.normal.textColor = Color.white;

        timeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19,
            alignment = TextAnchor.UpperLeft
        };
        timeStyle.normal.textColor = Color.white;

        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        smallStyle.normal.textColor = Color.white;

        rankStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 72,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        rankStyle.normal.textColor = Color.white;

        markerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        markerStyle.normal.textColor = Color.white;
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
}
