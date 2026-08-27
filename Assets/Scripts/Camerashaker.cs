using UnityEngine;

// Camera shake. Put this on CameraFX (same object as FirstPersonCameraRig).
// It does NOT move the transform itself when a rig is driving it - it just exposes
// offsets that FirstPersonCameraRig folds into its final pose.
[DefaultExecutionOrder(-60)]
public class CameraShaker : MonoBehaviour
{
    public static CameraShaker Instance { get; private set; }

    [Header("Trauma Shake")]
    [Tooltip("Trauma decays to zero over roughly 1/decay seconds.")]
    public float traumaDecay = 1.9f;
    [Tooltip("Shake = trauma^exponent. Higher means small hits stay subtle.")]
    public float traumaExponent = 2f;
    public float maxPositionShake = 0.11f;
    public float maxRotationShake = 3.2f;
    public float noiseFrequency = 24f;
    public float maxTrauma = 1f;

    [Header("Directional Kick")]
    public float kickStiffness = 190f;
    public float kickDamping = 21f;

    [Header("FOV Punch")]
    public float fovStiffness = 130f;
    public float fovDamping = 18f;

    [Header("Options")]
    public bool useUnscaledTime = true;   // keeps shaking during hitstop

    float trauma;

    Vector3 kickPos, kickPosVel;
    Vector3 kickRot, kickRotVel;
    float fovPunch, fovVel;

    float seedA, seedB, seedC, seedD, seedE, seedF;
    float noiseTime;

    bool driven;
    Vector3 baseLocalPos;
    Quaternion baseLocalRot;

    public Vector3 PositionOffset { get; private set; }
    public Quaternion RotationOffset { get; private set; } = Quaternion.identity;
    public float FovOffset => fovPunch;
    public float Trauma => trauma;

    void Awake()
    {
        Instance = this;

        seedA = Random.value * 100f;
        seedB = Random.value * 100f;
        seedC = Random.value * 100f;
        seedD = Random.value * 100f;
        seedE = Random.value * 100f;
        seedF = Random.value * 100f;
    }

    void Start()
    {
        baseLocalPos = transform.localPosition;
        baseLocalRot = transform.localRotation;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void MarkDriven() => driven = true;

    // ---------------------------------------------------------------- public API

    /// Random omnidirectional shake. 0.15 = footstep, 0.4 = solid hit, 0.8 = huge.
    public void AddTrauma(float amount)
    {
        trauma = Mathf.Min(maxTrauma, trauma + Mathf.Max(0f, amount));
    }

    /// Shake that falls off with distance from the camera.
    public void AddTraumaAtPoint(Vector3 point, float amount, float fullRange, float maxRange)
    {
        float d = Vector3.Distance(transform.position, point);
        if (d >= maxRange) return;

        float falloff = 1f - Mathf.InverseLerp(fullRange, maxRange, d);
        AddTrauma(amount * falloff);
    }

    /// localPos in metres, localEuler in degrees, in the camera's local space.
    public void AddKick(Vector3 localPos, Vector3 localEuler)
    {
        kickPosVel += localPos * kickStiffness * 0.05f;
        kickRotVel += localEuler * kickStiffness * 0.05f;
    }

    /// Positive widens the lens, negative pulls it in.
    public void AddFovPunch(float degrees)
    {
        fovVel += degrees * fovStiffness * 0.05f;
    }

    public void Impact(float traumaAmount, Vector3 kickLocalPos, Vector3 kickLocalEuler, float fov)
    {
        AddTrauma(traumaAmount);
        AddKick(kickLocalPos, kickLocalEuler);
        AddFovPunch(fov);
    }

    // ---------------------------------------------------------------- update

    void Update()
    {
        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (dt <= 0f) return;

        dt = Mathf.Min(dt, 0.05f);
        noiseTime += dt * noiseFrequency;

        trauma = Mathf.Max(0f, trauma - traumaDecay * dt);
        float shake = Mathf.Pow(trauma, traumaExponent);

        Vector3 noisePos = Vector3.zero;
        Vector3 noiseRot = Vector3.zero;

        if (shake > 0.0001f)
        {
            noisePos = new Vector3(
                Noise(seedA) * maxPositionShake,
                Noise(seedB) * maxPositionShake,
                Noise(seedC) * maxPositionShake * 0.5f) * shake;

            noiseRot = new Vector3(
                Noise(seedD) * maxRotationShake,
                Noise(seedE) * maxRotationShake,
                Noise(seedF) * maxRotationShake) * shake;
        }

        Spring(ref kickPos, ref kickPosVel, kickStiffness, kickDamping, dt);
        Spring(ref kickRot, ref kickRotVel, kickStiffness, kickDamping, dt);
        SpringFloat(ref fovPunch, ref fovVel, fovStiffness, fovDamping, dt);

        PositionOffset = noisePos + kickPos;
        RotationOffset = Quaternion.Euler(noiseRot + kickRot);

        if (!driven)
        {
            transform.localPosition = baseLocalPos + PositionOffset;
            transform.localRotation = baseLocalRot * RotationOffset;
        }
    }

    float Noise(float seed)
    {
        return Mathf.PerlinNoise(seed, noiseTime) * 2f - 1f;
    }

    static void Spring(ref Vector3 value, ref Vector3 velocity, float stiffness, float damping, float dt)
    {
        Vector3 accel = -value * stiffness - velocity * damping;
        velocity += accel * dt;
        value += velocity * dt;
    }

    static void SpringFloat(ref float value, ref float velocity, float stiffness, float damping, float dt)
    {
        float accel = -value * stiffness - velocity * damping;
        velocity += accel * dt;
        value += velocity * dt;
    }
}
