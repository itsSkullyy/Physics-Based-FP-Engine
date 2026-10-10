using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

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
    public float stickDepth = 0.12f;
    [Range(0f, 1f)] public float stickNormalBlend = 0.55f;
    public Vector3 stickEulerOffset = Vector3.zero;
    public bool levelRoll = true;
    [Tooltip("Keep the axe locked to whatever it hit, so it rides along with moving enemies and platforms.")]
    [FormerlySerializedAs("parentToSurface")]
    public bool followSurface = true;

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
    Matrix4x4 portalTravel = Matrix4x4.identity;
    // Scene whose portals the axe can fly through: the player's when thrown, then whichever
    // scene each portal it goes through comes out in. Every loaded scene shares one world,
    // so without this a portal from another scene that happens to overlap the throw (the
    // level's portals while you're in the tutorial) would carry the axe off into it.
    string space;
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

    // The axe isn't parented to what it hits. Parenting breaks on rigidbodies, shears
    // under non-uniformly scaled primitives and makes the axe count as part of the
    // enemy for grapple/sight checks. Instead it remembers its pose relative to the
    // collider and copies it every frame. stuckLocalRot is world space when there is
    // no surface to follow.
    Collider surfaceCollider;
    bool hasSurface;
    Vector3 stuckLocalPos;
    Quaternion stuckLocalRot;
    Vector3 wobbleAxisLocal = Vector3.right;
    float wobbleTimer;

    static readonly List<ThrownAxe> live = new List<ThrownAxe>();

    public bool IsStuck => stuck;
    public bool IsRecalling => recalling;
    /// The collider it's stuck in, or null when it isn't stuck in anything (flying,
    /// recalling, or lying loose).
    public Collider StuckSurface => stuck && !dropped && hasSurface ? surfaceCollider : null;
    public bool IsFlying => launched && !stuck && !dropped;
    public Vector3 Velocity => velocity;
    public static IReadOnlyList<ThrownAxe> Live => live;
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

    void OnEnable() { live.Add(this); }
    void OnDisable() { live.Remove(this); }

    /// Drops every axe stuck in a collider at or under root. Call this when that object
    /// is about to break apart or vanish.
    public static void DropAllStuckIn(Transform root)
    {
        if (root == null)
        {
            return;
        }

        for (int i = live.Count - 1; i >= 0; i--)
        {
            ThrownAxe axe = live[i];
            if (axe == null || !axe.hasSurface || axe.surfaceCollider == null)
            {
                continue;
            }
            if (axe.surfaceCollider.transform.IsChildOf(root))
            {
                axe.DropFromSurface();
            }
        }
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
        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }

        velocity = launchVelocity;
        spinSpeed = spinDegreesPerSecond;
        ignored = ignoreColliders;
        age = 0f;
        portalTravel = Matrix4x4.identity;
        space = PortalManager.PlayerSpace;

        ownColliders = GetComponentsInChildren<Collider>(true);

        foreach (Collider c in ownColliders)
        {
            if (c != null)
            {
                c.enabled = false;
            }
        }

        if (headPoint != null)
        {
            headLocalOffset = transform.InverseTransformPoint(headPoint.position);
        }
        else if (autoFitHead)
        {
            FitHeadOffset();
        }

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

        if (leaveTrail)
        {
            CreateTrail();
        }

        launched = true;

        if (SweepSegment(originPoint, lastHeadPos, sweepRadius, out RaycastHit spawnHit, out Vector3 spawnDir))
        {
            Stick(spawnHit, spawnDir);
        }
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (recalling)
        {
            HandleRecall(dt);
            return;
        }

        if (stuck)
        {
            stuckTimer += dt;
            return;
        }

        if (!launched || ExpiredThisStep(dt))
        {
            return;
        }

        velocity += Physics.gravity * (gravityScale * dt);
        SimulateFlight(dt);
    }

    bool ExpiredThisStep(float dt)
    {
        age += dt;
        if ((maxLifetime > 0f && age > maxLifetime) || rb.position.y < despawnBelowY)
        {
            Destroy(gameObject);
            return true;
        }
        return false;
    }

    // Moves the axe one physics step, split into sub-steps short enough that the
    // spinning head can't skip past a surface between them.
    void SimulateFlight(float dt)
    {
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

            // Tested before the stick sweeps, or the axe hits whatever sits behind the
            // portal in THIS scene instead of carrying on into the linked one. The axe
            // flies with its colliders disabled, so the portal's trigger never sees it -
            // it has to test the crossing itself.
            //
            // The head's path is checked first because the head leads the pivot and is
            // what the stick sweeps below actually use: waiting for the pivot to reach the
            // plane lets the head cross it and bury itself on the far side first.
            Portal crossed = Portal.FindSegmentCrossing(lastHeadPos, nextHead, space, out Matrix4x4 portalMatrix);
            if (crossed == null)
            {
                crossed = Portal.FindSegmentCrossing(pos, nextPos, space, out portalMatrix);
            }

            if (crossed != null)
            {
                CarryThroughPortal(crossed, portalMatrix, nextPos, nextRot);
                return;
            }

            if (SweepSegment(lastHeadPos, nextHead, sweepRadius, out RaycastHit headHit, out Vector3 headDir))
            {
                StickAt(pos, rot, headHit, headDir);
                return;
            }

            if (sweepBody && SweepSegment(pos, nextPos, bodySweepRadius, out RaycastHit bodyHit, out Vector3 bodyDir))
            {
                StickAt(pos, rot, bodyHit, bodyDir);
                return;
            }

            pos = nextPos;
            rot = nextRot;
            lastHeadPos = nextHead;
        }

        rb.MovePosition(pos);
        rb.MoveRotation(rot);
    }

    // Continues the throw on the far side of a portal. Everything the flight path is
    // built from has to move across together: where it is, how it's turned, where it's
    // going, the axis it spins about, and the head position the next sweep chains from -
    // a stale head position would sweep a line across the whole world and stick the axe
    // into the first thing that line clipped.
    void CarryThroughPortal(Portal crossed, Matrix4x4 portalMatrix, Vector3 crossingPos, Quaternion currentRot)
    {
        if (crossed.LinkedPortal != null)
        {
            space = crossed.LinkedPortal.gameObject.scene.name;
        }

        Vector3 exitPos = portalMatrix.MultiplyPoint3x4(crossingPos);
        Quaternion exitRot = portalMatrix.rotation * currentRot;

        velocity = portalMatrix.rotation * velocity;
        spinAxis = portalMatrix.rotation * spinAxis;

        // Running total of every portal the axe has been through, so a recall can work out
        // where the player is relative to whichever space it currently occupies. Coming
        // back out the way it went in cancels this back to identity.
        portalTravel = portalMatrix * portalTravel;

        // The no-progress watchdog measures distance to a target that just jumped; without
        // this it would count the jump as "no headway" and give up mid-flight.
        recallBestDist = float.MaxValue;
        recallStuckTimer = 0f;

        // Assigned rather than MovePosition'd: this is a teleport, and interpolating it
        // would smear the axe across the gap between the two scenes for a frame.
        rb.position = exitPos;
        rb.rotation = exitRot;
        transform.SetPositionAndRotation(exitPos, exitRot);

        lastHeadPos = exitPos + HeadWorldOffset(exitRot);
    }

    void StickAt(Vector3 pos, Quaternion rot, RaycastHit hit, Vector3 dir)
    {
        rb.position = pos;
        rb.rotation = rot;
        Stick(hit, dir);
    }

    // LateUpdate so it runs after animation, nav agents and rigidbody interpolation
    // have put the surface where it'll be drawn this frame.
    void LateUpdate()
    {
        if (!stuck || dropped)
        {
            return;
        }

        if (SurfaceGone())
        {
            DropFromSurface();
            return;
        }

        bool wobbling = wobbleTimer > 0f;
        if (!hasSurface && !wobbling)
        {
            return;
        }

        ApplyStuckPose(StuckRotation(wobbling));
    }

    bool SurfaceGone() =>
        hasSurface && (surfaceCollider == null || !surfaceCollider.enabled || !surfaceCollider.gameObject.activeInHierarchy);

    // the rotation it was stuck at, following the surface, plus the decaying wobble
    Quaternion StuckRotation(bool wobbling)
    {
        Quaternion rot = stuckLocalRot;
        if (hasSurface)
        {
            rot = surfaceCollider.transform.rotation * rot;
        }

        if (wobbling)
        {
            wobbleTimer -= Time.deltaTime;
            if (wobbleTimer > 0f)
            {
                float elapsed = stickWobbleDuration - wobbleTimer;
                float amp = stickWobbleAngle * Mathf.Exp(-stickWobbleDamp * elapsed);
                float angle = Mathf.Sin(elapsed * stickWobbleFrequency) * amp;
                rot *= Quaternion.AngleAxis(angle, wobbleAxisLocal);
            }
        }
        return rot;
    }

    void ApplyStuckPose(Quaternion rot)
    {
        if (hasSurface)
        {
            transform.SetPositionAndRotation(surfaceCollider.transform.TransformPoint(stuckLocalPos), rot);
        }
        else
        {
            transform.rotation = rot;
        }
    }

    void ClearSurface()
    {
        hasSurface = false;
        surfaceCollider = null;
    }

    // ---------------------------------------------------------------- recall

    /// Begin flying back to a target. Returns false if it is not ready (still on
    /// cooldown, not stuck, or already recalling). targetPoint lets the caller aim at a
    /// moving point like the player's chest; pass null to home on the transform's origin.
    /// onCaught fires when the axe reaches the catch radius.
    public bool Recall(Transform target, float catchRadius, System.Action onCaught,
                       System.Func<Vector3> targetPoint = null, float speedScale = 1f)
    {
        if (target == null)
        {
            return false;
        }
        if (!RecallReady)
        {
            return false;
        }

        recallTarget = target;
        recallTargetPoint = targetPoint;
        recallSpeedScale = Mathf.Max(0.1f, speedScale);
        recallCatchRadius = Mathf.Max(0f, catchRadius);
        onRecallCaught = onCaught;

        stuck = false;
        recalling = true;
        wobbleTimer = 0f;
        recallBestDist = float.MaxValue;
        recallStuckTimer = 0f;

        ClearSurface();

        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        if (ownColliders != null)
        {
            foreach (Collider c in ownColliders)
            {
                if (c != null)
                {
                    c.enabled = false;
                }
            }
        }

        velocity = (RecallAimPoint() - HeadPosition).normalized * (RecallSpeed * 0.25f);

        if (trail != null)
        {
            trail.emitting = true;
        }

        rb.useGravity = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        return true;
    }

    Vector3 RecallAimPoint()
    {
        if (recallTargetPoint != null)
        {
            return recallTargetPoint();
        }
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

        // If the axe went through a portal, the player is in a space it can't fly to
        // directly - aiming at their real position would send it across the world. Mapping
        // them through the same transform the axe travelled gives the point the player
        // WOULD be at on this side, which sits through the portal, so the flight home is
        // naturally steered back at the opening.
        Vector3 aim = portalTravel.MultiplyPoint3x4(RecallAimPoint());
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

        Vector3 desiredVel = desiredDir * RecallSpeed;
        velocity = Vector3.MoveTowards(velocity, desiredVel, recallAcceleration * recallSpeedScale * dt);
        if (velocity.magnitude > RecallSpeed)
        {
            velocity = velocity.normalized * RecallSpeed;
        }

        // Collision-swept movement: the axe is kinematic with its colliders off, so we
        // sweep a sphere along the intended step and slide along whatever it hits.
        // Iterated a couple of times so an inside corner is handled in one frame.
        Vector3 pos = rb.position;
        Vector3 remaining = velocity * dt;

        // Flying home through the opening it came out of. Handled before the obstacle
        // sweep for the same reason as on the way out - otherwise it collides with the
        // room behind the portal instead of passing through.
        Portal back = Portal.FindSegmentCrossing(pos, pos + remaining, space, out Matrix4x4 returnMatrix);
        if (back != null)
        {
            CarryThroughPortal(back, returnMatrix, pos + remaining, rb.rotation);
            return;
        }

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
        if (!recalling)
        {
            return;
        }
        recalling = false;
        velocity = Vector3.zero;
    }

    public bool IsLoose => dropped && !recalling;

    // a zip reel brings it in faster than a normal recall
    float recallSpeedScale = 1f;
    float RecallSpeed => recallMaxSpeed * recallSpeedScale;

    /// Skips the embed cooldown. Used by the zip pull, which is its own gate.
    public void ForceRecallReady()
    {
        if (recallCooldown > 0f)
        {
            stuckTimer = Mathf.Max(stuckTimer, recallCooldown);
        }
    }

    // ---------------------------------------------------------------- sweeping

    bool SweepSegment(Vector3 from, Vector3 to, float radius, out RaycastHit best, out Vector3 dir)
    {
        best = default;
        dir = Vector3.forward;

        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.0001f)
        {
            return false;
        }

        dir = delta / dist;
        float castDist = dist + skinWidth;

        // Triggers are never something to stick in. They're checkpoints, tip zones, kill
        // planes and the like, and the axe flies straight through them.
        const QueryTriggerInteraction qti = QueryTriggerInteraction.Ignore;

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

        if (!found)
        {
            return false;
        }

        // A sweep that starts already inside something reports distance 0 and a hit point
        // of (0,0,0). Sticking at that point is what sent the axe to the middle of the map,
        // so it sticks where the sweep started instead.
        if (best.distance <= 0f || best.normal.sqrMagnitude < 0.001f)
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
            if (h.collider == null)
            {
                continue;
            }
            if (IsSelf(h.collider) || IsIgnored(h.collider))
            {
                continue;
            }
            // A portal's trigger volume is a doorway, not a surface. It reaches out well
            // in front of the opening, so without this the axe embeds itself in mid-air
            // short of the portal instead of flying through it.
            if (h.collider.GetComponentInParent<Portal>() != null)
            {
                continue;
            }
            if (h.distance >= bestDist)
            {
                continue;
            }

            best = h;
            bestDist = h.distance;
            found = true;
        }

        return found;
    }

    bool IsSelf(Collider c)
    {
        if (c.transform == transform || c.transform.IsChildOf(transform))
        {
            return true;
        }

        if (ownColliders != null)
        {
            for (int i = 0; i < ownColliders.Length; i++)
            {
                if (ownColliders[i] == c)
                {
                    return true;
                }
            }
        }

        return false;
    }

    bool IsIgnored(Collider c)
    {
        if (ignored == null)
        {
            return false;
        }

        for (int i = 0; i < ignored.Length; i++)
        {
            if (ignored[i] == c)
            {
                return true;
            }
        }

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
        if (embed.sqrMagnitude < 0.001f)
        {
            embed = -hit.normal;
        }
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
                {
                    aligned = Quaternion.FromToRotation(currentUp, desiredUp) * aligned;
                }
            }
        }

        aligned *= Quaternion.Euler(stickEulerOffset);

        Vector3 headTarget = hit.point + embed * stickDepth;
        Vector3 finalPos = headTarget - HeadWorldOffset(aligned);

        // Grab the collider up front, so a wall that destroys itself in response still
        // gives us a valid reference this frame.
        Collider hitCollider = hit.collider;

        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
        transform.SetPositionAndRotation(finalPos, aligned);
        rb.position = finalPos;
        rb.rotation = aligned;

        hasSurface = followSurface && hitCollider != null;
        surfaceCollider = hasSurface ? hitCollider : null;
        if (hasSurface)
        {
            Transform s = hitCollider.transform;
            stuckLocalPos = s.InverseTransformPoint(finalPos);
            stuckLocalRot = Quaternion.Inverse(s.rotation) * aligned;
        }
        else
        {
            stuckLocalRot = aligned;
        }

        wobbleAxisLocal = Vector3.Cross(headDirLocal, Vector3.up);
        if (wobbleAxisLocal.sqrMagnitude < 0.001f)
        {
            wobbleAxisLocal = Vector3.Cross(headDirLocal, Vector3.forward);
        }
        wobbleAxisLocal = wobbleAxisLocal.sqrMagnitude > 0.001f
            ? wobbleAxisLocal.normalized
            : Vector3.right;

        wobbleTimer = stickWobbleDuration;

        if (trail != null)
        {
            trail.emitting = false;
        }

        float force = Mathf.Clamp01(impactSpeed / Mathf.Max(1f, impactReferenceSpeed));

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null)
        {
            fx.ImpactBurst(hit.point, hit.normal, Mathf.Lerp(0.35f, 1f, force));
        }

        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTraumaAtPoint(hit.point, impactShake * force,
                impactShakeFullRange, impactShakeMaxRange);
        }

        // Before the message below: if the hit breaks the wall or kills the enemy the axe
        // gets dropped, and that has to come after this or the loose axe ends up
        // grappleable again.
        if (becomesGrappable)
        {
            MakeGrappable();
        }

        // A BreakableWall listens for this and shatters; enemies take damage; anything
        // else ignores it. Sent after our own juice so a wall's shatter feedback layers
        // on top.
        if (hitCollider != null)
        {
            hitCollider.SendMessageUpwards("OnThrownAxeStuck", hit.point,
                SendMessageOptions.DontRequireReceiver);
        }
    }

    // Called when the surface the axe stuck into is destroyed, disabled or breaks apart.
    // The axe stops being a frozen kinematic prop and falls under gravity as a loose
    // body, staying recallable.
    public void DropFromSurface()
    {
        if (!stuck || dropped)
        {
            return;
        }
        dropped = true;

        ClearSurface();
        wobbleTimer = 0f;

        if (rb == null)
        {
            rb = GetComponent<Rigidbody>();
        }
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            rb.linearVelocity = Vector3.down * 1.5f + Random.insideUnitSphere * 0.6f;
            rb.angularVelocity = Random.insideUnitSphere * 2f;

            if (looseStopsBeingGrappleTarget)
            {
                RemoveGrappleTargeting();
            }
        }

        void RemoveGrappleTargeting()
        {
            if (grappleTrigger != null) { Destroy(grappleTrigger); grappleTrigger = null; }

            SetLayerRecursive(gameObject, originalLayer);

            if (!string.IsNullOrEmpty(grappleTag) && gameObject.CompareTag(grappleTag))
            {
                gameObject.tag = string.IsNullOrEmpty(originalTag) ? "Untagged" : originalTag;
            }
        }

        // The grapple trigger colliders were turned into triggers when it stuck; make
        // the real ones solid again so it can land, but leave the added grapple sphere
        // (if any) as a trigger.
        if (ownColliders != null)
        {
            foreach (Collider c in ownColliders)
            {
                if (c == null)
                {
                    continue;
                }
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
                    if (c == null)
                    {
                        continue;
                    }
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
            {
                SetLayerRecursive(gameObject, layer);
            }
            else
            {
                Debug.LogWarning("ThrownAxe: layer '" + grappleLayerName + "' does not exist. " +
                                 "Create it or clear grappleLayerName.", this);
            }
        }

        if (!string.IsNullOrEmpty(grappleTag))
        {
            gameObject.tag = grappleTag;
        }
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
                if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", solid);
                }
                if (mat.HasProperty("_BaseColor"))
                {
                    mat.SetColor("_BaseColor", solid);
                }
            }
        }
        if (mat != null)
        {
            trail.material = mat;
        }
    }

    // ---------------------------------------------------------------- helpers

    Vector3 HeadWorldOffset(Quaternion rot)
    {
        return rot * Vector3.Scale(headLocalOffset, transform.lossyScale);
    }

    static Vector3 ComputeSpinAxis(Vector3 vel)
    {
        Vector3 flat = new Vector3(vel.x, 0f, vel.z);
        if (flat.sqrMagnitude < 0.01f)
        {
            return Vector3.right;
        }

        Vector3 axis = Vector3.Cross(Vector3.up, flat.normalized);
        return axis.sqrMagnitude > 0.001f ? axis.normalized : Vector3.right;
    }

    // Guesses the blade position from the model's longest axis. Assigning headPoint
    // by hand is always better than this.
    void FitHeadOffset()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return;
        }

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
                else
                {
                    local.Encapsulate(p);
                }
            }
        }

        if (!init)
        {
            return;
        }

        Vector3 c = local.center;
        Vector3 e = local.extents;

        if (e.z >= e.x && e.z >= e.y)
        {
            headLocalOffset = new Vector3(c.x, c.y, FarEnd(c.z, e.z));
        }
        else if (e.y >= e.x)
        {
            headLocalOffset = new Vector3(c.x, FarEnd(c.y, e.y), c.z);
        }
        else
        {
            headLocalOffset = new Vector3(FarEnd(c.x, e.x), c.y, c.z);
        }
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
        {
            SetLayerRecursive(child.gameObject, layer);
        }
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
