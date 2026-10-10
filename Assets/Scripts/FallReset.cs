using UnityEngine;

// Trigger volume at the bottom of a tutorial pit. Falling in puts the player straight back
// at the last Checkpoint, facing into the room, with no damage and no death. That keeps
// failing a jump down to a second or two, the way Celeste does it.
//
// For pits in the real levels, use a Deathplane instead: it goes through PlayerHealth and
// also comes back at the last Checkpoint.
[RequireComponent(typeof(Collider))]
public class FallReset : MonoBehaviour
{
    [Tooltip("Used when no Checkpoint has been reached yet.")]
    public Transform fallback;
    public float shake = 0.25f;

    void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.attachedRigidbody == null)
        {
            return;
        }
        FirstPersonCharacterController player = other.attachedRigidbody.GetComponent<FirstPersonCharacterController>();
        if (player == null)
        {
            return;
        }

        Vector3 pos;
        Quaternion rot;
        if (Checkpoint.Last != null)
        {
            pos = Checkpoint.Last.SpawnPosition;
            rot = Checkpoint.Last.SpawnRotation;
        }
        else if (fallback != null)
        {
            pos = fallback.position;
            rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(fallback.forward, Vector3.up).normalized, Vector3.up);
        }
        else
        {
            return;
        }

        // a rope still attached would yank them straight back down the pit
        Grappling grappling = player.transform.root.GetComponentInChildren<Grappling>();
        if (grappling != null && grappling.IsSwinging)
        {
            grappling.Detach(false);
        }

        player.TeleportTo(pos, rot, Vector3.zero);

        JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
        if (fx != null)
        {
            fx.AirPuff(pos + Vector3.up, Vector3.up, 0.4f);
        }
        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTrauma(shake);
        }
    }

    void OnDrawGizmos()
    {
        Collider c = GetComponent<Collider>();
        if (c == null)
        {
            return;
        }
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.25f);
        Gizmos.DrawCube(c.bounds.center, c.bounds.size);
    }
}
