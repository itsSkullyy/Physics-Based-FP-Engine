using UnityEngine;

// Always-on course clock, pinned to the top of the screen. Mirrors CourseTimer.Elapsed.
public class CourseHUD : MonoBehaviour
{
    [Header("Clock")]
    public float clockScreenY = 0.02f;
    public int clockFontSize = 32;
    public Color clockColor = new Color(1f, 1f, 1f, 0.92f);

    [Header("Shadow")]
    public Color shadowColor = new Color(0f, 0f, 0f, 0.6f);
    public float shadowOffset = 2f;

    CourseTimer timer;
    GUIStyle clockStyle;

    void Start()
    {
        timer = CourseTimer.Get();
    }

    void OnGUI()
    {
        if (timer == null) return;
        EnsureStyles();

        DrawShadowed(CourseTimer.Format(timer.Elapsed), Screen.width * 0.5f,
            Screen.height * clockScreenY, clockStyle, clockColor);
    }

    void DrawShadowed(string text, float x, float y, GUIStyle style, Color color)
    {
        Vector2 size = style.CalcSize(new GUIContent(text));
        Rect rect = new Rect(x - size.x * 0.5f, y, size.x, size.y * 1.2f);

        Color old = GUI.color;

        GUI.color = shadowColor;
        GUI.Label(new Rect(rect.x + shadowOffset, rect.y + shadowOffset, rect.width, rect.height), text, style);

        GUI.color = color;
        GUI.Label(rect, text, style);

        GUI.color = old;
    }

    void EnsureStyles()
    {
        if (clockStyle != null) return;

        clockStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = clockFontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
    }
}
