using UnityEngine;

// Thrown axe. Simulates its own arc and sweeps a continuous chain of segments along the
// axe head's real path (plus the pivot's path) every physics step, so it never has a
// gap to slip through no matter how fast it flies or spins.
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
    public float spinSpeed = 2160f;             // degrees per second
    public bool scaleSpinWithThrowSpeed = true;
    public float spinReferenceSpeed = 42f;
    [Range(0.25f, 3f)] public float minSpinScale = 0.7f;
    [Range(0.25f, 3f)] public float maxSpinScale = 1.7f;
    public float maxLifetime = 30f;
    public float despawnBelowY = -200f;

    [Header("Sweeping")]
    public float sweepRadius = 0.08f;
    public bool sweepBody = true;
    public float bodySweepRadius = 0.06f;
    public float maxStepDistance = 0.22f;
    public int maxSubSteps = 14;
    public float skinWidth = 0.02f;

    [Header("Stick")]
    public LayerMask stickMask = ~0;
    [Tooltip("Let the axe embed in trigger colliders. Turn on if grappleable props use trigger colliders.")]
    public bool stickToTriggers = false;
    public float stickDepth = 0.12f;
    [Range(0f, 1f)] public float stickNormalBlend = 0.55f;
    public Vector3 stickEulerOffset = Vector3.zero;
    public bool levelRoll = true;
    public bool parentToSurface = true;

    [Header("Loose On Ground")]
    [Tooltip("A loose axe stops being a grapple anchor, so a zip pulls it back to you instead of pulling you to it.")]
    public bool looseStopsBeingGrappleTarget = true;

    int originalLayer;
    string originalTag;
    SphereCollider grappleTrigger;

    [Header("Impact Juice")]
    public float impactShake = 0.4f;
    public float impactShakeFullRange = 5f;
    public float impactShakeMaxRange = 30f;
    public float impactReferenceSpeed = 42f;
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

    [Header("Recall")]
    [Tooltip("Seconds the axe must stay stuck before it can be recalled.")]
    public float recallCooldown = 3f;
    public float recallMaxSpeed = 34f;
    public float recallAcceleration = 120f;
    public float recallTurnRate = 14f;
    public float recallSpin = 1440f;
    public float recallCatchDistance = 0.6f;
    [Header("Recall Avoidance")]
    public bool recallAvoidObstacles = true;
    public LayerMask recallObstacleMask = ~0;
    public float recallProbeDistance = 3f;
    public float recallProbeRadius = 0.35f;
    public float recallAvoidStrength = 26f;
    [Tooltip("If the axe makes no headway toward you for this long, it gives up and re-embeds where it is.")]
    public float recallNoProgressTimeout = 1.75f;

    Rigidbody rb;
    Collider[] ownColliders;
    Collider[] ignored;
    TrailRenderer trail;

    bool launched;
    bool stuck;
    bool dropped;
    bool recalling;
    float age;
    float stuckTimer;
    Vector3 velocity;
    Vector3 spinAxis = Vector3.right;
    float activeSpin;
    Vector3 lastHeadPos;

    Transform recallTarget;
    System.Func<Vector3> recallTargetPoint;
    float recallCatchRadius;
    System.Action onRecallCaught;
    float recallBestDist;
    float recallStuckTimer;

    Quaternion stuckLocalRot;
    Vector3 wobbleAxisLocal = Vector3.right;
    float wobbleTimer;

    public bool IsStuck => stuck;
    public bool IsRecalling => recalling;
    public Vector3 StickPoint => transform.position;
    public Vector3 HeadPosition => transform.position + HeadWorldOffset(transform.rotation);

    public float StuckTime => stuck ? stuckTimer : 0f;
    public float RecallCooldownProgress =>
        stuck && recallCooldown > 0.01f ? Mathf.Clamp01(stuckTimer / recallCooldown) : (stuck ? 1f : 0f);
    public bool RecallReady => stuck && !recalling && stuckTimer >= recallCooldown;

    void Awake()
    {
        originalLayer = gameObject.layer;
        originalTag = gameObject.tag;

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

    // originPoint should be where the throw came FROM (the camera), so the gap between
    // there and the spawn point gets swept too.
    public void Launch(Vector3 launchVelocity, float spinDegreesPerSecond,
                       Collider[] ignoreColliders, Vector3 originPoint)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();

        velocity = launchVelocity;
        spinSpeed = spinDegreesPerSecond;
        ignored = ignoreColliders;
        age = 0f;

        ownColliders = GetComponentsInChildren<Collider>(true);

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

        if (SweepSegment(originPoint, lastHeadPos, sweepRadius, out RaycastHit spawnHit, out Vector3 spawnDir))
            Stick(spawnHit, spawnDir);
    }

    void FixedUpdate()
    {
        if (recalling)
        {
            HandleRecall(Time.fixedDeltaTime);
            return;
        }

        if (stuck)
        {
            stuckTimer += Time.fixedDeltaTime;
            return;
        }

        if (!launched) return;

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
            // Chaining each segment from the last head position to the next one keeps
            // there from being a gap between steps.
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

    // ---------------------------------------------------------------- recall

    /// Begin flying back to a target. Returns false if it is not ready (still on
    /// cooldown, not stuck, or already recalling). targetPoint lets the caller aim at a
    /// moving point like the player's chest; pass null to home on the transform's origin.
    /// onCaught fires when the axe reaches the catch radius.
    public bool Recall(Transform target, float catchRadius, System.Action onCaught,
                       System.Func<Vector3> targetPoint = null)
    {
        if (target == null) return false;
        if (!RecallReady) return false;

        recallTarget = target;
        recallTargetPoint = targetPoint;
        recallCatchRadius = Mathf.Max(0f, catchRadius);
        onRecallCaught = onCaught;

        stuck = false;
        recalling = true;
        wobbleTimer = 0f;
        recallBestDist = float.MaxValue;
        recallStuckTimer = 0f;

        transform.SetParent(null, true);

        if (rb == null) rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        if (ownColliders != null)
            foreach (Collider c in ownColliders)
                if (c != null) c.enabled = false;

        velocity = (RecallAimPoint() - HeadPosition).normalized * (recallMaxSpeed * 0.25f);

        if (trail != null) trail.emitting = true;

        rb.useGravity = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        return true;
    }

    Vector3 RecallAimPoint()
    {
        if (recallTargetPoint != null) return recallTargetPoint();
        return recallTarget != null ? recallTarget.position : transform.position;
    }

    void HandleRecall(float dt)
    {
        if (recallTarget == null)
        {
            recalling = false;
            stuck = true;
            return;
        }

        Vector3 aim = RecallAimPoint();
        Vector3 head = HeadPosition;
        Vector3 toTarget = aim - head;
        float dist = toTarget.magnitude;

        if (dist <= recallCatchRadius + recallCatchDistance)
        {
            recalling = false;
            onRecallCaught?.Invoke();
            return;
        }

        // Progress watchdog: if the axe is genuinely walled off from the player it would
        // otherwise slide along that wall forever. Track the closest it has ever got; if
        // it spends too long making no headway, give up and re-embed where it is.
        if (dist < recallBestDist - 0.05f)
        {
            recallBestDist = dist;
            recallStuckTimer = 0f;
        }
        else
        {
            recallStuckTimer += dt;
            if (recallStuckTimer >= recallNoProgressTimeout)
            {
                recalling = false;
                stuck = true;
                stuckTimer = 0f;
                velocity = Vector3.zero;
                MakeGrappable();
                return;
            }
        }

        Vector3 desiredDir = dist > 0.001f ? toTarget / dist : transform.forward;

        // Steer the heading around obstacles so the flight curves toward gaps instead of
        // charging into a wall; the actual movement below is separately collision-swept.
        if (recallAvoidObstacles)
        {
            float probe = Mathf.Min(recallProbeDistance, dist);
            if (Physics.SphereCast(head, recallProbeRadius, desiredDir,
                    out RaycastHit obst, probe, recallObstacleMask, QueryTriggerInteraction.Ignore)
                && !IsSelf(obst.collider) && !IsIgnored(obst.collider))
            {
                Vector3 along = Vector3.ProjectOnPlane(desiredDir, obst.normal).normalized;
                float closeness = 1f - Mathf.Clamp01(obst.distance / Mathf.Max(0.01f, probe));
                desiredDir = Vector3.Lerp(desiredDir, along + obst.normal * 0.5f,
                    closeness).normalized;
                velocity += obst.normal * recallAvoidStrength * closeness * dt;
            }
        }

        Vector3 desiredVel = desiredDir * recallMaxSpeed;
        velocity = Vector3.MoveTowards(velocity, desiredVel, recallAcceleration * dt);
        if (velocity.magnitude > recallMaxSpeed)
            velocity = velocity.normalized * recallMaxSpeed;

        // Collision-swept movement: the axe is kinematic with its colliders off, so we
        // sweep a sphere along the intended step and slide along whatever it hits.
        // Iterated a couple of times so an inside corner is handled in one frame.
        Vector3 pos = rb.position;
        Vector3 remaining = velocity * dt;

        for (int iter = 0; iter < 3 && remaining.sqrMagnitude > 1e-8f; iter++)
        {
            float stepLen = remaining.magnitude;
            Vector3 stepDir = remaining / stepLen;

            if (Physics.SphereCast(pos, recallProbeRadius, stepDir, out RaycastHit hit,
                    stepLen + skinWidth, recallObstacleMask, QueryTriggerInteraction.Ignore)
                && !IsSelf(hit.collider) && !IsIgnored(hit.collider))
            {
                float travel = Mathf.Max(0f, hit.distance - skinWidth);
                pos += stepDir * travel;

                remaining = Vector3.ProjectOnPlane(remaining - stepDir * travel, hit.normal);
                velocity = Vector3.ProjectOnPlane(velocity, hit.normal);
            }
            else
            {
                pos += remaining;
                break;
            }
        }

        spinAxis = ComputeSpinAxis(velocity);
        Quaternion spinStep = Quaternion.AngleAxis(recallSpin * dt, spinAxis);
        Quaternion faceFlight = Quaternion.Slerp(rb.rotation,
            Quaternion.LookRotation(velocity.sqrMagnitude > 0.001f ? velocity.normalized : desiredDir, Vector3.up),
            1f - Mathf.Exp(-recallTurnRate * dt));
        Quaternion nextRot = spinStep * faceFlight;

        rb.MovePosition(pos);
        rb.MoveRotation(nextRot);
    }

    /// Hard stop of a recall. Leaves the axe floating in place, not stuck.
    public void CancelRecall()
    {
        if (!recalling) return;
        recalling = false;
        velocity = Vector3.zero;
    }

    public bool IsLoose => dropped && !recalling;

    /// Skips the embed cooldown. Used by the zip pull, which is its own gate.
    public void ForceRecallReady()
    {
        if (recallCooldown > 0f) stuckTimer = Mathf.Max(stuckTimer, recallCooldown);
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

        if (radius > 0.001f)
        {
            RaycastHit[] sphereHits = Physics.SphereCastAll(from, radius, dir, castDist, stickMask, qti);
            found = PickBest(sphereHits, ref best, ref bestDist);
        }

        // Thin probe backstop: catches the case where the sphere started already
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
        stuckTimer = 0f;
        recalling = false;

        float impactSpeed = velocity.magnitude;
        velocity = Vector3.zero;

        Vector3 embed = Vector3.Lerp(travelDir, -hit.normal, stickNormalBlend);
        if (embed.sqrMagnitude < 0.001f) embed = -hit.normal;
        embed.Normalize();

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

        Vector3 headTarget = hit.point + embed * stickDepth;
        Vector3 finalPos = headTarget - HeadWorldOffset(aligned);

        // Grab the collider we hit before reparenting, so a wall that destroys itself
        // in response still gives us a valid reference this frame.
        Collider hitCollider = hit.collider;

        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
        transform.SetPositionAndRotation(finalPos, aligned);
        rb.position = finalPos;
        rb.rotation = aligned;

        if (parentToSurface && hitCollider != null && hitCollider.attachedRigidbody == null)
            transform.SetParent(hitCollider.transform, true);

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

        float force = Mathf.Clamp01(impactSpeed / Mathf.Max(1f, impactReferenceSpeed));

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null)
            fx.ImpactBurst(hit.point, hit.normal, Mathf.Lerp(0.35f, 1f, force));

        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTraumaAtPoint(hit.point, impactShake * force,
                impactShakeFullRange, impactShakeMaxRange);

        // A BreakableWall listens for this and shatters; anything else ignores it. Sent
        // after our own juice so a wall's shatter feedback layers on top.
        if (hitCollider != null)
            hitCollider.SendMessageUpwards("OnThrownAxeStuck", hit.point,
                SendMessageOptions.DontRequireReceiver);

        if (becomesGrappable)
            MakeGrappable();
    }

    // Called when the surface the axe stuck into is destroyed out from under it. The
    // axe is already unparented by the caller; here it stops being a frozen kinematic
    // prop and falls under gravity as a loose body, staying grappleable and recallable.
    public void DropFromSurface()
    {
        if (!stuck || dropped) return;
        dropped = true;

        wobbleTimer = 0f;

        if (rb == null) rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            rb.linearVelocity = Vector3.down * 1.5f + Random.insideUnitSphere * 0.6f;
            rb.angularVelocity = Random.insideUnitSphere * 2f;

            if (looseStopsBeingGrappleTarget) RemoveGrappleTargeting();
        }

        void RemoveGrappleTargeting()
        {
            if (grappleTrigger != null) { Destroy(grappleTrigger); grappleTrigger = null; }

            SetLayerRecursive(gameObject, originalLayer);

            if (!string.IsNullOrEmpty(grappleTag) && gameObject.CompareTag(grappleTag))
                gameObject.tag = string.IsNullOrEmpty(originalTag) ? "Untagged" : originalTag;
        }

        // The grapple trigger colliders were turned into triggers when it stuck; make
        // the real ones solid again so it can land, but leave the added grapple sphere
        // (if any) as a trigger.
        if (ownColliders != null)
        {
            foreach (Collider c in ownColliders)
            {
                if (c == null) continue;
                c.enabled = true;
                c.isTrigger = false;
            }
        }
    }

    // Idempotent: safe to call again (e.g. when a gave-up recall re-embeds the axe in
    // place) without undoing the solid/loose state a dropped axe is already in.
    void MakeGrappable()
    {
        if (!dropped)
        {
            if (ownColliders != null)
            {
                foreach (Collider c in ownColliders)
                {
                    if (c == null) continue;
                    c.enabled = true;
                    c.isTrigger = true;
                }
            }

            if (addGrappleTrigger && GetComponent<SphereCollider>() == null)
            {
                grappleTrigger = gameObject.AddComponent<SphereCollider>();
                grappleTrigger.isTrigger = true;
                grappleTrigger.center = headLocalOffset;
                grappleTrigger.radius = grappleRadius / Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
            }
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
        trail.numCapVertices = 0;
        trail.numCornerVertices = 0;
        trail.alignment = LineAlignment.View;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.receiveShadows = false;
        trail.autodestruct = false;

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
