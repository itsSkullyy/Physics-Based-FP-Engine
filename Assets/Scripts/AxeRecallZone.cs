using UnityEngine;

// Walk-in zone that calls the axe home if it's stuck in one of the listed AxeButtons.
// Put it just past the door those buttons open: going through the door brings the axe
// flying back after you, and the closing AxeDoor lets it squeeze underneath.
[RequireComponent(typeof(Collider))]
public class AxeRecallZone : MonoBehaviour
{
    public AxeButton[] buttons;
    [Tooltip("Faster than a normal recall, so it's back before the door is down.")]
    public float speedScale = 1.6f;

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

        foreach (AxeButton b in buttons)
        {
            if (b == null || b.HeldAxe == null)
            {
                continue;
            }
            BattleAxe axe = player.transform.root.GetComponentInChildren<BattleAxe>(true);
            if (axe != null)
            {
                axe.RecallNow(speedScale);
            }
            return;
        }
    }
}
