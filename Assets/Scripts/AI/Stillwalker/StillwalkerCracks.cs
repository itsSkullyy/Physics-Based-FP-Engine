using System.Collections.Generic;
using UnityEngine;

// Glowing red cracks on the Stillwalker so you can see how close it is to breaking.
// Each crack grows a jagged line from where it was hit, walking over the body capsule.
// More cracks = longer lines, brighter glow, faster pulse. One hit from death it
// splits all over and flickers. Cracks fade out when it heals.
// Added by Stillwalker at runtime, no setup needed.
public class StillwalkerCracks : MonoBehaviour
{
    [Header("Look")]
    public Color crackColor = new Color(1f, 0.12f, 0.05f, 1f);
    public float lineWidth = 0.035f;
    [Tooltip("Brightness at the first crack and one hit from death. Above 1 blooms if the camera has bloom.")]
    public float minGlow = 1.6f;
    public float maxGlow = 5f;
    [Tooltip("Pulses per second at the first crack and one hit from death.")]
    public float minPulse = 1.5f;
    public float maxPulse = 9f;
    public float healFadeTime = 0.5f;

    [Header("Shape")]
    public float segmentLength = 0.11f;
    [Tooltip("Segments in the first crack. Each crack after that adds this many more.")]
    public int baseSegments = 5;
    public int segmentsPerCrack = 3;
    [Range(0f, 90f)] public float wiggle = 38f;
    [Tooltip("Pushes the lines out from the collider so they sit on top of a model that's a bit wider.")]
    public float surfaceOffset = 0.02f;

    Transform bodyRoot;
    Transform lineParent;
    CapsuleCollider capsule;
    Material mat;
    readonly List<LineRenderer> lines = new List<LineRenderer>();

    int level;
    int maxLevel = 4;
    float flash;
    float fade = 1f;
    bool healing;

    public void Setup(Transform root, Transform parent, int cracksToShatter)
    {
        bodyRoot = root;
        lineParent = parent != null ? parent : root;
        capsule = root.GetComponent<CapsuleCollider>();
        maxLevel = Mathf.Max(2, cracksToShatter);
        mat = EnemyVisuals.Unlit(crackColor);
    }

    // ---------------------------------------------------------------- api

    public void AddCrack(Vector3 worldPoint, int newLevel)
    {
        if (bodyRoot == null)
        {
            return;
        }
        if (healing)
        {
            ClearLines();
        }

        level = newLevel;
        healing = false;
        fade = 1f;
        flash = 1f;

        Vector3 start = bodyRoot.InverseTransformPoint(worldPoint);
        if (!NearBody(start))
        {
            start = RandomSurfacePoint(0.45f, 0.85f);
        }

        int segments = baseSegments + segmentsPerCrack * (level - 1);
        Grow(start, segments, 2);

        // one hit from death: it splits everywhere
        if (level >= maxLevel - 1)
        {
            for (int i = 0; i < 4; i++)
            {
                Grow(RandomSurfacePoint(0.15f, 0.95f), segments, 1);
            }
        }
    }

    public void Heal()
    {
        if (lines.Count == 0)
        {
            return;
        }
        healing = true;
    }

    public void Clear()
    {
        ClearLines();
        level = 0;
        healing = false;
    }

    // ---------------------------------------------------------------- glow

    void LateUpdate()
    {
        if (lines.Count == 0 || mat == null)
        {
            return;
        }
        if (UpdateHealFade(Time.deltaTime))
        {
            return;
        }
        UpdateGlow(Time.deltaTime);
    }

    // cracks fade out once it starts healing. True once they're gone.
    bool UpdateHealFade(float dt)
    {
        if (!healing)
        {
            return false;
        }

        fade -= dt / Mathf.Max(0.01f, healFadeTime);
        if (fade > 0f)
        {
            return false;
        }

        Clear();
        return true;
    }

    // more cracks = brighter glow and a faster pulse, with a flicker at one hit from death
    void UpdateGlow(float dt)
    {
        float t = Mathf.InverseLerp(1f, maxLevel - 1, level);
        float pulseSpeed = Mathf.Lerp(minPulse, maxPulse, t);
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f);

        if (level >= maxLevel - 1 && Random.value < 0.08f)
        {
            pulse *= 0.4f;
        }

        float glow = Mathf.Lerp(minGlow, maxGlow, t) * pulse + flash * 4f;
        flash = Mathf.Max(0f, flash - dt * 5f);

        Color c = crackColor * glow * fade;
        c.a = 1f;
        EnemyVisuals.SetColor(mat, c);
    }

    // ---------------------------------------------------------------- building

    void Grow(Vector3 start, int segments, int branches)
    {
        Vector3 normal = NormalAt(start);
        Vector3 dir = Vector3.ProjectOnPlane(Random.onUnitSphere, normal).normalized;
        List<Vector3> main = Walk(start, dir, segments);
        AddLine(main, lineWidth);

        for (int b = 0; b < branches && main.Count > 3; b++)
        {
            int from = Random.Range(1, main.Count - 1);
            Vector3 along = main[from + 1] - main[from];
            Vector3 n = NormalAt(main[from]);
            Vector3 side = Quaternion.AngleAxis(Random.Range(35f, 70f) * (Random.value < 0.5f ? 1f : -1f), n) * along.normalized;
            AddLine(Walk(main[from], side, Mathf.Max(2, segments / 2)), lineWidth * 0.7f);
        }
    }

    // walks across the capsule surface in root local space
    List<Vector3> Walk(Vector3 start, Vector3 dir, int segments)
    {
        var points = new List<Vector3> { Project(start) };
        Vector3 p = points[0];

        for (int i = 0; i < segments; i++)
        {
            Vector3 n = NormalAt(p);
            dir = Quaternion.AngleAxis(Random.Range(-wiggle, wiggle), n) * Vector3.ProjectOnPlane(dir, n).normalized;
            Vector3 next = Project(p + dir * segmentLength * Random.Range(0.7f, 1.3f));
            dir = (next - p).normalized;
            p = next;
            points.Add(p);
        }
        return points;
    }

    void AddLine(List<Vector3> rootLocal, float width)
    {
        GameObject go = new GameObject("Crack");
        go.transform.SetParent(lineParent, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.sharedMaterial = mat;
        lr.startColor = lr.endColor = Color.white;
        lr.startWidth = width;
        lr.endWidth = width * 0.3f;
        lr.numCornerVertices = 1;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;

        lr.positionCount = rootLocal.Count;
        for (int i = 0; i < rootLocal.Count; i++)
        {
            lr.SetPosition(i, lineParent.InverseTransformPoint(bodyRoot.TransformPoint(rootLocal[i])));
        }

        lines.Add(lr);
    }

    void ClearLines()
    {
        foreach (LineRenderer lr in lines)
        {
            if (lr != null)
            {
                Destroy(lr.gameObject);
            }
        }
        lines.Clear();
    }

    void OnDestroy()
    {
        if (mat != null)
        {
            Destroy(mat);
        }
    }

    // ---------------------------------------------------------------- capsule maths (root local)

    float Radius => (capsule != null ? capsule.radius : 0.35f) + surfaceOffset;
    Vector3 Center => capsule != null ? capsule.center : Vector3.up;
    float HalfLine => capsule != null ? Mathf.Max(0f, capsule.height * 0.5f - capsule.radius) : 0.6f;

    Vector3 AxisPoint(Vector3 p)
    {
        float y = Mathf.Clamp(p.y - Center.y, -HalfLine, HalfLine);
        return Center + Vector3.up * y;
    }

    Vector3 NormalAt(Vector3 p)
    {
        Vector3 n = p - AxisPoint(p);
        return n.sqrMagnitude > 0.0001f ? n.normalized : Vector3.forward;
    }

    Vector3 Project(Vector3 p) => AxisPoint(p) + NormalAt(p) * Radius;

    bool NearBody(Vector3 p) => (p - AxisPoint(p)).magnitude < Radius + 0.5f;

    // height01 is from the bottom of the capsule to the top
    Vector3 RandomSurfacePoint(float minHeight01, float maxHeight01)
    {
        float total = HalfLine * 2f + Radius * 2f;
        float y = Center.y - total * 0.5f + total * Random.Range(minHeight01, maxHeight01);
        float a = Random.value * Mathf.PI * 2f;
        return Project(new Vector3(Center.x + Mathf.Cos(a), y, Center.z + Mathf.Sin(a)));
    }
}
