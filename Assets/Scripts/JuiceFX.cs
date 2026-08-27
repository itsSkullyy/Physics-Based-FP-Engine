using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Central effects service, cel-shaded style. Everything is built at runtime with no
// assets to import: particles are real 3D meshes (faceted low-poly spheres, shards),
// cel shading is baked into per-facet UVs against a 2x1 palette texture, outlines are
// inverted hulls rendered flat black behind the solid pass, and nothing fades out -
// opaque materials can't blend, so particles shrink out instead.
public class JuiceFX : MonoBehaviour
{
    public static JuiceFX Instance { get; private set; }

    [Header("Cel Style")]
    [Tooltip("Object-space light direction the two-tone split is baked against.")]
    public Vector3 lightDirection = new Vector3(-0.4f, 1f, -0.35f);
    [Range(-0.6f, 0.6f)] public float terminator = 0.05f;
    [Range(0f, 1f)] public float shadowStrength = 0.52f;
    public Color shadowTint = new Color(0.35f, 0.42f, 0.75f, 1f);
    [Range(0f, 1f)] public float shadowTintAmount = 0.3f;

    [Header("Blockiness")]
    [Tooltip("0 = 20-face rock, 1 = 80 faces, 2 = 320 faces. Lower is chunkier.")]
    [Range(0, 2)] public int sphereSubdivisions = 1;

    [Header("Outlines")]
    public bool drawOutlines = true;
    public Color outlineColor = new Color(0.05f, 0.04f, 0.08f, 1f);
    [Tooltip("Fraction of the particle radius. Scales with particle size.")]
    [Range(0f, 0.5f)] public float outlineWidth = 0.13f;

    [Header("Dust")]
    public Color[] dustPalette =
    {
        new Color(0.93f, 0.89f, 0.79f),
        new Color(0.82f, 0.76f, 0.66f),
        new Color(0.70f, 0.66f, 0.62f)
    };
    public float dustScale = 1f;
    public int dustMinCount = 4;
    public int dustMaxCount = 22;
    public float dustGravity = 0.12f;

    [Header("Sparks")]
    public Color[] sparkPalette =
    {
        new Color(1f, 0.97f, 0.85f),
        new Color(1f, 0.82f, 0.35f),
        new Color(1f, 0.55f, 0.22f)
    };
    public float sparkScale = 1f;
    public int sparkMinCount = 4;
    public int sparkMaxCount = 16;
    public float sparkGravity = 1.1f;
    [Tooltip("How stretched the spark shards are along their travel direction.")]
    public float sparkLength = 3.4f;
    [Tooltip("Big slow shards thrown in on impact, anime style.")]
    public int flashShards = 4;
    public float flashScale = 2.6f;

    [Header("Hitstop")]
    public bool enableHitstop = true;
    public float maxHitstopDuration = 0.2f;

    ParticleSystem[] dustCores;
    ParticleSystem[] sparkCores;
    ParticleSystem dustOutline;
    ParticleSystem sparkOutline;

    float hitstopEnd;
    bool hitstopRunning;
    float defaultFixedDelta;

    public static JuiceFX Get()
    {
        if (Instance != null) return Instance;

        JuiceFX found = FindFirstObjectByType<JuiceFX>();
        if (found != null)
        {
            Instance = found;
            return Instance;
        }

        GameObject go = new GameObject("JuiceFX");
        Instance = go.AddComponent<JuiceFX>();
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
        defaultFixedDelta = Time.fixedDeltaTime;
        BuildSystems();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- public API

    /// Ring of dust spheres kicked outward along a surface. strength 0..1.
    public void LandDust(Vector3 point, Vector3 normal, float strength)
    {
        if (dustCores == null) return;
        strength = Mathf.Clamp01(strength);

        int count = Mathf.RoundToInt(Mathf.Lerp(dustMinCount, dustMaxCount, strength));
        Basis(normal, out Vector3 t1, out Vector3 t2);

        for (int i = 0; i < count; i++)
        {
            float ang = (i / (float)count) * Mathf.PI * 2f + Random.Range(-0.3f, 0.3f);
            Vector3 outward = t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang);

            var ep = new ParticleSystem.EmitParams();
            ep.position = point + outward * Random.Range(0.05f, 0.4f) + normal * 0.08f;
            ep.velocity = outward * Random.Range(1.1f, 3.4f) * Mathf.Lerp(0.55f, 1.7f, strength)
                        + normal * Random.Range(0.3f, 1.5f) * Mathf.Lerp(0.6f, 1.4f, strength);
            ep.startSize = Random.Range(0.16f, 0.44f) * Mathf.Lerp(0.7f, 1.6f, strength) * dustScale;
            ep.startLifetime = Random.Range(0.35f, 0.85f);
            ep.rotation3D = Vector3.zero;

            EmitDust(ep);
        }
    }

    /// Small continuous scuff, for sliding and footfalls.
    public void Scuff(Vector3 point, Vector3 normal, Vector3 drift, float strength)
    {
        if (dustCores == null) return;
        strength = Mathf.Clamp01(strength);

        int count = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, 5f, strength)));
        Basis(normal, out Vector3 t1, out Vector3 t2);

        for (int i = 0; i < count; i++)
        {
            Vector3 spread = (t1 * Random.Range(-1f, 1f) + t2 * Random.Range(-1f, 1f)).normalized;

            var ep = new ParticleSystem.EmitParams();
            ep.position = point + spread * Random.Range(0f, 0.22f) + normal * 0.07f;
            ep.velocity = drift * Random.Range(0.15f, 0.45f)
                        + spread * Random.Range(0.2f, 0.9f)
                        + normal * Random.Range(0.4f, 1.2f);
            ep.startSize = Random.Range(0.12f, 0.32f) * Mathf.Lerp(0.7f, 1.3f, strength) * dustScale;
            ep.startLifetime = Random.Range(0.3f, 0.7f);
            ep.rotation3D = Vector3.zero;

            EmitDust(ep);
        }
    }

    /// Sharp hit: shards cone out along the normal plus a dust puff and a slow flash burst.
    public void ImpactBurst(Vector3 point, Vector3 normal, float strength)
    {
        strength = Mathf.Clamp01(strength);

        LandDust(point, normal, strength * 0.7f);

        if (sparkCores == null) return;
        Basis(normal, out Vector3 t1, out Vector3 t2);

        int count = Mathf.RoundToInt(Mathf.Lerp(sparkMinCount, sparkMaxCount, strength));

        for (int i = 0; i < count; i++)
        {
            Vector3 spread = t1 * Random.Range(-1f, 1f) + t2 * Random.Range(-1f, 1f);
            Vector3 dir = (normal + spread * Random.Range(0.3f, 1.1f)).normalized;

            EmitSpark(
                point + normal * 0.05f,
                dir * Random.Range(4f, 11f) * Mathf.Lerp(0.6f, 1.5f, strength),
                Random.Range(0.05f, 0.12f) * sparkScale,
                Random.Range(0.16f, 0.42f));
        }

        int flash = Mathf.RoundToInt(flashShards * strength);
        for (int i = 0; i < flash; i++)
        {
            float ang = (i / Mathf.Max(1f, flash)) * Mathf.PI * 2f + Random.Range(-0.4f, 0.4f);
            Vector3 outward = t1 * Mathf.Cos(ang) + t2 * Mathf.Sin(ang);
            Vector3 dir = (outward + normal * 0.35f).normalized;

            EmitSpark(
                point + normal * 0.05f,
                dir * Random.Range(1.5f, 3f),
                Random.Range(0.1f, 0.16f) * sparkScale * flashScale,
                Random.Range(0.12f, 0.22f));
        }
    }

    /// Directionless air burst, for wall kicks, darts and mid-air pops.
    public void AirPuff(Vector3 point, Vector3 direction, float strength)
    {
        if (dustCores == null) return;
        strength = Mathf.Clamp01(strength);

        int count = Mathf.RoundToInt(Mathf.Lerp(dustMinCount, dustMaxCount * 0.7f, strength));
        Vector3 dir = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.up;

        for (int i = 0; i < count; i++)
        {
            Vector3 rand = Random.onUnitSphere;

            var ep = new ParticleSystem.EmitParams();
            ep.position = point + rand * Random.Range(0f, 0.3f);
            ep.velocity = Vector3.Lerp(rand, dir, 0.55f) * Random.Range(1.5f, 4.5f)
                          * Mathf.Lerp(0.6f, 1.5f, strength);
            ep.startSize = Random.Range(0.14f, 0.38f) * Mathf.Lerp(0.7f, 1.4f, strength) * dustScale;
            ep.startLifetime = Random.Range(0.25f, 0.6f);
            ep.rotation3D = Vector3.zero;

            EmitDust(ep);
        }
    }

    /// Freezes time briefly. Physics step is scaled to match so nothing tunnels.
    public void Hitstop(float duration, float timeScale = 0.05f)
    {
        if (!enableHitstop || duration <= 0f) return;

        duration = Mathf.Min(duration, maxHitstopDuration);
        hitstopEnd = Mathf.Max(hitstopEnd, Time.unscaledTime + duration);

        if (!hitstopRunning)
            StartCoroutine(HitstopRoutine(Mathf.Clamp(timeScale, 0.01f, 1f)));
    }

    IEnumerator HitstopRoutine(float scale)
    {
        hitstopRunning = true;

        Time.timeScale = scale;
        Time.fixedDeltaTime = defaultFixedDelta * scale;

        while (Time.unscaledTime < hitstopEnd)
            yield return null;

        Time.timeScale = 1f;
        Time.fixedDeltaTime = defaultFixedDelta;
        hitstopRunning = false;
    }

    // ---------------------------------------------------------------- emitting

    void EmitDust(ParticleSystem.EmitParams ep)
    {
        dustCores[Random.Range(0, dustCores.Length)].Emit(ep, 1);
        if (dustOutline != null) dustOutline.Emit(ep, 1);
    }

    void EmitSpark(Vector3 position, Vector3 velocity, float size, float lifetime)
    {
        var ep = new ParticleSystem.EmitParams();
        ep.position = position;
        ep.velocity = velocity;
        ep.startSize = size;
        ep.startLifetime = lifetime;

        ep.rotation3D = velocity.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(velocity).eulerAngles
            : Random.rotation.eulerAngles;

        sparkCores[Random.Range(0, sparkCores.Length)].Emit(ep, 1);
        if (sparkOutline != null) sparkOutline.Emit(ep, 1);
    }

    // ---------------------------------------------------------------- construction

    void BuildSystems()
    {
        Vector3 light = lightDirection.sqrMagnitude > 0.001f
            ? lightDirection.normalized
            : Vector3.up;

        Mesh sphere = MakeIcoSphere(sphereSubdivisions, light, terminator);
        Mesh shard = MakeShard(sparkLength, light, terminator);

        dustCores = new ParticleSystem[Mathf.Max(1, dustPalette.Length)];
        for (int i = 0; i < dustCores.Length; i++)
        {
            Color c = dustPalette.Length > 0 ? dustPalette[i] : Color.white;
            dustCores[i] = BuildSystem("Dust_" + i, sphere, ToneMaterial(c),
                dustGravity, 0.35f, false);
        }

        sparkCores = new ParticleSystem[Mathf.Max(1, sparkPalette.Length)];
        for (int i = 0; i < sparkCores.Length; i++)
        {
            Color c = sparkPalette.Length > 0 ? sparkPalette[i] : Color.white;
            sparkCores[i] = BuildSystem("Spark_" + i, shard, ToneMaterial(c),
                sparkGravity, 0.05f, true);
        }

        if (!drawOutlines || outlineWidth <= 0.001f) return;

        Material outlineMat = SolidMaterial(outlineColor);
        dustOutline = BuildSystem("DustOutline", MakeHull(sphere, outlineWidth), outlineMat,
            dustGravity, 0.35f, false);
        sparkOutline = BuildSystem("SparkOutline", MakeHull(shard, outlineWidth), outlineMat,
            sparkGravity, 0.05f, true);
    }

    ParticleSystem BuildSystem(string name, Mesh mesh, Material material,
                               float gravity, float dampen, bool shrinkOnly)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.maxParticles = 3000;
        main.startLifetime = 0.6f;
        main.startSpeed = 0f;
        main.startSize = 0.2f;
        main.gravityModifier = gravity;
        main.startRotation3D = true;
        main.startRotation = 0f;

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.enabled = false;

        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.dampen = dampen;
        limit.limit = new ParticleSystem.MinMaxCurve(50f);

        var sizeOverLife = ps.sizeOverLifetime;
        sizeOverLife.enabled = true;

        AnimationCurve curve;
        if (shrinkOnly)
        {
            curve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.7f, 0.55f),
                new Keyframe(1f, 0f));
        }
        else
        {
            curve = new AnimationCurve(
                new Keyframe(0f, 0.55f),
                new Keyframe(0.25f, 1.15f),
                new Keyframe(0.7f, 1f),
                new Keyframe(1f, 0f));
        }
        sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, curve);

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = mesh;
        renderer.material = material;
        renderer.alignment = ParticleSystemRenderSpace.World;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        ps.Play();
        return ps;
    }

    // ---------------------------------------------------------------- materials

    /// Flat two-tone material: a 2x1 point-filtered texture, shadow on the left, lit
    /// on the right. Facet UVs pick a side for the hard cel terminator.
    Material ToneMaterial(Color lit)
    {
        Color dark = new Color(lit.r * shadowStrength, lit.g * shadowStrength, lit.b * shadowStrength, 1f);
        dark = Color.Lerp(dark, dark * shadowTint, shadowTintAmount);
        dark.a = 1f;

        var tex = new Texture2D(2, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        tex.SetPixel(0, 0, dark);
        tex.SetPixel(1, 0, lit);
        tex.Apply();

        Material mat = new Material(UnlitTextureShader());
        SetTex(mat, tex);
        SetColor(mat, Color.white);
        return mat;
    }

    Material SolidMaterial(Color color)
    {
        Material mat = new Material(UnlitColorShader());
        SetColor(mat, color);
        return mat;
    }

    public static Shader UnlitTextureShader()
    {
        return FirstShader(
            "Unlit/Texture",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default");
    }

    public static Shader UnlitColorShader()
    {
        return FirstShader(
            "Unlit/Color",
            "Universal Render Pipeline/Unlit",
            "Sprites/Default");
    }

    static Shader FirstShader(params string[] names)
    {
        foreach (string n in names)
        {
            Shader s = Shader.Find(n);
            if (s != null) return s;
        }
        return Shader.Find("Sprites/Default");
    }

    static void SetTex(Material mat, Texture tex)
    {
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
    }

    static void SetColor(Material mat, Color c)
    {
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
    }

    // ---------------------------------------------------------------- meshes

    /// Faceted icosphere. Every triangle gets its own vertices so each facet reads as
    /// one flat tone instead of smooth-shaded.
    static Mesh MakeIcoSphere(int subdivisions, Vector3 light, float terminator)
    {
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;

        List<Vector3> baseVerts = new List<Vector3>
        {
            new Vector3(-1,  t, 0), new Vector3( 1,  t, 0),
            new Vector3(-1, -t, 0), new Vector3( 1, -t, 0),
            new Vector3( 0, -1,  t), new Vector3( 0,  1,  t),
            new Vector3( 0, -1, -t), new Vector3( 0,  1, -t),
            new Vector3( t,  0, -1), new Vector3( t,  0,  1),
            new Vector3(-t,  0, -1), new Vector3(-t,  0,  1)
        };

        for (int i = 0; i < baseVerts.Count; i++)
            baseVerts[i] = baseVerts[i].normalized * 0.5f;

        int[] baseTris =
        {
            0,11,5,  0,5,1,   0,1,7,   0,7,10,  0,10,11,
            1,5,9,   5,11,4,  11,10,2, 10,7,6,  7,1,8,
            3,9,4,   3,4,2,   3,2,6,   3,6,8,   3,8,9,
            4,9,5,   2,4,11,  6,2,10,  8,6,7,   9,8,1
        };

        List<Vector3> tris = new List<Vector3>();
        for (int i = 0; i < baseTris.Length; i += 3)
        {
            tris.Add(baseVerts[baseTris[i]]);
            tris.Add(baseVerts[baseTris[i + 1]]);
            tris.Add(baseVerts[baseTris[i + 2]]);
        }

        for (int s = 0; s < subdivisions; s++)
        {
            List<Vector3> next = new List<Vector3>(tris.Count * 4);

            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
                Vector3 ab = ((a + b) * 0.5f).normalized * 0.5f;
                Vector3 bc = ((b + c) * 0.5f).normalized * 0.5f;
                Vector3 ca = ((c + a) * 0.5f).normalized * 0.5f;

                next.Add(a);  next.Add(ab); next.Add(ca);
                next.Add(b);  next.Add(bc); next.Add(ab);
                next.Add(c);  next.Add(ca); next.Add(bc);
                next.Add(ab); next.Add(bc); next.Add(ca);
            }

            tris = next;
        }

        return BuildFacetedMesh("CelSphere", tris, light, terminator);
    }

    /// Elongated octahedron pointing down +Z, so LookRotation aims it along its travel.
    static Mesh MakeShard(float length, Vector3 light, float terminator)
    {
        float r = 0.5f;
        float half = 0.5f * Mathf.Max(1f, length);

        Vector3 tip = new Vector3(0f, 0f, half);
        Vector3 tail = new Vector3(0f, 0f, -half * 0.45f);

        Vector3[] ring =
        {
            new Vector3( r, 0f, 0f),
            new Vector3( 0f, r, 0f),
            new Vector3(-r, 0f, 0f),
            new Vector3( 0f,-r, 0f)
        };

        List<Vector3> tris = new List<Vector3>();
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = ring[i];
            Vector3 b = ring[(i + 1) % 4];

            tris.Add(tip); tris.Add(a); tris.Add(b);
            tris.Add(tail); tris.Add(b); tris.Add(a);
        }

        return BuildFacetedMesh("CelShard", tris, light, terminator);
    }

    /// Assigns each facet a UV of the shadow or lit pixel based on its face normal
    /// against the baked light.
    static Mesh BuildFacetedMesh(string name, List<Vector3> tris, Vector3 light, float terminator)
    {
        Vector3[] verts = new Vector3[tris.Count];
        Vector3[] normals = new Vector3[tris.Count];
        Vector2[] uvs = new Vector2[tris.Count];
        int[] indices = new int[tris.Count];

        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
            Vector3 faceNormal = Vector3.Cross(b - a, c - a).normalized;

            bool lit = Vector3.Dot(faceNormal, light) > terminator;
            Vector2 uv = lit ? new Vector2(0.75f, 0.5f) : new Vector2(0.25f, 0.5f);

            for (int k = 0; k < 3; k++)
            {
                verts[i + k] = tris[i + k];
                normals[i + k] = faceNormal;
                uvs[i + k] = uv;
                indices[i + k] = i + k;
            }
        }

        Mesh mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// Inverted hull: push every vertex out along its normal and flip the winding, so
    /// back-face culling shows only the inside of the shell as an outline.
    static Mesh MakeHull(Mesh src, float expand)
    {
        Vector3[] verts = src.vertices;
        Vector3[] normals = src.normals;
        int[] tris = src.triangles;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 dir = verts[i].sqrMagnitude > 0.0001f
                ? verts[i].normalized
                : normals[i];
            verts[i] += dir * expand * 0.5f;
        }

        int[] flipped = new int[tris.Length];
        for (int i = 0; i < tris.Length; i += 3)
        {
            flipped[i] = tris[i];
            flipped[i + 1] = tris[i + 2];
            flipped[i + 2] = tris[i + 1];
        }

        Mesh mesh = new Mesh { name = src.name + "_Hull", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(flipped, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------------------------------------------------------------- helpers

    static void Basis(Vector3 normal, out Vector3 t1, out Vector3 t2)
    {
        if (normal.sqrMagnitude < 0.001f) normal = Vector3.up;
        normal.Normalize();

        t1 = Vector3.Cross(normal, Vector3.up);
        if (t1.sqrMagnitude < 0.001f) t1 = Vector3.Cross(normal, Vector3.forward);
        t1.Normalize();

        t2 = Vector3.Cross(normal, t1).normalized;
    }
}