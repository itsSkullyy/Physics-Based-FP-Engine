using UnityEngine;

// Player HUD. Put this on the Player root - it finds everything itself.
//
//   bottom left   health bar, then the speed bar under it - a coloured square that
//                 fills and changes colour, no text or frame.
//   bottom right  the three weapon slots as plain numbered icons.
//
// IMGUI, matching the rest of the project's UI. Assign axeIcon / grappleIcon for your
// own sprites; left empty, the HUD draws generated ones.
[DefaultExecutionOrder(200)]
public class PlayerHUD : MonoBehaviour
{
    [Header("Refs")]
    public FirstPersonCharacterController controller;
    public PlayerHealth health;
    public WeaponSlots slots;
    public BattleAxe axe;

    [Header("Layout")]
    public float margin = 26f;
    [Tooltip("Hides the whole HUD while the cursor is free, e.g. with the rebind menu open.")]
    public bool hideWhenCursorFree = false;

    [Header("Bars")]
    public bool showHealth = true;
    public bool showSpeed = true;
    public float barWidth = 300f;
    public float barHeight = 22f;
    public float barGap = 8f;
    [Tooltip("Faint track behind the fill so an empty bar is still readable. Alpha 0 = gone.")]
    public Color barTrack = new Color(0f, 0f, 0f, 0.45f);
    public float barSmoothing = 12f;

    [Header("Health Colours")]
    public Color healthFull = new Color(0.35f, 0.95f, 0.55f, 1f);
    public Color healthMid = new Color(1f, 0.82f, 0.25f, 1f);
    public Color healthLow = new Color(1f, 0.28f, 0.25f, 1f);
    public Color damageFlash = new Color(1f, 1f, 1f, 1f);
    public float damageFlashTime = 0.18f;

    [Header("Speed Colours")]
    [Tooltip("Speed that counts as a full bar. 0 = worked out from the controller.")]
    public float speedReference = 0f;
    public Color speedIdle = new Color(0.52f, 0.60f, 0.72f, 1f);
    public Color speedCruise = new Color(0.30f, 0.85f, 1f, 1f);
    public Color speedFast = new Color(1f, 0.85f, 0.30f, 1f);
    public Color speedPeak = new Color(1f, 0.34f, 0.18f, 1f);
    [Tooltip("The bar pulses once you are at or past the reference speed.")]
    public float peakPulseSpeed = 7f;

    [Header("Slots")]
    public bool showSlots = true;
    public bool showInactiveSlots = true;
    public Texture2D axeIcon;
    public Texture2D grappleIcon;
    public float slotSize = 84f;
    public float slotGap = 4f;
    [Tooltip("Extra pixels the selected slot grows by.")]
    public float activeGrow = 10f;
    public bool showSlotNumbers = true;
    [Tooltip("Dark copy drawn behind the icon. With no box to sit in, this is what keeps " +
             "it readable against a bright wall.")]
    public bool iconShadow = true;
    public float iconShadowOffset = 2.5f;
    [Tooltip("Thin bar under the axe slot showing the recall cooldown while it is out.")]
    public bool showRecallProgress = true;

    [Header("Style")]
    public Color inactiveTint = new Color(1f, 1f, 1f, 0.3f);
    public Color textColor = new Color(1f, 1f, 1f, 0.92f);
    public Color shadowColor = new Color(0f, 0f, 0f, 0.55f);
    public Color readyColor = new Color(0.4f, 1f, 0.6f, 1f);

    float healthT;
    float speedT;
    float flashTimer;

    static Texture2D whiteTex;
    Texture2D genAxeIcon;
    Texture2D genGrappleIcon;
    Texture2D genEmptyIcon;

    GUIStyle numberStyle;

    void Awake()
    {
        if (controller == null) controller = GetComponent<FirstPersonCharacterController>();
        if (controller == null) controller = GetComponentInChildren<FirstPersonCharacterController>();
        if (health == null) health = GetComponent<PlayerHealth>();
        if (health == null) health = GetComponentInChildren<PlayerHealth>();
        if (slots == null) slots = GetComponent<WeaponSlots>();
        if (slots == null) slots = GetComponentInChildren<WeaponSlots>();
        if (axe == null && slots != null) axe = slots.axe;
        if (axe == null) axe = GetComponentInChildren<BattleAxe>(true);

        healthT = health != null ? health.Normalized : 1f;
    }

    void OnEnable()
    {
        if (health != null) health.Damaged += OnDamaged;
    }

    void OnDisable()
    {
        if (health != null) health.Damaged -= OnDamaged;
    }

    void OnDestroy()
    {
        if (genAxeIcon != null) Destroy(genAxeIcon);
        if (genGrappleIcon != null) Destroy(genGrappleIcon);
        if (genEmptyIcon != null) Destroy(genEmptyIcon);
    }

    void OnDamaged(float amount) => flashTimer = damageFlashTime;

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        float k = 1f - Mathf.Exp(-barSmoothing * dt);

        float targetHealth = health != null ? health.Normalized : 1f;
        healthT = Mathf.Lerp(healthT, targetHealth, k);

        float targetSpeed = 0f;
        if (controller != null)
            targetSpeed = Mathf.Clamp01(controller.CurrentSpeed / Mathf.Max(1f, SpeedReference));
        speedT = Mathf.Lerp(speedT, targetSpeed, k);

        if (flashTimer > 0f) flashTimer -= dt;
    }

    float SpeedReference
    {
        get
        {
            if (speedReference > 0.01f) return speedReference;
            if (controller == null) return 30f;
            return Mathf.Max(controller.maxSpeed, controller.dartMaxSpeed);
        }
    }

    // ---------------------------------------------------------------- drawing

    void OnGUI()
    {
        if (hideWhenCursorFree && Cursor.lockState != CursorLockMode.Locked) return;

        EnsureTextures();
        EnsureStyles();

        Color old = GUI.color;

        DrawBars();
        if (showSlots && slots != null) DrawSlots();

        GUI.color = old;
    }

    void DrawBars()
    {
        int rows = (showHealth && health != null ? 1 : 0) + (showSpeed && controller != null ? 1 : 0);
        if (rows == 0) return;

        float stack = rows * barHeight + (rows - 1) * barGap;
        float y = Screen.height - margin - stack;

        if (showHealth && health != null)
        {
            Color c = HealthColor(healthT);
            if (flashTimer > 0f)
                c = Color.Lerp(c, damageFlash, flashTimer / Mathf.Max(0.01f, damageFlashTime));

            DrawBar(new Rect(margin, y, barWidth, barHeight), healthT, c);
            y += barHeight + barGap;
        }

        if (showSpeed && controller != null)
        {
            Color c = SpeedColor(speedT);
            if (speedT >= 0.995f)
                c = Color.Lerp(c, Color.white,
                    (Mathf.Sin(Time.unscaledTime * peakPulseSpeed) * 0.5f + 0.5f) * 0.35f);

            DrawBar(new Rect(margin, y, barWidth, barHeight), speedT, c);
        }
    }

    void DrawBar(Rect r, float t, Color fill)
    {
        if (barTrack.a > 0.001f)
        {
            GUI.color = barTrack;
            GUI.DrawTexture(r, whiteTex);
        }

        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(t), r.height), whiteTex);
    }

    void DrawSlots()
    {
        int count = WeaponSlots.SlotCount;
        float stackHeight = count * slotSize + (count - 1) * slotGap;

        float right = Screen.width - margin;
        float top = Screen.height - margin - stackHeight;

        for (int i = 0; i < count; i++)
        {
            bool active = slots.Current == i;
            if (!active && !showInactiveSlots) continue;

            Rect r = new Rect(right - slotSize, top + i * (slotSize + slotGap), slotSize, slotSize);
            if (active)
                r = new Rect(r.x - activeGrow, r.y - activeGrow * 0.5f,
                             r.width + activeGrow, r.height + activeGrow);

            DrawSlotIcon(r, i, active);

            if (!showSlotNumbers) continue;

            Rect numRect = new Rect(r.x + 2f, r.y, 22f, 18f);
            if (iconShadow)
            {
                GUI.color = shadowColor;
                GUI.Label(new Rect(numRect.x + 1.5f, numRect.y + 1.5f, numRect.width, numRect.height),
                    (i + 1).ToString(), numberStyle);
            }

            GUI.color = active ? textColor : inactiveTint;
            GUI.Label(numRect, (i + 1).ToString(), numberStyle);
        }
    }

    void DrawSlotIcon(Rect chip, int index, bool active)
    {
        Texture2D icon = IconFor(index);
        if (icon == null) return;

        float pad = chip.width * 0.06f;
        Rect iconRect = new Rect(chip.x + pad, chip.y + pad,
                                 chip.width - pad * 2f, chip.height - pad * 2f);

        bool thrown = index == (int)WeaponSlots.Slot.Axe && axe != null && axe.IsThrown;

        Color tint = active ? Color.white : inactiveTint;
        if (thrown) tint.a *= 0.35f;

        if (iconShadow)
        {
            Color s = shadowColor;
            s.a *= tint.a;
            GUI.color = s;
            GUI.DrawTexture(new Rect(iconRect.x + iconShadowOffset, iconRect.y + iconShadowOffset,
                iconRect.width, iconRect.height), icon, ScaleMode.ScaleToFit, true);
        }

        GUI.color = tint;
        GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);

        if (!thrown || !showRecallProgress) return;

        ThrownAxe live = axe.ActiveAxe;
        if (live == null) return;

        Rect bar = new Rect(chip.x + chip.width * 0.15f, chip.yMax - 5f, chip.width * 0.7f, 3f);

        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(bar, whiteTex);

        float p = live.RecallReady ? 1f : live.RecallCooldownProgress;
        GUI.color = live.RecallReady ? readyColor : new Color(0.5f, 0.85f, 1f, 0.95f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(p), bar.height), whiteTex);
    }

    Texture2D IconFor(int index)
    {
        switch (index)
        {
            case (int)WeaponSlots.Slot.Axe: return axeIcon != null ? axeIcon : genAxeIcon;
            case (int)WeaponSlots.Slot.Grapple: return grappleIcon != null ? grappleIcon : genGrappleIcon;
            default: return genEmptyIcon;
        }
    }

    // ---------------------------------------------------------------- colours

    Color HealthColor(float t)
    {
        return t > 0.5f
            ? Color.Lerp(healthMid, healthFull, (t - 0.5f) * 2f)
            : Color.Lerp(healthLow, healthMid, t * 2f);
    }

    Color SpeedColor(float t)
    {
        if (t < 0.34f) return Color.Lerp(speedIdle, speedCruise, t / 0.34f);
        if (t < 0.67f) return Color.Lerp(speedCruise, speedFast, (t - 0.34f) / 0.33f);
        return Color.Lerp(speedFast, speedPeak, (t - 0.67f) / 0.33f);
    }

    // ---------------------------------------------------------------- primitives

    void EnsureStyles()
    {
        if (numberStyle != null) return;

        numberStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        numberStyle.normal.textColor = Color.white;
    }

    void EnsureTextures()
    {
        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }

        if (genAxeIcon == null && axeIcon == null) genAxeIcon = MakeIcon(128, AxeShape);
        if (genGrappleIcon == null && grappleIcon == null) genGrappleIcon = MakeIcon(128, GrappleShape);
        if (genEmptyIcon == null) genEmptyIcon = MakeIcon(128, EmptyShape);
    }

    // ---------------------------------------------------------------- generated icons

    // White silhouettes in an alpha texture, tinted at draw time.
    static Texture2D MakeIcon(int size, System.Func<Vector2, bool> inside)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Color[] px = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float a = 0f;

                for (int sy = 0; sy < 2; sy++)
                {
                    for (int sx = 0; sx < 2; sx++)
                    {
                        Vector2 p = new Vector2(
                            (x + 0.25f + sx * 0.5f) / size,
                            (y + 0.25f + sy * 0.5f) / size);

                        if (inside(p)) a += 0.25f;
                    }
                }

                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }

        tex.SetPixels(px);
        tex.Apply();
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    static bool AxeShape(Vector2 p)
    {
        if (SegDist(p, new Vector2(0.26f, 0.10f), new Vector2(0.70f, 0.90f)) < 0.055f)
            return true;

        return InQuad(p,
            new Vector2(0.60f, 0.56f),
            new Vector2(0.26f, 0.74f),
            new Vector2(0.34f, 0.96f),
            new Vector2(0.68f, 0.88f));
    }

    static bool GrappleShape(Vector2 p)
    {
        if (SegDist(p, new Vector2(0.28f, 0.08f), new Vector2(0.50f, 0.50f)) < 0.055f)
            return true;

        Vector2 c = new Vector2(0.55f, 0.66f);
        float d = (p - c).magnitude;
        if (Mathf.Abs(d - 0.22f) > 0.058f) return false;

        float ang = Mathf.Repeat(Mathf.Atan2(p.y - c.y, p.x - c.x) * Mathf.Rad2Deg, 360f);
        return ang >= 20f && ang <= 300f;
    }

    static bool EmptyShape(Vector2 p)
    {
        return Mathf.Abs(p.y - 0.5f) < 0.03f && Mathf.Abs(p.x - 0.5f) < 0.2f;
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len = ab.sqrMagnitude;
        if (len < 0.000001f) return (p - a).magnitude;

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len);
        return (p - (a + ab * t)).magnitude;
    }

    static bool InQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        return Side(p, a, b) && Side(p, b, c) && Side(p, c, d) && Side(p, d, a);
    }

    static bool Side(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 e = b - a;
        Vector2 v = p - a;
        return e.x * v.y - e.y * v.x <= 0f;
    }
}