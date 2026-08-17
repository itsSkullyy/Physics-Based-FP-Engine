using UnityEngine;

// Kill volume under the level. Goes through PlayerHealth when the player has one, so
// death runs through a single code path and the HUD sees it. Falls back to the old
// straight teleport when there is no health component, which keeps existing scenes
// working unchanged.
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
            // Let this plane's own respawn point stand in if the player has not been
            // given one, so a scene that only ever configured the deathplane still works.
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