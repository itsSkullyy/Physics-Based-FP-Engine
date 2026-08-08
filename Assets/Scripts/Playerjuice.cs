using UnityEngine;

// Watches the character controller's public state and fires juice off it.
// Requires no edits to FirstPersonCharacterController - it reads existing getters.
// Put this on the Player root.
[DefaultExecutionOrder(120)]
public class PlayerJuice : MonoBehaviour
{
    [Header("Refs")]
    public FirstPersonCharacterController controller;
    public CameraShaker shaker;
    public FirstPersonCameraRig cameraRig;
    public Grappling grappling;
    public BattleAxe axe;

    [Header("Landing")]
    public bool landDust = true;
    public float landMinSpeed = 4f;
    public float landBigSpeed = 24f;
    public float landShake = 0.4f;
    public float landFovPunch = -2.5f;
    public Vector3 landKick = new Vector3(0f, -0.05f, 0f);

    [Header("Footsteps")]
    public bool footstepDust = true;
    public float stepDistance = 2.6f;
    public float footstepMinSpeed = 3f;

    [Header("Slide")]
    public bool slideDust = true;
    public float slideDustInterval = 0.045f;
    public float slideMinSpeed = 5f;

    [Header("Wall Run")]
    public bool wallRunDust = true;
    public float wallRunDustInterval = 0.07f;
    public float wallRunStartShake = 0.18f;

    [Header("Wall Kick / Wall Jump")]
    public float wallKickShake = 0.3f;
    public float wallKickFov = 3.5f;
    public Vector3 wallKickKick = new Vector3(0f, 0.04f, -0.06f);

    [Header("Dart")]
    public float dartShake = 0.28f;
    public float dartFovPunch = 7f;
    public float dartChainFovBonus = 1.6f;
    public Vector3 dartKick = new Vector3(0f, 0f, -0.12f);

    [Header("Vault")]
    public float vaultShake = 0.16f;

    [Header("Grapple")]
    public float grappleAttachShake = 0.15f;
    public float grappleAttachFov = 3f;
    public float zipStartFov = 6f;
    public float zipStartShake = 0.2f;

    [Header("Axe")]
    public float axeThrowShake = 0.28f;
    public float axeThrowFov = 2.5f;
    public Vector3 axeThrowKick = new Vector3(0.03f, 0.05f, -0.14f);
    public Vector3 axeSwingKick = new Vector3(0f, -0.02f, 0.05f);
    public float axeHitShake = 0.5f;
    public float axeBounceShake = 0.7f;
    public float axeBounceFov = 8f;
    public float axeHitstop = 0.055f;
    public float axeHitstopScale = 0.06f;
    public float axeStickShake = 0.35f;
    public float axeStickFullRange = 6f;
    public float axeStickMaxRange = 28f;

    JuiceFX fx;

    bool wasGrounded;
    float lastFallSpeed;
    bool wasSliding;
    bool wasWallRunning;
    bool wasDarting;
    bool wasSwingingRope;
    bool wasZipping;
    bool wasAxeThrown;
    bool wasAxeSwinging;
    bool wasAxeStuck;
    int lastWallKicksLeft;
    int lastVaultTier;

    float stepAccum;
    float slideTimer;
    float wallRunTimer;
    Vector3 lastPos;

    void Awake()
    {
        if (controller == null) controller = GetComponent<FirstPersonCharacterController>();
        if (controller == null) controller = GetComponentInChildren<FirstPersonCharacterController>();

        if (controller == null)
        {
            Debug.LogError("PlayerJuice needs a FirstPersonCharacterController.", this);
            enabled = false;
            return;
        }

        if (grappling == null) grappling = controller.GetComponent<Grappling>();
        if (axe == null) axe = controller.GetComponentInChildren<BattleAxe>();
        if (cameraRig == null && controller.cameraTransform != null)
            cameraRig = controller.cameraTransform.GetComponentInChildren<FirstPersonCameraRig>();
        if (shaker == null) shaker = CameraShaker.Instance;
        if (shaker == null && cameraRig != null) shaker = cameraRig.GetComponent<CameraShaker>();

        lastPos = controller.transform.position;
        lastWallKicksLeft = controller.AirWallKicksLeft;
    }

    void Start()
    {
        fx = JuiceFX.Get();
        if (shaker == null) shaker = CameraShaker.Instance;

        if (axe != null)
        {
            axe.AxeThrown += OnAxeThrown;
            axe.AxeSwingStarted += OnAxeSwingStarted;
            axe.AxeHit += OnAxeHit;
        }
    }

    void OnDestroy()
    {
        if (axe != null)
        {
            axe.AxeThrown -= OnAxeThrown;
            axe.AxeSwingStarted -= OnAxeSwingStarted;
            axe.AxeHit -= OnAxeHit;
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;

        TrackLanding();
        TrackFootsteps(dt);
        TrackSlide(dt);
        TrackWallRun(dt);
        TrackWallKicks();
        TrackDart();
        TrackVault();
        TrackGrapple();
        TrackThrownAxe();

        lastPos = controller.transform.position;
    }

    // ---------------------------------------------------------------- ground

    void TrackLanding()
    {
        float vy = controller.Velocity.y;
        if (vy < 0f) lastFallSpeed = -vy;

        bool grounded = controller.IsGrounded;

        if (grounded && !wasGrounded && lastFallSpeed > landMinSpeed)
        {
            float strength = Mathf.InverseLerp(landMinSpeed, landBigSpeed, lastFallSpeed);

            if (landDust && fx != null && TryGetGround(out Vector3 point, out Vector3 normal))
                fx.LandDust(point, normal, strength);

            if (shaker != null)
            {
                shaker.AddTrauma(landShake * strength);
                shaker.AddKick(landKick * strength, new Vector3(2.5f * strength, 0f, 0f));
                shaker.AddFovPunch(landFovPunch * strength);
            }
        }

        if (grounded) lastFallSpeed = 0f;
        wasGrounded = grounded;
    }

    void TrackFootsteps(float dt)
    {
        if (!footstepDust || fx == null) return;

        if (!controller.IsGrounded || controller.IsSliding ||
            controller.CurrentSpeed < footstepMinSpeed)
        {
            stepAccum = 0f;
            return;
        }

        stepAccum += Vector3.Distance(controller.transform.position, lastPos);
        if (stepAccum < stepDistance) return;

        stepAccum = 0f;

        if (TryGetGround(out Vector3 point, out Vector3 normal))
        {
            float t = Mathf.InverseLerp(footstepMinSpeed, controller.maxSpeed, controller.CurrentSpeed);
            fx.Scuff(point, normal, -controller.Velocity.normalized, t * 0.5f);
        }
    }

    void TrackSlide(float dt)
    {
        if (!slideDust || fx == null) return;

        bool sliding = controller.IsSliding && controller.IsGrounded;

        if (sliding && !wasSliding && shaker != null)
            shaker.AddKick(new Vector3(0f, -0.03f, 0f), new Vector3(1.5f, 0f, 0f));

        wasSliding = sliding;

        if (!sliding || controller.CurrentSpeed < slideMinSpeed)
        {
            slideTimer = 0f;
            return;
        }

        slideTimer -= dt;
        if (slideTimer > 0f) return;
        slideTimer = slideDustInterval;

        if (TryGetGround(out Vector3 point, out Vector3 normal))
        {
            float t = Mathf.InverseLerp(slideMinSpeed, controller.maxSpeed * 1.4f, controller.CurrentSpeed);
            fx.Scuff(point, normal, -controller.Velocity, Mathf.Lerp(0.3f, 1f, t));
        }
    }

    // ---------------------------------------------------------------- air moves

    void TrackWallRun(float dt)
    {
        bool running = controller.IsWallRunning;

        if (running && !wasWallRunning && shaker != null)
        {
            shaker.AddTrauma(wallRunStartShake);
            shaker.AddKick(new Vector3(0.03f * controller.WallRunSide, 0f, 0f), Vector3.zero);
        }

        wasWallRunning = running;

        if (!running || !wallRunDust || fx == null) return;

        wallRunTimer -= dt;
        if (wallRunTimer > 0f) return;
        wallRunTimer = wallRunDustInterval;

        Vector3 origin = controller.transform.position + Vector3.up * 0.2f;
        Vector3 dir = controller.FlatRight * controller.WallRunSide;

        if (Physics.Raycast(origin, dir, out RaycastHit hit, controller.wallCheckDistance + 0.2f,
                controller.wallRunMask, QueryTriggerInteraction.Ignore))
        {
            float t = Mathf.InverseLerp(controller.minWallRunSpeed, controller.wallRunMaxSpeed,
                controller.CurrentSpeed);
            fx.Scuff(hit.point, hit.normal, -controller.Velocity, Mathf.Lerp(0.3f, 0.9f, t));
        }
    }

    void TrackWallKicks()
    {
        int left = controller.AirWallKicksLeft;

        if (left < lastWallKicksLeft)
        {
            if (fx != null)
                fx.AirPuff(controller.transform.position, controller.Velocity, 0.55f);

            if (shaker != null)
            {
                shaker.AddTrauma(wallKickShake);
                shaker.AddKick(wallKickKick, new Vector3(-2f, 0f, 0f));
                shaker.AddFovPunch(wallKickFov);
            }
        }

        lastWallKicksLeft = left;
    }

    void TrackDart()
    {
        bool darting = controller.IsDarting;

        if (darting && !wasDarting)
        {
            float chain = Mathf.Max(0, controller.DartChain - 1);

            if (fx != null)
                fx.AirPuff(controller.transform.position, -controller.Velocity, 0.7f);

            if (shaker != null)
            {
                shaker.AddTrauma(dartShake);
                shaker.AddKick(dartKick, new Vector3(-1.5f, 0f, 0f));
                shaker.AddFovPunch(dartFovPunch + chain * dartChainFovBonus);
            }
        }

        wasDarting = darting;
    }

    void TrackVault()
    {
        int tier = controller.VaultTier;

        if (tier > 0 && lastVaultTier == 0)
        {
            if (shaker != null)
                shaker.AddTrauma(vaultShake * tier * 0.6f);

            if (fx != null && TryGetGround(out Vector3 point, out Vector3 normal))
                fx.Scuff(point, normal, controller.Velocity, 0.4f);
        }

        lastVaultTier = tier;
    }

    void TrackGrapple()
    {
        if (grappling == null) return;

        bool swinging = grappling.IsSwinging;
        if (swinging && !wasSwingingRope && shaker != null)
        {
            shaker.AddTrauma(grappleAttachShake);
            shaker.AddFovPunch(grappleAttachFov);
            shaker.AddKick(new Vector3(0f, 0.03f, -0.05f), Vector3.zero);
        }
        wasSwingingRope = swinging;

        bool zipping = grappling.IsZipping;
        if (zipping && !wasZipping && shaker != null)
        {
            shaker.AddTrauma(zipStartShake);
            shaker.AddFovPunch(zipStartFov);
        }
        wasZipping = zipping;
    }

    // ---------------------------------------------------------------- axe

    void TrackThrownAxe()
    {
        if (axe == null) return;

        ThrownAxe thrown = axe.ActiveAxe;
        bool stuck = thrown != null && thrown.IsStuck;

        if (stuck && !wasAxeStuck && shaker != null)
            shaker.AddTraumaAtPoint(thrown.HeadPosition, axeStickShake,
                axeStickFullRange, axeStickMaxRange);

        wasAxeStuck = stuck;
    }

    void OnAxeThrown()
    {
        if (fx != null && controller.cameraTransform != null)
            fx.AirPuff(controller.cameraTransform.position + controller.cameraTransform.forward * 0.8f,
                controller.cameraTransform.forward, 0.3f);

        if (shaker == null) return;
        shaker.AddTrauma(axeThrowShake);
        shaker.AddKick(axeThrowKick, new Vector3(-3f, 1.5f, 2f));
        shaker.AddFovPunch(axeThrowFov);
    }

    void OnAxeSwingStarted()
    {
        if (shaker == null) return;
        shaker.AddKick(axeSwingKick, new Vector3(1.5f, 0f, -1f));
    }

    void OnAxeHit(Vector3 point, Vector3 normal, bool bounced)
    {
        if (fx != null)
        {
            fx.ImpactBurst(point, normal, bounced ? 1f : 0.6f);
            fx.Hitstop(axeHitstop, axeHitstopScale);
        }

        if (shaker == null) return;

        shaker.AddTrauma(bounced ? axeBounceShake : axeHitShake);
        shaker.AddKick(new Vector3(0f, 0.06f, -0.16f), new Vector3(-5f, 0f, 3f));

        if (bounced)
            shaker.AddFovPunch(axeBounceFov);
    }

    // ---------------------------------------------------------------- helpers

    bool TryGetGround(out Vector3 point, out Vector3 normal)
    {
        Vector3 origin = controller.transform.position + Vector3.up * 0.1f;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 4f,
                controller.groundMask, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            normal = hit.normal;
            return true;
        }

        point = controller.transform.position;
        normal = Vector3.up;
        return false;
    }
}