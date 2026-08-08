using UnityEngine;

// Thrown axe. Simulates its own arc and sweeps a CONTINUOUS chain of segments along the
// axe head's real path (plus the pivot's path) every physics step, so there is never a
// gap for it to slip through no matter how fast it flies or spins.
[RequireComponent(typeof(Rigidbody))]
public class ThrownAxe : MonoBehaviour
{
    [Header("Head")]
    [Tooltip("Empty placed at the blade. If empty, the head is guessed from the model bounds.")]
    public Transform headPoint;
    public Vector3 headLocalOffset = new Vector3(0f, 0f, 0.25f);
    public bool autoFitHead = true;

    [Header("Flight")]
    public float gravityScale = 1f;
    public float spinSpeed = 1440f;             // degrees per second, 4 rotations a second
    public bool scaleSpinWithThrowSpeed = true;
    public float spinReferenceSpeed = 26f;
    [Range(0.25f, 3f)] public float minSpinScale = 0.7f;
    [Range(0.25f, 3f)] public float maxSpinScale = 1.7f;
    public float maxLifetime = 30f;
    public float despawnBelowY = -200f;

    [Header("Sweeping")]
    public float sweepRadius = 0.08f;           // fatness of the head probe
    public bool sweepBody = true;
    public float bodySweepRadius = 0.06f;       // fatness of the pivot/handle probe
    public float maxStepDistance = 0.35f;       // shorter = more substeps = tighter arc
    public int maxSubSteps = 8;
    public float skinWidth = 0.02f;

    [Header("Stick")]
    public LayerMask stickMask = ~0;
    [Tooltip("Let the axe embed in trigger colliders. Turn this ON if your grappleable " +
             "props use trigger colliders, or the axe will fly straight through them.")]
    public bool stickToTriggers = false;
    public float stickDepth = 0.12f;
    [Range(0f, 1f)] public float stickNormalBlend = 0.55f;
    public Vector3 stickEulerOffset = Vector3.zero;
    public bool levelRoll = true;
    public bool parentToSurface = true;

    [Header("Impact Juice")]
    public float impactShake = 0.4f;
    public float impactShakeFullRange = 5f;
    public float impactShakeMaxRange = 30f;
    public float impactReferenceSpeed = 26f;
    public float stickWobbleAngle = 7f;
    public float stickWobbleFrequency = 26f;
    public float stickWobbleDamp = 7f;
    public float stickWobbleDuration = 0.6f;

    [Header("Trail")]
    public bool leaveTrail = true;
    public Color trailColor = new Color(1f, 1f, 1f, 0.5f);
    public float trailTime = 0.22f;
    public float trailStartWidth = 0.06f;
    public float trailEndWidth = 0f;
    public Material trailMaterial;

    [Header("Grapple")]
    public bool becomesGrappable = true;
    public string grappleLayerName = "Grappleable";
    public string grappleTag = "";
    public float grappleRadius = 0.6f;
    public bool addGrappleTrigger = true;

    Rigidbody rb;
    Collider[] ownColliders;
    Collider[] ignored;
    TrailRenderer trail;

    bool launched;
    bool stuck;
    float age;
    Vector3 velocity;
    Vector3 spinAxis = Vector3.right;
    float activeSpin;
    Vector3 lastHeadPos;

    Quaternion stuckLocalRot;
    Vector3 wobbleAxisLocal = Vector3.right;
    float wobbleTimer;

    public bool IsStuck => stuck;
    public Vector3 StickPoint => transform.position;
    public Vector3 HeadPosition => transform.position + HeadWorldOffset(transform.rotation);

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    public void Launch(Vector3 launchVelocity, float spinDegreesPerSecond, Collider[] ignoreColliders)
    {
        Launch(launchVelocity, spinDegreesPerSecond, ignoreColliders, transform.position);
    }

    // originPoint should be where the throw came FROM (the camera). The gap between there
    // and the spawn point gets swept too, so throwing while hugging a wall still lands.
    public void Launch(Vector3 launchVelocity, float spinDegreesPerSecond,
                       Collider[] ignoreColliders, Vector3 originPoint)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();

        velocity = launchVelocity;
        spinSpeed = spinDegreesPerSecond;
        ignored = ignoreColliders;
        age = 0f;

        ownColliders = GetComponentsInChildren<Collider>(true);

        // The axe never physically collides - it only uses its own sweep - so the
        // colliders stay off until it sticks and needs to be grapple-targetable.
        foreach (Collider c in ownColliders)
            if (c != null) c.enabled = false;

        if (headPoint != null)
            headLocalOffset = transform.InverseTransformPoint(headPoint.position);
        else if (autoFitHead)
            FitHeadOffset();

        activeSpin = spinSpeed;
        if (scaleSpinWithThrowSpeed && spinReferenceSpeed > 0.01f)
        {
            float scale = Mathf.Clamp(velocity.magnitude / spinReferenceSpeed, minSpinScale, maxSpinScale);
            activeSpin *= scale;
        }

        spinAxis = ComputeSpinAxis(velocity);

        rb.isKinematic = true;
        rb.position = transform.position;
        rb.rotation = transform.rotation;
        lastHeadPos = transform.position + HeadWorldOffset(transform.rotation);

        if (leaveTrail) CreateTrail();

        launched = true;

        // Close the gap between the thrower and the spawn point.
        if (SweepSegment(originPoint, lastHeadPos, sweepRadius, out RaycastHit spawnHit, out Vector3 spawnDir))
            Stick(spawnHit, spawnDir);
    }

    void FixedUpdate()
    {
        if (!launched || stuck) return;

        float dt = Time.fixedDeltaTime;
        age += dt;

        if ((maxLifetime > 0f && age > maxLifetime) || rb.position.y < despawnBelowY)
        {
            Destroy(gameObject);
            return;
        }

        velocity += Physics.gravity * (gravityScale * dt);

        Vector3 pos = rb.position;
        Quaternion rot = rb.rotation;

        float travel = velocity.magnitude * dt;
        int steps = Mathf.Clamp(
            Mathf.CeilToInt(travel / Mathf.Max(0.05f, maxStepDistance)), 1, Mathf.Max(1, maxSubSteps));
        float sdt = dt / steps;

        for (int i = 0; i < steps; i++)
        {
            Vector3 nextPos = pos + velocity * sdt;

            spinAxis = ComputeSpinAxis(velocity);
            Quaternion nextRot = Quaternion.AngleAxis(activeSpin * sdt, spinAxis) * rot;

            // The head orbits the pivot while it spins, so its true path is an arc.
            // Chaining each segment from the LAST head position to the NEXT one -
            // both using their own rotations - leaves no gap between steps. That gap
            // was what let it clip through thin geometry.
            Vector3 nextHead = nextPos + HeadWorldOffset(nextRot);

            if (SweepSegment(lastHeadPos, nextHead, sweepRadius,
                    out RaycastHit headHit, out Vector3 headDir))
            {
                rb.position = pos;
                rb.rotation = rot;
                Stick(headHit, headDir);
                return;
            }

            if (sweepBody && SweepSegment(pos, nextPos, bodySweepRadius,
                    out RaycastHit bodyHit, out Vector3 bodyDir))
            {
                rb.position = pos;
                rb.rotation = rot;
                Stick(bodyHit, bodyDir);
                return;
            }

            pos = nextPos;
            rot = nextRot;
            lastHeadPos = nextHead;
        }

        rb.MovePosition(pos);
        rb.MoveRotation(rot);
    }

    void Update()
    {
        if (!stuck || wobbleTimer <= 0f) return;

        wobbleTimer -= Time.deltaTime;
        float elapsed = stickWobbleDuration - Mathf.Max(0f, wobbleTimer);

        float amp = stickWobbleAngle * Mathf.Exp(-stickWobbleDamp * elapsed);
        float angle = Mathf.Sin(elapsed * stickWobbleFrequency) * amp;

        transform.localRotation = stuckLocalRot * Quaternion.AngleAxis(angle, wobbleAxisLocal);

        if (wobbleTimer <= 0f)
            transform.localRotation = stuckLocalRot;
    }

    // ---------------------------------------------------------------- sweeping

    bool SweepSegment(Vector3 from, Vector3 to, float radius, out RaycastHit best, out Vector3 dir)
    {
        best = default;
        dir = Vector3.forward;

        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.0001f) return false;

        dir = delta / dist;
        float castDist = dist + skinWidth;

        QueryTriggerInteraction qti = stickToTriggers
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        bool found = false;
        float bestDist = float.MaxValue;

        // Fat probe first so glancing hits on edges still register.
        if (radius > 0.001f)
        {
            RaycastHit[] sphereHits = Physics.SphereCastAll(from, radius, dir, castDist, stickMask, qti);
            found = PickBest(sphereHits, ref best, ref bestDist);
        }

        // Thin probe as a backstop: catches the case where the sphere started already
        // overlapping something and reported a degenerate zero-distance hit.
        RaycastHit[] rayHits = Physics.RaycastAll(from, dir, castDist, stickMask, qti);
        found |= PickBest(rayHits, ref best, ref bestDist);

        if (!found) return false;

        if (best.normal.sqrMagnitude < 0.001f)
        {
            best.normal = -dir;
            best.point = from;
        }

        return true;
    }

    bool PickBest(RaycastHit[] hits, ref RaycastHit best, ref float bestDist)
    {
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (h.collider == null) continue;
            if (IsSelf(h.collider) || IsIgnored(h.collider)) continue;
            if (h.distance >= bestDist) continue;

            best = h;
            bestDist = h.distance;
            found = true;
        }

        return found;
    }

    bool IsSelf(Collider c)
    {
        if (c.transform == transform || c.transform.IsChildOf(transform)) return true;

        if (ownColliders != null)
            for (int i = 0; i < ownColliders.Length; i++)
                if (ownColliders[i] == c) return true;

        return false;
    }

    bool IsIgnored(Collider c)
    {
        if (ignored == null) return false;

        for (int i = 0; i < ignored.Length; i++)
            if (ignored[i] == c) return true;

        return false;
    }

    // ---------------------------------------------------------------- sticking

    void Stick(RaycastHit hit, Vector3 travelDir)
    {
        stuck = true;
        launched = false;

        float impactSpeed = velocity.magnitude;
        velocity = Vector3.zero;

        Vector3 embed = Vector3.Lerp(travelDir, -hit.normal, stickNormalBlend);
        if (embed.sqrMagnitude < 0.001f) embed = -hit.normal;
        embed.Normalize();

        // Rotate whatever axis the head actually sits on so it points into the surface.
        // Works regardless of how the model is oriented.
        Vector3 headDirLocal = headLocalOffset.sqrMagnitude > 0.0001f
            ? headLocalOffset.normalized
            : Vector3.forward;

        Vector3 headDirWorld = rb.rotation * headDirLocal;
        Quaternion aligned = Quaternion.FromToRotation(headDirWorld, embed) * rb.rotation;

        if (levelRoll)
        {
            Vector3 desiredUp = Vector3.ProjectOnPlane(Vector3.up, embed);
            if (desiredUp.sqrMagnitude > 0.001f)
            {
                Vector3 currentUp = Vector3.ProjectOnPlane(aligned * Vector3.up, embed);
                if (currentUp.sqrMagnitude > 0.001f)
                    aligned = Quaternion.FromToRotation(currentUp, desiredUp) * aligned;
            }
        }

        aligned *= Quaternion.Euler(stickEulerOffset);

        // Place the pivot so the HEAD lands buried at the hit point, not the origin.
        Vector3 headTarget = hit.point + embed * stickDepth;
        Vector3 finalPos = headTarget - HeadWorldOffset(aligned);

        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
        transform.SetPositionAndRotation(finalPos, aligned);
        rb.position = finalPos;
        rb.rotation = aligned;

        if (parentToSurface && hit.collider != null && hit.collider.attachedRigidbody == null)
            transform.SetParent(hit.collider.transform, true);

        // Judder, bending around the axis perpendicular to the buried head.
        wobbleAxisLocal = Vector3.Cross(headDirLocal, Vector3.up);
        if (wobbleAxisLocal.sqrMagnitude < 0.001f)
            wobbleAxisLocal = Vector3.Cross(headDirLocal, Vector3.forward);
        wobbleAxisLocal = wobbleAxisLocal.sqrMagnitude > 0.001f
            ? wobbleAxisLocal.normalized
            : Vector3.right;

        stuckLocalRot = transform.localRotation;
        wobbleTimer = stickWobbleDuration;

        if (trail != null)
            trail.emitting = false;

        // Impact juice, scaled by how hard it was travelling.
        float force = Mathf.Clamp01(impactSpeed / Mathf.Max(1f, impactReferenceSpeed));

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null)
            fx.ImpactBurst(hit.point, hit.normal, Mathf.Lerp(0.35f, 1f, force));

        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTraumaAtPoint(hit.point, impactShake * force,
                impactShakeFullRange, impactShakeMaxRange);

        if (becomesGrappable)
            MakeGrappable();
    }

    void MakeGrappable()
    {
        // Colliders come back on as triggers: solid enough for the grapple's overlap
        // search to find, invisible to the player's capsule and to the grapple's
        // line-of-sight raycast (which ignores triggers).
        if (ownColliders != null)
        {
            foreach (Collider c in ownColliders)
            {
                if (c == null) continue;
                c.enabled = true;
                c.isTrigger = true;
            }
        }

        if (addGrappleTrigger)
        {
            SphereCollider grab = gameObject.AddComponent<SphereCollider>();
            grab.isTrigger = true;
            grab.center = headLocalOffset;
            grab.radius = grappleRadius / Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        }

        if (!string.IsNullOrEmpty(grappleLayerName))
        {
            int layer = LayerMask.NameToLayer(grappleLayerName);
            if (layer >= 0)
                SetLayerRecursive(gameObject, layer);
            else
                Debug.LogWarning("ThrownAxe: layer '" + grappleLayerName + "' does not exist. " +
                                 "Create it or clear grappleLayerName.", this);
        }

        if (!string.IsNullOrEmpty(grappleTag))
            gameObject.tag = grappleTag;
    }

    // ---------------------------------------------------------------- trail

    void CreateTrail()
    {
        GameObject go = new GameObject("AxeTrail");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = headLocalOffset;

        trail = go.AddComponent<TrailRenderer>();
        trail.time = trailTime;
        trail.numCapVertices = 0;      // hard corners read better than soft ones
        trail.numCornerVertices = 0;
        trail.alignment = LineAlignment.View;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.receiveShadows = false;
        trail.autodestruct = false;

        // Flat colour that tapers to a point by WIDTH, not by alpha, so it stays a
        // solid cel-shaded ribbon instead of a soft sprite smear.
        Color solid = new Color(trailColor.r, trailColor.g, trailColor.b, 1f);
        trail.startColor = solid;
        trail.endColor = solid;
        trail.widthCurve = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(0.55f, 0.6f),
            new Keyframe(1f, 0f));
        trail.startWidth = trailStartWidth;
        trail.endWidth = trailEndWidth;

        Material mat = trailMaterial;
        if (mat == null)
        {
            Shader shader = JuiceFX.UnlitColorShader();
            if (shader != null)
            {
                mat = new Material(shader);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", solid);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", solid);
            }
        }
        if (mat != null) trail.material = mat;
    }

    // ---------------------------------------------------------------- helpers

    Vector3 HeadWorldOffset(Quaternion rot)
    {
        return rot * Vector3.Scale(headLocalOffset, transform.lossyScale);
    }

    static Vector3 ComputeSpinAxis(Vector3 vel)
    {
        Vector3 flat = new Vector3(vel.x, 0f, vel.z);
        if (flat.sqrMagnitude < 0.01f) return Vector3.right;

        Vector3 axis = Vector3.Cross(Vector3.up, flat.normalized);
        return axis.sqrMagnitude > 0.001f ? axis.normalized : Vector3.right;
    }

    // Guesses the blade position from the model's longest axis. Assigning headPoint
    // by hand is always better than this.
    void FitHeadOffset()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        Bounds local = new Bounds();
        bool init = false;

        foreach (Renderer r in renderers)
        {
            Bounds b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);

                Vector3 p = transform.InverseTransformPoint(corner);
                if (!init) { local = new Bounds(p, Vector3.zero); init = true; }
                else local.Encapsulate(p);
            }
        }

        if (!init) return;

        Vector3 c = local.center;
        Vector3 e = local.extents;

        if (e.z >= e.x && e.z >= e.y)
            headLocalOffset = new Vector3(c.x, c.y, FarEnd(c.z, e.z));
        else if (e.y >= e.x)
            headLocalOffset = new Vector3(c.x, FarEnd(c.y, e.y), c.z);
        else
            headLocalOffset = new Vector3(FarEnd(c.x, e.x), c.y, c.z);
    }

    static float FarEnd(float center, float extent)
    {
        float plus = center + extent;
        float minus = center - extent;
        return Mathf.Abs(plus) >= Mathf.Abs(minus) ? plus : minus;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursive(child.gameObject, layer);
    }

    void OnDrawGizmosSelected()
    {
        Vector3 head = Application.isPlaying
            ? HeadPosition
            : transform.position + transform.rotation * Vector3.Scale(headLocalOffset, transform.lossyScale);

        Gizmos.color = stuck ? Color.green : Color.yellow;
        Gizmos.DrawWireSphere(head, sweepRadius);
        Gizmos.DrawLine(transform.position, head);

        if (sweepBody)
        {
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, bodySweepRadius);
        }

        Gizmos.color = new Color(0.3f, 1f, 0.6f, 0.4f);
        Gizmos.DrawWireSphere(head, grappleRadius);
    }
}