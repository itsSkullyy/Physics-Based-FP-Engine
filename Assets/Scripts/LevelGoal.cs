using UnityEngine;

// Floating pickup that ends the level. Bobs and spins in place, throws off the same
// mesh-based JuiceFX dust the rest of the game uses so it reads as part of the same
// world instead of a UI element pasted into 3D space, and on touch stops CourseTimer,
// grades the final time against the rank thresholds below, and hands off to
// LevelCompleteMenu.
[RequireComponent(typeof(Collider))]
public class LevelGoal : MonoBehaviour
{
    public string playerTag = "Player";

    [Header("Float")]
    public float bobHeight = 0.35f;
    public float bobSpeed = 1.6f;
    public float spinSpeed = 60f;

    [Header("Ambient Particles")]
    public bool ambientParticles = true;
    public float ambientInterval = 0.35f;
    public float ambientStrength = 0.25f;

    [Header("Pickup Feedback")]
    public bool useJuiceFX = true;
    public float pickupShake = 0.6f;
    public bool useImpactFrame = true;
    public float impactFreeze = 0.1f;
    public float impactOverlay = 0.25f;

    [Header("Rank Thresholds")]
    [Tooltip("Finish at or under this many seconds for an S rank.")]
    public float sTime = 30f;
    [Tooltip("Finish at or under this many seconds for an A rank.")]
    public float aTime = 45f;
    [Tooltip("Finish at or under this many seconds for a B rank.")]
    public float bTime = 60f;
    [Tooltip("Finish at or under this many seconds for a C rank.")]
    public float cTime = 90f;
    [Tooltip("Finish at or under this many seconds for a D rank. Slower than this is an F.")]
    public float dTime = 120f;

    Vector3 startPos;
    float ambientTimer;
    bool collected;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void Start()
    {
        startPos = transform.position;
    }

    void Update()
    {
        if (collected) return;

        float y = Mathf.Sin(Time.time * bobSpeed) * bobHeight;
        transform.position = startPos + Vector3.up * y;
        transform.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);

        if (!ambientParticles) return;

        ambientTimer -= Time.deltaTime;
        if (ambientTimer > 0f) return;
        ambientTimer = ambientInterval;

        JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
        if (fx != null) fx.AirPuff(transform.position, Vector3.up, ambientStrength);
    }

    void OnTriggerEnter(Collider other)
    {
        if (collected || !other.CompareTag(playerTag)) return;
        collected = true;

        CourseTimer timer = CourseTimer.Get();
        bool newBest = timer.FinishRun();
        float elapsed = timer.Elapsed;

        Vector3 point = other.ClosestPoint(transform.position);

        if (useJuiceFX)
        {
            JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
            if (fx != null) fx.ImpactBurst(point, Vector3.up, 1f);
        }

        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTrauma(pickupShake);

        if (useImpactFrame)
        {
            ImpactFrames frames = ImpactFrames.Get();
            frames.SetImpactPoint(point);
            frames.Freeze(impactFreeze, impactOverlay, 1f);
        }

        GetComponent<Collider>().enabled = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            r.enabled = false;

        LevelCompleteMenu.Get().Show(elapsed, RankFor(elapsed), newBest);
    }

    char RankFor(float time)
    {
        if (time <= sTime) return 'S';
        if (time <= aTime) return 'A';
        if (time <= bTime) return 'B';
        if (time <= cTime) return 'C';
        if (time <= dTime) return 'D';
        return 'F';
    }
}
