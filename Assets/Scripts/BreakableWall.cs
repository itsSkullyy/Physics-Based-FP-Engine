using UnityEngine;
using UnityEngine.InputSystem;

// A wall that stays solid until it takes a real hit, then bursts into a capped number of
// rigidbody shards. Breaks from a melee hit, a thrown axe stick, or running into it fast.
// Put this on a wall with a MeshFilter + MeshRenderer and a Collider.
[DisallowMultipleComponent]
public class BreakableWall : MonoBehaviour
{
    static readonly System.Collections.Generic.List<BreakableWall> All =
        new System.Collections.Generic.List<BreakableWall>();

    /// Rebuilds every breakable wall in the scene and clears all loose shards.
    public static void RespawnAll()
    {
        WallShard[] shards = FindObjectsByType<WallShard>(FindObjectsSortMode.None);
        foreach (WallShard s in shards)
            if (s != null) Destroy(s.gameObject);

        foreach (BreakableWall w in All)
            if (w != null) w.Respawn();
    }

    static Key debugRespawnKey = Key.B;
    public static Key DebugRespawnKey => debugRespawnKey;

    static BreakableWallDebug debugWatcher;

    static void EnsureDebugWatcher()
    {
        if (debugWatcher != null) return;
        GameObject go = new GameObject("BreakableWallDebug");
        DontDestroyOnLoad(go);
        debugWatcher = go.AddComponent<BreakableWallDebug>();
    }
    [Header("Shatter Grid")]
    [Tooltip("Fragments along each local axis. Keep the product modest - every shard is a rigidbody.")]
    public Vector3Int cuts = new Vector3Int(4, 4, 2);
    [Tooltip("Hard ceiling on live shards regardless of the grid.")]
    public int maxShards = 40;
    [Tooltip("Random per-shard size jitter so the break does not read as a clean grid.")]
    [Range(0f, 0.4f)] public float sizeJitter = 0.15f;

    [Header("Shard Physics")]
    public float shardMass = 0.6f;
    public float burstForce = 5.5f;
    public float directionalForce = 4f;
    public float spinForce = 6f;
    public float shardLifetime = 6f;
    public float shardFadeTime = 1.2f;
    public PhysicsMaterial shardPhysicsMaterial;
    [Tooltip("Layer for the shards. -1 keeps the wall's own layer.")]
    public int shardLayer = -1;

    [Header("Run-Through")]
    public bool breakOnHighSpeed = true;
    public float runThroughSpeed = 14f;
    [Range(0f, 1f)] public float speedKeptOnRunThrough = 0.6f;
    public string playerTag = "Player";

    [Header("Impact Frame")]
    public bool triggerImpactFrame = true;
    public float meleeFreeze = 0.07f;
    public float meleeOverlay = 0.16f;
    public float runThroughFreeze = 0.03f;
    public float runThroughOverlay = 0.1f;

    [Header("Juice")]
    public bool useJuiceFX = true;
    public float shatterShake = 0.55f;
    public float shatterShakeFullRange = 7f;
    public float shatterShakeMaxRange = 35f;

    [Header("Debug")]
    public bool logDebug = false;

    Renderer wallRenderer;
    Collider wallCollider;
    Material shardMaterial;
    Bounds localBounds;
    bool shattered;

    Mesh[] shardMeshes;
    Vector3[] shardLocalCenters;
    Vector3[] shardLocalSizes;

    RunThroughProbe probe;

    Vector3 spawnPos;
    Quaternion spawnRot;

    [Header("Debug Respawn")]
    [Tooltip("Rebuilds every breakable wall and clears all shards. Testing only.")]
    public Key respawnKey = Key.B;

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
    }

    void OnDestroy()
    {
        All.Remove(this);
    }

    void Awake()
    {
        wallRenderer = GetComponent<Renderer>();
        wallCollider = GetComponent<Collider>();

        if (wallRenderer == null || wallCollider == null)
        {
            Debug.LogError("BreakableWall needs a Renderer and a Collider.", this);
            enabled = false;
            return;
        }

        spawnPos = transform.position;
        spawnRot = transform.rotation;

        debugRespawnKey = respawnKey;
        EnsureDebugWatcher();

        shardMaterial = wallRenderer.sharedMaterial;

        MeshFilter mf = GetComponent<MeshFilter>();
        localBounds = mf != null && mf.sharedMesh != null
            ? mf.sharedMesh.bounds
            : InverseTransformBounds(wallRenderer.bounds);

        BuildShardGrid();

        if (breakOnHighSpeed)
            SetupRunThroughProbe();
    }

    public void Respawn()
    {
        transform.SetPositionAndRotation(spawnPos, spawnRot);

        shattered = false;

        if (wallCollider != null) wallCollider.enabled = true;
        if (wallRenderer != null) wallRenderer.enabled = true;
        if (probe != null) probe.gameObject.SetActive(true);

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    // ---------------------------------------------------------------- hit entry points

    void OnAxeHit(Vector3 point)
    {
        Vector3 dir = (transform.position - point).normalized;
        Shatter(point, dir, HitKind.Melee);
    }

    void OnThrownAxeStuck(Vector3 point)
    {
        DetachStuckAxe();

        Vector3 dir = (transform.position - point).normalized;
        Shatter(point, dir, HitKind.Thrown);
    }

    void DetachStuckAxe()
    {
        ThrownAxe[] children = GetComponentsInChildren<ThrownAxe>(true);
        foreach (ThrownAxe axe in children)
        {
            if (axe == null) continue;
            axe.transform.SetParent(null, true);
            axe.DropFromSurface();
        }
    }

    public void OnRunThrough(Rigidbody playerBody, Vector3 contactPoint)
    {
        if (shattered) return;

        Vector3 vel = playerBody.linearVelocity;
        Vector3 dir = vel.sqrMagnitude > 0.01f ? vel.normalized : -transform.forward;

        playerBody.linearVelocity = vel * speedKeptOnRunThrough;

        Shatter(contactPoint, dir, HitKind.RunThrough);
    }

    enum HitKind { Melee, Thrown, RunThrough }

    // ---------------------------------------------------------------- shatter

    void Shatter(Vector3 worldPoint, Vector3 worldDir, HitKind kind)
    {
        if (shattered) return;
        shattered = true;

        wallCollider.enabled = false;
        wallRenderer.enabled = false;

        int layer = shardLayer >= 0 ? shardLayer : gameObject.layer;
        Vector3 localHit = transform.InverseTransformPoint(worldPoint);

        int spawned = 0;
        for (int i = 0; i < shardMeshes.Length && spawned < maxShards; i++)
        {
            SpawnShard(i, worldPoint, worldDir, localHit, layer);
            spawned++;
        }

        DoImpactFeedback(worldPoint, kind);

        if (logDebug)
            Debug.Log($"[BreakableWall] Shattered by {kind} into {spawned} shards.", this);

        if (probe != null) probe.gameObject.SetActive(false);
        gameObject.SetActive(false);
    }

    void SpawnShard(int i, Vector3 worldPoint, Vector3 worldDir, Vector3 localHit, int layer)
    {
        Vector3 worldCenter = transform.TransformPoint(shardLocalCenters[i]);
        Quaternion worldRot = transform.rotation;
        Vector3 worldScale = transform.lossyScale;

        GameObject go = new GameObject("Shard");
        go.layer = layer;
        go.transform.SetParent(null, false);

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = shardMass;

        Vector3 fromHit = worldCenter - worldPoint;
        float dist = fromHit.magnitude;
        Vector3 radial = dist > 0.001f ? fromHit / dist : Random.onUnitSphere;
        float proximity = 1f - Mathf.Clamp01(dist / Mathf.Max(0.1f, localBounds.size.magnitude));

        Vector3 impulse = radial * burstForce * (0.5f + proximity)
                        + worldDir * directionalForce * (0.4f + proximity)
                        + Random.insideUnitSphere * burstForce * 0.2f;

        Vector3 torque = Random.insideUnitSphere * spinForce;

        WallShard shard = go.AddComponent<WallShard>();
        shard.Init(shardMeshes[i], shardMaterial, worldCenter, worldRot, worldScale,
            impulse, torque, shardLifetime, shardFadeTime, shardPhysicsMaterial);
    }

    void DoImpactFeedback(Vector3 worldPoint, HitKind kind)
    {
        if (useJuiceFX)
        {
            JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
            if (fx != null)
                fx.ImpactBurst(worldPoint, -transform.forward, kind == HitKind.RunThrough ? 0.8f : 1f);
        }

        if (CameraShaker.Instance != null)
        {
            float amt = kind == HitKind.RunThrough ? shatterShake * 0.7f : shatterShake;
            CameraShaker.Instance.AddTraumaAtPoint(worldPoint, amt,
                shatterShakeFullRange, shatterShakeMaxRange);
        }

        if (triggerImpactFrame)
        {
            ImpactFrames frames = ImpactFrames.Get();
            frames.SetImpactPoint(worldPoint);
            if (kind == HitKind.RunThrough)
                frames.Freeze(runThroughFreeze, runThroughOverlay, 0.8f);
            else
                frames.Freeze(meleeFreeze, meleeOverlay, 1f);
        }
    }

    // ---------------------------------------------------------------- grid build

    void BuildShardGrid()
    {
        int cx = Mathf.Max(1, cuts.x);
        int cy = Mathf.Max(1, cuts.y);
        int cz = Mathf.Max(1, cuts.z);

        int total = cx * cy * cz;
        int count = Mathf.Min(total, maxShards);

        shardMeshes = new Mesh[count];
        shardLocalCenters = new Vector3[count];
        shardLocalSizes = new Vector3[count];

        Vector3 min = localBounds.min;
        Vector3 cell = new Vector3(
            localBounds.size.x / cx,
            localBounds.size.y / cy,
            localBounds.size.z / cz);

        int idx = 0;
        for (int x = 0; x < cx && idx < count; x++)
        {
            for (int y = 0; y < cy && idx < count; y++)
            {
                for (int z = 0; z < cz && idx < count; z++)
                {
                    Vector3 jitter = new Vector3(
                        1f + Random.Range(-sizeJitter, sizeJitter),
                        1f + Random.Range(-sizeJitter, sizeJitter),
                        1f + Random.Range(-sizeJitter, sizeJitter));

                    Vector3 size = Vector3.Scale(cell, jitter);
                    Vector3 center = new Vector3(
                        min.x + cell.x * (x + 0.5f),
                        min.y + cell.y * (y + 0.5f),
                        min.z + cell.z * (z + 0.5f));

                    shardLocalCenters[idx] = center;
                    shardLocalSizes[idx] = size;
                    shardMeshes[idx] = BuildShardMesh(size, idx * 7919 + 13);
                    idx++;
                }
            }
        }
    }

    // Irregular convex chunk instead of a clean box, so the break reads as angular shards.
    static Mesh BuildShardMesh(Vector3 size, int seed)
    {
        System.Random rng = new System.Random(seed);
        float R() => (float)rng.NextDouble();

        Vector3 h = size * 0.5f;

        Vector3 bias = new Vector3(
            (R() - 0.5f) * size.x * 0.5f,
            (R() - 0.5f) * size.y * 0.5f,
            (R() - 0.5f) * size.z * 0.5f);

        Vector3[] corners =
        {
            new Vector3(-h.x, -h.y, -h.z), new Vector3( h.x, -h.y, -h.z),
            new Vector3( h.x,  h.y, -h.z), new Vector3(-h.x,  h.y, -h.z),
            new Vector3(-h.x, -h.y,  h.z), new Vector3( h.x, -h.y,  h.z),
            new Vector3( h.x,  h.y,  h.z), new Vector3(-h.x,  h.y,  h.z)
        };

        for (int i = 0; i < 8; i++)
        {
            float pull = 0.15f + R() * 0.45f;
            corners[i] = Vector3.Lerp(corners[i], bias, pull);
        }

        int tip = rng.Next(8);
        corners[tip] += (corners[tip] - bias).normalized * size.magnitude * 0.28f;
        int opp = 7 - tip;
        corners[opp] = Vector3.Lerp(corners[opp], bias, 0.5f);

        int[] t =
        {
            0,2,1, 0,3,2,
            5,6,4, 4,6,7,
            4,7,0, 0,7,3,
            1,2,5, 5,2,6,
            3,7,2, 2,7,6,
            4,0,5, 5,0,1
        };

        Mesh mesh = new Mesh { name = "Shard" };
        mesh.vertices = corners;
        mesh.triangles = t;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.hideFlags = HideFlags.HideAndDontSave;
        return mesh;
    }

    // ---------------------------------------------------------------- run-through probe

    void SetupRunThroughProbe()
    {
        GameObject probeGo = new GameObject("RunThroughProbe");
        probeGo.transform.SetParent(transform, false);
        probeGo.layer = gameObject.layer;

        BoxCollider trigger = probeGo.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = localBounds.center;
        trigger.size = localBounds.size + Vector3.one * 0.3f;

        probe = probeGo.AddComponent<RunThroughProbe>();
        probe.Init(this, runThroughSpeed, playerTag);
    }

    // ---------------------------------------------------------------- helpers

    Bounds InverseTransformBounds(Bounds world)
    {
        Vector3 localCenter = transform.InverseTransformPoint(world.center);
        Vector3 localSize = transform.InverseTransformVector(world.size);
        return new Bounds(localCenter, new Vector3(
            Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z)));
    }

    void OnDrawGizmosSelected()
    {
        Renderer r = GetComponent<Renderer>();
        MeshFilter mf = GetComponent<MeshFilter>();
        if (r == null) return;

        Bounds lb = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds();

        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.matrix = transform.localToWorldMatrix;

        int cx = Mathf.Max(1, cuts.x);
        int cy = Mathf.Max(1, cuts.y);
        int cz = Mathf.Max(1, cuts.z);

        Vector3 cell = new Vector3(lb.size.x / cx, lb.size.y / cy, lb.size.z / cz);
        Vector3 min = lb.min;

        for (int x = 0; x < cx; x++)
            for (int y = 0; y < cy; y++)
                for (int z = 0; z < cz; z++)
                {
                    Vector3 c = new Vector3(
                        min.x + cell.x * (x + 0.5f),
                        min.y + cell.y * (y + 0.5f),
                        min.z + cell.z * (z + 0.5f));
                    Gizmos.DrawWireCube(c, cell * 0.92f);
                }
    }
}

public class BreakableWallDebug : MonoBehaviour
{
    void Update()
    {
        Key k = BreakableWall.DebugRespawnKey;
        if (k != Key.None && Keyboard.current != null &&
            Keyboard.current[k].wasPressedThisFrame)
            BreakableWall.RespawnAll();
    }
}
