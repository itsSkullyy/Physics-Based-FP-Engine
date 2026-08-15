using UnityEngine;
using UnityEngine.InputSystem;

// A wall that stays solid until it takes a real hit, then bursts into a capped number of
// rigidbody shards. Three ways to break it:
//   1. Melee axe - BattleAxe.OnAxeHit calls SendMessageUpwards("OnAxeHit", point), which
//      lands here as OnAxeHit. No BattleAxe change needed.
//   2. Thrown axe - ThrownAxe reports its stick via SendMessageUpwards("OnThrownAxeStuck").
//   3. Running into it fast - a trigger child detects the player's speed and shatters,
//      bleeding off some of their velocity but letting them through.
//
// SETUP
//   - Put this on a wall with a MeshFilter + MeshRenderer and a Collider.
//   - The collider drives both the solid block and the run-through trigger, so a
//     BoxCollider (or any collider with sane bounds) is expected.
//   - Add the wall to the layers BattleAxe.hitMask / thrownStickMask / grappleMask see,
//     exactly as any other hittable surface.
//
// The fragment meshes are a shared grid of boxes carved from the wall's local bounds and
// built ONCE, so shattering is just spawning N cheap rigidbodies - no runtime mesh
// slicing, which is what usually tanks the framerate on effects like this.
[DisallowMultipleComponent]
public class BreakableWall : MonoBehaviour
{
    // Every wall registers itself so a single call can respawn them all. Survives the
    // wall being disabled (that is how a broken wall waits to be respawned), so the list
    // holds live and broken walls alike.
    static readonly System.Collections.Generic.List<BreakableWall> All =
        new System.Collections.Generic.List<BreakableWall>();

    /// Rebuilds every breakable wall in the scene and clears all loose shards. Wired to a
    /// debug key by BreakableWallDebug; call it yourself from a menu or console too.
    public static void RespawnAll()
    {
        // Kill every shard first, wherever it rolled to.
        WallShard[] shards = FindObjectsByType<WallShard>(FindObjectsSortMode.None);
        foreach (WallShard s in shards)
            if (s != null) Destroy(s.gameObject);

        foreach (BreakableWall w in All)
            if (w != null) w.Respawn();
    }

    // Key the debug watcher listens for. Static so the watcher (which is a separate always
    // -on object) can read it without needing a live wall instance.
    static Key debugRespawnKey = Key.B;
    public static Key DebugRespawnKey => debugRespawnKey;

    // A broken wall deactivates itself, and a deactivated component's Update never runs -
    // so if every wall is broken, no wall could hear the respawn key. This one-off object
    // lives forever and independently, so B works no matter how many walls are down.
    static BreakableWallDebug debugWatcher;

    static void EnsureDebugWatcher()
    {
        if (debugWatcher != null) return;
        GameObject go = new GameObject("BreakableWallDebug");
        DontDestroyOnLoad(go);
        debugWatcher = go.AddComponent<BreakableWallDebug>();
    }
    [Header("Shatter Grid")]
    [Tooltip("Fragments along each local axis. Keep the product modest - this IS the " +
             "shard count and every shard is a rigidbody. A z of 2+ gives the pieces real " +
             "depth so they read as chunks, not slabs.")]
    public Vector3Int cuts = new Vector3Int(4, 4, 2);
    [Tooltip("Hard ceiling on live shards regardless of the grid, so an oversized wall " +
             "cannot spawn hundreds of bodies at once.")]
    public int maxShards = 40;
    [Tooltip("Random per-shard size jitter so the break does not read as a clean grid.")]
    [Range(0f, 0.4f)] public float sizeJitter = 0.15f;

    [Header("Shard Physics")]
    public float shardMass = 0.6f;
    [Tooltip("Outward push from the break point.")]
    public float burstForce = 5.5f;
    [Tooltip("Extra push along the hit direction, so the side you struck blows inward.")]
    public float directionalForce = 4f;
    public float spinForce = 6f;
    public float shardLifetime = 6f;
    public float shardFadeTime = 1.2f;
    public PhysicsMaterial shardPhysicsMaterial;
    [Tooltip("Layer for the shards. Set to the layer your axe's stickMask includes and a " +
             "thrown axe can embed in a flying piece. -1 keeps the wall's own layer.")]
    public int shardLayer = -1;

    [Header("Run-Through")]
    public bool breakOnHighSpeed = true;
    [Tooltip("Player must be moving at least this fast to smash through.")]
    public float runThroughSpeed = 14f;
    [Tooltip("Fraction of the player's speed kept after bursting through. 0.6 = lose 40%.")]
    [Range(0f, 1f)] public float speedKeptOnRunThrough = 0.6f;
    public string playerTag = "Player";

    [Header("Impact Frame")]
    public bool triggerImpactFrame = true;
    [Tooltip("Melee and thrown hits get the full freeze. Run-through gets a lighter one.")]
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

    // Prebuilt shard meshes and their local placements, shared across all fragments.
    Mesh[] shardMeshes;
    Vector3[] shardLocalCenters;
    Vector3[] shardLocalSizes;

    RunThroughProbe probe;

    // Captured once so Respawn can put the husk back exactly as it started.
    Vector3 spawnPos;
    Quaternion spawnRot;

    [Header("Debug Respawn")]
    [Tooltip("Hard-coded key that rebuilds every breakable wall and clears all shards. " +
             "Placeholder for testing - swap for a real binding or remove for shipping.")]
    public Key respawnKey = Key.B;

    void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    void OnDisable()
    {
        // Stay in the registry while broken (we are disabled, not destroyed) so Respawn
        // can still find us. Only drop out on actual destruction, handled in OnDestroy.
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

        // Push this wall's chosen key to the static watcher, and make sure the watcher
        // exists. The watcher, not this component, listens for the key - it keeps running
        // even after every wall has broken and deactivated itself.
        debugRespawnKey = respawnKey;
        EnsureDebugWatcher();

        shardMaterial = wallRenderer.sharedMaterial;

        // Local-space bounds from the mesh, so the grid is independent of world rotation.
        MeshFilter mf = GetComponent<MeshFilter>();
        localBounds = mf != null && mf.sharedMesh != null
            ? mf.sharedMesh.bounds
            : InverseTransformBounds(wallRenderer.bounds);

        BuildShardGrid();

        if (breakOnHighSpeed)
            SetupRunThroughProbe();
    }

    // Puts a broken husk back: re-enable its renderer, collider and run-through probe,
    // reset the shatter latch, and drop it at its recorded spawn transform. A wall that
    // never broke is left as-is.
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

    // Melee. Matches BattleAxe.OnAxeHit -> SendMessageUpwards("OnAxeHit", point).
    void OnAxeHit(Vector3 point)
    {
        Vector3 dir = (transform.position - point).normalized;
        Shatter(point, dir, HitKind.Melee);
    }

    // Thrown. ThrownAxe sends this after it embeds (see the ThrownAxe edit).
    void OnThrownAxeStuck(Vector3 point)
    {
        // The axe parented itself to this wall when it stuck. We are about to destroy the
        // wall, so free the axe first - otherwise it dies as our child. Once unparented it
        // is left where it stuck; it falls under its own weight, or the player recalls it,
        // or it catches on a shard it happens to overlap. Exactly the brief's behaviour.
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

    // Called by the trigger probe when the player crosses it fast enough.
    public void OnRunThrough(Rigidbody playerBody, Vector3 contactPoint)
    {
        if (shattered) return;

        Vector3 vel = playerBody.linearVelocity;
        Vector3 dir = vel.sqrMagnitude > 0.01f ? vel.normalized : -transform.forward;

        // Bleed off some speed but keep them moving through the gap.
        playerBody.linearVelocity = vel * speedKeptOnRunThrough;

        Shatter(contactPoint, dir, HitKind.RunThrough);
    }

    enum HitKind { Melee, Thrown, RunThrough }

    // ---------------------------------------------------------------- shatter

    void Shatter(Vector3 worldPoint, Vector3 worldDir, HitKind kind)
    {
        if (shattered) return;
        shattered = true;

        // Turn the solid wall off first so shards spawn into empty space.
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

        // The husk has no more work to do this life. It is DEACTIVATED rather than
        // destroyed so RespawnAll can bring it back - it stays in the registry while off.
        // Shards are parentless, so they live on regardless.
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

        // Burst: radial from the hit point, plus a shove along the hit direction so the
        // struck face caves the right way. Closer shards get thrown harder.
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

    // Carves the local bounds into a grid of boxes ONCE. Each cell becomes a shared mesh
    // and a local placement. Nothing here runs at shatter time.
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
                    // Mesh is centred on the shard's own origin; placement handles position.
                    shardMeshes[idx] = BuildShardMesh(size, idx * 7919 + 13);
                    idx++;
                }
            }
        }
    }

    // An irregular convex chunk instead of a clean box, so the wall reads as shattering
    // into angular glass shards. Eight corners of a box, each pulled inward by a random
    // amount toward a jittered interior point, then two opposite corners yanked out to a
    // sharp spike. MeshCollider convexity cleans up whatever hull this produces, so the
    // exact vertex soup does not have to be watertight.
    static Mesh BuildShardMesh(Vector3 size, int seed)
    {
        System.Random rng = new System.Random(seed);
        float R() => (float)rng.NextDouble();

        Vector3 h = size * 0.5f;

        // Interior bias point - corners pull toward this, which skews the whole shard so
        // it is not centred or symmetric.
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
            // Pull each corner a random fraction toward the bias point (0.15..0.6),
            // giving uneven faces and sharp angles.
            float pull = 0.15f + R() * 0.45f;
            corners[i] = Vector3.Lerp(corners[i], bias, pull);
        }

        // Yank one corner out into a point - the "tip" of the shard - and nudge its
        // opposite in, so the piece tapers like a real spall fragment.
        int tip = rng.Next(8);
        corners[tip] += (corners[tip] - bias).normalized * size.magnitude * 0.28f;
        int opp = 7 - tip;
        corners[opp] = Vector3.Lerp(corners[opp], bias, 0.5f);

        // Same 12-triangle box topology; the displaced corners make every face a
        // different irregular quad. Normals are recalculated so lighting stays correct.
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

    // The wall's own collider is solid, so a fast player would bounce off it before we
    // could react. A slightly fattened trigger sitting in front catches them first.
    void SetupRunThroughProbe()
    {
        GameObject probeGo = new GameObject("RunThroughProbe");
        probeGo.transform.SetParent(transform, false);
        probeGo.layer = gameObject.layer;

        BoxCollider trigger = probeGo.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = localBounds.center;
        // A touch thicker than the wall so a fast body trips it before hitting the solid.
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

// Tiny always-on listener for the debug respawn key. Spawned once by BreakableWall and
// kept alive across the whole session, so pressing the key still works after every wall
// has broken and deactivated itself (a deactivated wall's own Update would never run).
// Debug scaffolding - delete this and the EnsureDebugWatcher call for shipping.
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