using UnityEngine;

// Start gate for a CourseTimer run. Arms the clock the moment the player crosses it;
// re-entering mid-run resets it, so walking back to the gate is a valid retry.
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
