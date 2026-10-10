using UnityEngine;

// Walk-in zone that becomes where the player comes back to. Sets PlayerHealth's respawn
// point (so deaths and kill planes use it) and is where a FallReset sends them. Put one
// across the doorway at the start of every room, so failing a room costs seconds, not a
// long walk back.
//
// The player comes back at this object's position facing its blue (forward) arrow, so
// point it into the room.
[RequireComponent(typeof(Collider))]
public class Checkpoint : MonoBehaviour
{
    [Tooltip("Optional separate spot to come back at. Empty = this object.")]
    public Transform spawnPoint;
    public bool puffOnReach = true;

    /// The last one the player walked through in this play session.
    public static Checkpoint Last { get; private set; }

    /// Forgets the last checkpoint, so the next one walked through counts again. LevelReset
    /// does this when the player starts a level over.
    public static void ClearLast() => Last = null;

    public Vector3 SpawnPosition => (spawnPoint != null ? spawnPoint : transform).position;
    public Quaternion SpawnRotation
    {
        get
        {
            Vector3 f = (spawnPoint != null ? spawnPoint : transform).forward;
            f.y = 0f;
            return Quaternion.LookRotation(f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward, Vector3.up);
        }
    }

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
        if (player == null || Last == this)
        {
            return;
        }

        Last = this;

        PlayerHealth health = player.GetComponent<PlayerHealth>();
        if (health != null)
        {
            health.respawnPoint = spawnPoint != null ? spawnPoint : transform;
        }

        if (puffOnReach)
        {
            JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
            if (fx != null)
            {
                fx.AirPuff(SpawnPosition + Vector3.up * 0.5f, Vector3.up, 0.35f);
            }
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.8f);
        Vector3 p = SpawnPosition;
        Gizmos.DrawWireSphere(p + Vector3.up, 0.5f);
        Gizmos.DrawLine(p + Vector3.up, p + Vector3.up + SpawnRotation * Vector3.forward * 1.5f);
    }
}
