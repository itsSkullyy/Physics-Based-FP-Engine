using UnityEngine;

// Held battle axe. Attach to an AxeHolder empty under CameraFX (same place GunSway went).
// Put the actual axe model as a child and assign it to axeVisual.
// All bindings live on PlayerInputRouter: swing = axeSwing, throw = axeThrow,
// pickup = axePickup, recall = axeRecall.
[DefaultExecutionOrder(90)]
public class BattleAxe : MonoBehaviour
{
    [Header("Refs")]
    public FirstPersonCharacterController controller;
    public Rigidbody playerBody;
    public PlayerInputRouter input;
    public Transform aimTransform;          // defaults to controller.cameraTransform
    public Transform axeVisual;             // the model, hidden while thrown
    public Grappling grappling;             // optional, used to clean up a dead anchor on pickup
    public ThrownAxe thrownAxePrefab;       // optional, built from axeVisual at runtime if null

    [Header("Look Sway")]
    public float swayAmount = 0.012f;
    public float maxSway = 0.06f;
    public float rotSwayAmount = 3f;
    public float maxRotSway = 9f;
    public float swaySmooth = 9f;

    [Header("Movement Bob")]
    public float bobFrequency = 8f;
    public float bobAmount = 0.02f;
    public float bobSideAmount = 0.014f;

    [Header("Jump / Fall Kick")]
    public float verticalKick = 0.008f;
    public float maxVerticalKick = 0.09f;

    [Header("Swing Timing")]
    public float windupTime = 0.11f;
    public float swingTime = 0.16f;
    public float recoverTime = 0.24f;
    [Range(0f, 1f)] public float hitWindowStart = 0.15f;
    [Range(0f, 1f)] public float hitWindowEnd = 0.85f;

    [Header("Swing Pose")]
    public Vector3 windupOffset = new Vector3(0.06f, 0.24f, -0.22f);
    public Vector3 windupEuler = new Vector3(-58f, 14f, -20f);
    public Vector3 impactOffset = new Vector3(-0.07f, -0.30f, 0.30f);
    public Vector3 impactEuler = new Vector3(72f, -12f, 16f);
    public Vector3 impactPunch = new Vector3(0f, 0.06f, -0.18f);
    public float punchDecay = 0.16f;

    [Header("Swing Hit")]
    public LayerMask hitMask = 0;           // 0 = copy controller.groundMask
    public float swingRange = 3.2f;
    public float swingRadius = 0.4f;
    public float castStartOffset = 0.25f;

    [Header("Mace Bounce")]
    public bool bounceOnHit = true;
    public float bounceSpeed = 10f;
    public float bounceUpSpeed = 8f;
    [Range(0f, 1f)] public float surfaceNormalInfluence = 0.5f;
    [Range(0f, 1f)] public float velocityKeep = 0.25f;
    [Range(0f, 1.5f)] public float fallToBounce = 0.55f;
    public float minBounceUp = 6f;
    public float maxBounceSpeed = 32f;
    public int maxAirBounces = 0;           // 0 = unlimited
    public bool bounceWhenGrounded = true;
    [Tooltip("Bouncing while on the rope cuts it, otherwise the rope solver eats the impulse.")]
    public bool bounceReleasesGrapple = true;

    [Header("Throw")]
    public float throwSpeed = 26f;
    public float throwUpSpeed = 3f;
    [Range(0f, 1f)] public float inheritPlayerVelocity = 0.5f;
    public float throwSpinSpeed = 1440f;     // degrees per second, 4 rotations a second
    public float throwSpawnForward = 0.6f;
    public float throwCooldown = 0.25f;

    [Header("Thrown Axe Runtime Setup")]
    [Tooltip("Only used when thrownAxePrefab is empty and the axe is built from axeVisual.")]
    public LayerMask thrownStickMask = ~0;
    public bool stickToTriggers = false;
    public float thrownScaleMultiplier = 1f;   // viewmodels are often smaller than world scale
    public float thrownGravityScale = 1f;
    public float headSweepRadius = 0.08f;
    public float stickDepth = 0.12f;
    public bool thrownLeaveTrail = true;
    public string grappleLayerName = "Grappleable";
    public string grappleTag = "";
    public float grappleRadius = 0.6f;
    public Vector3 stickEulerOffset = Vector3.zero;

    [Header("Pickup")]
    public float pickupDistance = 2.5f;
    public bool requireStuckToPickup = false;
    public bool allowRemoteRecall = true;
    public bool showPickupPrompt = true;
    public float grappleDetachRadius = 3f;

    [Header("Debug")]
    public bool logDebug = false;

    enum State { Idle, Windup, Swing, Recover, Thrown }

    State state = State.Idle;
    float stateTimer;
    float cooldownTimer;
    bool hitRegistered;
    int airBounces;
    bool wasGrounded;

    Vector3 basePos;
    Quaternion baseRot;
    Vector3 idlePos;
    Quaternion idleRot;
    Vector3 swingPos;
    Quaternion swingRot = Quaternion.identity;
    float bobTimer;
    float punch;

    ThrownAxe activeAxe;
    bool inPickupRange;
    Collider[] playerColliders;
    Renderer[] visualRenderers;

    // Juice hooks. PlayerJuice listens to these; hang your own VFX or audio here too.
    public event System.Action AxeThrown;
    public event System.Action AxeSwingStarted;
    public event System.Action<Vector3, Vector3, bool> AxeHit;   // point, normal, bounced

    public bool IsSwinging => state == State.Windup || state == State.Swing;
    public bool IsThrown => state == State.Thrown;
    public ThrownAxe ActiveAxe => activeAxe;

    void Awake()
    {
        if (controller == null) controller = GetComponentInParent<FirstPersonCharacterController>();
        if (controller == null)
        {
            Debug.LogError("BattleAxe needs a FirstPersonCharacterController.", this);
            enabled = false;
            return;
        }

        if (playerBody == null) playerBody = controller.GetComponent<Rigidbody>();
        if (input == null) input = PlayerInputRouter.Resolve(this);
        if (aimTransform == null) aimTransform = controller.cameraTransform;
        if (aimTransform == null) aimTransform = transform;
        if (grappling == null) grappling = controller.GetComponent<Grappling>();
        if (hitMask == 0) hitMask = controller.groundMask;

        playerColliders = controller.GetComponentsInChildren<Collider>(true);

        if (axeVisual != null)
        {
            visualRenderers = axeVisual.GetComponentsInChildren<Renderer>(true);

            if (axeVisual == transform)
                Debug.LogWarning("BattleAxe: axeVisual is this same GameObject. That works, but " +
                                 "a child object holding just the model is cleaner.", this);
        }

        basePos = transform.localPosition;
        baseRot = transform.localRotation;
        idlePos = basePos;
        idleRot = baseRot;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        cooldownTimer -= dt;

        HandleInput();
        UpdateState(dt);
        UpdateSway(dt);
        ApplyPose();

        if (controller.IsGrounded && !wasGrounded)
            airBounces = 0;
        wasGrounded = controller.IsGrounded;
    }

    // ---------------------------------------------------------------- input

    void HandleInput()
    {
        if (input == null) return;

        if (state == State.Thrown)
        {
            HandleThrownAxe();
            return;
        }

        if (state != State.Idle || !CanAct()) return;

        if (input.axeSwing.Pressed)
            StartSwing();
        else if (input.axeThrow.Pressed)
            ThrowAxe();
    }

    // Grappling is deliberately NOT blocking here - you can swing and throw mid-rope.
    // Vaulting is, because the body is kinematic during a vault and any velocity
    // change would be thrown away.
    bool CanAct()
    {
        return cooldownTimer <= 0f && !controller.IsVaulting;
    }

    // ---------------------------------------------------------------- swing

    void StartSwing()
    {
        state = State.Windup;
        stateTimer = 0f;
        hitRegistered = false;

        AxeSwingStarted?.Invoke();
    }

    void UpdateState(float dt)
    {
        if (state == State.Idle || state == State.Thrown)
        {
            swingPos = Vector3.zero;
            swingRot = Quaternion.identity;
            punch = Mathf.MoveTowards(punch, 0f, dt / Mathf.Max(0.01f, punchDecay));
            return;
        }

        stateTimer += dt;

        Quaternion windQ = Quaternion.Euler(windupEuler);
        Quaternion impactQ = Quaternion.Euler(impactEuler);

        switch (state)
        {
            case State.Windup:
            {
                float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, windupTime));
                float e = 1f - (1f - t) * (1f - t);
                swingPos = Vector3.Lerp(Vector3.zero, windupOffset, e);
                swingRot = Quaternion.Slerp(Quaternion.identity, windQ, e);

                if (t >= 1f) { state = State.Swing; stateTimer = 0f; }
                break;
            }
            case State.Swing:
            {
                float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, swingTime));
                float e = t * t;
                swingPos = Vector3.Lerp(windupOffset, impactOffset, e);
                swingRot = Quaternion.Slerp(windQ, impactQ, e);

                if (!hitRegistered && t >= hitWindowStart && t <= hitWindowEnd)
                    TryHit();

                if (t >= 1f) { state = State.Recover; stateTimer = 0f; }
                break;
            }
            case State.Recover:
            {
                float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, recoverTime));
                float e = t * t * (3f - 2f * t);
                swingPos = Vector3.Lerp(impactOffset, Vector3.zero, e);
                swingRot = Quaternion.Slerp(impactQ, Quaternion.identity, e);

                if (t >= 1f) { state = State.Idle; stateTimer = 0f; }
                break;
            }
        }

        punch = Mathf.MoveTowards(punch, 0f, dt / Mathf.Max(0.01f, punchDecay));
        swingPos += impactPunch * punch;
    }

    void TryHit()
    {
        Vector3 origin = aimTransform.position + aimTransform.forward * castStartOffset;
        Vector3 dir = aimTransform.forward;

        RaycastHit[] hits = Physics.SphereCastAll(origin, swingRadius, dir, swingRange,
            hitMask, QueryTriggerInteraction.Ignore);

        RaycastHit best = default;
        float bestDist = float.MaxValue;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (IsPlayerCollider(h.collider)) continue;
            if (h.distance >= bestDist) continue;

            best = h;
            bestDist = h.distance;
            found = true;
        }

        if (!found) return;

        hitRegistered = true;
        punch = 1f;

        Vector3 normal = best.normal.sqrMagnitude > 0.001f ? best.normal : -dir;
        Vector3 point = best.point.sqrMagnitude > 0.0001f ? best.point : origin + dir * swingRadius;

        OnAxeHit(best.collider, point, normal);

        bool bounced = bounceOnHit && ApplyBounce(dir, normal);
        AxeHit?.Invoke(point, normal, bounced);
    }

    // Hook for damage / impact VFX. Extend freely.
    protected virtual void OnAxeHit(Collider hitCollider, Vector3 point, Vector3 normal)
    {
        hitCollider.SendMessageUpwards("OnAxeHit", point, SendMessageOptions.DontRequireReceiver);
    }

    bool ApplyBounce(Vector3 aimDir, Vector3 normal)
    {
        if (playerBody == null) return false;
        if (!bounceWhenGrounded && controller.IsGrounded) return false;

        if (maxAirBounces > 0 && !controller.IsGrounded)
        {
            if (airBounces >= maxAirBounces) return false;
            airBounces++;
        }

        // A rope or a zip would overwrite the bounce next physics step, so cut them first.
        if (grappling != null && bounceReleasesGrapple)
        {
            if (grappling.IsZipping) grappling.StopZip();
            if (grappling.IsSwinging) grappling.Detach(false);
        }

        Vector3 away = Vector3.Lerp(-aimDir, normal, surfaceNormalInfluence);
        if (away.sqrMagnitude < 0.001f) away = normal;
        away.Normalize();

        Vector3 vel = playerBody.linearVelocity;
        float fall = Mathf.Max(0f, -vel.y);

        Vector3 v = vel * velocityKeep;
        if (v.y < 0f) v.y = 0f;

        v += away * bounceSpeed;
        v.y += bounceUpSpeed + fall * fallToBounce;

        if (v.y < minBounceUp) v.y = minBounceUp;
        if (v.magnitude > maxBounceSpeed) v = v.normalized * maxBounceSpeed;

        playerBody.linearVelocity = v;
        return true;
    }

    bool IsPlayerCollider(Collider c)
    {
        if (c == null) return true;
        if (playerBody != null && c.attachedRigidbody == playerBody) return true;

        for (int i = 0; i < playerColliders.Length; i++)
            if (playerColliders[i] == c) return true;

        return false;
    }

    // ---------------------------------------------------------------- throw

    void ThrowAxe()
    {
        Vector3 spawn = aimTransform.position + aimTransform.forward * throwSpawnForward;
        Quaternion rot = Quaternion.LookRotation(aimTransform.forward, Vector3.up);

        ThrownAxe axe = SpawnThrownAxe(spawn, rot);
        if (axe == null) return;

        Vector3 velocity = aimTransform.forward * throwSpeed + Vector3.up * throwUpSpeed;
        if (playerBody != null)
            velocity += playerBody.linearVelocity * inheritPlayerVelocity;

        axe.Launch(velocity, throwSpinSpeed, playerColliders, aimTransform.position);

        activeAxe = axe;
        state = State.Thrown;
        stateTimer = 0f;
        cooldownTimer = throwCooldown;
        swingPos = Vector3.zero;
        swingRot = Quaternion.identity;

        if (axeVisual != null)
            SetAxeVisible(false);

        AxeThrown?.Invoke();
    }

    ThrownAxe SpawnThrownAxe(Vector3 pos, Quaternion rot)
    {
        if (thrownAxePrefab != null)
            return Instantiate(thrownAxePrefab, pos, rot);

        if (axeVisual == null)
        {
            Debug.LogError("BattleAxe needs either thrownAxePrefab or axeVisual to throw.", this);
            return null;
        }

        GameObject go = Instantiate(axeVisual.gameObject, pos, rot);
        go.name = "ThrownAxe";
        go.SetActive(true);
        go.transform.localScale = axeVisual.lossyScale * thrownScaleMultiplier;

        // If axeVisual is the holder itself, the clone carries copies of BattleAxe and
        // friends. Those would fight over input and state, so nothing but the model
        // survives the trip.
        StripClone(go);

        ThrownAxe axe = go.GetComponent<ThrownAxe>();
        if (axe == null) axe = go.AddComponent<ThrownAxe>();

        // A collider is only needed once it sticks, as a grapple target - the flight
        // path is resolved by ThrownAxe's own sweep, not by physics collisions.
        if (go.GetComponentInChildren<Collider>() == null)
            AddFittedCollider(go);

        axe.stickMask = thrownStickMask;
        axe.stickToTriggers = stickToTriggers;
        axe.gravityScale = thrownGravityScale;
        axe.sweepRadius = headSweepRadius;
        axe.stickDepth = stickDepth;
        axe.leaveTrail = thrownLeaveTrail;
        axe.grappleLayerName = grappleLayerName;
        axe.grappleTag = grappleTag;
        axe.grappleRadius = grappleRadius;
        axe.stickEulerOffset = stickEulerOffset;

        return axe;
    }

    static void StripClone(GameObject go)
    {
        foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && !(mb is ThrownAxe)) Destroy(mb);

        foreach (Camera c in go.GetComponentsInChildren<Camera>(true))
            Destroy(c);

        foreach (AudioListener a in go.GetComponentsInChildren<AudioListener>(true))
            Destroy(a);
    }

    static void AddFittedCollider(GameObject go)
    {
        BoxCollider box = go.AddComponent<BoxCollider>();
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            box.size = Vector3.one * 0.3f;
            return;
        }

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        Vector3 scale = go.transform.lossyScale;
        box.center = go.transform.InverseTransformPoint(b.center);
        box.size = new Vector3(
            b.size.x / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
            b.size.y / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
            b.size.z / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));
    }

    // ---------------------------------------------------------------- pickup

    void HandleThrownAxe()
    {
        inPickupRange = false;

        if (activeAxe == null)
        {
            RestoreAxe();
            return;
        }

        float dist = Vector3.Distance(controller.transform.position, activeAxe.transform.position);
        bool stuckOk = !requireStuckToPickup || activeAxe.IsStuck;
        inPickupRange = stuckOk && dist <= pickupDistance;

        if (input.axePickup.Pressed && !inPickupRange && logDebug)
            Debug.Log("[BattleAxe] Pickup pressed but not in range. Distance " +
                      dist.ToString("0.00") + " / " + pickupDistance +
                      ", stuck = " + activeAxe.IsStuck, this);

        if (inPickupRange && input.axePickup.Pressed)
        {
            CollectAxe();
            return;
        }

        if (allowRemoteRecall && input.axeRecall.Pressed)
            CollectAxe();
    }

    void CollectAxe()
    {
        if (activeAxe != null)
        {
            if (grappling != null && grappling.IsSwinging &&
                Vector3.Distance(grappling.Anchor, activeAxe.transform.position) < grappleDetachRadius)
                grappling.Detach(false);

            Destroy(activeAxe.gameObject);
            activeAxe = null;
        }

        RestoreAxe();
    }

    void RestoreAxe()
    {
        activeAxe = null;
        state = State.Idle;
        stateTimer = 0f;
        cooldownTimer = Mathf.Max(cooldownTimer, 0.1f);

        if (axeVisual != null)
            SetAxeVisible(true);

        if (logDebug) Debug.Log("[BattleAxe] Axe back in hand.", this);
    }

    // Hides the model by switching renderers off rather than deactivating the object.
    // Deactivating would kill this component's Update if axeVisual happens to be the
    // same GameObject BattleAxe lives on, and then nothing could ever pick it back up.
    void SetAxeVisible(bool visible)
    {
        if (axeVisual == null) return;

        if (visualRenderers == null || visualRenderers.Length == 0)
            visualRenderers = axeVisual.GetComponentsInChildren<Renderer>(true);

        if (visualRenderers != null && visualRenderers.Length > 0)
        {
            foreach (Renderer r in visualRenderers)
                if (r != null) r.enabled = visible;

            // Recover from a scene saved while the object was switched off.
            if (visible && axeVisual != transform && !axeVisual.gameObject.activeSelf)
                axeVisual.gameObject.SetActive(true);

            return;
        }

        // No renderers at all - fall back to SetActive, but never on ourselves.
        if (axeVisual != transform)
            axeVisual.gameObject.SetActive(visible);
    }

    // ---------------------------------------------------------------- pose

    void UpdateSway(float dt)
    {
        Vector2 look = input != null ? input.LookDelta : Vector2.zero;

        Vector3 swayOffset = new Vector3(
            Mathf.Clamp(-look.x * swayAmount, -maxSway, maxSway),
            Mathf.Clamp(-look.y * swayAmount, -maxSway, maxSway),
            0f);

        Quaternion swayRotation = Quaternion.Euler(
            Mathf.Clamp(look.y * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount * 0.5f, -maxRotSway, maxRotSway));

        Vector3 bob = Vector3.zero;
        if (controller.IsGrounded && !controller.IsSliding && controller.CurrentSpeed > 1f)
        {
            float speedFactor = controller.CurrentSpeed / Mathf.Max(1f, controller.baseSpeed);
            bobTimer += dt * bobFrequency * Mathf.Min(speedFactor, 2f);
            bob.y = Mathf.Sin(bobTimer * 2f) * bobAmount * speedFactor;
            bob.x = Mathf.Cos(bobTimer) * bobSideAmount * speedFactor;
        }
        else
        {
            bobTimer = 0f;
        }

        float vy = controller.Velocity.y;
        bob.y += Mathf.Clamp(-vy * verticalKick, -maxVerticalKick, maxVerticalKick);

        float t = 1f - Mathf.Exp(-swaySmooth * dt);
        idlePos = Vector3.Lerp(idlePos, basePos + swayOffset + bob, t);
        idleRot = Quaternion.Slerp(idleRot, baseRot * swayRotation, t);
    }

    void ApplyPose()
    {
        transform.localPosition = idlePos + swingPos;
        transform.localRotation = idleRot * swingRot;
    }

    void OnGUI()
    {
        if (!showPickupPrompt || !inPickupRange || state != State.Thrown) return;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 18
        };
        style.normal.textColor = Color.white;

        string label = input != null ? input.axePickup.Label : "G";
        GUI.Label(new Rect(0f, Screen.height * 0.58f, Screen.width, 30f),
            "[" + label + "] Pick up axe", style);
    }

    void OnDrawGizmosSelected()
    {
        if (aimTransform == null) return;

        Gizmos.color = Color.red;
        Vector3 origin = aimTransform.position + aimTransform.forward * castStartOffset;
        Gizmos.DrawWireSphere(origin, swingRadius);
        Gizmos.DrawWireSphere(origin + aimTransform.forward * swingRange, swingRadius);
    }
}