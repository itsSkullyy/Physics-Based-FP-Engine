using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// EQS. Generate points, run tests, pick the best. A negative test score throws the
// point out, otherwise the score is the weighted average of the tests.
// EQSService spreads queries over frames.
public class EQSItem
{
    public Vector3 Point;
    public UnityEngine.Object Context;
    public float Score;
    public bool Valid = true;
}

public abstract class EQSGenerator
{
    public abstract void Generate(List<EQSItem> items);
}

public abstract class EQSTest
{
    public string Name;
    public float Weight = 1f;

    public bool FilterOnly;

    // negative = discard, otherwise 0..1
    public abstract float Run(EQSItem item);

    // ------------------------------------------------------------ built-in tests

    public static EQSTest OnNavMesh(float maxSnap = 1.5f) => new FuncTest("OnNavMesh", true, item =>
    {
        if (!NavMesh.SamplePosition(item.Point, out NavMeshHit hit, maxSnap, NavMesh.AllAreas))
            return -1f;
        item.Point = hit.position;
        return 1f;
    });

    public static EQSTest Distance(Func<Vector3> to, float min, float max, AnimationCurve curve) =>
        new FuncTest("Distance", false, item =>
        {
            float d = Vector3.Distance(item.Point, to());
            if (d < min || d > max) return -1f;
            float t = Mathf.InverseLerp(min, max, d);
            return curve != null ? Mathf.Clamp01(curve.Evaluate(t)) : t;
        });

    // wantVisible = false makes it a hiding test
    public static EQSTest LineOfSight(Func<Vector3> to, float eyeHeight = 1.5f, bool wantVisible = true,
                                      Func<Collider, bool> ignore = null, bool filter = true) =>
        new FuncTest("LineOfSight", filter, item =>
        {
            Vector3 eye = item.Point + Vector3.up * eyeHeight;
            Vector3 target = to();
            bool visible = Clear(eye, target, ignore);
            return visible == wantVisible ? 1f : (filter ? -1f : 0f);
        });

    public static EQSTest Dot(Func<Vector3> origin, Func<Vector3> direction, AnimationCurve curve) =>
        new FuncTest("Dot", false, item =>
        {
            Vector3 to = item.Point - origin();
            to.y = 0f;
            Vector3 dir = direction();
            dir.y = 0f;
            if (to.sqrMagnitude < 0.001f || dir.sqrMagnitude < 0.001f) return 0.5f;
            float d = Vector3.Dot(to.normalized, dir.normalized);
            float t = (d + 1f) * 0.5f;
            return curve != null ? Mathf.Clamp01(curve.Evaluate(t)) : t;
        });

    public static EQSTest NearCover(float radius = 2f, float height = 1f, int rays = 8) =>
        new FuncTest("NearCover", false, item =>
        {
            Vector3 o = item.Point + Vector3.up * height;
            float best = radius;
            for (int i = 0; i < rays; i++)
            {
                float a = i * Mathf.PI * 2f / rays;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                if (Physics.Raycast(o, dir, out RaycastHit hit, radius, ~0, QueryTriggerInteraction.Ignore)
                    && hit.rigidbody == null)
                    best = Mathf.Min(best, hit.distance);
            }
            return best >= radius ? 0f : 1f - best / radius;
        });

    public static EQSTest Custom(string name, Func<EQSItem, float> run, bool filterOnly = false) =>
        new FuncTest(name, filterOnly, run);

    public static bool Clear(Vector3 from, Vector3 to, Func<Collider, bool> ignore = null)
    {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.01f) return true;

        RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit h in hits)
        {
            if (ignore != null && ignore(h.collider)) continue;
            return false;
        }
        return true;
    }

    class FuncTest : EQSTest
    {
        readonly Func<EQSItem, float> run;

        public FuncTest(string name, bool filterOnly, Func<EQSItem, float> run)
        {
            Name = name;
            FilterOnly = filterOnly;
            this.run = run;
        }

        public override float Run(EQSItem item) => run(item);
    }
}

public static class EQSGen
{
    public static EQSGenerator Ring(Func<Vector3> center, float minRadius, float maxRadius, int rings, int perRing) =>
        new FuncGen(items =>
        {
            Vector3 c = center();
            float offset = UnityEngine.Random.value * Mathf.PI * 2f;
            for (int r = 0; r < rings; r++)
            {
                float radius = rings > 1 ? Mathf.Lerp(minRadius, maxRadius, r / (float)(rings - 1)) : maxRadius;
                for (int i = 0; i < perRing; i++)
                {
                    float a = offset + i * Mathf.PI * 2f / perRing + r * 0.5f;
                    items.Add(new EQSItem { Point = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius });
                }
            }
        });

    public static EQSGenerator Grid(Func<Vector3> center, float halfExtent, float spacing) =>
        new FuncGen(items =>
        {
            Vector3 c = center();
            for (float x = -halfExtent; x <= halfExtent; x += spacing)
                for (float z = -halfExtent; z <= halfExtent; z += spacing)
                    items.Add(new EQSItem { Point = c + new Vector3(x, 0f, z) });
        });

    public static EQSGenerator From(Action<List<EQSItem>> fill) => new FuncGen(fill);

    class FuncGen : EQSGenerator
    {
        readonly Action<List<EQSItem>> fill;
        public FuncGen(Action<List<EQSItem>> fill) { this.fill = fill; }
        public override void Generate(List<EQSItem> items) => fill(items);
    }
}

public class EQSQuery
{
    public readonly string Name;
    public readonly List<EQSItem> LastItems = new List<EQSItem>();
    public EQSItem LastBest { get; private set; }
    public float LastRunTime { get; private set; } = -999f;

    readonly EQSGenerator generator;
    readonly List<EQSTest> tests = new List<EQSTest>();

    public EQSQuery(string name, EQSGenerator generator)
    {
        Name = name;
        this.generator = generator;
    }

    public EQSQuery Add(EQSTest test, float weight = 1f)
    {
        test.Weight = weight;
        tests.Add(test);
        return this;
    }

    public EQSItem Run()
    {
        LastItems.Clear();
        generator.Generate(LastItems);
        LastRunTime = Time.time;

        EQSItem best = null;

        foreach (EQSItem item in LastItems)
        {
            float total = 0f;
            float weights = 0f;
            item.Valid = true;

            foreach (EQSTest t in tests)
            {
                float s = t.Run(item);
                if (s < 0f)
                {
                    item.Valid = false;
                    break;
                }
                if (t.FilterOnly) continue;

                total += s * t.Weight;
                weights += t.Weight;
            }

            if (!item.Valid)
            {
                item.Score = 0f;
                continue;
            }

            item.Score = weights > 0f ? total / weights : 1f;
            if (best == null || item.Score > best.Score) best = item;
        }

        LastBest = best;
        return best;
    }

    // best few points at least minSpacing apart
    public List<EQSItem> Best(int count, float minSpacing)
    {
        List<EQSItem> sorted = new List<EQSItem>();
        foreach (EQSItem i in LastItems) if (i.Valid) sorted.Add(i);
        sorted.Sort((a, b) => b.Score.CompareTo(a.Score));

        List<EQSItem> picked = new List<EQSItem>();
        foreach (EQSItem i in sorted)
        {
            bool tooClose = false;
            foreach (EQSItem p in picked)
                if (Vector3.Distance(i.Point, p.Point) < minSpacing) { tooClose = true; break; }
            if (tooClose) continue;

            picked.Add(i);
            if (picked.Count >= count) break;
        }
        return picked;
    }
}

// Runs a few queries per frame. Re-asking while queued just swaps the callback.
public class EQSService : MonoBehaviour
{
    public static EQSService Instance { get; private set; }

    [Tooltip("Upper limit on queries run per frame across every enemy.")]
    public int maxQueriesPerFrame = 3;

    readonly Queue<EQSQuery> queue = new Queue<EQSQuery>();
    readonly Dictionary<EQSQuery, Action<EQSItem>> pending = new Dictionary<EQSQuery, Action<EQSItem>>();

    public static EQSService Get()
    {
        if (Instance != null) return Instance;

        Instance = FindFirstObjectByType<EQSService>();
        if (Instance != null) return Instance;

        GameObject go = new GameObject("EQSService");
        Instance = go.AddComponent<EQSService>();
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
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public bool IsPending(EQSQuery q) => pending.ContainsKey(q);

    public void Enqueue(EQSQuery query, Action<EQSItem> done)
    {
        if (query == null) return;
        if (pending.ContainsKey(query))
        {
            pending[query] = done;
            return;
        }
        pending[query] = done;
        queue.Enqueue(query);
    }

    void Update()
    {
        int budget = Mathf.Max(1, maxQueriesPerFrame);
        while (budget-- > 0 && queue.Count > 0)
        {
            EQSQuery q = queue.Dequeue();
            pending.TryGetValue(q, out Action<EQSItem> done);
            pending.Remove(q);

            EQSItem best = q.Run();
            done?.Invoke(best);
        }
    }
}
