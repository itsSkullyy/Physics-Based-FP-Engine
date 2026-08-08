using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(100)]
public class Grappling : MonoBehaviour
{
    [Header("Refs")]
    public FirstPersonCharacterController controller;
    public Rigidbody rb;
    public Transform gunTip;
    public LineRenderer ropeLine;
    public PlayerInputRouter input;

    [Header("Targeting")]
    public LayerMask grappleMask = ~0;
    public string grappleTag = "";
    public bool allowTriggerGrapples = true;
    public float maxGrappleDistance = 50f;
    public float aimAssistAngle = 30f;
    public float angleWeight = 1f;
    public float distanceWeight = 0.4f;
    public float minTargetDistance = 2f;

    [Header("Zip")]
    public float zipMaxSpeed = 28f;
    public float zipAcceleration = 70f;
    public float zipArrivalDistance = 2.5f;
    public float zipUpwardFling = 8f;
    [Range(0f, 1f)] public float zipSpeedCarry = 0.6f;
    public float zipCompletionBoost = 10f;
    public float zipSteerForce = 11f;

    [Header("Swing Attach")]
    public float maxSwingDistance = 45f;
    public float minSwingDistance = 3f;
    public float reattachCooldown = 0.06f;
    [Range(0.5f, 1f)] public float initialRopeScale = 0.92f;
    public bool breakOnObstruction = false;
    public float obstructionTolerance = 1.25f;
    public float obstructionGrace = 0.25f;

    [Header("Rope Length")]
    public float minRopeLength = 2.5f;
    public float manualReelSpeed = 14f;
    public float autoReelSpeed = 3f;
    public float slackTakeUpSpeed = 10f;

    [Header("Rope Solver")]
    [Range(0f, 1f)] public float ropeStiffness = 0.6f;
    public float maxCorrectionSpeed = 25f;
    public bool constrainWhileWalking = false;

    [Header("Launch")]
    public float swingLaunchSpeed = 12f;
    [Range(0f, 1f)] public float fallToSwingConversion = 0.55f;

    [Header("Swing Feel")]
    public float maxSwingSpeed = 32f;
    public float speedClampDecel = 14f;
    public float hardSpeedCeiling = 38f;
    public float swingGravityScale = 1.4f;
    public float swingAirControl = 16f;

    [Header("Above The Anchor")]
    [Range(0f, 1f)] public float aboveAnchorLaunchScale = 0f;
    public float dropInSpeed = 8f;
    public float aboveAnchorGravityScale = 2.4f;
    [Range(0f, 1f)] public float aboveAnchorStiffness = 0.9f;
    public float aboveAnchorReelSpeed = 3f;
    [Range(0.3f, 1f)] public float aboveAnchorMinRopeFactor = 0.75f;
    public float aboveAnchorDamping = 3f;
    public float aboveAnchorDampMaxSpeed = 9f;

    [Header("Ground Swing")]
    [Range(0f, 1f)] public float groundedLift = 0.12f;
    public float groundStickForce = 22f;
    public float groundStickMinSpeed = 6f;
    public float slideSpeedBonus = 8f;
    public float slidePullAccel = 18f;
    public float slidePullMinSpeed = 4f;

    [Header("Dismount")]
    public float releaseUpBoost = 0.5f;
    public float jumpOffUpSpeed = 6.5f;
    public float jumpOffForwardSpeed = 3f;

    [Header("Rope Visual")]
    public bool useOwnWorldRope = true;
    public Material ropeMaterial;
    public Color ropeColor = new Color(0.85f, 0.85f, 0.9f, 1f);
    public float ropeStartWidth = 0.04f;
    public float ropeEndWidth = 0.02f;
    public int ropeSegments = 14;
    public float ropeDrawSpeed = 140f;
    public float sagAmount = 0.35f;

    [Header("Crosshair")]
    public float reticleSize = 40f;
    public float crosshairSize = 6f;
    public Color crosshairColor = new Color(1f, 1f, 1f, 0.9f);
    public Color reticleColor = new Color(1f, 1f, 1f, 0.55f);
    public Color reticleLockedColor = new Color(0.3f, 1f, 0.6f, 0.95f);

    bool isSwinging;
    Vector3 anchor;
    float ropeLength;
    float attachRopeLength;
    float cooldownTimer;
    float pendingReel;
    float blockedTimer;
    bool jumpQueued;

    bool isZipping;
    Vector3 zipPoint;

    Vector3 currentRopeEnd;
    GameObject ropeObject;

    bool hasTarget;
    Vector3 targetPoint;
    Vector3 targetDisplayPoint;

    Camera cam;
    Texture2D dotTex;
    Texture2D ringTex;

    Vector3 Pos => rb != null ? rb.position : transform.position;

    public bool IsSwinging => isSwinging;
    public bool IsZipping => isZipping;
    public Vector3 Anchor => anchor;
    public float RopeLength => ropeLength;

    bool SlideGrappling => controller.IsGrounded && controller.IsSliding;
    float CurrentMaxSpeed => SlideGrappling ? maxSwingSpeed + slideSpeedBonus : maxSwingSpeed;
    float CurrentCeiling => CurrentMaxSpeed + Mathf.Max(0f, hardSpeedCeiling - maxSwingSpeed);

    bool RopeIdleOnGround => controller.IsGrounded && !controller.IsSliding && !constrainWhileWalking;

    float ActiveGravityScale => controller.IsGrounded
        ? 1f
        : Mathf.Lerp(swingGravityScale, aboveAnchorGravityScale, AboveAnchorAmount());

    void Awake()
    {
        if (controller == null) controller = GetComponent<FirstPersonCharacterController>();
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (input == null) input = PlayerInputRouter.Resolve(this);

        if (controller == null)
        {
            Debug.LogError("Grappling needs a FirstPersonCharacterController.", this);
            enabled = false;
            return;
        }

        if (gunTip == null) gunTip = controller.cameraTransform;

        if (ropeLine == null && useOwnWorldRope)
            ropeLine = CreateWorldRope();

        if (ropeLine == null)
            Debug.LogWarning("Grappling has no LineRenderer. Rope will be invisible.", this);
        else
            ropeLine.useWorldSpace = true;

        if (controller.cameraTransform != null)
            cam = controller.cameraTransform.GetComponent<Camera>();
        if (cam == null)
            cam = Camera.main;

        dotTex = MakeDotTexture(16);
        ringTex = MakeRingTexture(64, 6f);
    }

    LineRenderer CreateWorldRope()
    {
        ropeObject = new GameObject("GrappleRope");
        ropeObject.transform.SetParent(null, true);
        ropeObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        ropeObject.transform.localScale = Vector3.one;

        LineRenderer lr = ropeObject.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 0;
        lr.startWidth = ropeStartWidth;
        lr.endWidth = ropeEndWidth;
        lr.numCapVertices = 2;
        lr.alignment = LineAlignment.View;
        lr.textureMode = LineTextureMode.Tile;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.startColor = ropeColor;
        lr.endColor = ropeColor;

        Material mat = ropeMaterial;
        if (mat == null)
        {
            Shader s = Shader.Find("Sprites/Default");
            if (s != null) mat = new Material(s);
        }
        if (mat != null) lr.material = mat;

        return lr;
    }

    void OnDestroy()
    {
        if (ropeObject != null)
            Destroy(ropeObject);
    }

    void Update()
    {
        cooldownTimer -= Time.deltaTime;

        UpdateTarget();

        if (input != null)
        {
            if (input.grappleSwing.Pressed) TryAttach();
            if (input.grappleSwing.Released && isSwinging) Detach(false);

            if (input.grapplePull.Pressed) TryStartZip();
            if (input.grapplePull.Released) StopZip();

            if (isSwinging)
            {
                float scroll = input.ScrollY;
                if (Mathf.Abs(scroll) > 0.01f)
                    pendingReel += Mathf.Sign(scroll) * manualReelSpeed * Time.deltaTime;
            }

            if (isSwinging && input.jump.Pressed && !controller.IsGrounded)
                jumpQueued = true;
        }
    }

    void FixedUpdate()
    {
        if (isZipping)
        {
            HandleZip();
            return;
        }

        if (!isSwinging) return;

        if (rb.isKinematic || controller.IsVaulting)
        {
            Detach(false);
            return;
        }

        float dt = Time.fixedDeltaTime;

        if (!AnchorStillValid())
        {
            Detach(false);
            return;
        }

        UpdateRopeLength(dt);
        ApplySwingGravity();
        ApplyAirControl();
        ApplySlidePull();
        SolveRope(dt);
        ApplyGroundStick();
        ApplyAboveAnchorDamping(dt);
        ClampSpeed(dt);

        if (jumpQueued) Detach(true);
    }

    void LateUpdate()
    {
        DrawRope();
    }

    void UpdateTarget()
    {
        hasTarget = false;
        if (controller.cameraTransform == null || isZipping || isSwinging) return;

        Transform camT = controller.cameraTransform;

        QueryTriggerInteraction qti = allowTriggerGrapples
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        Collider[] candidates = Physics.OverlapSphere(camT.position, maxGrappleDistance, grappleMask, qti);

        float bestScore = float.MaxValue;

        foreach (Collider c in candidates)
        {
            if (c.attachedRigidbody == rb) continue;
            if (!string.IsNullOrEmpty(grappleTag) && !c.CompareTag(grappleTag)) continue;

            Vector3 approxPoint = c.bounds.ClosestPoint(camT.position);
            Vector3 toPoint = approxPoint - camT.position;
            float dist = toPoint.magnitude;
            if (dist < minTargetDistance || dist > maxGrappleDistance) continue;

            float angle = Vector3.Angle(camT.forward, toPoint);
            if (angle > aimAssistAngle) continue;

            Vector3 dir = toPoint / dist;
            Vector3 point = approxPoint;
            if (c.Raycast(new Ray(camT.position, dir), out RaycastHit surf, maxGrappleDistance))
                point = surf.point;

            float pointDist = Mathf.Max(0.05f, Vector3.Distance(camT.position, point));
            if (Physics.Raycast(camT.position, dir, out RaycastHit block,
                    pointDist - 0.05f, ~0, QueryTriggerInteraction.Ignore)
                && block.collider != c)
                continue;

            float score = (angle / aimAssistAngle) * angleWeight +
                          (dist / maxGrappleDistance) * distanceWeight;

            if (score < bestScore)
            {
                bestScore = score;
                hasTarget = true;
                targetPoint = point;
                targetDisplayPoint = c.bounds.center;
            }
        }
    }

    public bool TryGetGrapplePoint(out Vector3 point)
    {
        point = default;

        Transform camT = controller.cameraTransform;
        if (camT == null) return false;

        if (hasTarget)
        {
            point = targetPoint;
            return true;
        }

        QueryTriggerInteraction qti = allowTriggerGrapples
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        Ray ray = new Ray(camT.position, camT.forward);
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
        if (isZipping || isSwinging || controller.IsVaulting) return;
        if (!TryGetGrapplePoint(out Vector3 point)) return;

        zipPoint = point;
        isZipping = true;
        controller.IsZipping = true;
        currentRopeEnd = gunTip != null ? gunTip.position : transform.position;
    }

    public void StopZip()
    {
        if (!isZipping) return;

        isZipping = false;
        controller.IsZipping = false;

        if (ropeLine != null)
            ropeLine.positionCount = 0;
    }

    void HandleZip()
    {
        if (rb.isKinematic || controller.IsVaulting)
        {
            StopZip();
            return;
        }

        Vector3 toPoint = zipPoint - Pos;
        float dist = toPoint.magnitude;

        if (dist <= zipArrivalDistance)
        {
            if (controller.TryZipWallRun())
            {
                StopZip();
                return;
            }

            Vector3 v = rb.linearVelocity;
            Vector3 dir = v.sqrMagnitude > 1f
                ? v.normalized
                : (controller.cameraTransform != null ? controller.cameraTransform.forward : transform.forward);

            Vector3 outVel = dir * (v.magnitude * zipSpeedCarry + zipCompletionBoost);
            outVel.y = Mathf.Max(outVel.y, zipUpwardFling);
            rb.linearVelocity = outVel;

            StopZip();
            return;
        }

        Vector3 desired = (toPoint / dist) * zipMaxSpeed;
        rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, desired,
            zipAcceleration * Time.fixedDeltaTime);

        Vector3 wish = ReadWishDir();
        if (wish != Vector3.zero)
            rb.AddForce(wish * zipSteerForce, ForceMode.Acceleration);
    }

    void TryAttach()
    {
        if (isSwinging || isZipping || cooldownTimer > 0f) return;
        if (controller.IsVaulting) return;
        if (!TryGetGrapplePoint(out Vector3 point)) return;

        float dist = Vector3.Distance(Pos, point);
        if (dist < minSwingDistance || dist > maxSwingDistance) return;

        anchor = point;
        ropeLength = Mathf.Max(minRopeLength, dist * initialRopeScale);
        attachRopeLength = ropeLength;
        isSwinging = true;
        controller.IsSwinging = true;
        jumpQueued = false;
        pendingReel = 0f;
        blockedTimer = 0f;
        currentRopeEnd = gunTip != null ? gunTip.position : transform.position;

        ApplySwingLaunch();
    }

    void ApplySwingLaunch()
    {
        Vector3 ropeDir = (anchor - Pos).normalized;
        Vector3 vel = rb.linearVelocity;
        float above = AboveAnchorAmount();

        Vector3 wish = ReadWishDir();
        if (wish == Vector3.zero) wish = controller.FlatForward;

        Vector3 tangent = Vector3.ProjectOnPlane(wish, ropeDir);
        if (tangent.sqrMagnitude < 0.001f)
            tangent = Vector3.ProjectOnPlane(controller.FlatForward, ropeDir);

        float fall = Mathf.Max(0f, -vel.y);
        float boost = swingLaunchSpeed + fall * fallToSwingConversion;
        boost *= Mathf.Lerp(1f, aboveAnchorLaunchScale, above);

        if (tangent.sqrMagnitude > 0.001f)
        {
            tangent.Normalize();
            float along = Vector3.Dot(vel, tangent);
            float add = Mathf.Clamp(CurrentMaxSpeed - along, 0f, boost);
            vel += tangent * add;
        }

        if (above > 0.01f)
            vel += ropeDir * (dropInSpeed * above);

        rb.linearVelocity = vel;
    }

    float AboveAnchorAmount()
    {
        Vector3 toAnchor = anchor - Pos;
        if (toAnchor.sqrMagnitude < 0.0001f) return 0f;
        return Mathf.Clamp01(Vector3.Dot(toAnchor.normalized, Vector3.down));
    }

    public void Detach(bool jumped)
    {
        if (!isSwinging) return;

        isSwinging = false;
        controller.IsSwinging = false;
        jumpQueued = false;
        pendingReel = 0f;
        cooldownTimer = reattachCooldown;

        if (ropeLine != null)
            ropeLine.positionCount = 0;

        Vector3 v = rb.linearVelocity;

        if (jumped)
        {
            v.y = Mathf.Max(v.y + jumpOffUpSpeed * 0.6f, jumpOffUpSpeed);

            Vector3 horiz = new Vector3(v.x, 0f, v.z);
            Vector3 dir = horiz.sqrMagnitude > 1f ? horiz.normalized : controller.FlatForward;
            v += dir * jumpOffForwardSpeed;
        }
        else
        {
            v.y += releaseUpBoost;
        }

        float ceiling = CurrentCeiling;
        float speed = v.magnitude;
        if (speed > ceiling)
            v *= ceiling / speed;

        rb.linearVelocity = v;
    }

    bool AnchorStillValid()
    {
        Vector3 pos = Pos;
        float dist = Vector3.Distance(pos, anchor);

        if (dist > maxSwingDistance * 1.5f) return false;
        if (!breakOnObstruction) return true;

        Vector3 dir = (anchor - pos) / Mathf.Max(dist, 0.001f);
        float checkDist = dist - obstructionTolerance;

        bool blocked = checkDist > 0.5f && Physics.Raycast(pos, dir, checkDist,
            controller.groundMask, QueryTriggerInteraction.Ignore);

        blockedTimer = blocked ? blockedTimer + Time.fixedDeltaTime : 0f;
        return blockedTimer < obstructionGrace;
    }

    void UpdateRopeLength(float dt)
    {
        float dist = Vector3.Distance(Pos, anchor);

        if (Mathf.Abs(pendingReel) > 0.0001f)
        {
            ropeLength -= pendingReel;
            pendingReel = 0f;
        }

        float above = AboveAnchorAmount();

        if (above > 0.15f && !RopeIdleOnGround)
        {
            float floor = Mathf.Max(minRopeLength, attachRopeLength * aboveAnchorMinRopeFactor);
            ropeLength = Mathf.Max(floor, ropeLength - aboveAnchorReelSpeed * above * dt);
        }
        else if (autoReelSpeed > 0f && !RopeIdleOnGround)
        {
            ropeLength -= autoReelSpeed * dt;
        }

        if (slackTakeUpSpeed > 0f && dist < ropeLength && above < 0.15f)
            ropeLength = Mathf.MoveTowards(ropeLength, dist, slackTakeUpSpeed * dt);

        ropeLength = Mathf.Clamp(ropeLength, minRopeLength, maxSwingDistance);
    }

    void SolveRope(float dt)
    {
        Vector3 pos = Pos;
        Vector3 toAnchor = anchor - pos;
        float dist = toAnchor.magnitude;
        if (dist < 0.01f) return;

        Vector3 dir = toAnchor / dist;

        if (RopeIdleOnGround)
        {
            ropeLength = Mathf.Min(Mathf.Max(ropeLength, dist), maxSwingDistance);
            return;
        }

        if (dist <= ropeLength) return;

        Vector3 vel = rb.linearVelocity;
        Vector3 predicted = vel + Physics.gravity * (ActiveGravityScale * dt);

        Vector3 impulse = Vector3.zero;

        float outward = -Vector3.Dot(predicted, dir);
        if (outward > 0f)
            impulse += dir * outward;

        float stiffness = Mathf.Lerp(ropeStiffness, aboveAnchorStiffness, AboveAnchorAmount());
        float error = dist - ropeLength;
        impulse += dir * Mathf.Min(error * stiffness / dt, maxCorrectionSpeed);

        if (controller.IsGrounded)
        {
            float up = Vector3.Dot(impulse, Vector3.up);
            if (up > 0f)
                impulse -= Vector3.up * up * (1f - groundedLift);
        }

        rb.linearVelocity = vel + impulse;
    }

    void ApplyGroundStick()
    {
        if (!controller.IsGrounded || RopeIdleOnGround) return;
        if (controller.CurrentSpeed < groundStickMinSpeed) return;

        float dist = Vector3.Distance(Pos, anchor);
        if (dist < ropeLength) return;

        rb.AddForce(Vector3.down * groundStickForce, ForceMode.Acceleration);
    }

    void ApplySlidePull()
    {
        if (!SlideGrappling || slidePullAccel <= 0f) return;

        Vector3 pos = Pos;
        Vector3 toAnchor = anchor - pos;
        float dist = toAnchor.magnitude;
        if (dist < ropeLength || dist < 0.01f) return;

        Vector3 vel = rb.linearVelocity;
        if (vel.magnitude < slidePullMinSpeed) return;

        Vector3 tangent = Vector3.ProjectOnPlane(vel, toAnchor / dist);
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.01f) return;

        rb.AddForce(tangent.normalized * slidePullAccel, ForceMode.Acceleration);
    }

    void ApplyAboveAnchorDamping(float dt)
    {
        if (aboveAnchorDamping <= 0f) return;

        float above = AboveAnchorAmount();
        if (above < 0.5f) return;

        Vector3 vel = rb.linearVelocity;
        if (vel.magnitude > aboveAnchorDampMaxSpeed) return;

        Vector3 toAnchor = anchor - Pos;
        if (toAnchor.sqrMagnitude < 0.0001f) return;

        Vector3 tangential = Vector3.ProjectOnPlane(vel, toAnchor.normalized);

        float damp = Mathf.Clamp01(aboveAnchorDamping * above * dt);
        rb.linearVelocity = vel - tangential * damp;
    }

    void ApplySwingGravity()
    {
        if (controller.IsGrounded) return;

        float scale = ActiveGravityScale;
        if (Mathf.Approximately(scale, 1f)) return;

        rb.AddForce(Physics.gravity * (scale - 1f), ForceMode.Acceleration);
    }

    void ApplyAirControl()
    {
        Vector3 wish = ReadWishDir();
        if (wish == Vector3.zero) return;

        Vector3 ropeDir = (anchor - Pos).normalized;

        float outward = -Vector3.Dot(wish, ropeDir);
        Vector3 usable = outward > 0f ? wish + ropeDir * outward : wish;
        if (usable.sqrMagnitude < 0.001f) return;

        rb.AddForce(usable.normalized * swingAirControl, ForceMode.Acceleration);
    }

    void ClampSpeed(float dt)
    {
        float cap = CurrentMaxSpeed;

        Vector3 v = rb.linearVelocity;
        float speed = v.magnitude;
        if (speed <= cap || speed < 0.01f) return;

        float target = Mathf.Max(cap, speed - speedClampDecel * dt);
        target = Mathf.Min(target, CurrentCeiling);

        rb.linearVelocity = v * (target / speed);
    }

    Vector3 ReadWishDir()
    {
        if (input == null) return Vector3.zero;

        Vector2 m = input.Move;
        Vector3 dir = controller.FlatRight * m.x + controller.FlatForward * m.y;
        return dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.zero;
    }

    void DrawRope()
    {
        if (ropeLine == null) return;

        if (!isSwinging && !isZipping)
        {
            if (ropeLine.positionCount != 0)
                ropeLine.positionCount = 0;
            return;
        }

        Vector3 start = gunTip != null ? gunTip.position : transform.position;
        Vector3 end = isZipping ? zipPoint : anchor;

        currentRopeEnd = Vector3.MoveTowards(currentRopeEnd, end, ropeDrawSpeed * Time.deltaTime);

        if (isZipping)
        {
            if (ropeLine.positionCount != 2)
                ropeLine.positionCount = 2;

            ropeLine.SetPosition(0, start);
            ropeLine.SetPosition(1, currentRopeEnd);
            return;
        }

        int count = Mathf.Max(2, ropeSegments);
        if (ropeLine.positionCount != count)
            ropeLine.positionCount = count;

        float dist = Vector3.Distance(start, anchor);
        float slack = Mathf.Max(0f, ropeLength - dist) * sagAmount;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            Vector3 p = Vector3.Lerp(start, currentRopeEnd, t);
            p += Vector3.down * (Mathf.Sin(t * Mathf.PI) * slack);
            ropeLine.SetPosition(i, p);
        }
    }

    void OnGUI()
    {
        if (dotTex == null) return;

        float half = crosshairSize * 0.5f;
        GUI.color = crosshairColor;
        GUI.DrawTexture(new Rect(Screen.width * 0.5f - half, Screen.height * 0.5f - half,
            crosshairSize, crosshairSize), dotTex);

        bool show = isZipping || isSwinging || hasTarget;
        if (show && cam != null && controller.cameraTransform != null)
        {
            Vector3 worldPoint = isZipping ? zipPoint : (isSwinging ? anchor : targetDisplayPoint);
            Vector3 sp = cam.WorldToScreenPoint(worldPoint);

            if (sp.z > 0f)
            {
                float dist = Vector3.Distance(controller.cameraTransform.position, worldPoint);
                float size = Mathf.Lerp(reticleSize * 1.5f, reticleSize * 0.75f,
                    Mathf.Clamp01(dist / maxGrappleDistance));
                float h = size * 0.5f;

                GUI.color = (isZipping || isSwinging) ? reticleLockedColor : reticleColor;
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

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || !isSwinging) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, anchor);
        Gizmos.DrawWireSphere(anchor, 0.3f);
        Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
        Gizmos.DrawWireSphere(anchor, ropeLength);
    }
}