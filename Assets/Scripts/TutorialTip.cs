using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

// Walk-in zone that shows a tip card (AbilityTooltip) the first time the player enters.
// Put it on any object with a trigger collider - a BoxCollider stretched across a doorway
// works best - and type the card in the inspector.
//
// Keys come from the player's own bindings: write {jump}, {slide}, {primary}, {secondary},
// {zip}, {axePickup}, {slot2} (any action field on PlayerInputRouter) and the card shows
// [Space], [Left Shift], ... or whatever it's been rebound to. {move} shows WASD.
[RequireComponent(typeof(Collider))]
public class TutorialTip : MonoBehaviour
{
    [Header("Card")]
    public string title = "JUMP";
    [Tooltip("Small line under the title.")]
    public string subtitle = "MOVEMENT";
    [Tooltip("One line each. {jump}, {slide} etc. become the player's keys.")]
    [TextArea(1, 3)] public string[] lines = { "{jump}  Jump. Hold it to go higher, tap it for a hop." };
    public Color accent = new Color(0.45f, 0.85f, 1f, 1f);
    public float seconds = 8f;

    [Header("When")]
    [Tooltip("Only the first time. Off shows it every time you walk in (after the cooldown).")]
    public bool once = true;
    public float cooldown = 20f;
    [Tooltip("Skip the card if the player already has this many seconds of play in the scene - handy for tips that only matter on a first visit. 0 = always show.")]
    public float onlyBeforeSeconds = 0f;

    bool shown;
    float lastShown = -999f;

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
        if (other.attachedRigidbody == null || other.attachedRigidbody.GetComponent<FirstPersonCharacterController>() == null)
        {
            return;
        }
        if ((once && shown) || Time.unscaledTime - lastShown < cooldown)
        {
            return;
        }
        if (onlyBeforeSeconds > 0f && Time.timeSinceLevelLoad > onlyBeforeSeconds)
        {
            return;
        }

        shown = true;
        lastShown = Time.unscaledTime;

        string[] filled = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            filled[i] = Fill(lines[i]);
        }
        AbilityTooltip.Get().Show(title, filled, accent, seconds, subtitle);
    }

    static readonly Regex Token = new Regex(@"\{(\w+)\}");

    /// Swaps {action} tokens for the player's current key, in bold brackets.
    public static string Fill(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }
        return Token.Replace(line, m => "<b>[" + KeyFor(m.Groups[1].Value) + "]</b>");
    }

    static string KeyFor(string action)
    {
        if (action == "move")
        {
            return "WASD";
        }

        PlayerInputRouter input = PlayerInputRouter.Instance;
        if (input != null)
        {
            FieldInfo field = typeof(PlayerInputRouter).GetField(action, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.GetValue(input) is GameAction gameAction)
            {
                string label = gameAction.Label;
                if (!string.IsNullOrEmpty(label) && label != "-")
                {
                    return label;
                }
            }
        }
        return action;
    }

    void OnDrawGizmos()
    {
        Collider c = GetComponent<Collider>();
        if (c == null)
        {
            return;
        }
        Gizmos.color = new Color(accent.r, accent.g, accent.b, 0.25f);
        Gizmos.DrawCube(c.bounds.center, c.bounds.size);
    }
}
