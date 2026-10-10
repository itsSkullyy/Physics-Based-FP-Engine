using UnityEngine;
using UnityEngine.SceneManagement;

// Floating pickup that gives the player an ability: the battle axe or the grapple.
//
// In the scene the game starts in, the ability is taken off the player at load, and
// touching this gives it back, switches to it and shows a controls card (AbilityTooltip)
// with the player's own key bindings. Arriving through a portal keeps whatever you
// already had, so a pickup you already own just isn't there.
//
// With no renderers under it, it makes its own model at runtime: the axe borrows the real
// axe model off the player's thrown axe, the grapple gets a simple stand-in. Drop a model
// in as a child to use that instead.
[RequireComponent(typeof(SphereCollider))]
public class AbilityPickup : MonoBehaviour
{
    public enum Ability { Axe, Grapple }

    public Ability ability = Ability.Axe;
    [Tooltip("Takes the ability away at the start so this pickup is how the player gets it. Only when this scene is the one the game started in.")]
    public bool lockUntilPickedUp = true;
    [Tooltip("Switch straight to the new ability when it's picked up.")]
    public bool equipOnPickup = true;
    public float pickupRadius = 1.6f;

    [Header("Float")]
    public float bobHeight = 0.25f;
    public float bobSpeed = 1.8f;
    public float spinSpeed = 70f;

    [Header("Look")]
    public Color glowColor = new Color(1f, 0.62f, 0.2f, 1f);
    public float lightRange = 7f;
    public float lightIntensity = 3f;
    public float ambientInterval = 0.3f;
    [Tooltip("Axe only: show the game's real axe model (the one you throw) instead of the blocky stand-in.")]
    public bool useRealAxeModel = true;
    [Tooltip("How long the real axe model is, end to end, in metres.")]
    public float realAxeLength = 1.1f;

    [Header("Tooltip")]
    public float tooltipSeconds = 10f;
    [Tooltip("Extra line at the bottom of the card about what to do with it right here, e.g. 'Smash the wall ahead'. Empty for none.")]
    [TextArea] public string levelHint = "";

    [Header("Pickup Feedback")]
    public float pickupShake = 0.4f;
    public float impactFreeze = 0.06f;
    public float impactOverlay = 0.18f;

    WeaponSlots.Slot Slot => ability == Ability.Axe ? WeaponSlots.Slot.Axe : WeaponSlots.Slot.Grapple;

    Transform model;
    Light glow;
    Vector3 startPos;
    float ambientTimer;
    bool collected;

    void Reset()
    {
        SphereCollider sphere = GetComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = pickupRadius;
    }

    void Awake()
    {
        SphereCollider sphere = GetComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.radius = pickupRadius;
    }

    // Start rather than Awake: WeaponSlots (and the player it sits on) has to be awake and
    // carried over by PortalManager before this can take anything off it.
    void Start()
    {
        startPos = transform.position;
        BuildLook();

        WeaponSlots slots = FindSlots();
        if (slots == null)
        {
            return;
        }

        bool startScene = gameObject.scene == SceneManager.GetActiveScene();
        if (lockUntilPickedUp && startScene)
        {
            slots.SetOwned(Slot, false);
        }
        else if (slots.Owns(Slot))
        {
            // came in through a portal with it already
            collected = true;
            gameObject.SetActive(false);
        }
    }

    static WeaponSlots FindSlots()
    {
        FirstPersonCharacterController player = PortalManager.FindPlayer();
        if (player == null)
        {
            return null;
        }
        WeaponSlots slots = player.GetComponentInParent<WeaponSlots>();
        return slots != null ? slots : player.transform.root.GetComponentInChildren<WeaponSlots>(true);
    }

    void Update()
    {
        if (collected)
        {
            return;
        }

        float y = Mathf.Sin(Time.time * bobSpeed) * bobHeight;
        transform.position = startPos + Vector3.up * y;
        if (model != null)
        {
            model.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);
        }
        if (glow != null)
        {
            glow.intensity = lightIntensity * (0.8f + 0.2f * Mathf.Sin(Time.time * 3f));
        }

        ambientTimer -= Time.deltaTime;
        if (ambientTimer <= 0f)
        {
            ambientTimer = ambientInterval;
            JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
            if (fx != null)
            {
                Vector3 side = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward * 0.6f;
                fx.AirPuff(transform.position + side, Vector3.up, 0.2f);
            }
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (collected || other.attachedRigidbody == null)
        {
            return;
        }
        FirstPersonCharacterController player = other.attachedRigidbody.GetComponent<FirstPersonCharacterController>();
        if (player == null)
        {
            return;
        }

        WeaponSlots slots = player.GetComponentInParent<WeaponSlots>();
        if (slots == null)
        {
            slots = player.transform.root.GetComponentInChildren<WeaponSlots>(true);
        }
        if (slots == null)
        {
            return;
        }

        collected = true;
        slots.SetOwned(Slot, true, equipOnPickup);
        Feedback(other.ClosestPoint(transform.position));
        AbilityTooltip.Get().Show(Title(), Lines(), glowColor, tooltipSeconds);
        gameObject.SetActive(false);
    }

    void Feedback(Vector3 point)
    {
        JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(transform.position, Vector3.up, 1f);
        }
        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTrauma(pickupShake);
        }
        ImpactFrames frames = ImpactFrames.Get();
        frames.SetImpactPoint(point);
        frames.Freeze(impactFreeze, impactOverlay, 1f);
        Synth.Play(GameSounds.AbilityGet);
    }

    // ---------------------------------------------------------------- card text

    string Title() => ability == Ability.Axe ? "BATTLE AXE" : "GRAPPLE";

    // Built when picked up, from whatever the player has the actions bound to right now.
    string[] Lines()
    {
        PlayerInputRouter input = PlayerInputRouter.Instance;
        string primary = Key(input != null ? input.primary : null, "LMB");
        string secondary = Key(input != null ? input.secondary : null, "RMB");
        string zip = Key(input != null ? input.zip : null, "RMB");
        string recall = Key(input != null ? input.axePickup : null, "G");
        string jump = Key(input != null ? input.jump : null, "Space");
        string slot2 = Key(input != null ? input.slot2 : null, "2");

        string[] lines = ability == Ability.Axe
            ? new[]
            {
                primary + "  Swing. Swing at the floor while falling to bounce back up.",
                "Hold " + primary + "  to charge a throw.  " + secondary + "  throws it straight away.",
                recall + "  Call the axe back. Walk over it to pick it up.",
                "A swing smashes walls you can break."
            }
            : new[]
            {
                zip + "  Zip to the point in the reticle. Works whatever you're holding.",
                slot2 + "  Grapple slot: hold  " + primary + "  to swing, scroll to reel in and out.",
                jump + "  while swinging jumps off with a boost.",
                "A thrown axe stuck in a wall is a grapple point too."
            };

        if (string.IsNullOrEmpty(levelHint))
        {
            return lines;
        }
        string[] withHint = new string[lines.Length + 1];
        lines.CopyTo(withHint, 0);
        withHint[lines.Length] = "<b>" + levelHint + "</b>";
        return withHint;
    }

    static string Key(GameAction action, string fallback)
    {
        string label = action != null ? action.Label : "";
        if (string.IsNullOrEmpty(label) || label == "-")
        {
            label = fallback;
        }
        return "<b>[" + label + "]</b>";
    }

    // ---------------------------------------------------------------- stand-in model

    void BuildLook()
    {
        if (GetComponentInChildren<Renderer>() != null)
        {
            model = transform.childCount > 0 ? transform.GetChild(0) : null;
        }
        else if (ability == Ability.Axe)
        {
            model = useRealAxeModel ? CopyRealAxe() : null;
            if (model == null)
            {
                model = BuildAxe();
            }
        }
        else
        {
            model = BuildGrapple();
        }

        GameObject lightGo = new GameObject("Glow");
        lightGo.transform.SetParent(transform, false);
        glow = lightGo.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = glowColor;
        glow.range = lightRange;
        glow.intensity = lightIntensity;
        glow.shadows = LightShadows.None;
    }

    // Copies just the meshes off the player's thrown axe prefab (or the held model if there
    // isn't one), so nothing on it - the ThrownAxe script, rigidbody, colliders - comes along.
    // Stood upright with the head on top and sized to realAxeLength, whatever the model's own
    // scale and facing. Null if there's no axe to copy.
    Transform CopyRealAxe()
    {
        FirstPersonCharacterController player = PortalManager.FindPlayer();
        BattleAxe held = player != null ? player.transform.root.GetComponentInChildren<BattleAxe>(true) : null;
        if (held == null)
        {
            return null;
        }
        Transform source = held.thrownAxePrefab != null ? held.thrownAxePrefab.transform : held.axeVisual;
        if (source == null)
        {
            return null;
        }

        Transform root = NewPivot("Axe Model");
        root.localScale = Vector3.one;
        Transform inner = new GameObject("Mesh").transform;
        inner.SetParent(root, false);

        Matrix4x4 toSource = source.worldToLocalMatrix;
        Bounds bounds = default;
        bool any = false;

        foreach (MeshFilter filter in source.GetComponentsInChildren<MeshFilter>(true))
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (filter.sharedMesh == null || renderer == null)
            {
                continue;
            }

            Matrix4x4 m = toSource * filter.transform.localToWorldMatrix;
            GameObject part = new GameObject(filter.name);
            part.transform.SetParent(inner, false);
            part.transform.localPosition = m.GetColumn(3);
            part.transform.localRotation = m.rotation;
            part.transform.localScale = m.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;

            Bounds b = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = m.MultiplyPoint3x4(new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z));
                if (!any) { bounds = new Bounds(corner, Vector3.zero); any = true; }
                else
                {
                    bounds.Encapsulate(corner);
                }
            }
        }

        if (!any)
        {
            Destroy(root.gameObject);
            return null;
        }

        // The handle runs along the model's longest side, and the head is the end furthest
        // from the pivot (same guess ThrownAxe makes). Point that end up.
        Vector3 size = bounds.size;
        Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right
            : size.y >= size.z ? Vector3.up
            : Vector3.forward;
        float far = Vector3.Dot(bounds.max, axis);
        float near = Vector3.Dot(bounds.min, axis);
        Vector3 headDir = Mathf.Abs(far) >= Mathf.Abs(near) ? axis : -axis;

        float longest = Mathf.Max(size.x, size.y, size.z);
        float scale = longest > 0.0001f ? realAxeLength / longest : 1f;

        Quaternion upright = Quaternion.FromToRotation(headDir, Vector3.up);
        inner.localRotation = upright;
        inner.localScale = Vector3.one * scale;
        inner.localPosition = -(upright * bounds.center) * scale;

        root.localRotation = Quaternion.Euler(0f, 0f, 20f);
        return root;
    }

    Transform BuildAxe()
    {
        Transform root = NewPivot("Axe Model");
        root.localRotation = Quaternion.Euler(0f, 0f, 20f);
        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0f), new Vector3(0.07f, 0.55f, 0.07f), Vector3.zero, new Color(0.38f, 0.24f, 0.13f));
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.42f, 0.17f), new Vector3(0.08f, 0.34f, 0.4f), Vector3.zero, new Color(0.72f, 0.74f, 0.78f));
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.42f, -0.06f), new Vector3(0.09f, 0.14f, 0.12f), Vector3.zero, new Color(0.3f, 0.3f, 0.33f));
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.42f, 0.38f), new Vector3(0.085f, 0.38f, 0.04f), Vector3.zero, glowColor, true);
        return root;
    }

    Transform BuildGrapple()
    {
        Transform root = NewPivot("Grapple Model");
        Color body = new Color(0.16f, 0.17f, 0.2f);
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.22f, 0.22f, 0.55f), Vector3.zero, body);
        Part(root, PrimitiveType.Cube, new Vector3(0f, -0.2f, -0.12f), new Vector3(0.12f, 0.26f, 0.12f), new Vector3(15f, 0f, 0f), body);
        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.03f, 0.38f), new Vector3(0.09f, 0.12f, 0.09f), new Vector3(90f, 0f, 0f), new Color(0.5f, 0.52f, 0.56f));
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.08f, 0.56f), new Vector3(0.03f, 0.14f, 0.03f), new Vector3(-35f, 0f, 0f), new Color(0.75f, 0.77f, 0.8f));
        Part(root, PrimitiveType.Cube, new Vector3(0.06f, -0.02f, 0.56f), new Vector3(0.03f, 0.14f, 0.03f), new Vector3(30f, 0f, 40f), new Color(0.75f, 0.77f, 0.8f));
        Part(root, PrimitiveType.Cube, new Vector3(-0.06f, -0.02f, 0.56f), new Vector3(0.03f, 0.14f, 0.03f), new Vector3(30f, 0f, -40f), new Color(0.75f, 0.77f, 0.8f));
        Part(root, PrimitiveType.Cube, new Vector3(0f, 0.115f, 0f), new Vector3(0.23f, 0.02f, 0.4f), Vector3.zero, glowColor, true);
        return root;
    }

    Transform NewPivot(string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * 1.6f;
        return go.transform;
    }

    static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Color color, bool emissive = false)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            return;
        }
        Material mat = new Material(shader) { color = color };
        if (emissive)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * 2.5f);
        }
        go.GetComponent<Renderer>().material = mat;
    }
}
