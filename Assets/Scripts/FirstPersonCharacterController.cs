using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class FirstPersonCharacterController : MonoBehaviour
{
    [Header("Camera")]
    public Transform cameraTransform;
    public PlayerInputRouter input;
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

    [Header("Air Slide")]
    public bool enableAirSlide = true;
    [Range(0f, 1f)] public float airSlideControlScale = 0.9f;
    public float airSlideCounterScale = 1f;

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
    public int maxAirWallKicks = 2;

    [Header("Darting")]
    public bool enableDarting = true;
    public float dartWindow = 0.28f;
    public float dartInputBuffer = 0.2f;
    [Range(0f, 1.5f)] public float dartVerticalConversion = 0.75f;
    public float dartSpeedBoost = 4f;
    public float dartChainBonus = 1.5f;
    public int dartMaxChain = 5;
    public float dartMaxSpeed = 34f;
    public float dartUpKeep = 1.5f;
    [Range(0f, 1f)] public float dartSteer = 0.35f;
    public float dartChainResetTime = 1.2f;
    public bool dartRefundsWallKick = true;

    [Header("Zip Wall Run")]
    public bool enableZipWallRun = true;
    public float zipWallRunSearchDistance = 1.6f;
    public float zipWallRunBoostSpeed = 17f;

    [Header("Ground Check")]
    public LayerMask groundMask = ~0;
    public float groundCheckRadius = 0.35f;
    public float groundCheckDistance = 0.75f;

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
    bool isAirSliding;
    bool slideBoostGiven;

    CapsuleCollider capsule;
    float standHeight;
    float capsuleBottomY;
    float currentHeight;
    Vector3 camStandLocalPos;
    bool crouchedByObstruction;

    bool isZipping;
    bool isSwinging;

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
    int airWallKicks;

    float dartTimer;
    float dartBufferCounter;
    float dartChainTimer;
    float dartFlashTimer;
    int dartChain;

    Vector3 visualStartScale = Vector3.one;
    Vector3 visualStartLocalPos;

    public bool IsGrounded => isGrounded;
    public bool IsSliding => isSliding;
    public bool IsAirSliding => isAirSliding;
    public bool IsZipping { get => isZipping; set => isZipping = value; }
    public bool IsSwinging { get => isSwinging; set => isSwinging = value; }
    public bool IsVaulting => isVaulting;
    public int VaultTier => isVaulting ? vaultTier : 0;
    public bool IsWallRunning => isWallRunning;
    public int WallRunSide => isWallRunning ? wallRunSide : 0;
    public float CurrentSpeed => new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude;
    public Vector3 Velocity => rb.linearVelocity;
    public float CrouchAmount => standHeight > 0f ? 1f - currentHeight / standHeight : 0f;
    public Vector3 FlatForward => Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
    public Vector3 FlatRight => Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
    public bool DartWindowOpen => dartTimer > 0f;
    public bool IsDarting => dartFlashTimer > 0f;
    public int DartChain => dartChain;
    public int AirWallKicksLeft => Mathf.Max(0, maxAirWallKicks - airWallKicks);

    void Awake()
    {
        if (input == null) input = PlayerInputRouter.Resolve(this);

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

        capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            standHeight = capsule.height;
            capsuleBottomY = capsule.center.y - standHeight * 0.5f;
            currentHeight = standHeight;
        }

        if (cameraTransform != null)
            camStandLocalPos = cameraTransform.localPosition;

        if (characterVisual != null)
        {
            visualStartScale = characterVisual.localScale;
            visualStartLocalPos = characterVisual.localPosition;
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
        UpdateCameraHeight();
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
        HandleDart();
        HandleVault();
        ApplyBetterGravity();
        ApplyGroundStick();
    }

    Vector2 ReadMoveInput()
    {
        return input != null ? input.Move : Vector2.zero;
    }

    void HandleLook()
    {
        Vector2 delta = input != null ? input.LookDelta : Vector2.zero;

        yaw += delta.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -maxLookAngle, maxLookAngle);

        if (cameraTransform != null)
            cameraTransform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        if (characterVisual != null)
            characterVisual.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    void BufferJumpInput()
    {
        if (input == null) return;

        if (input.jump.Pressed)
            jumpBufferCounter = jumpBufferTime;

        if (input.dart.Pressed)
            dartBufferCounter = dartInputBuffer;

        jumpHeld = input.jump.Held;
    }

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
            {
                isGrounded = true;
                airWallKicks = 0;
            }
            else
            {
                onSteepSlope = true;
            }
        }
    }

    void ApplyGroundStick()
    {
        if (!isGrounded || isZipping || isVaulting || isWallRunning) return;
        if (rb.linearVelocity.y > 2f) return;

        rb.AddForce(-groundNormal * groundStickForce, ForceMode.Acceleration);
    }

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
        }
        else if (isSliding)
        {
            SlideMovement(hasInput, moveDir, horizVel);
        }
        else if (isSwinging && !isGrounded)
        {
        }
        else if (hasInput)
        {
            float control = isGrounded ? 1f : airControl;
            if (isAirSliding)
                control *= airSlideControlScale;

            float speedAlong = Vector3.Dot(horizVel, new Vector3(moveDir.x, 0f, moveDir.z).normalized);

            if (speedAlong < targetSpeed)
            {
                float accel = Mathf.Min(acceleration, (targetSpeed - speedAlong) / Time.fixedDeltaTime);
                rb.AddForce(moveDir * accel * control, ForceMode.Acceleration);
            }

            Vector3 flatMove = new Vector3(moveDir.x, 0f, moveDir.z).normalized;
            Vector3 lateral = horizVel - Vector3.Project(horizVel, flatMove);
            float counter = isGrounded ? counterMovement : airCounterMovement;
            if (isAirSliding)
                counter *= airSlideCounterScale;
            rb.AddForce(-lateral * counter, ForceMode.Acceleration);

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

    void HandleSlideState()
    {
        if (!enableSlide) { isSliding = false; return; }

        bool slideKey = input != null && input.slide.Held;

        bool wasSliding = isSliding;
        isSliding = slideKey && !isVaulting &&
                    (isGrounded || (wasSliding && HasCeilingAbove()));

        isAirSliding = enableAirSlide && slideKey && !isSliding &&
                       !isGrounded && !isVaulting && !isWallRunning;

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

    bool WantsCrouchHeight => isSliding || isAirSliding || crouchedByObstruction || isVaulting;

    void UpdateCrouchHeight()
    {
        if (capsule == null) return;

        float targetHeight = WantsCrouchHeight ? slideHeight : standHeight;

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
        else if (enableWallKick && !isSwinging && wallKickCooldownTimer <= 0f &&
                 airWallKicks < maxAirWallKicks && TryWallKick())
        {
            jumpBufferCounter = 0f;
        }
    }

    void ApplyBetterGravity()
    {
        if (isZipping || isVaulting || isWallRunning || isSwinging) return;

        Vector3 extra = Vector3.zero;

        if (rb.linearVelocity.y < 0f)
            extra = Physics.gravity * (fallMultiplier - 1f);
        else if (rb.linearVelocity.y > 0f && !jumpHeld)
            extra = Physics.gravity * (lowJumpMultiplier - 1f);

        rb.AddForce(extra, ForceMode.Acceleration);
    }

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

        if (!enableWallRun || isGrounded || isVaulting || isZipping || isSwinging) return;
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
        OpenDartWindow();
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
        airWallKicks++;
        OpenDartWindow();
        return true;
    }

    public bool TryZipWallRun()
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

    void OpenDartWindow()
    {
        if (!enableDarting) return;

        dartTimer = dartWindow;
        dartBufferCounter = 0f;
    }

    void HandleDart()
    {
        dartTimer -= Time.fixedDeltaTime;
        dartBufferCounter -= Time.fixedDeltaTime;
        dartFlashTimer -= Time.fixedDeltaTime;
        dartChainTimer -= Time.fixedDeltaTime;

        if (dartChainTimer <= 0f)
            dartChain = 0;

        if (!enableDarting || isVaulting || isZipping || isWallRunning) return;
        if (isGrounded || dartTimer <= 0f || dartBufferCounter <= 0f) return;

        PerformDart();
    }

    void PerformDart()
    {
        Vector3 v = rb.linearVelocity;
        Vector3 horiz = new Vector3(v.x, 0f, v.z);

        Vector3 dir = horiz.sqrMagnitude > 0.5f ? horiz.normalized : FlatForward;

        Vector2 input = ReadMoveInput();
        Vector3 wishDir = FlatRight * input.x + FlatForward * input.y;
        if (wishDir.sqrMagnitude > 0.01f)
            dir = Vector3.Slerp(dir, wishDir.normalized, dartSteer).normalized;

        dartChain = Mathf.Min(dartChain + 1, dartMaxChain);

        float converted = Mathf.Max(0f, v.y) * dartVerticalConversion;
        float speed = horiz.magnitude + converted + dartSpeedBoost + dartChainBonus * (dartChain - 1);
        speed = Mathf.Min(speed, dartMaxSpeed);

        rb.linearVelocity = new Vector3(dir.x * speed, dartUpKeep, dir.z * speed);

        momentum = 1f;
        lastMoveDir = dir;
        slideBoostGiven = false;

        if (dartRefundsWallKick)
            airWallKicks = Mathf.Max(0, airWallKicks - 1);

        wallKickCooldownTimer = 0f;
        dartTimer = 0f;
        dartBufferCounter = 0f;
        dartChainTimer = dartChainResetTime;
        dartFlashTimer = 0.25f;
        groundIgnoreCounter = 0.05f;
    }

    void TryAutoVault()
    {
        vaultCooldownTimer -= Time.fixedDeltaTime;
        if (vaultCooldownTimer > 0f || isVaulting) return;

        TryStartVault();
    }

    bool TryStartVault()
    {
        if (!enableVault || isVaulting || isZipping || isSwinging || capsule == null) return false;

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

    void OnDrawGizmosSelected()
    {
        Gizmos.color = isGrounded ? Color.green : (onSteepSlope ? Color.yellow : Color.red);
        Gizmos.DrawWireSphere(transform.position + Vector3.down * groundCheckDistance, groundCheckRadius);
    }
}