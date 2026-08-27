using UnityEngine;

// Finish gate for a CourseTimer run. Crossing it stops the clock and fires the same
// juice hooks the rest of the game uses (shake, impact frame, a JuiceFX burst).
[RequireComponent(typeof(Collider))]
public class FinishZone : MonoBehaviour
{
    public string playerTag = "Player";

    [Header("Feedback")]
    public bool useJuiceFX = true;
    public float finishShake = 0.5f;
    public bool useImpactFrame = true;
    public float impactFreeze = 0.08f;
    public float impactOverlay = 0.2f;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;

        bool wasRunning = CourseTimer.Get().Running;
        CourseTimer.Get().FinishRun();
        if (!wasRunning) return;

        Vector3 point = other.ClosestPoint(transform.position);

        if (useJuiceFX)
        {
            JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
            if (fx != null) fx.ImpactBurst(point, Vector3.up, 1f);
        }

        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTrauma(finishShake);

        if (useImpactFrame)
        {
            ImpactFrames frames = ImpactFrames.Get();
            frames.SetImpactPoint(point);
            frames.Freeze(impactFreeze, impactOverlay, 1f);
        }
    }
}
