using UnityEngine;
using UnityEngine.InputSystem;

public class FirstPersonCharacterController : MonoBehaviour
{
    public Transform cameraTransform;

    [Header("Camera Junk")] public float mouseSensitivity = 0.1f;
    public float maxLookAngle = 89f;
    public bool lockCursor = true;

    [Header("Movement")] public float baseSpeed = 5.5f;
    public float maxSpeed = 10f;
    public float momentumBuildRate = 0.6f;
    public float momentumDecayRate = 1.8f;
    public float acceleration = 55f;
    [Range(0f, 1f)] public float airControl = 0.4f;

    [Header("Ground Friction")] public float groundFriction = 9f;

    [Header("Jump")] public float jumpHeight = 1.6f;
    public float fallMultiplier = 2.4f;
    public float lowJumpMultiplier = 3.0f;
    public float coyoteTime = 0.12f;
    public float jumpBufferTime = 0.12f;

    [Header("Ground Check")] public LayerMask groundMask = ~0;
    public float groundCheckRadius = 0.35f;
    public float groundCheckOffset = 0.95f;

    [Header("Grapple")] public LineRenderer lineRenderer;
    public Transform gunTip;
    public LayerMask grappleMask = ~0;
    public string grappleTag = "";
    public float maxGrappleDistance = 45f;
    public float spring = 4.5f;
    public float damper = 7f;
    public float massScale = 4.5f;
    [Range(0f, 1f)] public float jointMinMultiplier = 0.25f;
    [Range(0f, 1f)] public float jointMaxMultiplier = 0.8f;
    public float reelSpeed = 9f;
    public float reelMinDistance = 3f;
    public float reelForce = 30f;
    public float releaseBoost = 4f;
    public float ropeDrawSpeed = 90f;

    Rigidbody rb;
    float pitch;
    float momentum;
    Vector3 lastMoveDir;
    bool isGrounded;
    float coyoteCounter;
    float jumpBufferCounter;
    bool jumpHeld;

    bool isGrappling;
    Vector3 grapplePoint;
    SpringJoint joint;
    Vector3 currentRopeEnd;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.freezeRotation = true;
        rb.useGravity = true;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (gunTip == null)
            gunTip = cameraTransform;

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
        HandleGrappleInput();
    }

    void LateUpdate()
    {
        DrawRope();
    }

    void FixedUpdate()
    {
        GroundCheck();
        HandleMovement();
        HandleJump();
        HandleReel();
        ApplyBetterGravity();
    }

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
        float mouseX = delta.x * mouseSensitivity;
        float mouseY = delta.y * mouseSensitivity;

        transform.Rotate(Vector3.up * mouseX);

        pitch = Mathf.Clamp(pitch - mouseY, -maxLookAngle, maxLookAngle);
        if (cameraTransform != null)
            cameraTransform.localEulerAngles = new Vector3(pitch, 0f, 0f);
    }

    void HandleMovement()
    {
        Vector2 input = ReadMoveInput();

        Vector3 inputDir = (transform.right * input.x + transform.forward * input.y);
        bool hasInput = inputDir.sqrMagnitude > 0.01f;
        inputDir = hasInput ? inputDir.normalized : Vector3.zero;

        if (hasInput)
        {
            float alignment = Vector3.Dot(inputDir, lastMoveDir);
            if (alignment < 0f)
                momentum += alignment * momentumDecayRate * Time.fixedDeltaTime;

            momentum += momentumBuildRate * Time.fixedDeltaTime;
            lastMoveDir = inputDir;
        }
        else
        {
            momentum -= momentumDecayRate * Time.fixedDeltaTime;
        }

        momentum = Mathf.Clamp01(momentum);

        float targetSpeed = Mathf.Lerp(baseSpeed, maxSpeed, momentum);

        Vector3 horizVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);

        if (hasInput)
        {
            Vector3 targetVel = inputDir * targetSpeed;
            Vector3 velDiff = targetVel - horizVel;

            float control = isGrounded ? 1f : airControl;
            Vector3 force = velDiff * acceleration * control;
            rb.AddForce(force, ForceMode.Acceleration);
        }
        else if (isGrounded && !isGrappling)
        {
            Vector3 friction = -horizVel * groundFriction;
            rb.AddForce(friction, ForceMode.Acceleration);
        }

        horizVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        float cap = Mathf.Max(targetSpeed, baseSpeed);
        if (isGrounded && !isGrappling && horizVel.magnitude > cap)
        {
            Vector3 clamped = horizVel.normalized * cap;
            rb.linearVelocity = new Vector3(clamped.x, rb.linearVelocity.y, clamped.z);
        }
    }

    void BufferJumpInput()
    {
        Keyboard k = Keyboard.current;
        if (k == null) return;

        if (k.spaceKey.wasPressedThisFrame)
            jumpBufferCounter = jumpBufferTime;

        jumpHeld = k.spaceKey.isPressed;
    }

    void HandleJump()
    {
        coyoteCounter = isGrounded ? coyoteTime : coyoteCounter - Time.fixedDeltaTime;
        jumpBufferCounter -= Time.fixedDeltaTime;

        if (jumpBufferCounter > 0f && coyoteCounter > 0f)
        {
            float g = Mathf.Abs(Physics.gravity.y);
            float jumpVelocity = Mathf.Sqrt(2f * g * jumpHeight);

            Vector3 v = rb.linearVelocity;
            v.y = jumpVelocity;
            rb.linearVelocity = v;

            jumpBufferCounter = 0f;
            coyoteCounter = 0f;
        }
    }

    void ApplyBetterGravity()
    {
        Vector3 extra = Vector3.zero;

        if (rb.linearVelocity.y < 0f)
        {
            extra = Physics.gravity * (fallMultiplier - 1f);
        }
        else if (rb.linearVelocity.y > 0f && !jumpHeld)
        {
            extra = Physics.gravity * (lowJumpMultiplier - 1f);
        }

        rb.AddForce(extra, ForceMode.Acceleration);
    }

    void GroundCheck()
    {
        Vector3 origin = transform.position + Vector3.down * groundCheckOffset;
        isGrounded = Physics.CheckSphere(origin, groundCheckRadius, groundMask,
            QueryTriggerInteraction.Ignore);
    }

    void HandleGrappleInput()
    {
        Keyboard k = Keyboard.current;
        if (k == null) return;

        if (k.fKey.wasPressedThisFrame)
            TryStartGrapple();

        if (k.fKey.wasReleasedThisFrame)
            StopGrapple();
    }

    void TryStartGrapple()
    {
        if (cameraTransform == null) return;

        Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, maxGrappleDistance, grappleMask,
                QueryTriggerInteraction.Ignore))
        {
            if (!string.IsNullOrEmpty(grappleTag) && !hit.collider.CompareTag(grappleTag))
                return;

            grapplePoint = hit.point;
            isGrappling = true;

            float distance = Vector3.Distance(transform.position, grapplePoint);

            joint = gameObject.AddComponent<SpringJoint>();
            joint.autoConfigureConnectedAnchor = false;
            joint.connectedAnchor = grapplePoint;
            joint.maxDistance = distance * jointMaxMultiplier;
            joint.minDistance = distance * jointMinMultiplier;
            joint.spring = spring;
            joint.damper = damper;
            joint.massScale = massScale;

            currentRopeEnd = gunTip != null ? gunTip.position : transform.position;

            if (lineRenderer != null)
                lineRenderer.positionCount = 2;
        }
    }

    void StopGrapple()
    {
        if (!isGrappling) return;

        isGrappling = false;

        if (joint != null)
            Destroy(joint);

        if (releaseBoost > 0f)
            rb.AddForce(rb.linearVelocity.normalized * releaseBoost, ForceMode.VelocityChange);

        if (lineRenderer != null)
            lineRenderer.positionCount = 0;
    }

    void HandleReel()
    {
        if (!isGrappling || joint == null) return;

        Keyboard k = Keyboard.current;
        bool reeling = k != null && k.leftCtrlKey.isPressed;
        if (!reeling) return;

        float newMax = Mathf.Max(reelMinDistance, joint.maxDistance - reelSpeed * Time.fixedDeltaTime);
        joint.maxDistance = newMax;
        joint.minDistance = Mathf.Min(joint.minDistance, newMax);

        Vector3 toPoint = (grapplePoint - transform.position).normalized;
        rb.AddForce(toPoint * reelForce, ForceMode.Acceleration);
    }

    void DrawRope()
    {
        if (lineRenderer == null || !isGrappling) return;

        Vector3 start = gunTip != null ? gunTip.position : transform.position;
        currentRopeEnd = Vector3.MoveTowards(currentRopeEnd, grapplePoint, ropeDrawSpeed * Time.deltaTime);

        lineRenderer.SetPosition(0, start);
        lineRenderer.SetPosition(1, currentRopeEnd);
    }
}