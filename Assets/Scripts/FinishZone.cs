using UnityEngine;

// Finish gate for a CourseTimer run. Crossing it stops the clock and cashes in the same
// juice hooks everything else in the game uses (shake, impact frame, a JuiceFX burst) so
// the payoff for finishing a course reads on the same level as landing an axe hit, not
// like a silent UI event bolted on the side.
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

        // FinishRun no-ops if no run was in progress, so walking through the finish
        // without ever touching the start gate is silently ignored rather than logging
        // a bogus time.
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
