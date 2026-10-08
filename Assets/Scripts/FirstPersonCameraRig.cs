using UnityEngine;
using Unity.Cinemachine;

// Camera effects. Attach to CameraFX (child of CameraAnchor).
// Auto-configures the CinemachineCamera as a hard first person mount.
public class FirstPersonCameraRig : MonoBehaviour
{
    public FirstPersonCharacterController controller;
    public CinemachineCamera cineCam;
    public CameraShaker shaker;

    [Header("Setup")]
    public bool autoConfigureCineCam = true;

    [Header("FOV")]
    public float baseFov = 75f;
    public float speedFovBoost = 14f;
    public float vaultFovBoost = 4f;
    public float fovLerpSpeed = 8f;

    [Header("Tilt")]
    public float strafeTiltAngle = 2.5f;
    public float slideTiltAngle = 4f;
    public float wallRunTiltAngle = 12f;
    public float tiltLerpSpeed = 8f;

    [Header("Landing Dip")]
    [Tooltip("Landings slower than this get no dip. A normal jump lands at about 9.")]
    public float dipStartFallSpeed = 11f;
    public float dipFullFallSpeed = 30f;
    public float maxFallDip = 0.16f;
    [Tooltip("How much of the dip is kept when landing at top speed or into a slide.")]
    [Range(0f, 1f)] public float fastLandingDipKeep = 0.35f;
    [Tooltip("Time to sink into the dip, so it eases down instead of snapping.")]
    public float dipAttackTime = 0.05f;
    public float maxLandDip = 0.3f;
    public float dipRecoverSpeed = 7f;

    [Header("Vault Roll")]
    public float lowVaultRoll = 6f;
    public float regularVaultRoll = 10f;
    public float jumpVaultRoll = 14f;
    public float vaultRollSpeed = 11f;

    float currentFov;
    float currentTilt;
    float currentRoll;
    float dip;
    float dipTarget;
    float dipVelocity;
    float dipTargetVelocity;
    bool wasGrounded;
    float lastFallSpeed;
    Vector3 baseLocalPos;

    /// 1 for a normal landing, down to fastLandingDipKeep when landing fast or crouched,
    /// since those landings keep their speed and shouldn't feel like a stomp.
    public static float LandingSoftness(FirstPersonCharacterController c, float fastKeep)
    {
        float speedT = Mathf.InverseLerp(c.baseSpeed, c.maxSpeed, c.CurrentSpeed);
        if (c.IsSliding || c.IsAirSliding || c.CrouchAmount > 0.3f)
        {
            speedT = 1f;
        }
        return Mathf.Lerp(1f, fastKeep, speedT);
    }

    void Start()
    {
        baseLocalPos = transform.localPosition;

        HookUpShaker();
        WarnAboutSetupMistakes();
        if (autoConfigureCineCam)
        {
            ConfigureCineCam();
        }

        currentFov = baseFov;
        if (cineCam != null)
        {
            cineCam.Lens.FieldOfView = baseFov;
        }
    }

    // the rig adds the shaker's offsets in itself, so the shaker shouldn't move the camera too
    void HookUpShaker()
    {
        if (shaker == null)
        {
            shaker = GetComponent<CameraShaker>();
        }
        if (shaker == null)
        {
            shaker = CameraShaker.Instance;
        }
        if (shaker != null)
        {
            shaker.MarkDriven();
        }
    }

    void WarnAboutSetupMistakes()
    {
        if (controller != null && controller.cameraTransform == transform)
        {
            Debug.LogError("Rig is on the CameraAnchor. Put it on a child (CameraFX).", this);
        }

        if (cineCam != null && cineCam.transform == transform)
        {
            Debug.LogError("Rig is on the CinemachineCamera. Put it on CameraFX.", this);
        }
    }

    void ConfigureCineCam()
    {
        if (cineCam == null)
        {
            return;
        }

        cineCam.Target.TrackingTarget = transform;

        RemoveIfPresent<CinemachinePanTilt>();
        RemoveIfPresent<CinemachineInputAxisController>();
        RemoveIfPresent<CinemachineFollow>();
        RemoveIfPresent<CinemachineOrbitalFollow>();
        RemoveIfPresent<CinemachinePositionComposer>();
        RemoveIfPresent<CinemachineRotationComposer>();
        RemoveIfPresent<CinemachineHardLookAt>();

        if (!cineCam.TryGetComponent(out CinemachineHardLockToTarget _))
        {
            cineCam.gameObject.AddComponent<CinemachineHardLockToTarget>();
        }
        if (!cineCam.TryGetComponent(out CinemachineRotateWithFollowTarget _))
        {
            cineCam.gameObject.AddComponent<CinemachineRotateWithFollowTarget>();
        }
    }

    void RemoveIfPresent<T>() where T : Component
    {
        if (cineCam != null && cineCam.TryGetComponent(out T comp))
        {
            Destroy(comp);
        }
    }

    void Update()
    {
        if (controller == null)
        {
            return;
        }

        TrackFall();
        UpdateLens();
        UpdateDip();
        UpdateVaultRoll();
    }

    void TrackFall()
    {
        float vy = controller.Velocity.y;
        if (vy < 0f)
        {
            lastFallSpeed = -vy;
        }

        if (controller.IsGrounded && !wasGrounded)
        {
            float t = Mathf.InverseLerp(dipStartFallSpeed, dipFullFallSpeed, lastFallSpeed);
            if (t > 0f)
            {
                float amount = t * t * maxFallDip * LandingSoftness(controller, fastLandingDipKeep);
                dipTarget = Mathf.Min(dipTarget + amount, maxLandDip);
            }
            lastFallSpeed = 0f;
        }

        wasGrounded = controller.IsGrounded;
    }

    void UpdateLens()
    {
        if (cineCam == null)
        {
            return;
        }

        float speedT = Mathf.Clamp01((controller.CurrentSpeed - controller.baseSpeed) /
            Mathf.Max(1f, controller.maxSpeed * 1.6f - controller.baseSpeed));
        float targetFov = baseFov
            + (controller.IsVaulting ? vaultFovBoost : 0f)
            + speedT * speedFovBoost;

        currentFov = Mathf.Lerp(currentFov, targetFov,
            1f - Mathf.Exp(-fovLerpSpeed * Time.deltaTime));
        cineCam.Lens.FieldOfView = currentFov + (shaker != null ? shaker.FovOffset : 0f);

        float targetTilt;
        if (controller.IsWallRunning)
        {
            targetTilt = controller.WallRunSide * wallRunTiltAngle;
        }
        else
        {
            float strafe = Vector3.Dot(controller.Velocity, controller.FlatRight);
            float strafeT = Mathf.Clamp(strafe / Mathf.Max(1f, controller.maxSpeed), -1f, 1f);
            targetTilt = -strafeT * (controller.IsSliding ? slideTiltAngle : strafeTiltAngle);
        }

        currentTilt = Mathf.Lerp(currentTilt, targetTilt,
            1f - Mathf.Exp(-tiltLerpSpeed * Time.deltaTime));
        cineCam.Lens.Dutch = currentTilt;
    }

    void UpdateDip()
    {
        dipTarget = Mathf.SmoothDamp(dipTarget, 0f, ref dipTargetVelocity, 1f / dipRecoverSpeed);
        dip = Mathf.SmoothDamp(dip, dipTarget, ref dipVelocity, dipAttackTime);

        Vector3 shakeOffset = shaker != null ? shaker.PositionOffset : Vector3.zero;
        transform.localPosition = baseLocalPos + Vector3.down * dip + shakeOffset;
    }

    /// Punches the camera downward. Used by landings and heavy impacts.
    public void AddDip(float amount)
    {
        dipTarget = Mathf.Min(dipTarget + Mathf.Max(0f, amount), maxLandDip);
    }

    void UpdateVaultRoll()
    {
        float targetRoll = 0f;
        switch (controller.VaultTier)
        {
            case 1: targetRoll = lowVaultRoll; break;
            case 2: targetRoll = regularVaultRoll; break;
            case 3: targetRoll = jumpVaultRoll; break;
        }

        float speed = targetRoll > 0f ? vaultRollSpeed * 1.5f : vaultRollSpeed;
        currentRoll = Mathf.Lerp(currentRoll, targetRoll,
            1f - Mathf.Exp(-speed * Time.deltaTime));
        Quaternion shakeRot = shaker != null ? shaker.RotationOffset : Quaternion.identity;
        transform.localRotation = Quaternion.Euler(0f, 0f, -currentRoll) * shakeRot;
    }
}