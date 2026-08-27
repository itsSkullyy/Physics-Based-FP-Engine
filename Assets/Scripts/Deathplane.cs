using UnityEngine;

// Kill volume under the level. Routes through PlayerHealth when present so death goes
// through one code path and the HUD sees it. Falls back to a plain teleport otherwise.
public class Deathplane : MonoBehaviour
{
    [SerializeField] private Transform respawnPoint;

    [Header("Behaviour")]
    [Tooltip("On: falling in kills outright. Off: it deals damage and puts you back.")]
    [SerializeField] private bool kills = true;
    [SerializeField] private float damage = 35f;
    [SerializeField] private string playerTag = "Player";

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;

        PlayerHealth health = other.GetComponentInParent<PlayerHealth>();

        if (health != null)
        {
            if (health.respawnPoint == null) health.respawnPoint = respawnPoint;

            if (kills)
            {
                health.Kill();
            }
            else
            {
                health.Damage(damage);
                if (respawnPoint != null) health.Teleport(respawnPoint.position);
            }

            return;
        }

        if (respawnPoint != null)
        {
            other.transform.position = respawnPoint.position;

            Rigidbody body = other.attachedRigidbody;
            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
        else
        {
            Debug.Log("Respawn Point is Missing");
        }
    }
}