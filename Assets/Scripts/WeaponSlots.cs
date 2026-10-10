using System;
using UnityEngine;

// Three-slot loadout. Put this on the Player root, next to PlayerInputRouter.
//
//   Slot 1  Battle axe      - primary swings / holds to charge a throw, secondary throws
//                             instantly, and (once thrown) picks up or recalls.
//   Slot 2  Swing grapple   - primary fires and holds the rope, release lets go.
//   Slot 3  Empty hands     - nothing equipped.
//
// The axe and grapple share the primary/secondary buttons; which one they drive is
// decided here rather than by the weapons themselves. Zip sits outside this - its own
// action, works on every slot including empty hands.
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

    [Header("Abilities")]
    [Tooltip("Whether the player has the axe. AbilityPickup takes it away at the start of the tutorial and gives it back when picked up. A slot you don't have is skipped when switching and hidden on the HUD.")]
    public bool hasAxe = true;
    [Tooltip("Whether the player has the grapple (zip and swing). Same as above.")]
    public bool hasGrapple = true;

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

    /// Fires when the player is given an ability they didn't have.
    public event Action<Slot> AbilityUnlocked;

    public bool Owns(int index) =>
        index == (int)Slot.Axe ? hasAxe : index == (int)Slot.Grapple ? hasGrapple : true;

    public bool Owns(Slot slot) => Owns((int)slot);

    float cooldown;
    bool scrollLatched;
    bool started;

    void Awake()
    {
        if (input == null)
        {
            input = PlayerInputRouter.Resolve(this);
        }
        if (axe == null)
        {
            axe = GetComponentInChildren<BattleAxe>(true);
        }
        if (grappling == null)
        {
            grappling = GetComponent<Grappling>();
        }
        if (grappling == null)
        {
            grappling = GetComponentInChildren<Grappling>(true);
        }

        Current = Mathf.Clamp(startSlot, 0, SlotCount - 1);
        if (!Owns(Current))
        {
            Current = FirstOwned();
        }
    }

    int FirstOwned()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            if (Owns(i))
            {
                return i;
            }
        }
        return (int)Slot.Empty;
    }

    /// Gives or takes away the axe or the grapple. Taking away the slot you're holding
    /// moves you to the first one you still have.
    public void SetOwned(Slot slot, bool owned, bool equip = false)
    {
        if (slot == Slot.Empty || Owns(slot) == owned)
        {
            if (owned && equip)
            {
                Select((int)slot);
            }
            return;
        }

        if (slot == Slot.Axe)
        {
            hasAxe = owned;
        }
        else
        {
            hasGrapple = owned;
        }

        if (!Owns(Current))
        {
            Current = FirstOwned();
        }
        if (owned && equip)
        {
            Current = (int)slot;
        }

        if (started)
        {
            Apply();
        }
        SlotChanged?.Invoke(Current);

        if (owned)
        {
            AbilityUnlocked?.Invoke(slot);
        }
    }

    void OnEnable()
    {
        if (axe == null)
        {
            return;
        }
        axe.AxeThrown += OnAxeThrown;
        axe.AxeReturned += OnAxeReturned;
    }

    void OnDisable()
    {
        if (axe == null)
        {
            return;
        }
        axe.AxeThrown -= OnAxeThrown;
        axe.AxeReturned -= OnAxeReturned;
    }

    void Start()
    {
        // Applied here rather than Awake so the weapons have finished their own Awake
        // and cached their renderers before this starts hiding things.
        started = true;
        Apply();
        SlotChanged?.Invoke(Current);
    }

    void Update()
    {
        if (input == null || !input.inputEnabled)
        {
            return;
        }

        cooldown -= Time.unscaledDeltaTime;

        if (Hit(input.slot1)) { Select(0); return; }
        if (Hit(input.slot2)) { Select(1); return; }
        if (Hit(input.slot3)) { Select(2); return; }

        if (Hit(input.slotNext)) { SelectDown(); return; }
        if (Hit(input.slotPrev)) { SelectUp(); return; }

        if (scrollSwitchesSlots)
        {
            HandleScroll();
        }
    }
    
    static bool Hit(GameAction a) => a != null && a.Pressed;

    void HandleScroll()
    {
        if (grappling != null && grappling.IsSwinging) { scrollLatched = false; return; }

        float s = input.ScrollY;

        if (Mathf.Abs(s) < scrollThreshold) { scrollLatched = false; return; }
        if (scrollLatched || cooldown > 0f)
        {
            return;
        }

        scrollLatched = true;

        if (s > 0f)
        {
            SelectUp();
        }
        else
        {
            SelectDown();
        }
    }

    // ---------------------------------------------------------------- selection

    /// One slot toward slot 1, skipping slots you don't have.
    public void SelectUp() => Step(-1);

    /// One slot toward slot 3, skipping slots you don't have. This is what an axe throw does.
    public void SelectDown() => Step(1);

    void Step(int dir)
    {
        for (int i = 1; i < SlotCount; i++)
        {
            int index = Wrap(Current + dir * i);
            if (Owns(index))
            {
                Select(index);
                return;
            }
        }
    }

    int Wrap(int index) => wrapAround
        ? ((index % SlotCount) + SlotCount) % SlotCount
        : Mathf.Clamp(index, 0, SlotCount - 1);

    public void Equip(Slot slot) => Select((int)slot);

    public void Select(int index)
    {
        index = Wrap(index);

        if (index == Current || !Owns(index))
        {
            return;
        }

        Current = index;
        cooldown = switchCooldown;

        if (started)
        {
            Apply();
        }

        if (logDebug)
        {
            Debug.Log("[WeaponSlots] Slot " + (Current + 1) + " (" + CurrentSlot + ")", this);
        }

        SlotChanged?.Invoke(Current);
    }

    void Apply()
    {
        if (axe != null)
        {
            axe.SetEquipped(AxeEquipped);
        }

        if (grappling != null)
        {
            grappling.swingEquipped = GrappleEquipped;

            if (!GrappleEquipped && grappling.IsSwinging)
            {
                grappling.Detach(false);
            }

            // zip works from every slot, so without the grapple the whole component is off:
            // no zip, no swing, no reticle
            grappling.enabled = hasGrapple;
        }
    }

    // ---------------------------------------------------------------- axe hooks

    void OnAxeThrown()
    {
        if (swapDownOnThrow)
        {
            SelectDown();
        }
    }

    void OnAxeReturned()
    {
        if (swapBackOnAxeReturned)
        {
            Select((int)Slot.Axe);
        }
    }
}
