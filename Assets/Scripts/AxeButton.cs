using UnityEngine;

// A big target the thrown axe can stick into. While the axe is stuck in it, it's pressed
// and every AxeDoor that lists it stays open. Pull the axe out (recall, or a zip reel) and
// it lets go. Put it on the object with the collider the axe hits, out of reach on foot,
// so the only way to press it is a throw.
[RequireComponent(typeof(Collider))]
public class AxeButton : MonoBehaviour
{
    [Tooltip("Optional. Tinted idle/pressed so it's obvious it's working.")]
    public Renderer face;
    public Color idleColor = new Color(1f, 0.3f, 0.2f, 1f);
    public Color pressedColor = new Color(0.35f, 1f, 0.45f, 1f);

    public bool Pressed { get; private set; }
    /// The axe stuck in it right now, if any.
    public ThrownAxe HeldAxe { get; private set; }
    /// The last axe that was stuck in it, and when it came out - AxeDoor uses these to hold
    /// itself up over that axe while it flies back through the doorway.
    public ThrownAxe LastAxe { get; private set; }
    public float ReleasedAt { get; private set; } = -99f;

    MaterialPropertyBlock block;

    void Start()
    {
        Tint(idleColor);
    }

    void Update()
    {
        ThrownAxe held = null;
        foreach (ThrownAxe axe in ThrownAxe.Live)
        {
            Collider surface = axe != null ? axe.StuckSurface : null;
            if (surface != null && surface.transform.IsChildOf(transform))
            {
                held = axe;
                break;
            }
        }

        HeldAxe = held;
        bool now = held != null;
        if (now == Pressed)
        {
            return;
        }

        Pressed = now;
        if (now)
        {
            LastAxe = held;
            Tint(pressedColor);
            Synth.PlayAt(GameSounds.ChargeFull, transform.position, 1f, 0.8f);
        }
        else
        {
            ReleasedAt = Time.time;
            Tint(idleColor);
        }
    }

    void Tint(Color c)
    {
        if (face == null)
        {
            return;
        }
        block ??= new MaterialPropertyBlock();
        face.GetPropertyBlock(block);
        block.SetColor("_BaseColor", c);
        block.SetColor("_Color", c);
        face.SetPropertyBlock(block);
    }
}
