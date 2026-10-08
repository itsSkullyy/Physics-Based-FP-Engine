using System.Collections.Generic;
using UnityEngine;

// Grenade explosion built at runtime like JuiceFX: flash, fireball, smoke, debris,
// shockwave ring and the BOOM text.
public class ExplosionFX : MonoBehaviour
{
    public static ExplosionFX Instance { get; private set; }

    [Header("Colours")]
    public Color flashColor = new Color(1f, 0.97f, 0.82f);
    public Color[] firePalette =
    {
        new Color(1f, 0.92f, 0.4f),
        new Color(1f, 0.6f, 0.12f),
        new Color(0.95f, 0.28f, 0.08f)
    };
    public Color smokeDark = new Color(0.16f, 0.14f, 0.15f);
    public Color smokeLight = new Color(0.36f, 0.33f, 0.33f);
    public Color debrisColor = new Color(0.22f, 0.12f, 0.1f);

    [Header("Boom Text")]
    public bool showBoomText = true;
    public string boomWord = "BOOM!";
    public int boomFontSize = 64;
    public Color boomColor = new Color(1f, 0.86f, 0.15f);
    public Color boomOutline = new Color(0.28f, 0.05f, 0.04f);
    public float boomDuration = 0.9f;

    ParticleSystem flash, fire, smoke, debris;
    Material particleMat;

    struct Ring
    {
        public LineRenderer line;
        public Vector3 center;
        public float radius;
        public float start;
    }
    readonly List<Ring> rings = new List<Ring>();
    readonly Stack<LineRenderer> ringPool = new Stack<LineRenderer>();

    struct Boom
    {
        public Vector3 world;
        public float start;
        public float tilt;
    }
    readonly List<Boom> booms = new List<Boom>();
    GUIStyle boomStyle;

    const float RingTime = 0.35f;
    const int RingSegments = 40;

    public static ExplosionFX Get()
    {
        if (Instance != null)
        {
            return Instance;
        }

        Instance = FindFirstObjectByType<ExplosionFX>();
        if (Instance != null)
        {
            return Instance;
        }

        GameObject go = new GameObject("ExplosionFX");
        Instance = go.AddComponent<ExplosionFX>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        Build();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ---------------------------------------------------------------- setup

    void Build()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (s == null)
        {
            s = Shader.Find("Particles/Standard Unlit");
        }
        if (s == null)
        {
            s = Shader.Find("Sprites/Default");
        }
        particleMat = new Material(s);
        if (particleMat.HasProperty("_BaseColor"))
        {
            particleMat.SetColor("_BaseColor", Color.white);
        }

        Mesh sphere = PrimitiveMesh(PrimitiveType.Sphere);
        Mesh cube = PrimitiveMesh(PrimitiveType.Cube);

        AnimationCurve pop = new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.25f, 1f), new Keyframe(1f, 0f));
        AnimationCurve shrink = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        AnimationCurve swell = new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0f));

        flash = MakeSystem("Flash", sphere, 0f, pop, 0f, false);
        fire = MakeSystem("Fire", sphere, 0f, shrink, 4f, false);
        smoke = MakeSystem("Smoke", sphere, -0.12f, swell, 1.2f, false);
        debris = MakeSystem("Debris", cube, 1.6f, shrink, 0.3f, true);
    }

    static Mesh PrimitiveMesh(PrimitiveType type)
    {
        GameObject tmp = GameObject.CreatePrimitive(type);
        Mesh m = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);
        return m;
    }

    ParticleSystem MakeSystem(string name, Mesh mesh, float gravity, AnimationCurve sizeCurve, float drag, bool tumble)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        // looping with emission off so it only has what Explode() emits
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 600;
        main.gravityModifier = gravity;
        main.startRotation3D = tumble;

        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        if (drag > 0f)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag = drag;
            limit.multiplyDragByParticleSize = false;
            limit.multiplyDragByParticleVelocity = false;
        }

        if (tumble)
        {
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-8f, 8f);
            rot.y = new ParticleSystem.MinMaxCurve(-8f, 8f);
            rot.z = new ParticleSystem.MinMaxCurve(-8f, 8f);
        }

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Mesh;
        r.mesh = mesh;
        r.sharedMaterial = particleMat;
        r.alignment = ParticleSystemRenderSpace.World;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;

        ps.Play();
        return ps;
    }

    // ---------------------------------------------------------------- explode

    public void Explode(Vector3 position, float radius)
    {
        float s = Mathf.Max(0.3f, radius / 5f);

        Emit(flash, position, Vector3.zero, radius * 0.9f, 0.2f, flashColor);

        for (int i = 0; i < 6; i++)
        {
            Emit(fire, position + Random.insideUnitSphere * 0.4f * s, Random.onUnitSphere * Random.Range(0.5f, 2.5f) * s,
                Random.Range(1.4f, 2.2f) * s, Random.Range(0.45f, 0.75f), firePalette[Random.Range(0, firePalette.Length)]);
        }

        for (int i = 0; i < 18; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y) * 0.6f + 0.15f;
            Emit(fire, position, dir.normalized * Random.Range(4f, 10f) * s, Random.Range(0.5f, 1.2f) * s,
                Random.Range(0.3f, 0.6f), firePalette[Random.Range(0, firePalette.Length)]);
        }

        for (int i = 0; i < 12; i++)
        {
            Vector3 outward = Random.insideUnitSphere;
            outward.y = 0f;
            Vector3 v = Vector3.up * Random.Range(1.5f, 3.5f) * s + outward * Random.Range(1f, 2.5f) * s;
            Emit(smoke, position + Random.insideUnitSphere * radius * 0.25f, v, Random.Range(1f, 2f) * s,
                Random.Range(1f, 1.8f), Color.Lerp(smokeDark, smokeLight, Random.value));
        }

        for (int i = 0; i < 14; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y);
            Emit(debris, position + Vector3.up * 0.2f, dir * Random.Range(6f, 13f) * s + Vector3.up * Random.Range(2f, 6f),
                Random.Range(0.12f, 0.3f) * s, Random.Range(0.7f, 1.2f),
                Random.value < 0.6f ? debrisColor : firePalette[2]);
        }

        StartRing(position, radius);

        if (showBoomText)
        {
            booms.Add(new Boom { world = position + Vector3.up * 1.2f, start = Time.unscaledTime, tilt = Random.Range(-12f, 12f) });
        }

        JuiceFX juice = JuiceFX.Get();
        if (juice != null)
        {
            juice.ImpactBurst(position, Vector3.up, 1f);
        }

        EnemySounds.PlayAt(EnemySounds.Explosion, position, 1f, Random.Range(0.92f, 1.05f), 80f);
    }

    static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color color)
    {
        var ep = new ParticleSystem.EmitParams
        {
            position = pos,
            velocity = vel,
            startSize = size,
            startLifetime = life,
            startColor = color,
            rotation3D = new Vector3(Random.Range(0f, 360f), Random.Range(0f, 360f), Random.Range(0f, 360f))
        };
        ps.Emit(ep, 1);
    }

    // ---------------------------------------------------------------- shockwave

    void StartRing(Vector3 center, float radius)
    {
        LineRenderer lr = ringPool.Count > 0 ? ringPool.Pop() : MakeRing();
        lr.enabled = true;

        if (Physics.Raycast(center + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore))
        {
            center = hit.point;
        }

        rings.Add(new Ring { line = lr, center = center + Vector3.up * 0.08f, radius = radius, start = Time.time });
    }

    LineRenderer MakeRing()
    {
        GameObject go = new GameObject("Shockwave");
        go.transform.SetParent(transform, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = RingSegments;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        Shader sh = Shader.Find("Sprites/Default");
        lr.material = sh != null ? new Material(sh) : particleMat;
        return lr;
    }

    void Update()
    {
        UpdateRings();
        booms.RemoveAll(b => Time.unscaledTime - b.start > boomDuration);
    }

    // shockwave rings grow out along the ground and fade, then go back in the pool
    void UpdateRings()
    {
        for (int i = rings.Count - 1; i >= 0; i--)
        {
            Ring r = rings[i];
            float t = (Time.time - r.start) / RingTime;
            if (t >= 1f)
            {
                r.line.enabled = false;
                ringPool.Push(r.line);
                rings.RemoveAt(i);
                continue;
            }
            DrawRing(r, t);
        }
    }

    void DrawRing(Ring r, float t)
    {
        float eased = 1f - (1f - t) * (1f - t);
        float rad = Mathf.Lerp(0.3f, r.radius * 1.6f, eased);
        for (int k = 0; k < RingSegments; k++)
        {
            float a = k / (float)RingSegments * Mathf.PI * 2f;
            r.line.SetPosition(k, r.center + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad));
        }

        float width = Mathf.Lerp(0.7f, 0f, t) * Mathf.Max(0.4f, r.radius / 5f);
        r.line.startWidth = width;
        r.line.endWidth = width;
        Color c = Color.Lerp(flashColor, firePalette[1], t);
        r.line.startColor = c;
        r.line.endColor = c;
    }

    // ---------------------------------------------------------------- BOOM!

    void OnGUI()
    {
        if (booms.Count == 0)
        {
            return;
        }
        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        EnsureBoomStyle();
        foreach (Boom b in booms)
        {
            DrawBoom(cam, b);
        }
    }

    void EnsureBoomStyle()
    {
        if (boomStyle != null)
        {
            return;
        }
        boomStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold
        };
    }

    // pops in big, settles, then shrinks away at the end
    float BoomScale(float age)
    {
        if (age < 0.12f)
        {
            return Mathf.Lerp(0.2f, 1.35f, age / 0.12f);
        }
        if (age < 0.25f)
        {
            return Mathf.Lerp(1.35f, 1f, (age - 0.12f) / 0.13f);
        }
        if (age > boomDuration - 0.25f)
        {
            return Mathf.Lerp(1f, 0f, (age - (boomDuration - 0.25f)) / 0.25f);
        }
        return 1f;
    }

    void DrawBoom(Camera cam, Boom b)
    {
        Vector3 sp = cam.WorldToScreenPoint(b.world);
        if (sp.z <= 0f)
        {
            return;
        }

        float scale = BoomScale(Time.unscaledTime - b.start);
        float distanceScale = Mathf.Clamp(14f / Mathf.Max(1f, sp.z), 0.45f, 1.4f);
        int size = Mathf.Max(1, Mathf.RoundToInt(boomFontSize * scale * distanceScale));
        boomStyle.fontSize = size;

        Vector2 center = new Vector2(sp.x, Screen.height - sp.y);
        Rect r = new Rect(center.x - size * 4f, center.y - size, size * 8f, size * 2f);

        Matrix4x4 old = GUI.matrix;
        GUIUtility.RotateAroundPivot(b.tilt, center);

        // outline, shadow, fill
        float o = Mathf.Max(2f, size * 0.06f);
        boomStyle.normal.textColor = boomOutline;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f;
            GUI.Label(new Rect(r.x + Mathf.Cos(a) * o, r.y + Mathf.Sin(a) * o, r.width, r.height), boomWord, boomStyle);
        }
        GUI.Label(new Rect(r.x + o * 1.6f, r.y + o * 2f, r.width, r.height), boomWord, boomStyle);

        boomStyle.normal.textColor = boomColor;
        GUI.Label(r, boomWord, boomStyle);

        GUI.matrix = old;
    }
}
