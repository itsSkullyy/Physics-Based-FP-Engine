using UnityEngine;

// Trigger sitting just in front of a BreakableWall. It watches for the player crossing it
// above a speed threshold and reports that back to the wall, which shatters and lets them
// through. Created and configured by BreakableWall - you never add this by hand.
[RequireComponent(typeof(Collider))]
public class RunThroughProbe : MonoBehaviour
{
    BreakableWall wall;
    float speedThreshold;
    string playerTag;

    public void Init(BreakableWall owner, float threshold, string tag)
    {
        wall = owner;
        speedThreshold = threshold;
        playerTag = tag;
    }

    void OnTriggerEnter(Collider other)
    {
        Evaluate(other);
    }

    // Also checked on stay: a player who was just under the threshold on entry but
    // accelerated inside the trigger (a dart, a slide launch) still gets to smash through.
    void OnTriggerStay(Collider other)
    {
        Evaluate(other);
    }

    void Evaluate(Collider other)
    {
        if (wall == null) return;
        if (!string.IsNullOrEmpty(playerTag) && !other.CompareTag(playerTag)) return;

        Rigidbody body = other.attachedRigidbody;
        if (body == null) return;

        if (body.linearVelocity.magnitude < speedThreshold) return;

        Vector3 contact = GetComponent<Collider>().ClosestPoint(body.position);
        wall.OnRunThrough(body, contact);
    }
}
