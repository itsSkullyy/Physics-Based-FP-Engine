using UnityEngine;

// Held battle axe. Attach to an AxeHolder empty under CameraFX (same place GunSway went).
// Put the actual axe model as a child and assign it to axeVisual.
//
// INPUT LAYOUT
//   axeSwing (RT / LMB)  - tap to swing, HOLD to charge a throw, release to throw.
//   axeThrow (LT / RMB)  - optional instant throw, kept as an alternate. Switch it off
//                          with allowDedicatedThrowButton if you want that trigger freed.
//   axePickup / axeRecall unchanged.
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
    public float windupTime = 0.06f;
    public float swingTime = 0.09f;
    public float recoverTime = 0.14f;
    // Wide window on purpose: the swing is short enough that a frame spike could
    // otherwise skip clean past a narrow one and eat the hit entirely.
    [Range(0f, 1f)] public float hitWindowStart = 0.05f;
    [Range(0f, 1f)] public float hitWindowEnd = 0.95f;

    [Header("Charge Throw")]
    [Tooltip("Off = the old behaviour, where the swing fires straight off the press and " +
             "throwing lives entirely on the dedicated throw button.")]
    public bool enableChargeThrow = true;
    [Tooltip("Hold the swing button longer than this and it becomes a throw charge " +
             "instead of a swing. The windup pose plays immediately either way, so the " +
             "axe still reacts on the same frame you press.")]
    public float holdToChargeTime = 0.16f;
    [Tooltip("How long a full charge takes, measured from the moment charging begins.")]
    public float chargeTime = 0.55f;
    [Tooltip("Release below this and you get a swing instead of a limp throw. 0 = always throw.")]
    [Range(0f, 1f)] public float minChargeToThrow = 0f;
    public bool allowDedicatedThrowButton = true;

    [Header("Charge Power")]
    [Tooltip("All three default to 0, so a charged throw flies exactly like the throw does " +
             "today and the charge is pure telegraph. Raise them to make holding pay off.")]
    public float chargeSpeedBonus = 0f;
    public float chargeUpSpeedBonus = 0f;
    public float chargeSpinBonus = 0f;

    [Header("Charge Pose")]
    public Vector3 chargeOffset = new Vector3(0.10f, 0.30f, -0.32f);
    public Vector3 chargeEuler = new Vector3(-74f, 22f, -26f);
    public float chargePoseSmooth = 14f;
    public float chargeShakeAmount = 0.013f;
    public float chargeShakeFrequency = 34f;

    [Header("Charge Meter")]
    public bool showChargeMeter = true;
    public float chargeMeterWidth = 120f;
    public float chargeMeterHeight = 4f;
    public float chargeMeterScreenY = 0.545f;
    public Color chargeMeterBack = new Color(0f, 0f, 0f, 0.45f);
    public Color chargeMeterFill = new Color(1f, 0.85f, 0.4f, 0.9f);
    public Color chargeMeterFull = Color.white;

    [Header("Swing Pose")]
    public Vector3 windupOffset = new Vector3(0.06f, 0.24f, -0.22f);
    public Vector3 windupEuler = new Vector3(-58f, 14f, -20f);
    public Vector3 impactOffset = new Vector3(-0.07f, -0.30f, 0.30f);
    public Vector3 impactEuler = new Vector3(72f, -12f, 16f);
    public Vector3 impactPunch = new Vector3(0f, 0.08f, -0.24f);
    public float punchDecay = 0.12f;

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
    public float throwSpeed = 42f;
    public float throwUpSpeed = 2f;
    [Range(0f, 1f)] public float inheritPlayerVelocity = 0.5f;
    public float throwSpinSpeed = 2160f;     // degrees per second, 6 rotations a second
    public float throwSpawnForward = 0.6f;
    public float throwCooldown = 0.15f;

    [Header("Thrown Axe Runtime Setup")]
    [Tooltip("Only used when thrownAxePrefab is empty and the axe is built from axeVisual.")]
    public LayerMask thrownStickMask = ~0;
    public bool stickToTriggers = false;
    public float thrownScaleMultiplier = 1f;   // viewmodels are often smaller than world scale
    public float thrownGravityScale = 0.85f;
    public float headSweepRadius = 0.08f;
    public float stickDepth = 0.12f;
    public bool thrownLeaveTrail = true;
    public string grappleLayerName = "Grappleable";
    public string grappleTag = "";
    public float grappleRadius = 0.6f;
    public Vector3 stickEulerOffset = Vector3.zero;

    [Header("Thrown Axe Sweep Budget")]
    [Tooltip("Shorter steps and more substeps keep the swept arc continuous at high " +
             "throw speeds. Raise the substep count if you push throwSpeed higher.")]
    public float thrownMaxStepDistance = 0.22f;
    public int thrownMaxSubSteps = 14;

    [Header("Pickup")]
    public float pickupDistance = 2.5f;
    public bool requireStuckToPickup = false;
    public bool allowRemoteRecall = true;
    public bool showPickupPrompt = true;
    public float grappleDetachRadius = 3f;

    [Header("Debug")]
    public bool logDebug = false;

    enum State { Idle, Windup, Charging, Swing, Recover, Thrown }

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

    // Captured whenever one state hands over to the next, so the pose carries on from
    // wherever it actually got to instead of snapping to where it "should" be. That
    // matters now that a windup can be cut short at any point by an early release.
    Vector3 poseFromPos;
    Quaternion poseFromRot = Quaternion.identity;

    float charge01;
    bool chargeFullFired;

    ThrownAxe activeAxe;
    bool inPickupRange;
    Collider[] playerColliders;
    Renderer[] visualRenderers;
    static Texture2D whiteTex;

    // Juice hooks. PlayerJuice listens to these; hang your own VFX or audio here too.
    public event System.Action AxeThrown;
    public event System.Action AxeSwingStarted;
    public event System.Action<Vector3, Vector3, bool> AxeHit;   // point, normal, bounced
    public event System.Action AxeChargeStarted;
    public event System.Action AxeChargeFull;
    public event System.Action<float> AxeChargeReleased;         // 0..1 charge at release

    public bool IsSwinging => state == State.Windup || state == State.Swing;
    public bool IsCharging => state == State.Charging;
    public bool IsThrown => state == State.Thrown;
    public float ChargeAmount => state == State.Charging ? charge01 : 0f;
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

        // Windup and Charging both wait on the button rather than on a timer, so they
        // get first look at the input every frame.
        if (state == State.Windup)
        {
            UpdateWindupIntent();
            return;
        }

        if (state == State.Charging)
        {
            UpdateChargeIntent();
            return;
        }

        if (state != State.Idle || !CanAct()) return;

        if (input.axeSwing.Pressed)
            StartWindup();
        else if (allowDedicatedThrowButton && input.axeThrow.Pressed)
            ThrowAxe(0f);
    }

    // Released early means a swing, held past the threshold means a charge. Release is
    // tested first so a fast tap can never be misread as the start of a hold.
    void UpdateWindupIntent()
    {
        if (!enableChargeThrow) return;

        if (!input.axeSwing.Held)
        {
            BeginSwing();
            return;
        }

        if (stateTimer >= Mathf.Max(0.01f, holdToChargeTime))
            BeginCharge();
    }

    void UpdateChargeIntent()
    {
        if (input.axeSwing.Held) return;

        // A charge abandoned almost immediately reads as a fumbled swing, so hand them
        // the swing rather than a throw with nothing behind it.
        if (charge01 < minChargeToThrow)
        {
            BeginSwing();
            return;
        }

        ThrowAxe(charge01);
    }

    // Grappling is deliberately NOT blocking here - you can swing and throw mid-rope.
    // Vaulting is, because the body is kinematic during a vault and any velocity
    // change would be thrown away.
    bool CanAct()
    {
        return cooldownTimer <= 0f && !controller.IsVaulting;
    }

    // ---------------------------------------------------------------- swing

    void StartWindup()
    {
        state = State.Windup;
        stateTimer = 0f;
        hitRegistered = false;
        charge01 = 0f;
        chargeFullFired = false;

        poseFromPos = swingPos;
        poseFromRot = swingRot;

        AxeSwingStarted?.Invoke();
    }

    void BeginSwing()
    {
        poseFromPos = swingPos;
        poseFromRot = swingRot;

        state = State.Swing;
        stateTimer = 0f;
        hitRegistered = false;
        charge01 = 0f;
        chargeFullFired = false;
    }

    void BeginCharge()
    {
        poseFromPos = swingPos;
        poseFromRot = swingRot;

        state = State.Charging;
        stateTimer = 0f;
        charge01 = 0f;
        chargeFullFired = false;

        AxeChargeStarted?.Invoke();
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
                swingPos = Vector3.Lerp(poseFromPos, windupOffset, e);
                swingRot = Quaternion.Slerp(poseFromRot, windQ, e);

                // With charging on, the windup holds at full pull-back and waits on the
                // button. With it off, the timer drives the swing exactly like before.
                if (!enableChargeThrow && t >= 1f)
                    BeginSwing();
                break;
            }
            case State.Charging:
            {
                charge01 = chargeTime <= 0.01f ? 1f : Mathf.Clamp01(stateTimer / chargeTime);

                float e = 1f - Mathf.Exp(-chargePoseSmooth * stateTimer);
                swingPos = Vector3.Lerp(poseFromPos, chargeOffset, e);
                swingRot = Quaternion.Slerp(poseFromRot, Quaternion.Euler(chargeEuler), e);

                // Tremble builds with the charge, so the release is telegraphed.
                float tremble = chargeShakeAmount * charge01;
                float ct = Time.time * chargeShakeFrequency;
                swingPos += new Vector3(Mathf.Sin(ct) * tremble,
                                        Mathf.Cos(ct * 1.37f) * tremble,
                                        0f);

                if (!chargeFullFired && charge01 >= 1f)
                {
                    chargeFullFired = true;
                    AxeChargeFull?.Invoke();
                }
                break;
            }
            case State.Swing:
            {
                float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, swingTime));
                float e = t * t;
                swingPos = Vector3.Lerp(poseFromPos, impactOffset, e);
                swingRot = Quaternion.Slerp(poseFromRot, impactQ, e);

                if (!hitRegistered && t >= hitWindowStart && t <= hitWindowEnd)
                    TryHit();

                if (t >= 1f)
                {
                    poseFromPos = swingPos;
                    poseFromRot = swingRot;
                    state = State.Recover;
                    stateTimer = 0f;
                }
                break;
            }
            case State.Recover:
            {
                float t = Mathf.Clamp01(stateTimer / Mathf.Max(0.01f, recoverTime));
                float e = t * t * (3f - 2f * t);
                swingPos = Vector3.Lerp(poseFromPos, Vector3.zero, e);
                swingRot = Quaternion.Slerp(poseFromRot, Quaternion.identity, e);

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

    void ThrowAxe(float charge)
    {
        charge = Mathf.Clamp01(charge);

        float speed = throwSpeed + chargeSpeedBonus * charge;
        float up = throwUpSpeed + chargeUpSpeedBonus * charge;
        float spin = throwSpinSpeed + chargeSpinBonus * charge;

        Vector3 spawn = aimTransform.position + aimTransform.forward * throwSpawnForward;
        Quaternion rot = Quaternion.LookRotation(aimTransform.forward, Vector3.up);

        ThrownAxe axe = SpawnThrownAxe(spawn, rot, speed);
        if (axe == null) return;

        Vector3 velocity = aimTransform.forward * speed + Vector3.up * up;
        if (playerBody != null)
            velocity += playerBody.linearVelocity * inheritPlayerVelocity;

        axe.Launch(velocity, spin, playerColliders, aimTransform.position);

        activeAxe = axe;
        state = State.Thrown;
        stateTimer = 0f;
        cooldownTimer = throwCooldown;
        swingPos = Vector3.zero;
        swingRot = Quaternion.identity;
        charge01 = 0f;
        chargeFullFired = false;

        if (axeVisual != null)
            SetAxeVisible(false);

        AxeChargeReleased?.Invoke(charge);
        AxeThrown?.Invoke();
    }

    ThrownAxe SpawnThrownAxe(Vector3 pos, Quaternion rot, float launchSpeed)
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

        // Calibrated against the speed THIS throw actually used rather than the base
        // value, so a charged throw cannot double-scale the spin or peg the shake.
        axe.spinReferenceSpeed = launchSpeed;
        axe.impactReferenceSpeed = launchSpeed;

        // At high speed a single physics step covers a lot of ground, so the swept
        // chain needs more, shorter segments to stay continuous around the spin arc.
        axe.maxStepDistance = thrownMaxStepDistance;
        axe.maxSubSteps = thrownMaxSubSteps;

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
        charge01 = 0f;
        chargeFullFired = false;
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
        DrawChargeMeter();

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

    void DrawChargeMeter()
    {
        if (!showChargeMeter || state != State.Charging) return;

        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }

        float w = chargeMeterWidth;
        float h = chargeMeterHeight;
        float x = (Screen.width - w) * 0.5f;
        float y = Screen.height * chargeMeterScreenY;

        Color old = GUI.color;

        GUI.color = chargeMeterBack;
        GUI.DrawTexture(new Rect(x - 1f, y - 1f, w + 2f, h + 2f), whiteTex);

        GUI.color = charge01 >= 1f ? chargeMeterFull : chargeMeterFill;
        GUI.DrawTexture(new Rect(x, y, w * charge01, h), whiteTex);

        GUI.color = old;
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