using UnityEngine;
using UnityEngine.InputSystem;

// First person parkour controller.
// WASD move, Space jump, Shift slide, LMB hold = zip grapple.
// cameraTransform must be the CameraAnchor empty, not a real camera.
[RequireComponent(typeof(Rigidbody))]
public class FirstPersonCharacterController : MonoBehaviour
{
    [Header("Camera")]
    public Transform cameraTransform;
    public float mouseSensitivity = 0.1f;
    public float maxLookAngle = 89f;
    public bool lockCursor = true;

    [Header("Movement")]
    public float baseSpeed = 7f;
    public float maxSpeed = 17f;
    public float acceleration = 80f;
    [Range(0f, 1f)] public float airControl = 0.6f;
    public float momentumBuildRate = 0.8f;
    public float momentumDecayRate = 1.4f;

    [Header("Character Visual")]
    public Transform characterVisual;
    public bool visualPivotAtFeet = false;

    [Header("Friction")]
    public float groundFriction = 8f;
    public float counterMovement = 12f;
    public float airCounterMovement = 4f;
    public float overspeedDrag = 1.1f;

    [Header("Slopes")]
    public float maxSlopeAngle = 45f;
    public float groundStickForce = 30f;
    public float steepSlideForce = 22f;

    [Header("Slide")]
    public bool enableSlide = true;
    public float slideFriction = 0.2f;
    public float slideBoost = 3f;
    public float slideSlopeAccel = 30f;
    public float slideMinSpeed = 4f;
    [Range(0f, 1f)] public float slideSteer = 0.2f;
    public float slideCrawlSpeed = 3f;
    public float slideHeight = 0.9f;
    public float crouchTransitionSpeed = 12f;

    [Header("Slide Launch")]
    public bool enableSlideLaunch = true;
    public float slideLaunchMinSpeed = 6f;
    [Range(0f, 1.5f)] public float slideLaunchUpFactor = 0.75f;
    [Range(0f, 1f)] public float slideLaunchForwardKeep = 0.5f;
    public float slideLaunchMinUp = 7f;

    [Header("Jump")]
    public float jumpHeight = 1.7f;
    public float fallMultiplier = 2.3f;
    public float lowJumpMultiplier = 2.8f;
    public float coyoteTime = 0.12f;
    public float jumpBufferTime = 0.12f;
    public float jumpGroundIgnoreTime = 0.15f;

    [Header("Vault")]
    public bool enableVault = true;
    public float vaultCheckDistance = 1.1f;
    public float minVaultHeight = 0.35f;
    public float lowVaultMaxHeight = 0.75f;
    public float regularVaultMaxHeight = 1.25f;
    public float jumpVaultMaxHeight = 2.1f;
    [Range(0f, 1f)] public float lowVaultSpeedKeep = 0.95f;
    [Range(0f, 1f)] public float regularVaultSpeedKeep = 0.75f;
    [Range(0f, 1f)] public float jumpVaultSpeedKeep = 0.25f;
    public float lowVaultDuration = 0.25f;
    public float regularVaultDuration = 0.35f;
    public float jumpVaultDuration = 0.5f;
    public float vaultExitUpPop = 1.5f;
    public float autoVaultCooldown = 0.3f;

    [Header("Wall Run")]
    public bool enableWallRun = true;
    public LayerMask wallRunMask = ~0;
    public string wallRunTag = "";
    public float wallCheckDistance = 0.85f;
    public float minWallRunSpeed = 4f;
    public float wallRunMaxDuration = 1.8f;
    [Range(0f, 1f)] public float wallRunGravityScale = 0.25f;
    public float wallRunStickForce = 12f;
    public float wallRunAccel = 30f;
    public float wallRunMaxSpeed = 14f;
    public float wallJumpUpSpeed = 7.5f;
    public float wallJumpAwaySpeed = 7f;
    public float wallRunCooldown = 0.3f;

    [Header("Wall Kick")]
    public bool enableWallKick = true;
    public float wallKickDistance = 0.9f;
    public float wallKickUpSpeed = 7f;
    public float wallKickAwaySpeed = 8f;
    public float wallKickCooldown = 0.25f;

    [Header("Zip Wall Run")]
    public bool enableZipWallRun = true;
    public float zipWallRunSearchDistance = 1.6f;
    public float zipWallRunBoostSpeed = 17f;

    [Header("Ground Check")]
    public LayerMask groundMask = ~0;
    public float groundCheckRadius = 0.35f;
    public float groundCheckDistance = 0.75f;

    [Header("Zip")]
    public LineRenderer lineRenderer;
    public Transform gunTip;
    public LayerMask grappleMask = ~0;
    public string grappleTag = "";
    public bool allowTriggerGrapples = true;
    public float maxGrappleDistance = 50f;
    public float zipMaxSpeed = 28f;
    public float zipAcceleration = 70f;
    public float zipArrivalDistance = 2.5f;
    public float zipUpwardFling = 8f;
    [Range(0f, 1f)] public float zipSpeedCarry = 0.6f;
    public float zipCompletionBoost = 10f;
    public float zipSteerForce = 11f;
    public float ropeDrawSpeed = 120f;

    [Header("Targeting & Crosshair")]
    public float aimAssistAngle = 30f;
    public float angleWeight = 1f;
    public float distanceWeight = 0.4f;
    public float minTargetDistance = 2f;
    public float reticleSize = 40f;
    public float crosshairSize = 6f;
    public Color crosshairColor = new Color(1f, 1f, 1f, 0.9f);
    public Color reticleColor = new Color(1f, 1f, 1f, 0.55f);
    public Color reticleLockedColor = new Color(0.3f, 1f, 0.6f, 0.95f);

    Rigidbody rb;
    float pitch;
    float yaw;
    float momentum;
    Vector3 lastMoveDir = Vector3.forward;

    bool isGrounded;
    bool onSteepSlope;
    Vector3 groundNormal = Vector3.up;
    float coyoteCounter;
    float jumpBufferCounter;
    float groundIgnoreCounter;
    bool jumpHeld;

    bool isSliding;
    bool slideBoostGiven;

    CapsuleCollider capsule;
    float standHeight;
    float capsuleBottomY;
    float currentHeight;
    Vector3 camStandLocalPos;
    bool crouchedByObstruction;

    bool isZipping;
    Vector3 zipPoint;
    Vector3 currentRopeEnd;

    bool isVaulting;
    int vaultTier;
    Vector3 vaultDir;
    Vector3 vaultStart;
    Vector3 vaultControl;
    Vector3 vaultEnd;
    float vaultDuration;
    float vaultEntrySpeed;
    float vaultKeep;
    float vaultTimer;
    float vaultCooldownTimer;

    bool isWallRunning;
    int wallRunSide;
    Vector3 wallNormal;
    float wallRunTimer;
    float wallRunCooldownTimer;
    float wallKickCooldownTimer;

    Vector3 visualStartScale = Vector3.one;
    Vector3 visualStartLocalPos;

    Camera cam;
    bool hasTarget;
    Vector3 targetPoint;
    Vector3 targetDisplayPoint;
    Collider targetCollider;
    Texture2D dotTex;
    Texture2D ringTex;

    public bool IsGrounded => isGrounded;
    public bool IsSliding => isSliding;
    public bool IsZipping => isZipping;
    public bool IsVaulting => isVaulting;
    public int VaultTier => isVaulting ? vaultTier : 0;
    public bool IsWallRunning => isWallRunning;
    public int WallRunSide => isWallRunning ? wallRunSide : 0;
    public float CurrentSpeed => new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude;
    public Vector3 Velocity => rb.linearVelocity;
    public float CrouchAmount => standHeight > 0f ? 1f - currentHeight / standHeight : 0f;
    public Vector3 FlatForward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    public Vector3 FlatRight => Quaternion.Euler(0f, yaw, 0f) * Vector3.right;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        yaw = transform.eulerAngles.y;

        if (cameraTransform == null)
            Debug.LogError("cameraTransform not assigned. Assign the CameraAnchor empty.", this);
        else if (cameraTransform.GetComponent<Camera>() != null)
            Debug.LogWarning("cameraTransform is a real Camera. Assign the CameraAnchor empty instead.", this);

        if (gunTip == null)
            gunTip = cameraTransform;

        capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            standHeight = capsule.height;
            capsuleBottomY = capsule.center.y - standHeight * 0.5f;
            currentHeight = standHeight;
        }

        if (cameraTransform != null)
        {
            camStandLocalPos = cameraTransform.localPosition;
            cam = cameraTransform.GetComponent<Camera>();
        }
        if (cam == null)
            cam = Camera.main;

        if (characterVisual != null)
        {
            visualStartScale = characterVisual.localScale;
            visualStartLocalPos = characterVisual.localPosition;
        }

        dotTex = MakeDotTexture(16);
        ringTex = MakeRingTexture(64, 6f);

        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
            lineRenderer.useWorldSpace = true;
        }

        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        HandleLook();
        BufferJumpInput();
        UpdateGrappleTarget();
        HandleZipInput();
        UpdateCameraHeight();
    }

    void LateUpdate()
    {
        DrawRope();
    }

    void FixedUpdate()
    {
        GroundCheck();
        UpdateWallRun();
        HandleSlideState();
        AlignSlideVelocityToGround();
        UpdateCrouchHeight();
        TryAutoVault();
        HandleMovement();
        HandleJump();
        HandleVault();
        HandleZip();
        ApplyBetterGravity();
        ApplyGroundStick();
    }

    // -------------------- Input --------------------

    Vector2 ReadMoveInput()
    {
        Vector2 move = Vector2.zero;
        Keyboard k = Keyboard.current;
        if (k == null) return move;

        if (k.wKey.isPressed || k.upArrowKey.isPressed) move.y += 1f;
        if (k.sKey.isPressed || k.downArrowKey.isPressed) move.y -= 1f;
        if (k.dKey.isPressed || k.rightArrowKey.isPressed) move.x += 1f;
        if (k.aKey.isPressed || k.leftArrowKey.isPressed) move.x -= 1f;
        return move;
    }

    void HandleLook()
    {
        Vector2 delta = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;

        yaw += delta.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
            cameraTransform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        if (characterVisual != null)
            characterVisual.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    void BufferJumpInput()
    {
        Keyboard k = Keyboard.current;
        if (k == null) return;

        if (k.spaceKey.wasPressedThisFrame)
            jumpBufferCounter = jumpBufferTime;

        jumpHeld = k.spaceKey.isPressed;
    }

    // -------------------- Ground --------------------

    void GroundCheck()
    {
        isGrounded = false;
        onSteepSlope = false;
        groundNormal = Vector3.up;

        if (groundIgnoreCounter > 0f)
        {
            groundIgnoreCounter -= Time.fixedDeltaTime;
            return;
        }

        if (Physics.SphereCast(transform.position, groundCheckRadius, Vector3.down,
                out RaycastHit hit, groundCheckDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            groundNormal = hit.normal;
            float angle = Vector3.Angle(hit.normal, Vector3.up);

            if (angle <= maxSlopeAngle)
                isGrounded = true;
            else
                onSteepSlope = true;
        }
    }

    void ApplyGroundStick()
    {
        if (!isGrounded || isZipping || isVaulting || isWallRunning) return;
        if (rb.linearVelocity.y > 2f) return;

        rb.AddForce(-groundNormal * groundStickForce, ForceMode.Acceleration);
    }

    // -------------------- Movement --------------------

    void HandleMovement()
    {
        Vector2 input = ReadMoveInput();
        Vector3 wishDir = FlatRight * input.x + FlatForward * input.y;
        bool hasInput = wishDir.sqrMagnitude > 0.01f;
        wishDir = hasInput ? wishDir.normalized : Vector3.zero;

        Vector3 moveDir = wishDir;
        if (isGrounded && hasInput)
            moveDir = Vector3.ProjectOnPlane(wishDir, groundNormal).normalized;

        UpdateMomentum(hasInput, wishDir);

        float targetSpeed = Mathf.Lerp(baseSpeed, maxSpeed, momentum);
        Vector3 horizVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

        if (isVaulting)
        {
        }
        else if (isWallRunning)
        {
            WallRunMovement();
        }
        else if (isZipping)
        {
            if (hasInput)
                rb.AddForce(wishDir * zipSteerForce, ForceMode.Acceleration);
        }
        else if (isSliding)
        {
            SlideMovement(hasInput, moveDir, horizVel);
        }
        else if (hasInput)
        {
            float control = isGrounded ? 1f : airControl;
            float speedAlong = Vector3.Dot(horizVel, new Vector3(moveDir.x, 0f, moveDir.z).normalized);

            if (speedAlong < targetSpeed)
            {
                float accel = Mathf.Min(acceleration, (targetSpeed - speedAlong) / Time.fixedDeltaTime);
                rb.AddForce(moveDir * accel * control, ForceMode.Acceleration);
            }

            Vector3 flatMove = new Vector3(moveDir.x, 0f, moveDir.z).normalized;
            Vector3 lateral = horizVel - Vector3.Project(horizVel, flatMove);
            rb.AddForce(-lateral * (isGrounded ? counterMovement : airCounterMovement),
                ForceMode.Acceleration);

            if (isGrounded)
            {
                float overspeed = horizVel.magnitude - targetSpeed;
                if (overspeed > 0f)
                    rb.AddForce(-horizVel.normalized * overspeed * overspeedDrag, ForceMode.Acceleration);
            }
        }
        else if (isGrounded)
        {
            rb.AddForce(-horizVel * groundFriction, ForceMode.Acceleration);
        }

        if (onSteepSlope)
        {
            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
            rb.AddForce(downhill * steepSlideForce, ForceMode.Acceleration);
        }
    }

    void UpdateMomentum(bool hasInput, Vector3 wishDir)
    {
        if (hasInput)
        {
            float alignment = Vector3.Dot(wishDir, lastMoveDir);
            if (alignment < 0f)
                momentum += alignment * momentumDecayRate * Time.fixedDeltaTime;

            momentum += momentumBuildRate * Time.fixedDeltaTime;
            lastMoveDir = wishDir;
        }
        else if (isGrounded && !isSliding)
        {
            momentum -= momentumDecayRate * Time.fixedDeltaTime;
        }

        momentum = Mathf.Clamp01(momentum);
    }

    // -------------------- Slide --------------------

    void HandleSlideState()
    {
        if (!enableSlide) { isSliding = false; return; }

        Keyboard k = Keyboard.current;
        bool slideKey = k != null && k.leftShiftKey.isPressed;

        bool wasSliding = isSliding;
        // Stays sliding under a ceiling even if grounding flickers.
        isSliding = slideKey && !isVaulting &&
                    (isGrounded || (wasSliding && HasCeilingAbove()));

        if (isSliding && !wasSliding)
        {
            Vector3 horizVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
            if (!slideBoostGiven && horizVel.magnitude > slideMinSpeed)
            {
                rb.AddForce(horizVel.normalized * slideBoost, ForceMode.VelocityChange);
                slideBoostGiven = true;
            }
        }

        if (!slideKey && isGrounded && !wasSliding)
            slideBoostGiven = false;

        if (!isSliding && (wasSliding || crouchedByObstruction))
            crouchedByObstruction = HasCeilingAbove();
    }

    void AlignSlideVelocityToGround()
    {
        if (!isSliding || !isGrounded) return;

        Vector3 vel = rb.linearVelocity;
        if (vel.y > 0.5f) return;

        Vector3 alongGround = Vector3.ProjectOnPlane(vel, groundNormal);
        if (alongGround.sqrMagnitude < 0.01f) return;

        rb.linearVelocity = alongGround.normalized * vel.magnitude;
    }

    void SlideMovement(bool hasInput, Vector3 moveDir, Vector3 horizVel)
    {
        rb.AddForce(-horizVel * slideFriction, ForceMode.Acceleration);

        float slopeAngle = Vector3.Angle(groundNormal, Vector3.up);
        if (slopeAngle > 3f)
        {
            Vector3 downhill = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
            rb.AddForce(downhill * slideSlopeAccel * (slopeAngle / maxSlopeAngle), ForceMode.Acceleration);
        }

        if (!hasInput) return;

        // Below crawl speed: real acceleration so the slide doubles as a crawl.
        Vector3 flatMove = new Vector3(moveDir.x, 0f, moveDir.z).normalized;
        float along = Vector3.Dot(horizVel, flatMove);

        if (along < slideCrawlSpeed)
        {
            float accel = Mathf.Min(acceleration * 0.6f,
                (slideCrawlSpeed - along) / Time.fixedDeltaTime);
            rb.AddForce(moveDir * accel, ForceMode.Acceleration);
        }
        else
        {
            rb.AddForce(moveDir * acceleration * slideSteer, ForceMode.Acceleration);
        }
    }

    bool WantsCrouchHeight => isSliding || crouchedByObstruction || isVaulting;

    void UpdateCrouchHeight()
    {
        if (capsule == null) return;

        float targetHeight = WantsCrouchHeight ? slideHeight : standHeight;

        // Never grow into a ceiling.
        if (targetHeight > currentHeight && HasCeilingAbove())
            targetHeight = currentHeight;

        currentHeight = Mathf.MoveTowards(currentHeight, targetHeight,
            crouchTransitionSpeed * Time.fixedDeltaTime);

        capsule.height = currentHeight;
        Vector3 c = capsule.center;
        c.y = capsuleBottomY + currentHeight * 0.5f;
        capsule.center = c;

        if (characterVisual != null)
        {
            float ratio = currentHeight / standHeight;
            Vector3 s = visualStartScale;
            s.y *= ratio;
            characterVisual.localScale = s;

            Vector3 p = visualStartLocalPos;
            if (!visualPivotAtFeet)
                p.y -= (standHeight - currentHeight) * 0.5f;
            characterVisual.localPosition = p;
        }
    }

    void UpdateCameraHeight()
    {
        if (cameraTransform == null || capsule == null) return;

        float crouchDelta = standHeight - currentHeight;
        Vector3 target = camStandLocalPos + Vector3.down * crouchDelta;
        cameraTransform.localPosition = Vector3.Lerp(cameraTransform.localPosition, target,
            1f - Mathf.Exp(-crouchTransitionSpeed * Time.deltaTime));
    }

    bool HasCeilingAbove()
    {
        if (capsule == null) return false;

        float needed = (standHeight - currentHeight) + 0.05f;
        Vector3 origin = transform.position +
            Vector3.up * (capsuleBottomY + currentHeight - capsule.radius);

        return Physics.SphereCast(origin, capsule.radius * 0.9f, Vector3.up,
            out _, needed, groundMask, QueryTriggerInteraction.Ignore);
    }

    // -------------------- Jump / gravity --------------------

    void HandleJump()
    {
        coyoteCounter = isGrounded ? coyoteTime : coyoteCounter - Time.fixedDeltaTime;
        jumpBufferCounter -= Time.fixedDeltaTime;

        if (jumpBufferCounter <= 0f || isVaulting) return;

        if (coyoteCounter > 0f)
        {
            float g = Mathf.Abs(Physics.gravity.y);
            Vector3 v = rb.linearVelocity;
            v.y = Mathf.Sqrt(2f * g * jumpHeight);
            rb.linearVelocity = v;

            jumpBufferCounter = 0f;
            coyoteCounter = 0f;
            groundIgnoreCounter = jumpGroundIgnoreTime;
            isGrounded = false;
        }
        else if (isWallRunning)
        {
            WallJump();
            jumpBufferCounter = 0f;
        }
        else if (enableWallKick && wallKickCooldownTimer <= 0f && TryWallKick())
        {
            jumpBufferCounter = 0f;
        }
    }

    void ApplyBetterGravity()
    {
        if (isZipping || isVaulting || isWallRunning) return;

        Vector3 extra = Vector3.zero;

        if (rb.linearVelocity.y < 0f)
            extra = Physics.gravity * (fallMultiplier - 1f);
        else if (rb.linearVelocity.y > 0f && !jumpHeld)
            extra = Physics.gravity * (lowJumpMultiplier - 1f);

        rb.AddForce(extra, ForceMode.Acceleration);
    }

    // -------------------- Wall run / kick --------------------

    void UpdateWallRun()
    {
        wallRunCooldownTimer -= Time.fixedDeltaTime;
        wallKickCooldownTimer -= Time.fixedDeltaTime;

        if (isWallRunning)
        {
            wallRunTimer += Time.fixedDeltaTime;

            bool wallStillThere = CheckWallSide(wallRunSide, out RaycastHit hit);
            float alongSpeed = Vector3.ProjectOnPlane(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z), wallNormal).magnitude;

            if (isGrounded || !wallStillThere || wallRunTimer > wallRunMaxDuration ||
                alongSpeed < minWallRunSpeed * 0.7f)
            {
                StopWallRun();
                return;
            }

            wallNormal = hit.normal;
            return;
        }

        if (!enableWallRun || isGrounded || isVaulting || isZipping) return;
        if (wallRunCooldownTimer > 0f) return;
        if (ReadMoveInput().y <= 0.1f) return;

        for (int side = -1; side <= 1; side += 2)
        {
            if (!CheckWallSide(side, out RaycastHit hit)) continue;

            float alongSpeed = Vector3.ProjectOnPlane(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z), hit.normal).magnitude;
            if (alongSpeed < minWallRunSpeed) continue;

            StartWallRun(side, hit.normal);
            return;
        }
    }

    bool CheckWallSide(int side, out RaycastHit hit)
    {
        Vector3 dir = FlatRight * side;
        Vector3 origin = transform.position + Vector3.up * 0.2f;

        if (Physics.Raycast(origin, dir, out hit, wallCheckDistance,
                wallRunMask, QueryTriggerInteraction.Ignore)
            && Vector3.Angle(hit.normal, Vector3.up) > 60f
            && (string.IsNullOrEmpty(wallRunTag) || hit.collider.CompareTag(wallRunTag)))
            return true;

        hit = default;
        return false;
    }

    void StartWallRun(int side, Vector3 normal)
    {
        isWallRunning = true;
        wallRunSide = side;
        wallNormal = normal;
        wallRunTimer = 0f;

        Vector3 v = rb.linearVelocity;
        if (v.y < 0f) v.y *= 0.35f;
        float into = Vector3.Dot(v, -normal);
        if (into > 0f) v += normal * into;
        rb.linearVelocity = v;
    }

    void StopWallRun()
    {
        if (!isWallRunning) return;
        isWallRunning = false;
        wallRunCooldownTimer = wallRunCooldown;
    }

    void WallRunMovement()
    {
        Vector3 v = rb.linearVelocity;

        float outward = Vector3.Dot(v, wallNormal);
        if (outward > 0f) v -= wallNormal * outward;

        Vector3 horiz = new Vector3(v.x, 0f, v.z);
        Vector3 along = Vector3.ProjectOnPlane(horiz, wallNormal);
        Vector3 alongDir = along.sqrMagnitude > 0.01f
            ? along.normalized
            : Vector3.ProjectOnPlane(FlatForward, wallNormal).normalized;

        float targetSpeed = Mathf.Clamp(along.magnitude, baseSpeed,
            Mathf.Max(wallRunMaxSpeed, along.magnitude));
        Vector3 newHoriz = Vector3.MoveTowards(horiz, alongDir * targetSpeed,
            wallRunAccel * Time.fixedDeltaTime);

        rb.linearVelocity = new Vector3(newHoriz.x, v.y, newHoriz.z);

        rb.AddForce(-Physics.gravity * (1f - wallRunGravityScale), ForceMode.Acceleration);
        rb.AddForce(-wallNormal * wallRunStickForce, ForceMode.Acceleration);
    }

    void WallJump()
    {
        Vector3 v = rb.linearVelocity;
        Vector3 along = Vector3.ProjectOnPlane(new Vector3(v.x, 0f, v.z), wallNormal) * 0.9f;

        rb.linearVelocity = along
            + wallNormal * wallJumpAwaySpeed
            + Vector3.up * wallJumpUpSpeed;

        StopWallRun();
        groundIgnoreCounter = jumpGroundIgnoreTime;
    }

    bool TryWallKick()
    {
        Vector3[] dirs =
        {
            FlatForward, -FlatForward,
            FlatRight, -FlatRight
        };

        RaycastHit best = default;
        float bestDist = float.MaxValue;
        foreach (Vector3 d in dirs)
        {
            Vector3 origin = transform.position + Vector3.up * 0.2f;

            if (Physics.Raycast(origin, d, out RaycastHit hit, wallKickDistance,
                    groundMask, QueryTriggerInteraction.Ignore)
                && Vector3.Angle(hit.normal, Vector3.up) > 60f
                && hit.distance < bestDist)
            {
                best = hit;
                bestDist = hit.distance;
            }
        }
        if (bestDist == float.MaxValue) return false;

        Vector3 v = rb.linearVelocity;
        Vector3 tangential = (v - best.normal * Vector3.Dot(v, best.normal)) * 0.5f;

        rb.linearVelocity = new Vector3(tangential.x, 0f, tangential.z)
            + best.normal * wallKickAwaySpeed
            + Vector3.up * wallKickUpSpeed;

        wallKickCooldownTimer = wallKickCooldown;
        groundIgnoreCounter = 0.1f;
        return true;
    }

    bool TryZipWallRun()
    {
        if (!enableZipWallRun || !enableWallRun || isGrounded) return false;

        RaycastHit best = default;
        float bestDist = float.MaxValue;
        Vector3 origin = transform.position + Vector3.up * 0.2f;

        for (int i = 0; i < 8; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, zipWallRunSearchDistance,
                    wallRunMask, QueryTriggerInteraction.Ignore)
                && Vector3.Angle(hit.normal, Vector3.up) > 60f
                && (string.IsNullOrEmpty(wallRunTag) || hit.collider.CompareTag(wallRunTag))
                && hit.distance < bestDist)
            {
                best = hit;
                bestDist = hit.distance;
            }
        }
        if (bestDist == float.MaxValue) return false;

        Vector3 n = best.normal;

        Vector3 refDir = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        if (refDir.sqrMagnitude < 1f)
            refDir = FlatForward;
        Vector3 alongDir = Vector3.ProjectOnPlane(refDir, n).normalized;
        if (alongDir.sqrMagnitude < 0.01f)
            alongDir = Vector3.ProjectOnPlane(FlatForward, n).normalized;

        int side = Vector3.Dot(-n, FlatRight) > 0f ? 1 : -1;
        StartWallRun(side, n);

        float boost = Mathf.Max(CurrentSpeed, zipWallRunBoostSpeed);
        rb.linearVelocity = alongDir * boost + Vector3.up * 1f;

        return true;
    }

    // -------------------- Vault --------------------

    void TryAutoVault()
    {
        vaultCooldownTimer -= Time.fixedDeltaTime;
        if (vaultCooldownTimer > 0f || isVaulting) return;

        TryStartVault();
    }

    bool TryStartVault()
    {
        if (!enableVault || isVaulting || isZipping || capsule == null) return false;

        Vector3 fwd = FlatForward;

        bool pushingForward = ReadMoveInput().y > 0.1f;
        float speedToward = Vector3.Dot(rb.linearVelocity, fwd);
        if (isSliding)
        {
            if (speedToward < slideLaunchMinSpeed) return false;
        }
        else if (!pushingForward || speedToward < 1.5f)
        {
            return false;
        }

        float feetY = transform.position.y + capsuleBottomY;

        RaycastHit wallHit = default;
        bool foundWall = false;
        float[] checkHeights = { 0.3f, 0.75f, 1.2f };
        foreach (float h in checkHeights)
        {
            Vector3 origin = transform.position + Vector3.up * (capsuleBottomY + h);
            if (Physics.Raycast(origin, fwd, out RaycastHit hit, vaultCheckDistance,
                    groundMask, QueryTriggerInteraction.Ignore)
                && Vector3.Angle(hit.normal, Vector3.up) > 60f
                && Vector3.Dot(-hit.normal, fwd) > 0.5f)
            {
                wallHit = hit;
                foundWall = true;
                break;
            }
        }
        if (!foundWall) return false;

        Vector3 probe = wallHit.point - wallHit.normal * 0.2f;
        Vector3 topStart = new Vector3(probe.x, feetY + jumpVaultMaxHeight + 0.3f, probe.z);
        if (!Physics.Raycast(topStart, Vector3.down, out RaycastHit topHit,
                jumpVaultMaxHeight + 0.3f, groundMask, QueryTriggerInteraction.Ignore))
            return false;
        if (Vector3.Angle(topHit.normal, Vector3.up) > maxSlopeAngle) return false;

        float height = topHit.point.y - feetY;
        if (height < minVaultHeight || height > jumpVaultMaxHeight) return false;

        Vector3 dir = Vector3.ProjectOnPlane(-wallHit.normal, Vector3.up).normalized;

        if (isSliding && enableSlideLaunch && height <= regularVaultMaxHeight)
        {
            SlideLaunch(dir);
            return true;
        }

        Vector3 overPoint = topHit.point + dir * 0.4f + Vector3.up * 0.15f;
        float r = capsule.radius * 0.9f;
        Vector3 capBottom = overPoint + Vector3.up * r;
        Vector3 capTop = overPoint + Vector3.up * Mathf.Max(slideHeight - r, r + 0.05f);
        if (Physics.CheckCapsule(capBottom, capTop, r, groundMask, QueryTriggerInteraction.Ignore))
            return false;

        if (height <= lowVaultMaxHeight)
        {
            vaultTier = 1; vaultKeep = lowVaultSpeedKeep; vaultDuration = lowVaultDuration;
        }
        else if (height <= regularVaultMaxHeight)
        {
            vaultTier = 2; vaultKeep = regularVaultSpeedKeep; vaultDuration = regularVaultDuration;
        }
        else
        {
            vaultTier = 3; vaultKeep = jumpVaultSpeedKeep; vaultDuration = jumpVaultDuration;
        }

        float pivotAboveFeet = -capsuleBottomY;

        vaultDir = dir;
        vaultEntrySpeed = Mathf.Max(CurrentSpeed, baseSpeed * 0.6f);

        vaultStart = rb.position;
        vaultEnd = overPoint + Vector3.up * pivotAboveFeet;
        float peakY = topHit.point.y + 0.3f + pivotAboveFeet;
        vaultControl = Vector3.Lerp(vaultStart, vaultEnd, 0.4f);
        vaultControl.y = Mathf.Max(peakY, vaultEnd.y + 0.1f);

        vaultDuration *= Mathf.Clamp(8f / vaultEntrySpeed, 0.75f, 1.15f);

        vaultTimer = 0f;
        isVaulting = true;

        // Kinematic glide, momentum handed back in FinishVault.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.isKinematic = true;

        return true;
    }

    void HandleVault()
    {
        if (!isVaulting) return;

        vaultTimer += Time.fixedDeltaTime;
        float t = Mathf.Clamp01(vaultTimer / vaultDuration);
        float e = t * t * (3f - 2f * t);

        Vector3 a = Vector3.Lerp(vaultStart, vaultControl, e);
        Vector3 b = Vector3.Lerp(vaultControl, vaultEnd, e);
        rb.MovePosition(Vector3.Lerp(a, b, e));

        if (t >= 1f)
            FinishVault();
    }

    void FinishVault()
    {
        if (!isVaulting) return;

        isVaulting = false;
        vaultCooldownTimer = autoVaultCooldown;

        rb.isKinematic = false;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        float exitSpeed = vaultEntrySpeed * vaultKeep;
        if (vaultTier != 3)
            exitSpeed = Mathf.Max(exitSpeed, baseSpeed * 0.5f);

        rb.linearVelocity = vaultDir * exitSpeed + Vector3.up * vaultExitUpPop;
    }

    void SlideLaunch(Vector3 wallDir)
    {
        Vector3 v = rb.linearVelocity;
        float speed = v.magnitude;

        Vector3 horiz = new Vector3(v.x, 0f, v.z);
        Vector3 travelDir = horiz.sqrMagnitude > 1f ? horiz.normalized : wallDir;

        float up = Mathf.Max(speed * slideLaunchUpFactor, slideLaunchMinUp);
        Vector3 forward = travelDir * (speed * slideLaunchForwardKeep);

        rb.linearVelocity = new Vector3(forward.x, up, forward.z);

        isSliding = false;
        groundIgnoreCounter = 0.15f;
        vaultCooldownTimer = 0.4f;
    }

    // -------------------- Zip --------------------

    void HandleZipInput()
    {
        Mouse m = Mouse.current;
        if (m == null) return;

        if (m.leftButton.wasPressedThisFrame)
            TryStartZip();

        if (m.leftButton.wasReleasedThisFrame)
            StopZip();
    }

    bool TryGetGrapplePoint(out Vector3 point)
    {
        point = default;
        if (cameraTransform == null) return false;

        if (hasTarget)
        {
            point = targetPoint;
            return true;
        }

        QueryTriggerInteraction qti = allowTriggerGrapples
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);
        RaycastHit[] hits = Physics.RaycastAll(ray, maxGrappleDistance, grappleMask, qti);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit h in hits)
        {
            bool tagOk = string.IsNullOrEmpty(grappleTag) || h.collider.CompareTag(grappleTag);

            if (tagOk)
            {
                point = h.point;
                return true;
            }

            if (!h.collider.isTrigger)
                return false;
        }

        return false;
    }

    void TryStartZip()
    {
        if (isZipping || isVaulting) return;
        if (!TryGetGrapplePoint(out Vector3 point)) return;

        zipPoint = point;
        isZipping = true;

        currentRopeEnd = gunTip != null ? gunTip.position : transform.position;

        if (lineRenderer != null)
            lineRenderer.positionCount = 2;
    }

    void StopZip()
    {
        if (!isZipping) return;

        isZipping = false;

        if (lineRenderer != null)
            lineRenderer.positionCount = 0;
    }

    void HandleZip()
    {
        if (!isZipping) return;

        Vector3 toPoint = zipPoint - transform.position;
        float dist = toPoint.magnitude;

        if (dist <= zipArrivalDistance)
        {
            if (TryZipWallRun())
            {
                StopZip();
                return;
            }

            Vector3 v = rb.linearVelocity;
            Vector3 dir = v.sqrMagnitude > 1f
                ? v.normalized
                : (cameraTransform != null ? cameraTransform.forward : transform.forward);

            Vector3 outVel = dir * (v.magnitude * zipSpeedCarry + zipCompletionBoost);
            outVel.y = Mathf.Max(outVel.y, zipUpwardFling);
            rb.linearVelocity = outVel;

            StopZip();
            return;
        }

        Vector3 desired = (toPoint / dist) * zipMaxSpeed;
        rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, desired,
            zipAcceleration * Time.fixedDeltaTime);
    }

    // -------------------- Targeting --------------------

    void UpdateGrappleTarget()
    {
        hasTarget = false;
        targetCollider = null;
        if (cameraTransform == null || isZipping) return;

        QueryTriggerInteraction qti = allowTriggerGrapples
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        Collider[] candidates = Physics.OverlapSphere(cameraTransform.position,
            maxGrappleDistance, grappleMask, qti);

        float bestScore = float.MaxValue;

        foreach (Collider c in candidates)
        {
            if (c.attachedRigidbody == rb) continue;
            if (!string.IsNullOrEmpty(grappleTag) && !c.CompareTag(grappleTag)) continue;

            Vector3 approxPoint = c.bounds.ClosestPoint(cameraTransform.position);
            Vector3 toPoint = approxPoint - cameraTransform.position;
            float dist = toPoint.magnitude;
            if (dist < minTargetDistance || dist > maxGrappleDistance) continue;

            float angle = Vector3.Angle(cameraTransform.forward, toPoint);
            if (angle > aimAssistAngle) continue;

            Vector3 dir = toPoint / dist;
            Vector3 anchor = approxPoint;
            if (c.Raycast(new Ray(cameraTransform.position, dir), out RaycastHit surf, maxGrappleDistance))
                anchor = surf.point;

            float anchorDist = Mathf.Max(0.05f, Vector3.Distance(cameraTransform.position, anchor));
            if (Physics.Raycast(cameraTransform.position, dir, out RaycastHit block,
                    anchorDist - 0.05f, ~0, QueryTriggerInteraction.Ignore)
                && block.collider != c)
                continue;

            float score = (angle / aimAssistAngle) * angleWeight +
                          (dist / maxGrappleDistance) * distanceWeight;

            if (score < bestScore)
            {
                bestScore = score;
                hasTarget = true;
                targetPoint = anchor;
                targetDisplayPoint = c.bounds.center;
                targetCollider = c;
            }
        }
    }

    // -------------------- HUD --------------------

    void OnGUI()
    {
        if (dotTex == null) return;

        float half = crosshairSize * 0.5f;
        GUI.color = crosshairColor;
        GUI.DrawTexture(new Rect(Screen.width * 0.5f - half, Screen.height * 0.5f - half,
            crosshairSize, crosshairSize), dotTex);

        bool show = isZipping || hasTarget;
        if (show && cam != null && cameraTransform != null)
        {
            Vector3 worldPoint = isZipping ? zipPoint : targetDisplayPoint;
            Vector3 sp = cam.WorldToScreenPoint(worldPoint);

            if (sp.z > 0f)
            {
                float dist = Vector3.Distance(cameraTransform.position, worldPoint);
                float size = Mathf.Lerp(reticleSize * 1.5f, reticleSize * 0.75f,
                    Mathf.Clamp01(dist / maxGrappleDistance));
                float h = size * 0.5f;

                GUI.color = isZipping ? reticleLockedColor : reticleColor;
                GUI.DrawTexture(new Rect(sp.x - h, Screen.height - sp.y - h, size, size), ringTex);
            }
        }

        GUI.color = Color.white;
    }

    static Texture2D MakeDotTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f;
                float dy = y - r + 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
            }
        }
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    static Texture2D MakeRingTexture(int size, float thickness)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f;
        float ringR = r - thickness * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - r + 0.5f;
                float dy = y - r + 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(thickness * 0.5f - Mathf.Abs(d - ringR) + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    void DrawRope()
    {
        if (lineRenderer == null || !isZipping) return;

        Vector3 start = gunTip != null ? gunTip.position : transform.position;
        currentRopeEnd = Vector3.MoveTowards(currentRopeEnd, zipPoint, ropeDrawSpeed * Time.deltaTime);

        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, currentRopeEnd);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = isGrounded ? Color.green : (onSteepSlope ? Color.yellow : Color.red);
        Gizmos.DrawWireSphere(transform.position + Vector3.down * groundCheckDistance, groundCheckRadius);
    }
}