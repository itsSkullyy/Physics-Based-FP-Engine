using UnityEngine;

// A slab in a doorway that slides up while any of its AxeButtons is pressed and drops
// shut when the axe comes out.
//
// The trick it's built for: throw the axe into the button, walk through the open door,
// and an AxeRecallZone past it calls the axe home. The axe leaving the button starts the
// door closing, but while that axe is flying back through the doorway the door's bottom
// edge holds just above it, then slams the moment it's through - so the axe always just
// squeezes under. Put this on the door object itself, in its closed position.
[RequireComponent(typeof(Collider))]
public class AxeDoor : MonoBehaviour
{
    public AxeButton[] buttons;
    public float openHeight = 4.2f;
    public float openSpeed = 5f;
    public float closeSpeed = 9f;
    [Tooltip("Gap kept between the door's bottom edge and the returning axe while it passes under.")]
    public float axeClearance = 0.7f;
    [Tooltip("Longest the door will hold for a returning axe before it gives up and shuts.")]
    public float maxHoldTime = 2.5f;

    Vector3 closedLocalPos;
    float closedBottom;
    float lift;
    ThrownAxe passing;
    float passingSide;
    bool wasOpen;

    void Awake()
    {
        closedLocalPos = transform.localPosition;
        closedBottom = GetComponent<Collider>().bounds.min.y;
    }

    void Update()
    {
        bool open = false;
        foreach (AxeButton b in buttons)
        {
            if (b != null && b.Pressed)
            {
                open = true;
                break;
            }
        }

        float target = open ? openHeight : 0f;

        if (open)
        {
            passing = null;
        }
        else
        {
            if (passing == null)
            {
                passing = ReturningAxe();
                if (passing != null)
                {
                    passingSide = Side(passing.HeadPosition);
                }
            }

            if (passing != null)
            {
                bool through = Side(passing.HeadPosition) != passingSide;
                if (!passing.IsRecalling || through || TimeSinceRelease() > maxHoldTime)
                {
                    passing = null;
                }
                else
                {
                    float overAxe = passing.HeadPosition.y - closedBottom + axeClearance;
                    target = Mathf.Clamp(overAxe, 0f, openHeight);
                }
            }
        }

        float speed = target > lift ? openSpeed : closeSpeed;
        lift = Mathf.MoveTowards(lift, target, speed * Time.deltaTime);
        transform.localPosition = closedLocalPos + Vector3.up * lift;

        bool isOpen = lift > 0.05f;
        if (isOpen != wasOpen)
        {
            wasOpen = isOpen;
            Synth.PlayAt(isOpen ? GameSounds.GrappleReel : GameSounds.WallKick, transform.position, 1f, isOpen ? 0.6f : 0.7f);
        }
    }

    // the axe that just came out of one of our buttons and is on its way back
    ThrownAxe ReturningAxe()
    {
        foreach (AxeButton b in buttons)
        {
            if (b != null && b.LastAxe != null && b.LastAxe.IsRecalling && Time.time - b.ReleasedAt < maxHoldTime)
            {
                return b.LastAxe;
            }
        }
        return null;
    }

    float TimeSinceRelease()
    {
        float best = float.MaxValue;
        foreach (AxeButton b in buttons)
        {
            if (b != null)
            {
                best = Mathf.Min(best, Time.time - b.ReleasedAt);
            }
        }
        return best;
    }

    float Side(Vector3 point) => Mathf.Sign(Vector3.Dot(point - transform.position, transform.forward));
}
