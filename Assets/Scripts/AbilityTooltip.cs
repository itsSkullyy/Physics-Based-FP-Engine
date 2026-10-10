using UnityEngine;

// Controls card that slides in on the left when the player picks up an ability. Stays up
// for a few seconds while they keep playing (nothing pauses), then slides away. A new card
// replaces the old one. IMGUI, matching the other menus; creates itself when first needed.
public class AbilityTooltip : MonoBehaviour
{
    public static AbilityTooltip Instance { get; private set; }

    [Header("Card")]
    public int width = 400;
    [Tooltip("Top of the card, as a fraction of screen height.")]
    public float screenY = 0.3f;
    public float margin = 24f;
    public Color panelColor = new Color(0f, 0f, 0f, 0.82f);
    public float slideTime = 0.4f;

    string title;
    string[] lines;
    Color accent = Color.white;
    float shownAt = -99f;
    float duration;
    bool showing;

    static Texture2D whiteTex;
    GUIStyle titleStyle;
    GUIStyle subStyle;
    GUIStyle lineStyle;

    public static AbilityTooltip Get()
    {
        if (Instance != null)
        {
            return Instance;
        }

        AbilityTooltip found = FindFirstObjectByType<AbilityTooltip>();
        if (found != null) { Instance = found; return Instance; }

        GameObject go = new GameObject("AbilityTooltip");
        Instance = go.AddComponent<AbilityTooltip>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void Show(string cardTitle, string[] cardLines, Color accentColor, float seconds)
    {
        title = cardTitle;
        lines = cardLines;
        accent = accentColor;
        duration = seconds;
        shownAt = Time.unscaledTime;
        showing = true;
    }

    void OnGUI()
    {
        if (!showing || lines == null)
        {
            return;
        }

        float age = Time.unscaledTime - shownAt;
        if (age > duration + slideTime)
        {
            showing = false;
            return;
        }

        EnsureStyles();

        float slideIn = EaseOutBack(Mathf.Clamp01(age / slideTime));
        float slideOut = age > duration ? EaseInCubic(Mathf.Clamp01((age - duration) / slideTime)) : 0f;
        float offscreen = width + margin;
        float x = margin - (1f - slideIn) * offscreen - slideOut * offscreen;

        float pad = 18f;
        float innerW = width - pad * 2f - 6f;
        float height = 14f + 30f + 22f + 8f;
        foreach (string line in lines)
        {
            height += lineStyle.CalcHeight(new GUIContent(line), innerW) + 6f;
        }
        height += 10f;

        Rect panel = new Rect(x, Screen.height * screenY, width, height);

        Color old = GUI.color;
        GUI.color = panelColor;
        GUI.DrawTexture(panel, whiteTex);
        GUI.color = accent;
        GUI.DrawTexture(new Rect(panel.x, panel.y, 5f, panel.height), whiteTex);
        GUI.color = old;

        float innerX = panel.x + pad + 6f;
        float y = panel.y + 14f;

        GUI.color = accent;
        GUI.Label(new Rect(innerX, y, innerW, 30f), title, titleStyle);
        y += 30f;
        GUI.color = new Color(1f, 1f, 1f, 0.6f);
        GUI.Label(new Rect(innerX, y, innerW, 20f), "NEW ABILITY", subStyle);
        y += 30f;
        GUI.color = old;

        foreach (string line in lines)
        {
            float h = lineStyle.CalcHeight(new GUIContent(line), innerW);
            GUI.Label(new Rect(innerX, y, innerW, h), line, lineStyle);
            y += h + 6f;
        }

        // time left, as a thin bar along the bottom
        float left = 1f - Mathf.Clamp01(age / duration);
        GUI.color = new Color(accent.r, accent.g, accent.b, 0.7f);
        GUI.DrawTexture(new Rect(panel.x + 5f, panel.yMax - 3f, (panel.width - 5f) * left, 3f), whiteTex);
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

        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = Color.white;

        subStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        subStyle.normal.textColor = Color.white;

        lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = true };
        lineStyle.normal.textColor = Color.white;
    }
}
