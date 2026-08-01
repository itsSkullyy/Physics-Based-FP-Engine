using UnityEngine;
using Unity.Cinemachine;

// Camera effects. Attach to CameraFX (child of CameraAnchor).
// Auto-configures the CinemachineCamera as a hard first person mount.
public class FirstPersonCameraRig : MonoBehaviour
{
    public FirstPersonCharacterController controller;
    public CinemachineCamera cineCam;

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
    public float landDipBase = 0.08f;
    public float landDipPerFallSpeed = 0.008f;
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
    float dipVelocity;
    bool wasGrounded;
    float lastFallSpeed;
    Vector3 baseLocalPos;

    void Start()
    {
        baseLocalPos = transform.localPosition;

        if (controller != null && controller.cameraTransform == transform)
            Debug.LogError("Rig is on the CameraAnchor. Put it on a child (CameraFX).", this);

        if (cineCam != null && cineCam.transform == transform)
            Debug.LogError("Rig is on the CinemachineCamera. Put it on CameraFX.", this);

        if (autoConfigureCineCam)
            ConfigureCineCam();

        currentFov = baseFov;
        if (cineCam != null)
            cineCam.Lens.FieldOfView = baseFov;
    }

    void ConfigureCineCam()
    {
        if (cineCam == null) return;

        cineCam.Target.TrackingTarget = transform;

        RemoveIfPresent<CinemachinePanTilt>();
        RemoveIfPresent<CinemachineInputAxisController>();
        RemoveIfPresent<CinemachineFollow>();
        RemoveIfPresent<CinemachineOrbitalFollow>();
        RemoveIfPresent<CinemachinePositionComposer>();
        RemoveIfPresent<CinemachineRotationComposer>();
        RemoveIfPresent<CinemachineHardLookAt>();

        if (!cineCam.TryGetComponent(out CinemachineHardLockToTarget _))
            cineCam.gameObject.AddComponent<CinemachineHardLockToTarget>();
        if (!cineCam.TryGetComponent(out CinemachineRotateWithFollowTarget _))
            cineCam.gameObject.AddComponent<CinemachineRotateWithFollowTarget>();
    }

    void RemoveIfPresent<T>() where T : Component
    {
        if (cineCam != null && cineCam.TryGetComponent(out T comp))
            Destroy(comp);
    }

    void Update()
    {
        if (controller == null) return;

        TrackFall();
        UpdateLens();
        UpdateDip();
        UpdateVaultRoll();
    }

    void TrackFall()
    {
        float vy = controller.Velocity.y;
        if (vy < 0f)
            lastFallSpeed = -vy;

        if (controller.IsGrounded && !wasGrounded)
        {
            float amount = landDipBase + lastFallSpeed * landDipPerFallSpeed;
            dip = Mathf.Min(dip + amount, maxLandDip);
            lastFallSpeed = 0f;
        }

        wasGrounded = controller.IsGrounded;
    }

    void UpdateLens()
    {
        if (cineCam == null) return;

        float speedT = Mathf.Clamp01((controller.CurrentSpeed - controller.baseSpeed) /
            Mathf.Max(1f, controller.maxSpeed * 1.6f - controller.baseSpeed));
        float targetFov = baseFov
            + (controller.IsVaulting ? vaultFovBoost : 0f)
            + speedT * speedFovBoost;

        currentFov = Mathf.Lerp(currentFov, targetFov,
            1f - Mathf.Exp(-fovLerpSpeed * Time.deltaTime));
        cineCam.Lens.FieldOfView = currentFov;

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
        dip = Mathf.SmoothDamp(dip, 0f, ref dipVelocity, 1f / dipRecoverSpeed);
        transform.localPosition = baseLocalPos + Vector3.down * dip;
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
        transform.localRotation = Quaternion.Euler(0f, 0f, -currentRoll);
    }
}