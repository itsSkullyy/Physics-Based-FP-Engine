using System.Collections;
using UnityEngine;

// ULTRAKILL-style impact frame. One in the scene (auto-spawned if missing).
//
//   ImpactFrames.Hit(worldPoint);
//   ImpactFrames.Get().Freeze(0.1f, 0.28f, 1f);   // freeze, overlay, intensity
//
// A hard time freeze (timeScale 0, distinct from JuiceFX.Hitstop which only slows
// time), a screen grab recoloured into black-and-red, and speed lines with a
// chromatic split layered over that.
[DefaultExecutionOrder(-70)]
public class ImpactFrames : MonoBehaviour
{
    public static ImpactFrames Instance { get; private set; }

    [Header("Freeze")]
    [Tooltip("Real seconds the game sits at timeScale 0.")]
    public float defaultFreeze = 0.1f;
    [Tooltip("Hard ceiling so a spammed effect can never lock the game up.")]
    public float maxFreeze = 0.4f;
    [Tooltip("Physics step is scaled to match the freeze so nothing tunnels on resume.")]
    public bool scaleFixedDelta = true;

    [Header("Overlay")]
    public bool showOverlay = true;
    [Tooltip("Real seconds the effect takes to fade after the freeze ends.")]
    public float defaultOverlay = 0.28f;
    [Range(0f, 1f)] public float maxOverlayAlpha = 1f;

    [Header("Red World")]
    [Tooltip("Recolour the frozen frame into a hard red-and-black two-tone.")]
    public bool recolourWorld = true;
    [Tooltip("How fully the world is crushed to red/black. 1 = pure two-tone, no original.")]
    [Range(0f, 1f)] public float recolourStrength = 1f;
    [Tooltip("Anything darker than this goes to shadow, anything brighter ramps to red.")]
    public Color shadowColor = new Color(0f, 0f, 0f, 1f);
    public Color midColor = new Color(0.85f, 0.02f, 0.03f, 1f);
    public Color highlightColor = new Color(1f, 0.15f, 0.1f, 1f);
    [Tooltip("Brightness split between black and red. Lower = more of the frame turns red.")]
    [Range(0f, 1f)] public float threshold = 0.32f;
    [Tooltip("Softness of the black/red boundary. Tiny = razor sharp.")]
    [Range(0.001f, 0.4f)] public float edgeSoftness = 0.09f;
    [Tooltip("Darkening toward the screen edges. Higher = tighter, more focused frame.")]
    [Range(0f, 2f)] public float vignetteStrength = 0.9f;

    [Header("Grain")]
    [Tooltip("Film-grain noise mixed into the image and the black/red edge.")]
    [Range(0f, 1f)] public float grain = 0.35f;
    [Tooltip("Noise cell size in pixels. Smaller = finer grain.")]
    public float grainScale = 1.5f;
    [Tooltip("Sharpens the grain toward hard specks. 0 = soft static, 1 = sharp flecks.")]
    [Range(0f, 1f)] public float grainSpike = 0.5f;

    [Header("Edges")]
    [Tooltip("Bright hot lines drawn along real brightness edges in the frame. 0 = off.")]
    [Range(0f, 2f)] public float edgeLines = 0.6f;
    [Tooltip("Higher = only the hardest edges light up, thinner and sharper.")]
    [Range(0.5f, 4f)] public float edgePower = 1.5f;

    [Header("Flash")]
    [Tooltip("Extra red bloom laid over the recoloured frame right at the moment of impact.")]
    public Color flashColor = new Color(1f, 0.06f, 0.09f, 1f);
    [Range(0f, 1f)] public float maxFlashAlpha = 0.25f;

    [Header("Sharp Lines")]
    public bool drawLines = true;
    public int lineCount = 26;
    public Color lineColor = new Color(1f, 0.9f, 0.85f, 1f);
    [Range(0.1f, 1f)] public float lineReach = 0.65f;
    public float lineThickness = 3.5f;
    public bool linesConvergeOnImpact = true;

    [Header("Chromatic Edge")]
    public bool chromaticSplit = true;
    public Color splitColor = new Color(0f, 0.9f, 1f, 1f);
    public float splitOffset = 6f;

    float defaultFixedDelta;
    bool frozen;

    float overlayT;
    float overlayDuration;
    float overlayIntensity;
    Vector2 impactScreen = new Vector2(0.5f, 0.5f);
    float lineSeed;

    // Screen grab held for the life of one overlay.
    RenderTexture grabbedFrame;
    Material recolourMat;
    bool grabQueued;

    static Texture2D whiteTex;

    public bool IsFrozen => frozen;

    public static ImpactFrames Get()
    {
        if (Instance != null) return Instance;
        ImpactFrames found = FindFirstObjectByType<ImpactFrames>();
        if (found != null) { Instance = found; return Instance; }
        GameObject go = new GameObject("ImpactFrames");
        Instance = go.AddComponent<ImpactFrames>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        defaultFixedDelta = Time.fixedDeltaTime;
        EnsureTex();
        BuildRecolourMaterial();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (frozen)
        {
            Time.timeScale = 1f;
            Time.fixedDeltaTime = defaultFixedDelta;
        }
        ReleaseGrab();
        if (recolourMat != null) Destroy(recolourMat);
    }

    void BuildRecolourMaterial()
    {
        if (!recolourWorld) return;

        Shader s = Shader.Find("Hidden/ImpactRedWorld");
        if (s == null)
        {
            Debug.LogWarning("ImpactFrames: 'Hidden/ImpactRedWorld' shader not found. Add " +
                             "ImpactRedWorld.shader to the project (and to Always Included " +
                             "Shaders). Falling back to the flat red flash only.", this);
            recolourWorld = false;
            return;
        }
        recolourMat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
    }

    static void EnsureTex()
    {
        if (whiteTex != null) return;
        whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        whiteTex.SetPixel(0, 0, Color.white);
        whiteTex.Apply();
        whiteTex.hideFlags = HideFlags.HideAndDontSave;
    }

    // ---------------------------------------------------------------- API

    public static void Hit(Vector3 worldPoint, float intensity = 1f)
    {
        ImpactFrames inst = Get();
        inst.SetImpactPoint(worldPoint);
        inst.Freeze(inst.defaultFreeze, inst.defaultOverlay, intensity);
    }

    public static void HitScreen(Vector2 viewportPoint, float intensity = 1f)
    {
        ImpactFrames inst = Get();
        inst.impactScreen = viewportPoint;
        inst.Freeze(inst.defaultFreeze, inst.defaultOverlay, intensity);
    }

    public void SetImpactPoint(Vector3 worldPoint)
    {
        Camera cam = Camera.main;
        if (cam == null) { impactScreen = new Vector2(0.5f, 0.5f); return; }
        Vector3 vp = cam.WorldToViewportPoint(worldPoint);
        impactScreen = vp.z > 0f
            ? new Vector2(Mathf.Clamp01(vp.x), Mathf.Clamp01(vp.y))
            : new Vector2(0.5f, 0.5f);
    }

    public void Freeze(float freezeSeconds, float overlaySeconds, float intensity = 1f)
    {
        intensity = Mathf.Clamp01(intensity);

        if (showOverlay && overlaySeconds > 0f)
        {
            overlayDuration = overlaySeconds;
            overlayT = 1f;
            overlayIntensity = intensity;
            lineSeed = Random.value * 1000f;

            if (recolourWorld && recolourMat != null)
                grabQueued = true;
        }

        freezeSeconds = Mathf.Min(freezeSeconds, maxFreeze);
        if (freezeSeconds > 0f && !frozen && isActiveAndEnabled)
            StartCoroutine(FreezeRoutine(freezeSeconds));
    }

    IEnumerator FreezeRoutine(float seconds)
    {
        frozen = true;

        if (grabQueued)
            yield return StartCoroutine(GrabFrame());

        float prevScale = Time.timeScale;
        Time.timeScale = 0f;
        if (scaleFixedDelta) Time.fixedDeltaTime = defaultFixedDelta * 0.0001f;

        float end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end)
            yield return null;

        if (Mathf.Approximately(Time.timeScale, 0f))
            Time.timeScale = prevScale <= 0f ? 1f : prevScale;
        Time.fixedDeltaTime = defaultFixedDelta;

        frozen = false;
    }

    IEnumerator GrabFrame()
    {
        grabQueued = false;
        yield return new WaitForEndOfFrame();

        int w = Mathf.Max(16, Screen.width);
        int h = Mathf.Max(16, Screen.height);

        if (grabbedFrame == null || grabbedFrame.width != w || grabbedFrame.height != h)
        {
            ReleaseGrab();
            grabbedFrame = new RenderTexture(w, h, 0, RenderTextureFormat.Default);
            grabbedFrame.hideFlags = HideFlags.HideAndDontSave;
        }

        ScreenCapture.CaptureScreenshotIntoRenderTexture(grabbedFrame);
    }

    void ReleaseGrab()
    {
        if (grabbedFrame != null)
        {
            grabbedFrame.Release();
            Destroy(grabbedFrame);
            grabbedFrame = null;
        }
    }

    void Update()
    {
        if (overlayT <= 0f) return;

        overlayT -= Time.unscaledDeltaTime / Mathf.Max(0.01f, overlayDuration);
        if (overlayT <= 0f)
        {
            overlayT = 0f;
            ReleaseGrab();
        }
    }

    // ---------------------------------------------------------------- drawing

    void OnGUI()
    {
        if (overlayT <= 0f || overlayIntensity <= 0f) return;
        EnsureTex();

        float w = Screen.width;
        float h = Screen.height;
        float e = overlayT * overlayT;
        float a = Mathf.Min(maxOverlayAlpha, e * overlayIntensity);

        Vector2 focus = new Vector2(impactScreen.x * w, (1f - impactScreen.y) * h);
        Color prev = GUI.color;

        if (recolourWorld && recolourMat != null && grabbedFrame != null && Event.current.type == EventType.Repaint)
        {
            recolourMat.SetColor("_Shadow", shadowColor);
            recolourMat.SetColor("_Mid", midColor);
            recolourMat.SetColor("_High", highlightColor);
            recolourMat.SetFloat("_Strength", recolourStrength);
            recolourMat.SetFloat("_Threshold", threshold);
            recolourMat.SetFloat("_Edge", edgeSoftness);
            recolourMat.SetFloat("_Alpha", a);
            recolourMat.SetFloat("_FocusX", impactScreen.x);
            recolourMat.SetFloat("_FocusY", impactScreen.y);
            recolourMat.SetFloat("_Vignette", vignetteStrength);
            recolourMat.SetFloat("_Grain", grain);
            recolourMat.SetFloat("_GrainScale", Mathf.Max(0.5f, grainScale));
            recolourMat.SetFloat("_Spike", grainSpike);
            recolourMat.SetFloat("_Edges", edgeLines);
            recolourMat.SetFloat("_EdgePower", edgePower);
            recolourMat.SetFloat("_Time01", Time.realtimeSinceStartup);

            recolourMat.SetVector("_MainTex_TexelSize", new Vector4(
                1f / grabbedFrame.width, 1f / grabbedFrame.height,
                grabbedFrame.width, grabbedFrame.height));

            Graphics.DrawTexture(new Rect(0, h, w, -h), grabbedFrame, recolourMat);
        }

        Color flash = flashColor;
        flash.a = Mathf.Min(maxFlashAlpha, flashColor.a) * a;
        GUI.color = flash;
        GUI.DrawTexture(new Rect(0, 0, w, h), whiteTex);

        if (drawLines)
            DrawSpeedLines(w, h, focus, a);

        GUI.color = prev;
    }

    void DrawSpeedLines(float w, float h, Vector2 focus, float a)
    {
        Vector2 target = linesConvergeOnImpact ? focus : new Vector2(w * 0.5f, h * 0.5f);
        float halfDiag = Mathf.Sqrt(w * w + h * h) * 0.5f;
        float inner = halfDiag * (1f - lineReach);
        Matrix4x4 baseMatrix = GUI.matrix;

        for (int i = 0; i < lineCount; i++)
        {
            float rnd = Frac(Mathf.Sin((i + lineSeed) * 12.9898f) * 43758.5453f);
            float ang = (i / (float)lineCount) * 360f + (rnd - 0.5f) * (360f / lineCount);
            float rad = ang * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            float len = halfDiag - inner * (0.6f + rnd * 0.4f);
            float thick = lineThickness * (0.6f + rnd * 0.9f);

            Vector2 outer = target + dir * (halfDiag + thick);
            DrawLine(outer, target + dir * (halfDiag - len), thick, baseMatrix, a);
        }
        GUI.matrix = baseMatrix;
    }

    void DrawLine(Vector2 from, Vector2 to, float thickness, Matrix4x4 baseMatrix, float a)
    {
        Vector2 delta = to - from;
        float len = delta.magnitude;
        if (len < 0.01f) return;
        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;

        GUIUtility.RotateAroundPivot(angle, from);
        if (chromaticSplit)
        {
            Color s = splitColor; s.a = a * 0.5f;
            GUI.color = s;
            GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f + splitOffset, len, thickness), whiteTex);
        }
        Color c = lineColor; c.a = a;
        GUI.color = c;
        GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, len, thickness), whiteTex);
        GUI.matrix = baseMatrix;
    }

    static float Frac(float x) => x - Mathf.Floor(x);
}