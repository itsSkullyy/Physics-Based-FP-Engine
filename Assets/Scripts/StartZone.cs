using UnityEngine;

// Start gate for a CourseTimer run. Drop a trigger volume at the spot a run should begin
// (the respawn point is the obvious choice) and it arms the clock the moment the player
// crosses it. Re-entering mid-run resets the clock rather than ignoring you, so walking
// back to the gate after a bad attempt is a valid way to retry - no scene reload needed.
[RequireComponent(typeof(Collider))]
public class StartZone : MonoBehaviour
{
    public string playerTag = "Player";

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;
        CourseTimer.Get().StartRun();
    }
}
