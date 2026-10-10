using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

// Points sampled over the NavMesh with low/high cover in 8 directions and how exposed
// each one is, plus every grapple point and wall-run wall. Built over a few frames when
// the level loads.
public class TacticalMap : MonoBehaviour
{
    public static TacticalMap Instance { get; private set; }

    [Header("Sampling")]
    public float spacing = 2.5f;
    public int maxPoints = 12000;

    [Header("Cover")]
    public float coverCheckDistance = 2f;
    public float lowCoverHeight = 0.9f;
    public float highCoverHeight = 1.7f;

    [Header("Exposure")]
    public int exposureRays = 12;
    public float exposureDistance = 25f;

    [Header("Build Budget")]
    public float msPerFrame = 3f;

    public class Point
    {
        public Vector3 position;
        public byte lowCover;
        public byte highCover;
        // 0 = boxed in, 1 = wide open
        public float exposure;
        public bool nearWallRun;
    }

    public bool Ready { get; private set; }
    public IReadOnlyList<Point> Points => points;
    public IReadOnlyList<Vector3> GrapplePoints => grapplePoints;
    public IReadOnlyList<Collider> WallRunWalls => wallRunWalls;

    const float CellSize = 8f;

    readonly List<Point> points = new List<Point>();
    readonly Dictionary<Vector2Int, List<Point>> cells = new Dictionary<Vector2Int, List<Point>>();
    readonly List<Vector3> grapplePoints = new List<Vector3>();
    readonly List<Collider> wallRunWalls = new List<Collider>();

    LayerMask wallRunMask;
    string wallRunTag = "";

    public static TacticalMap Get()
    {
        if (Instance != null)
        {
            return Instance;
        }

        Instance = FindFirstObjectByType<TacticalMap>();
        if (Instance != null)
        {
            return Instance;
        }

        GameObject go = new GameObject("TacticalMap");
        Instance = go.AddComponent<TacticalMap>();
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
        StartCoroutine(Build());
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ---------------------------------------------------------------- build

    IEnumerator Build()
    {
        yield return null;

        GatherRoutes();

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0)
        {
            UnityEngine.Debug.LogWarning("[TacticalMap] No NavMesh in this scene, leaders won't have a cover map.");
            Ready = true;
            yield break;
        }

        Stopwatch sw = Stopwatch.StartNew();
        HashSet<Vector3Int> taken = new HashSet<Vector3Int>();
        List<Vector3> candidates = new List<Vector3>();

        for (int t = 0; t + 2 < tri.indices.Length; t += 3)
        {
            SampleTriangle(tri.vertices[tri.indices[t]], tri.vertices[tri.indices[t + 1]],
                tri.vertices[tri.indices[t + 2]], candidates, taken);
            if (candidates.Count >= maxPoints)
            {
                break;
            }

            if (sw.Elapsed.TotalMilliseconds > msPerFrame)
            {
                yield return null;
                sw.Restart();
            }
        }

        foreach (Vector3 c in candidates)
        {
            Point p = Analyse(c);
            points.Add(p);

            Vector2Int key = CellOf(p.position);
            if (!cells.TryGetValue(key, out List<Point> list))
            {
                list = new List<Point>();
                cells[key] = list;
            }
            list.Add(p);

            if (sw.Elapsed.TotalMilliseconds > msPerFrame)
            {
                yield return null;
                sw.Restart();
            }
        }

        Ready = true;
    }

    // grid points inside the triangle, or its centre if it's tiny
    void SampleTriangle(Vector3 a, Vector3 b, Vector3 c, List<Vector3> into, HashSet<Vector3Int> taken)
    {
        float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
        float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
        float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));

        bool any = false;
        for (float x = Mathf.Ceil(minX / spacing) * spacing; x <= maxX; x += spacing)
        {
            for (float z = Mathf.Ceil(minZ / spacing) * spacing; z <= maxZ; z += spacing)
            {
                if (!InTriangleXZ(x, z, a, b, c, out float y))
                {
                    continue;
                }
                any = true;
                TryAdd(new Vector3(x, y, z), into, taken);
            }
        }

        if (!any)
        {
            TryAdd((a + b + c) / 3f, into, taken);
        }
    }

    void TryAdd(Vector3 p, List<Vector3> into, HashSet<Vector3Int> taken)
    {
        Vector3Int key = new Vector3Int(Mathf.RoundToInt(p.x / spacing), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z / spacing));
        if (taken.Add(key))
        {
            into.Add(p);
        }
    }

    static bool InTriangleXZ(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float y)
    {
        y = 0f;
        float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
        if (Mathf.Abs(d) < 1e-6f)
        {
            return false;
        }

        float w1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
        float w2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
        float w3 = 1f - w1 - w2;
        if (w1 < -0.001f || w2 < -0.001f || w3 < -0.001f)
        {
            return false;
        }

        y = w1 * a.y + w2 * b.y + w3 * c.y;
        return true;
    }

    Point Analyse(Vector3 pos)
    {
        Point p = new Point { position = pos };

        Vector3 low = pos + Vector3.up * lowCoverHeight;
        Vector3 high = pos + Vector3.up * highCoverHeight;
        for (int i = 0; i < 8; i++)
        {
            Vector3 dir = SectorDir(i);
            if (Blocked(low, dir, coverCheckDistance))
            {
                p.lowCover |= (byte)(1 << i);
            }
            if (Blocked(high, dir, coverCheckDistance))
            {
                p.highCover |= (byte)(1 << i);
            }
        }

        int open = 0;
        Vector3 eye = pos + Vector3.up * 1.6f;
        for (int i = 0; i < exposureRays; i++)
        {
            float a = i * Mathf.PI * 2f / exposureRays;
            if (!Blocked(eye, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)), exposureDistance))
            {
                open++;
            }
        }
        p.exposure = open / (float)exposureRays;

        if (wallRunMask.value != 0)
        {
            foreach (Collider c in Physics.OverlapSphere(pos + Vector3.up, 3f, wallRunMask, QueryTriggerInteraction.Ignore))
            {
                if (!string.IsNullOrEmpty(wallRunTag) && !c.CompareTag(wallRunTag))
                {
                    continue;
                }
                p.nearWallRun = true;
                break;
            }
        }

        return p;
    }

    // static geometry only
    static bool Blocked(Vector3 origin, Vector3 dir, float distance)
    {
        if (!Physics.Raycast(origin, dir, out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }
        return hit.rigidbody == null;
    }

    static Vector3 SectorDir(int i)
    {
        float a = i * 45f * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
    }

    void GatherRoutes()
    {
        grapplePoints.Clear();
        wallRunWalls.Clear();

        Grappling g = FindFirstObjectByType<Grappling>();
        FirstPersonCharacterController ctrl = FindFirstObjectByType<FirstPersonCharacterController>();

        if (ctrl != null)
        {
            wallRunMask = ctrl.wallRunMask;
            wallRunTag = ctrl.wallRunTag;
            if (wallRunMask.value == ~0 && string.IsNullOrEmpty(wallRunTag))
            {
                wallRunMask = 0;
            }
        }

        foreach (Collider c in FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (c.GetComponentInParent<EnemyAgent>() != null)
            {
                continue;
            }

            if (g != null && (g.grappleMask.value & (1 << c.gameObject.layer)) != 0
                && (string.IsNullOrEmpty(g.grappleTag) || c.CompareTag(g.grappleTag)))
            {
                grapplePoints.Add(c.bounds.center);
            }

            if (wallRunMask.value != 0 && (wallRunMask.value & (1 << c.gameObject.layer)) != 0
                && (string.IsNullOrEmpty(wallRunTag) || c.CompareTag(wallRunTag)) && !c.isTrigger)
            {
                wallRunWalls.Add(c);
            }
        }
    }

    // ---------------------------------------------------------------- queries

    static Vector2Int CellOf(Vector3 p) =>
        new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));

    public void Near(Vector3 center, float radius, List<Point> into)
    {
        if (!Ready)
        {
            return;
        }
        Vector2Int lo = CellOf(center - new Vector3(radius, 0f, radius));
        Vector2Int hi = CellOf(center + new Vector3(radius, 0f, radius));
        float r2 = radius * radius;

        for (int x = lo.x; x <= hi.x; x++)
        {
            for (int z = lo.y; z <= hi.y; z++)
            {
                if (!cells.TryGetValue(new Vector2Int(x, z), out List<Point> list))
                {
                    continue;
                }
                foreach (Point p in list)
                {
                    if ((p.position - center).sqrMagnitude <= r2)
                    {
                        into.Add(p);
                    }
                }
            }
        }
    }

    public Point Nearest(Vector3 pos, float maxDistance = 4f)
    {
        List<Point> near = new List<Point>();
        Near(pos, maxDistance, near);
        Point best = null;
        float bestD = float.MaxValue;
        foreach (Point p in near)
        {
            float d = (p.position - pos).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    // 1 = high cover facing the threat, 0.6 = low cover, 0.3 = off to the side
    public float CoverFrom(Point p, Vector3 threat)
    {
        Vector3 d = threat - p.position;
        d.y = 0f;
        if (d.sqrMagnitude < 0.01f)
        {
            return 0f;
        }

        float angle = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
        int sector = ((Mathf.RoundToInt(angle / 45f) % 8) + 8) % 8;

        if ((p.highCover & (1 << sector)) != 0)
        {
            return 1f;
        }
        if ((p.lowCover & (1 << sector)) != 0)
        {
            return 0.6f;
        }

        int left = (sector + 7) % 8, right = (sector + 1) % 8;
        if (((p.highCover >> left) & 1) != 0 || ((p.highCover >> right) & 1) != 0)
        {
            return 0.3f;
        }
        return 0f;
    }

    public float CoverAt(Vector3 pos, Vector3 threat)
    {
        Point p = Nearest(pos, 3f);
        return p != null ? CoverFrom(p, threat) : 0f;
    }

    // nearest covered point to a slot, falls back to the slot
    public Vector3 SnapToCover(Vector3 slot, float radius, Vector3 threat, bool needLineOfSight,
                               Func<Vector3, bool> reject = null, float eyeHeight = 1.5f)
    {
        List<Point> near = new List<Point>();
        Near(slot, radius, near);

        Point best = null;
        float bestScore = float.MinValue;
        foreach (Point p in near)
        {
            if (reject != null && reject(p.position))
            {
                continue;
            }
            if (needLineOfSight && !Clear(p.position + Vector3.up * eyeHeight, threat))
            {
                continue;
            }

            float score = CoverFrom(p, threat) * 1.5f + (1f - Vector3.Distance(p.position, slot) / radius);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        if (best != null)
        {
            return best.position;
        }
        return NavMesh.SamplePosition(slot, out NavMeshHit hit, radius + 2f, NavMesh.AllAreas) ? hit.position : slot;
    }

    static bool Clear(Vector3 from, Vector3 to)
    {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.01f)
        {
            return true;
        }
        foreach (RaycastHit h in Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.rigidbody == null)
            {
                return false;
            }
        }
        return true;
    }
}
