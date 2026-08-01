using UnityEngine;

public class Deathplane : MonoBehaviour
{
    [SerializeField] private Transform respawnPoint;


    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            if (respawnPoint != null)
            {
                other.transform.position = respawnPoint.position;
            }
            else
            {
                Debug.Log("Respawn Point is Missing");
            }
        }
    }
}