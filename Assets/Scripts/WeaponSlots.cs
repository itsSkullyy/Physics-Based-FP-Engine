using System;
using UnityEngine;

// Three-slot loadout. Put this on the Player root, next to PlayerInputRouter.
//
//   Slot 1  Battle axe      - primary swings / holds to charge a throw, secondary throws
//                             instantly, and (once thrown) picks up or recalls.
//   Slot 2  Swing grapple   - primary fires and holds the rope, release lets go.
//   Slot 3  Empty hands     - nothing equipped.
//
// The whole point is that the axe and the grapple SHARE the primary and secondary
// buttons; which one they drive is decided here, not by the weapons themselves. The
// zip grapple deliberately sits outside all of this - it is bound to its own action and
// works on every slot, including the empty one.
//
// Throwing the axe drops you one slot down onto the grapple. Switch back to slot 1 to
// recall it (the recall cooldown ring stays visible in the world meanwhile).
[DefaultExecutionOrder(-450)]   // after PlayerInputRouter (-500), before the weapons
public class WeaponSlots : MonoBehaviour
{
    public const int SlotCount = 3;

    public enum Slot { Axe = 0, Grapple = 1, Empty = 2 }

    [Header("Refs")]
    public PlayerInputRouter input;
    public BattleAxe axe;
    public Grappling grappling;

    [Header("Start")]
    [Range(0, 2)] public int startSlot = 0;

    [Header("Switching")]
    [Tooltip("Mouse wheel (and d-pad up/down) cycles slots. Ignored while swinging, " +
             "because the wheel is reeling the rope then.")]
    public bool scrollSwitchesSlots = true;
    [Range(0.02f, 1f)] public float scrollThreshold = 0.15f;
    [Tooltip("Wrapping means slot 3 -> slot 1. Off clamps at both ends.")]
    public bool wrapAround = true;
    public float switchCooldown = 0.1f;

    [Header("Auto Swap")]
    [Tooltip("Throwing the axe moves you one slot DOWN, onto the grapple.")]
    public bool swapDownOnThrow = true;
    [Tooltip("Catching the axe again puts you straight back on slot 1.")]
    public bool swapBackOnAxeReturned = true;

    [Header("Debug")]
    public bool logDebug = false;

    public int Current { get; private set; }
    public Slot CurrentSlot => (Slot)Current;
    public bool AxeEquipped => Current == (int)Slot.Axe;
    public bool GrappleEquipped => Current == (int)Slot.Grapple;

    /// Fires with the new slot index whenever the selection changes.
    public event Action<int> SlotChanged;

    float cooldown;
    bool scrollLatched;
    bool started;

    void Awake()
    {
        if (input == null) input = PlayerInputRouter.Resolve(this);
        if (axe == null) axe = GetComponentInChildren<BattleAxe>(true);
        if (grappling == null) grappling = GetComponent<Grappling>();
        if (grappling == null) grappling = GetComponentInChildren<Grappling>(true);

        Current = Mathf.Clamp(startSlot, 0, SlotCount - 1);
    }

    void OnEnable()
    {
        if (axe == null) return;
        axe.AxeThrown += OnAxeThrown;
        axe.AxeReturned += OnAxeReturned;
    }

    void OnDisable()
    {
        if (axe == null) return;
        axe.AxeThrown -= OnAxeThrown;
        axe.AxeReturned -= OnAxeReturned;
    }

    void Start()
    {
        // Applied here rather than in Awake so the weapons have finished their own Awake
        // and their renderers are cached before we start hiding things.
        started = true;
        Apply();
        SlotChanged?.Invoke(Current);
    }

    void Update()
    {
        if (input == null || !input.inputEnabled) return;

        cooldown -= Time.unscaledDeltaTime;

        if (Hit(input.slot1)) { Select(0); return; }
        if (Hit(input.slot2)) { Select(1); return; }
        if (Hit(input.slot3)) { Select(2); return; }

        if (Hit(input.slotNext)) { SelectDown(); return; }
        if (Hit(input.slotPrev)) { SelectUp(); return; }

        if (scrollSwitchesSlots) HandleScroll();
    }
    
    static bool Hit(GameAction a) => a != null && a.Pressed;

    void HandleScroll()
    {
        // The wheel belongs to the rope while it is out.
        if (grappling != null && grappling.IsSwinging) { scrollLatched = false; return; }

        float s = input.ScrollY;

        if (Mathf.Abs(s) < scrollThreshold) { scrollLatched = false; return; }
        if (scrollLatched || cooldown > 0f) return;

        scrollLatched = true;

        if (s > 0f) SelectUp();
        else SelectDown();
    }

    // ---------------------------------------------------------------- selection

    /// One slot toward slot 1.
    public void SelectUp() => Select(Current - 1);

    /// One slot toward slot 3. This is what an axe throw does.
    public void SelectDown() => Select(Current + 1);

    public void Equip(Slot slot) => Select((int)slot);

    public void Select(int index)
    {
        index = wrapAround
            ? ((index % SlotCount) + SlotCount) % SlotCount
            : Mathf.Clamp(index, 0, SlotCount - 1);

        if (index == Current) return;

        Current = index;
        cooldown = switchCooldown;

        if (started) Apply();

        if (logDebug) Debug.Log("[WeaponSlots] Slot " + (Current + 1) + " (" + CurrentSlot + ")", this);

        SlotChanged?.Invoke(Current);
    }

    // Single place that decides what "equipped" means for each weapon. Nothing else
    // touches those flags, so there is no way for the two to end up both live.
    void Apply()
    {
        if (axe != null)
            axe.SetEquipped(AxeEquipped);

        if (grappling != null)
        {
            grappling.swingEquipped = GrappleEquipped;

            // Putting the grapple away mid-swing has to cut the rope, otherwise the
            // solver keeps hauling on a player who is holding an axe.
            if (!GrappleEquipped && grappling.IsSwinging)
                grappling.Detach(false);
        }
    }

    // ---------------------------------------------------------------- axe hooks

    void OnAxeThrown()
    {
        if (swapDownOnThrow) SelectDown();
    }

    void OnAxeReturned()
    {
        if (swapBackOnAxeReturned) Select((int)Slot.Axe);
    }
}
