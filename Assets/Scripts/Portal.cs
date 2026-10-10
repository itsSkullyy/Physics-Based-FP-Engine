using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// A seamless, cross-scene portal (walk/run/dart through it at full speed, no loading
// screen - the linked scene streams in ahead of time). Put this on the Portal prefab's
// root. Two portal instances are linked by name: set linkedSceneName + linkedPortalId to
// point at the OTHER portal's scene and portalId.
//
// SETUP:
//  - This portal's local +Z axis must point outward from the wall/surface, in the
//    direction a player is moving when they exit through it. Both linked portals must be
//    upright (no tilt) - portals only rotate the player around the world Y axis.
//  - portalId must be unique within this portal's own scene and must match the linked
//    portal's linkedPortalId (and vice versa).
//  - linkedSceneName must be added to Build Settings, or it can't be streamed in at
//    runtime.
//  - If the destination looks visually clipped/wrong right at the seam, try toggling
//    flipClipPlane.
//  - Tick startClosed for a portal that should only appear later (the end-of-level exit
//    and where it brings you out). It stays invisible and solid-air until Open() is
//    called, then tears open. Opening one side opens its partner too.
//  - Enemies path through open portals: each one adds a NavMesh link from the floor in
//    front of it to the floor in front of its partner, and EnemyAgent hops them across
//    when they step on it. The bottom of the opening needs to sit on NavMesh.
[RequireComponent(typeof(BoxCollider))]
public class Portal : MonoBehaviour
{
    [Header("Identity")]
    [Tooltip("Unique within this portal's own scene.")]
    public string portalId = "PortalA";
    [Tooltip("Scene the linked portal lives in. Must be in Build Settings.")]
    public string linkedSceneName;
    [Tooltip("portalId of the portal on the other side, inside linkedSceneName.")]
    public string linkedPortalId = "PortalB";

    [Header("Shape")]
    public Vector2 portalSize = new Vector2(2f, 3f);
    [Tooltip("Depth of the crossing trigger volume, centered on the surface.")]
    public float triggerDepth = 1.5f;

    [Header("Frame")]
    [Tooltip("Width of the recessed lip around the opening, sticking outward from portalSize. Gives the portal real edge thickness so it reads as a physical opening rather than a flat card at a grazing angle - the single biggest cue a plain flat quad is missing.")]
    public float frameWidth = 0.12f;
    [Tooltip("Depth of the lip along the portal's own forward axis, split evenly in front of and behind the surface plane so the cue reads from either approach direction.")]
    public float frameDepth = 0.18f;
    public Color frameColor = new Color(0.05f, 0.05f, 0.05f);

    [Header("References")]
    public Transform surfaceTransform;
    public MeshRenderer surfaceRenderer;
    public Camera portalCamera;
    public Transform frameTop;
    public Transform frameBottom;
    public Transform frameLeft;
    public Transform frameRight;

    [Header("Rendering")]
    [Range(0.25f, 1f)] public float resolutionScale = 1f;
    [Tooltip("Pulls the portal camera's near plane onto the linked portal's surface so the destination room's near-side floor/walls can't bleed into the opening. If the view through a portal ever goes blank, this plane's facing is inverted for that orientation - tick flipClipPlane.")]
    public bool useObliqueClip = true;
    public bool flipClipPlane = false;
    public float clipPlaneOffset = 0.05f;

    [Header("Teleport")]
    [Tooltip("Extra push along the exit direction so the player doesn't land exactly on the surface plane.")]
    public float exitPush = 0.05f;
    public float teleportCooldown = 0.15f;

    [Header("Opening")]
    [Tooltip("Starts sealed: invisible, can't be crossed and enemies can't path through it until Open() is called. Use it for the end-of-level exit and the spot it brings you out at.")]
    public bool startClosed;
    public float openDuration = 1.8f;
    [Tooltip("Colour of the light and frame glow while it tears open. Settles down to a soft glow once it's open.")]
    public Color glowColor = new Color(0.45f, 0.85f, 1f, 1f);
    public float glowIntensity = 6f;
    [Range(0f, 1f)] public float restingGlow = 0.2f;
    public float openShake = 0.5f;
    public float openShakeRange = 45f;
    [Tooltip("Where a closed portal appears when it opens.\n" +
             "Where Placed: right where it sits in the scene.\n" +
             "In Front Of Player: floating ahead of the player, facing them, at the moment it opens.\n" +
             "At Scene Start: just ahead of where the player starts in this scene, facing the same way, so you come out inside the start room.")]
    public OpenPlacement openPlacement = OpenPlacement.WherePlaced;
    [Tooltip("In Front Of Player: how far ahead. Pulled in if a wall is closer.")]
    public float placeDistance = 9f;
    [Tooltip("In Front Of Player: how far the bottom of the opening floats above the player's feet.")]
    public float floatHeight = 0.4f;
    [Tooltip("At Scene Start: how far ahead of the start point.")]
    public float spawnDistance = 5f;

    [Header("Closing")]
    [Tooltip("Closes itself (and its partner) a moment after the player goes through. Only for portals that started closed.")]
    public bool closeBehindPlayer = true;
    public float closeDelay = 0.4f;
    public float closeDuration = 1f;

    [Header("Enemies")]
    [Tooltip("Lets NavMesh agents path through. Needs NavMesh under the bottom edge of the opening on both sides.")]
    public bool enemiesCanPass = true;
    [Tooltip("How far in front of the opening enemies step on and off.")]
    public float enemyLinkInset = 1.2f;

    /// Fired after the player goes through: (portal they went in, portal they came out of).
    public static event System.Action<Portal, Portal> PlayerTravelled;

    // Read by PortalCompositeFeature, which composites every ready portal's render
    // texture directly into whichever camera it's rendering for - stencil-clipped to
    // the surface silhouette - instead of this portal showing it via a textured quad in
    // the normal render queue. PortalCameras lets that feature skip re-compositing
    // portals visible from within another portal's own camera (no recursive portals).
    public static readonly List<Portal> ActivePortals = new List<Portal>();
    public static readonly HashSet<Camera> PortalCameras = new HashSet<Camera>();

    public bool IsRenderReady => linkedPortal != null && renderTexture != null && IsVisible;
    public RenderTexture PortalRenderTexture => renderTexture;
    public Matrix4x4 SurfaceMatrix => surfaceTransform != null ? surfaceTransform.localToWorldMatrix : Matrix4x4.identity;
    public Mesh SurfaceMesh =>
        surfaceTransform != null && surfaceTransform.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;

    public bool IsLinked => linkedPortal != null;
    public Portal LinkedPortal => linkedPortal;

    public bool IsOpen => openState == OpenState.Open;
    public bool IsOpening => openState == OpenState.Opening;
    public bool IsClosing => openState == OpenState.Closing;
    public bool IsVisible => openState != OpenState.Closed;

    /// Linked and open far enough to go through. During the opening animation that's once
    /// the tear is wide enough to fit a person; once it starts closing it's shut.
    public bool Crossable => linkedPortal != null
        && (openState == OpenState.Open || (openState == OpenState.Opening && OpenProgress >= CrossableAt));

    /// 0 closed, 1 fully open (opening only).
    public float OpenProgress => openState == OpenState.Open ? 1f
        : openState == OpenState.Opening ? Mathf.Clamp01(openTimer / Mathf.Max(0.01f, openDuration))
        : 0f;

    /// Size of the opening right now, smaller than portalSize while it opens or closes.
    public Vector2 CurrentSize => Vector2.Scale(portalSize, shapeFactor);

    /// Maps a pose on this portal's side into the linked portal's space. Apply it to
    /// anything that travels through - a body's position/rotation/velocity, or a world
    /// point something still on this side is anchored to.
    public Matrix4x4 TeleportMatrix => linkedPortal != null ? PortalMatrix() : Matrix4x4.identity;

    /// Does the segment from->to pass through this portal's opening, entering from the
    /// approach (local +Z) side? t is how far along the segment the crossing happens.
    public bool TryGetSegmentCrossing(Vector3 from, Vector3 to, out float t)
    {
        t = 0f;
        if (!Crossable) return false;

        Vector3 localFrom = transform.InverseTransformPoint(from);
        Vector3 localTo = transform.InverseTransformPoint(to);

        // Same front-to-back convention the player's own crossing test uses, so a thing
        // that just came OUT of a portal moving away can never immediately re-enter it.
        if (localFrom.z <= 0f || localTo.z > 0f) return false;

        float span = localFrom.z - localTo.z;
        if (span <= Mathf.Epsilon) return false;

        t = localFrom.z / span;
        Vector3 crossing = Vector3.Lerp(localFrom, localTo, t);
        Vector2 size = CurrentSize;
        return Mathf.Abs(crossing.x) <= size.x * 0.5f
            && Mathf.Abs(crossing.y) <= size.y * 0.5f;
    }

    /// First portal the segment passes through, if any, with the transform to apply on
    /// the far side. Used by things that move themselves rather than relying on trigger
    /// callbacks - a thrown axe flies with its colliders disabled, and an aim ray has no
    /// collider at all.
    public static Portal FindSegmentCrossing(Vector3 from, Vector3 to, out Matrix4x4 matrix) =>
        FindSegmentCrossing(from, to, PortalManager.PlayerSpace, out matrix);

    /// Same, but only portals belonging to the scene named `space`. Every streamed-in
    /// scene shares the one world, so a portal in another loaded scene can sit right where
    /// this thing is flying without really being there. Empty space means any portal.
    public static Portal FindSegmentCrossing(Vector3 from, Vector3 to, string space, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.identity;

        Portal nearest = null;
        float nearestT = float.MaxValue;

        for (int i = 0; i < ActivePortals.Count; i++)
        {
            Portal portal = ActivePortals[i];
            if (portal == null) continue;
            if (!string.IsNullOrEmpty(space) && portal.gameObject.scene.name != space) continue;
            if (!portal.TryGetSegmentCrossing(from, to, out float t)) continue;
            if (t >= nearestT) continue;

            nearestT = t;
            nearest = portal;
        }

        if (nearest == null) return null;

        matrix = nearest.TeleportMatrix;
        return nearest;
    }

    Portal linkedPortal;
    RenderTexture renderTexture;
    Material frameMaterial;

    Rigidbody trackedBody;
    FirstPersonCharacterController trackedController;
    float trackedPrevLocalZ;
    float trackedPrevCameraLocalZ;
    bool cameraTracked;
    float teleportLockout;

    BoxCollider triggerCollider;

    const float SurfaceFlatThickness = 0.01f;
    const float CrossingMargin = 0.15f;
    const float ObliqueMinFarCornerDistance = 1f;

    public enum OpenPlacement { WherePlaced, InFrontOfPlayer, AtSceneStart }

    enum OpenState { Closed, Opening, Open, Closing }
    OpenState openState = OpenState.Open;
    Vector2 shapeFactor = Vector2.one;
    float openTimer;
    float closeTimer;
    bool closeQueued;
    float closeAt;
    bool openPartnerWhenLinked;
    bool tore;
    float sparkTimer;
    Renderer[] frameRenderers;
    Light glowLight;

    // The opening is a vertical slit of light first, then it tears sideways into the full
    // doorway with a bit of overshoot.
    const float SlitPhase = 0.28f;
    const float SlitWidth = 0.025f;
    const float CrossableAt = 0.6f;

    void Awake()
    {
        triggerCollider = GetComponent<BoxCollider>();
        frameRenderers = CollectFrameRenderers();
        if (startClosed)
        {
            openState = OpenState.Closed;
            shapeFactor = Vector2.zero;
        }
        ApplyPortalSize();

        // The surface mesh is no longer drawn through Unity's normal per-object
        // rendering - PortalCompositeFeature draws it manually (twice: mask, then
        // composite) from a custom render pass instead. Disabling the renderer here
        // stops it from ALSO being drawn a third time, untextured, by the regular
        // opaque queue.
        if (surfaceRenderer != null)
            surfaceRenderer.enabled = false;

        Shader frameShader = Shader.Find("Universal Render Pipeline/Lit");
        if (frameShader == null) frameShader = Shader.Find("Standard");
        if (frameShader != null)
        {
            frameMaterial = new Material(frameShader) { hideFlags = HideFlags.HideAndDontSave };
            frameMaterial.color = frameColor;
            ApplyFrameMaterial();
        }

        if (portalCamera != null)
            portalCamera.enabled = false;

        if (startClosed)
            BuildGlowLight();
        ApplyVisibility();
    }

    Renderer[] CollectFrameRenderers()
    {
        List<Renderer> list = new List<Renderer>();
        foreach (Transform t in new[] { frameTop, frameBottom, frameLeft, frameRight })
        {
            if (t != null && t.TryGetComponent(out Renderer r)) list.Add(r);
        }
        return list.ToArray();
    }

    void ApplyFrameMaterial()
    {
        if (frameMaterial == null) return;
        if (frameTop != null && frameTop.TryGetComponent(out MeshRenderer top)) top.material = frameMaterial;
        if (frameBottom != null && frameBottom.TryGetComponent(out MeshRenderer bottom)) bottom.material = frameMaterial;
        if (frameLeft != null && frameLeft.TryGetComponent(out MeshRenderer left)) left.material = frameMaterial;
        if (frameRight != null && frameRight.TryGetComponent(out MeshRenderer right)) right.material = frameMaterial;
    }

    void OnEnable()
    {
        PortalManager.Get().RegisterPortal(this);
        PortalManager.Get().RequestSceneLoad(linkedSceneName);

        ActivePortals.Add(this);
        if (portalCamera != null) PortalCameras.Add(portalCamera);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        PlayerTravelled += OnAnyPlayerTravel;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        PlayerTravelled -= OnAnyPlayerTravel;

        ActivePortals.Remove(this);
        if (portalCamera != null) PortalCameras.Remove(portalCamera);

        if (PortalManager.Instance != null)
        {
            PortalManager.Instance.UnregisterPortal(this);
            PortalManager.Instance.ReleaseSceneLoad(linkedSceneName);
        }

        RemoveNavLinks();
        linkedPortal = null;
        if (portalCamera != null) portalCamera.enabled = false;
        ReleaseRenderTexture();
    }

    void OnDestroy()
    {
        if (frameMaterial != null) Destroy(frameMaterial);
        ReleaseRenderTexture();
    }

    void OnValidate()
    {
        if (!Application.isPlaying)
            ApplyPortalSize();
    }

    void ApplyPortalSize()
    {
        // In the editor it always shows full size, so a closed portal can still be placed.
        ApplyShape(Application.isPlaying ? CurrentSize : portalSize);

        BoxCollider box = triggerCollider != null ? triggerCollider : GetComponent<BoxCollider>();
        if (box != null)
        {
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(portalSize.x, portalSize.y, Mathf.Max(0.05f, triggerDepth));
        }
    }

    void ApplyShape(Vector2 size)
    {
        if (surfaceTransform != null)
        {
            // A box, not a quad: ProtectSurfaceFromClipping() needs real depth to grow
            // into while the player is mid-crossing. Its facing doesn't matter (the
            // composite shader samples by screen position, not by the mesh's own UVs), so
            // no orientation fixup is needed here.
            surfaceTransform.localRotation = Quaternion.identity;
            surfaceTransform.localScale = new Vector3(size.x, size.y, SurfaceFlatThickness);
            surfaceTransform.localPosition = Vector3.zero;
        }

        // A thin recessed lip around the opening, centered on the surface plane and
        // sticking out on both the +Z and -Z sides, so a grazing-angle view from either
        // approach direction still shows real edge thickness instead of a knife-edge
        // silhouette - the parallax cue a flat quad has none of.
        float bw = Mathf.Max(0.01f, frameWidth);
        float bd = Mathf.Max(0.01f, frameDepth);
        if (frameTop != null)
        {
            frameTop.localPosition = new Vector3(0f, size.y * 0.5f + bw * 0.5f, 0f);
            frameTop.localScale = new Vector3(size.x + bw * 2f, bw, bd);
        }
        if (frameBottom != null)
        {
            frameBottom.localPosition = new Vector3(0f, -(size.y * 0.5f + bw * 0.5f), 0f);
            frameBottom.localScale = new Vector3(size.x + bw * 2f, bw, bd);
        }
        if (frameLeft != null)
        {
            frameLeft.localPosition = new Vector3(-(size.x * 0.5f + bw * 0.5f), 0f, 0f);
            frameLeft.localScale = new Vector3(bw, size.y, bd);
        }
        if (frameRight != null)
        {
            frameRight.localPosition = new Vector3(size.x * 0.5f + bw * 0.5f, 0f, 0f);
            frameRight.localScale = new Vector3(bw, size.y, bd);
        }
    }

    void Update()
    {
        if (linkedPortal == null)
            TryResolveLink();

        if (closeQueued && Time.time >= closeAt)
        {
            closeQueued = false;
            Close();
        }

        if (openState == OpenState.Opening)
            AnimateOpening(Time.deltaTime);
        else if (openState == OpenState.Closing)
            AnimateClosing(Time.deltaTime);
        else if (openState == OpenState.Open && startClosed)
            IdleGlow(Time.deltaTime);

        if (portalCamera != null)
            portalCamera.enabled = linkedPortal != null && IsVisible;

        UpdateNavLinks();
    }

    // ---------------------------------------------------------------- opening

    /// Tears the portal open (and its partner on the other side). Safe to call more than once.
    public void Open()
    {
        if (openState != OpenState.Closed) return;

        PlaceForOpening();
        openState = OpenState.Opening;
        openTimer = 0f;
        tore = false;
        sparkTimer = 0f;
        closeQueued = false;
        shapeFactor = Vector2.zero;
        ApplyVisibility();

        if (linkedPortal != null) linkedPortal.Open();
        else openPartnerWhenLinked = true;

        Synth.PlayAt(GameSounds.PortalOpen, transform.position, 1f, 1f, openShakeRange * 2f);
        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.AirPuff(transform.position, transform.forward, 0.6f);
    }

    void PlaceForOpening()
    {
        if (openPlacement == OpenPlacement.InFrontOfPlayer) PlaceInFrontOfPlayer();
        else if (openPlacement == OpenPlacement.AtSceneStart) PlaceAtSceneStart();
    }

    // Floating ahead of wherever the player is looking, facing them, so they can carry
    // straight on into it.
    void PlaceInFrontOfPlayer()
    {
        FirstPersonCharacterController player = PortalManager.FindPlayer();
        Camera cam = Camera.main;
        if (player == null || cam == null) return;

        Vector3 eye = cam.transform.position;
        Vector3 ahead = player.FlatForward;

        float distance = placeDistance;
        if (FirstSolidHit(eye, ahead, placeDistance + 1f, out RaycastHit wall))
            distance = Mathf.Max(3f, wall.distance - 1.5f);

        Collider body = player.GetComponent<Collider>();
        float feet = body != null ? body.bounds.min.y : player.transform.position.y - 1f;

        Vector3 centre = eye + ahead * distance;
        centre.y = feet + floatHeight + portalSize.y * 0.5f;
        transform.SetPositionAndRotation(centre, Quaternion.LookRotation(-ahead, Vector3.up));
    }

    // Just ahead of where the player starts in this scene, facing the same way, standing on
    // the floor there. The start point is always inside the room, wherever the room is.
    void PlaceAtSceneStart()
    {
        if (!PortalManager.Get().TryGetSceneSpawn(gameObject.scene, out Pose spawn)) return;

        Vector3 ahead = spawn.forward;
        Vector3 chest = spawn.position + Vector3.up * 0.5f;

        float distance = spawnDistance;
        if (FirstSolidHit(chest, ahead, spawnDistance + 1f, out RaycastHit wall))
            distance = Mathf.Max(1.5f, wall.distance - 1.5f);

        Vector3 spot = spawn.position + ahead * distance;
        float bottom = FindFloor(spot + Vector3.up * 2f, 10f, out Vector3 floor)
            ? floor.y + 0.05f
            : spawn.position.y - 1f;

        Vector3 centre = new Vector3(spot.x, bottom + portalSize.y * 0.5f, spot.z);
        transform.SetPositionAndRotation(centre, Quaternion.LookRotation(ahead, Vector3.up));
    }

    // Level geometry only: skips triggers, portals, the player, enemies and loose props.
    static bool IsSolidLevel(Collider c) =>
        c.GetComponentInParent<Portal>() == null
        && (c.attachedRigidbody == null || c.attachedRigidbody.isKinematic)
        && c.GetComponentInParent<NavMeshAgent>() == null
        && c.GetComponentInParent<FirstPersonCharacterController>() == null
        && c.gameObject.scene.name != "DontDestroyOnLoad";

    static bool FirstSolidHit(Vector3 from, Vector3 dir, float range, out RaycastHit hit)
    {
        RaycastHit[] hits = Physics.RaycastAll(from, dir, range, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (RaycastHit h in hits)
        {
            if (!IsSolidLevel(h.collider)) continue;
            hit = h;
            return true;
        }
        hit = default;
        return false;
    }

    static bool FindFloor(Vector3 from, float range, out Vector3 point)
    {
        bool found = FirstSolidHit(from, Vector3.down, range, out RaycastHit hit);
        point = found ? hit.point : from;
        return found;
    }

    // x = width factor, y = height factor
    static Vector2 OpenShape(float p)
    {
        if (p >= 1f) return Vector2.one;
        if (p <= 0f) return Vector2.zero;

        float h = EaseOutCubic(Mathf.Clamp01(p / SlitPhase));
        float w = p < SlitPhase
            ? SlitWidth
            : Mathf.LerpUnclamped(SlitWidth, 1f, EaseOutBack((p - SlitPhase) / (1f - SlitPhase)));
        return new Vector2(w, h);
    }

    static float EaseOutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

    static float EaseOutBack(float t)
    {
        const float c1 = 1.4f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    static float EaseInBack(float t)
    {
        const float c1 = 1.4f;
        const float c3 = c1 + 1f;
        return c3 * t * t * t - c1 * t * t;
    }

    void AnimateOpening(float dt)
    {
        openTimer += dt;
        float p = OpenProgress;
        shapeFactor = OpenShape(p);
        Vector2 size = CurrentSize;
        ApplyShape(size);

        // glow builds while the slit forms, flashes on the tear, then settles
        float glow = p < SlitPhase
            ? Mathf.Lerp(0.3f, 1f, p / SlitPhase)
            : Mathf.Lerp(1.6f, restingGlow, EaseOutCubic((p - SlitPhase) / (1f - SlitPhase)));
        SetGlow(glow);

        if (!tore && p >= SlitPhase)
        {
            tore = true;
            Tear();
        }

        // sparks peel off the edge of the opening the whole time it's growing
        sparkTimer -= dt;
        if (sparkTimer <= 0f)
        {
            sparkTimer = 0.035f;
            EmitRimPuff(size, 0.45f);
        }

        if (p >= 1f)
        {
            openState = OpenState.Open;
            shapeFactor = Vector2.one;
            ApplyShape(portalSize);
            SetGlow(restingGlow);
        }
    }

    // ---------------------------------------------------------------- closing

    Vector2 closeFrom;

    /// Seals it again (and its partner): the opening played backwards, pinching in to a
    /// slit of light that then shrinks away to nothing.
    public void Close()
    {
        if (openState == OpenState.Closed || openState == OpenState.Closing) return;

        closeQueued = false;
        openState = OpenState.Closing;
        closeTimer = 0f;
        sparkTimer = 0f;
        closeFrom = shapeFactor;

        if (linkedPortal != null) linkedPortal.Close();

        Synth.PlayAt(GameSounds.PortalClose, transform.position, 1f, 1f, openShakeRange * 2f);
    }

    void OnAnyPlayerTravel(Portal entered, Portal exited)
    {
        if (!startClosed || !closeBehindPlayer) return;
        // a third portal pointing one-way at either end of the pair goes too, or it's left
        // hanging open somewhere with nobody to close it
        bool involved = entered == this || exited == this
            || (linkedPortal != null && (linkedPortal == entered || linkedPortal == exited));
        if (!involved) return;
        if (openState != OpenState.Open && openState != OpenState.Opening) return;

        // a beat after, so the player is clear of the opening before it shuts
        closeQueued = true;
        closeAt = Time.time + closeDelay;
    }

    const float PinchPhase = 0.6f;

    // width pinches in (bulging out a touch first), then the slit shrinks top and bottom
    Vector2 CloseShape(float p)
    {
        if (p < PinchPhase)
        {
            float w = Mathf.LerpUnclamped(closeFrom.x, SlitWidth, EaseInBack(p / PinchPhase));
            return new Vector2(Mathf.Max(0f, w), closeFrom.y);
        }

        float u = (p - PinchPhase) / (1f - PinchPhase);
        return new Vector2(SlitWidth * (1f - u * u), closeFrom.y * (1f - u * u * u));
    }

    void AnimateClosing(float dt)
    {
        closeTimer += dt;
        float p = Mathf.Clamp01(closeTimer / Mathf.Max(0.01f, closeDuration));
        shapeFactor = CloseShape(p);
        Vector2 size = CurrentSize;
        ApplyShape(size);

        // brightens as it pinches shut, then goes out with it
        float glow = p < PinchPhase
            ? Mathf.Lerp(restingGlow, 1.4f, p / PinchPhase)
            : Mathf.Lerp(1.4f, 0f, (p - PinchPhase) / (1f - PinchPhase));
        SetGlow(glow);

        sparkTimer -= dt;
        if (sparkTimer <= 0f)
        {
            sparkTimer = 0.05f;
            EmitRimPuff(size, 0.3f);
        }

        if (p >= 1f)
        {
            openState = OpenState.Closed;
            shapeFactor = Vector2.zero;
            SetGlow(0f);
            ApplyVisibility();

            JuiceFX fx = JuiceFX.Get();
            if (fx != null) fx.ImpactBurst(transform.position, transform.forward, 0.6f);
            if (CameraShaker.Instance != null)
                CameraShaker.Instance.AddTraumaAtPoint(transform.position, openShake * 0.5f, openShakeRange * 0.2f, openShakeRange * 0.6f);
        }
    }

    void Tear()
    {
        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(transform.position, transform.forward, 1.3f);
            fx.ImpactBurst(transform.position, -transform.forward, 0.8f);
        }
        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTraumaAtPoint(transform.position, openShake, openShakeRange * 0.3f, openShakeRange);
    }

    float idlePuffTimer;

    // a slow pulse and the odd wisp off the edge, so an open exit is easy to spot from far off
    void IdleGlow(float dt)
    {
        SetGlow(restingGlow * (1f + 0.35f * Mathf.Sin(Time.time * 2.2f)));

        idlePuffTimer -= dt;
        if (idlePuffTimer <= 0f)
        {
            idlePuffTimer = 0.4f;
            EmitRimPuff(portalSize, 0.22f);
        }
    }

    void EmitRimPuff(Vector2 size, float strength)
    {
        JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
        if (fx == null) return;

        // random point on the edge of the rectangle, pushed outward from the middle
        float halfW = size.x * 0.5f;
        float halfH = size.y * 0.5f;
        Vector3 local;
        float perimeterSpot = Random.value * 2f * (size.x + size.y);
        if (perimeterSpot < size.x) local = new Vector3(perimeterSpot - halfW, halfH, 0f);
        else if (perimeterSpot < size.x * 2f) local = new Vector3(perimeterSpot - size.x - halfW, -halfH, 0f);
        else if (perimeterSpot < size.x * 2f + size.y) local = new Vector3(-halfW, perimeterSpot - size.x * 2f - halfH, 0f);
        else local = new Vector3(halfW, perimeterSpot - size.x * 2f - size.y - halfH, 0f);

        Vector3 point = transform.TransformPoint(local);
        Vector3 outward = transform.TransformDirection(new Vector3(local.x, local.y, 0f)).normalized;
        Vector3 dir = (outward + transform.forward * Random.Range(-0.6f, 0.6f)).normalized;
        fx.AirPuff(point, dir, strength);
    }

    void BuildGlowLight()
    {
        GameObject go = new GameObject("OpenGlow");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.forward * 0.6f;
        glowLight = go.AddComponent<Light>();
        glowLight.type = LightType.Point;
        glowLight.color = glowColor;
        glowLight.range = Mathf.Max(portalSize.x, portalSize.y) * 1.6f;
        glowLight.shadows = LightShadows.None;
        glowLight.intensity = 0f;
        glowLight.enabled = false;
    }

    void SetGlow(float amount)
    {
        if (glowLight != null)
        {
            glowLight.enabled = amount > 0.001f;
            glowLight.intensity = glowIntensity * amount;
        }

        if (frameMaterial != null && startClosed)
        {
            frameMaterial.EnableKeyword("_EMISSION");
            frameMaterial.SetColor("_EmissionColor", glowColor * (glowIntensity * 0.5f * amount));
            frameMaterial.color = Color.Lerp(frameColor, glowColor, Mathf.Clamp01(amount));
        }
    }

    // Closed: nothing drawn, nothing to touch. The editor still shows the gizmo.
    void ApplyVisibility()
    {
        bool visible = IsVisible;
        foreach (Renderer r in frameRenderers)
        {
            if (r != null) r.enabled = visible;
        }
        if (triggerCollider != null) triggerCollider.enabled = visible;
        if (!visible) ApplyShape(Vector2.zero);
    }

    // ---------------------------------------------------------------- enemies

    readonly List<NavMeshLinkInstance> navLinks = new List<NavMeshLinkInstance>();
    float nextLinkTry;
    bool warnedNoNavMesh;
    Vector3 linkStart;
    Vector3 linkEnd;

    // One-way link from the floor in front of this portal to the floor in front of the
    // partner (the partner adds the way back). A zero-width point link on purpose: a wide
    // link's edges sit across the link direction, which here runs between two scenes and
    // has nothing to do with the doorway's own width. Agents never actually walk it, see
    // EnemyAgent - they get carried across the moment they step on.
    void UpdateNavLinks()
    {
        if (!enemiesCanPass || !Crossable)
        {
            RemoveNavLinks();
            return;
        }
        if (navLinks.Count > 0 || Time.time < nextLinkTry) return;
        nextLinkTry = Time.time + 1f;

        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            int agentType = NavMesh.GetSettingsByIndex(i).agentTypeID;
            if (!TryGetFloorInFront(agentType, out Vector3 start) ||
                !linkedPortal.TryGetFloorInFront(agentType, out Vector3 end))
            {
                continue;
            }

            NavMeshLinkData data = new NavMeshLinkData
            {
                startPosition = start,
                endPosition = end,
                width = 0f,
                costModifier = -1f,
                bidirectional = false,
                area = 0,
                agentTypeID = agentType
            };
            NavMeshLinkInstance link = NavMesh.AddLink(data);
            if (!NavMesh.IsLinkValid(link)) continue;

            NavMesh.SetLinkOwner(link, this);
            navLinks.Add(link);
            if (navLinks.Count == 1)
            {
                linkStart = start;
                linkEnd = end;
            }
        }

        // The other scene's runtime NavMesh can take a moment, so this keeps retrying - but
        // only says so once.
        if (navLinks.Count == 0 && !warnedNoNavMesh && Time.timeSinceLevelLoad > 5f)
        {
            warnedNoNavMesh = true;
            Debug.LogWarning($"[Portal] '{portalId}': no NavMesh found under the bottom of the opening on one side, so enemies can't follow through it yet.", this);
        }
    }

    void RemoveNavLinks()
    {
        foreach (NavMeshLinkInstance link in navLinks)
            NavMesh.RemoveLink(link);
        navLinks.Clear();
    }

    // Floor in front of the opening. A floating portal's bottom edge can sit well off the
    // ground, so this looks down from it rather than assuming it's standing on the floor.
    Vector3 FloorProbe
    {
        get
        {
            Vector3 bottom = transform.position + transform.forward * enemyLinkInset
                             - Vector3.up * (portalSize.y * 0.5f - 0.6f);
            return FindFloor(bottom, 8f, out Vector3 floor) ? floor : bottom;
        }
    }

    public bool TryGetFloorInFront(int agentTypeID, out Vector3 point)
    {
        NavMeshQueryFilter filter = new NavMeshQueryFilter { agentTypeID = agentTypeID, areaMask = NavMesh.AllAreas };
        bool found = NavMesh.SamplePosition(FloorProbe, out NavMeshHit hit, 2.5f, filter);
        point = found ? hit.position : FloorProbe;
        return found;
    }

    public bool HasNavMeshInFront() => NavMesh.SamplePosition(FloorProbe, out _, 2.5f, NavMesh.AllAreas);

    /// Moves an agent standing on this portal's link out the other side, keeping where it
    /// was across the doorway and which way it was heading. Returns false if there's
    /// nowhere to put it.
    public bool CarryAgent(NavMeshAgent agent)
    {
        if (!Crossable || agent == null) return false;

        Transform body = agent.transform;
        Matrix4x4 portalMatrix = PortalMatrix();

        // same spot across the doorway, just stepped out the far side
        Vector3 local = transform.InverseTransformPoint(body.position);
        float halfW = Mathf.Max(0f, portalSize.x * 0.5f - agent.radius - 0.2f);
        Vector3 exitLocal = new Vector3(Mathf.Clamp(-local.x, -halfW, halfW), local.y, linkedPortal.enemyLinkInset);
        Vector3 exit = linkedPortal.transform.TransformPoint(exitLocal);

        NavMeshQueryFilter filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = NavMesh.AllAreas };
        if (!NavMesh.SamplePosition(exit, out NavMeshHit hit, 3f, filter))
        {
            if (!linkedPortal.TryGetFloorInFront(agent.agentTypeID, out Vector3 fallback)) return false;
            hit.position = fallback;
        }

        Vector3 velocity = portalMatrix.rotation * agent.velocity;
        Vector3 facing = portalMatrix.rotation * body.forward;
        facing.y = 0f;

        Vector3 from = body.position;
        if (agent.isOnOffMeshLink) agent.CompleteOffMeshLink();
        if (!agent.Warp(hit.position)) return false;
        if (facing.sqrMagnitude > 0.001f) body.rotation = Quaternion.LookRotation(facing, Vector3.up);
        agent.velocity = velocity;

        JuiceFX fx = JuiceFX.Instance != null ? JuiceFX.Instance : JuiceFX.Get();
        if (fx != null)
        {
            fx.AirPuff(from + Vector3.up, -transform.forward, 0.5f);
            fx.AirPuff(hit.position + Vector3.up, linkedPortal.transform.forward, 0.5f);
        }
        return true;
    }

    /// Flat distance from a to b, allowing one trip through any open portal - how far an
    /// enemy really has to go when the player is on the other side of one.
    public static float TravelDistance(Vector3 a, Vector3 b)
    {
        float best = FlatDistance(a, b);
        for (int i = 0; i < ActivePortals.Count; i++)
        {
            Portal p = ActivePortals[i];
            if (p == null || !p.Crossable) continue;
            float via = FlatDistance(a, p.transform.position) + FlatDistance(p.linkedPortal.transform.position, b);
            if (via < best) best = via;
        }
        return best;
    }

    /// Length of a NavMesh path, not counting the hop across a portal link (which spans the
    /// whole gap between two scenes but takes no time to cross).
    public static float PathLength(Vector3[] corners)
    {
        float length = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            if (IsPortalHop(corners[i - 1], corners[i])) continue;
            length += Vector3.Distance(corners[i - 1], corners[i]);
        }
        return length;
    }

    static bool IsPortalHop(Vector3 from, Vector3 to)
    {
        for (int i = 0; i < ActivePortals.Count; i++)
        {
            Portal p = ActivePortals[i];
            if (p == null || p.navLinks.Count == 0) continue;
            if ((from - p.linkStart).sqrMagnitude < 4f && (to - p.linkEnd).sqrMagnitude < 4f) return true;
        }
        return false;
    }

    static float FlatDistance(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        d.y = 0f;
        return d.magnitude;
    }

    // Runs after all movement for the frame, so the surface is already sized correctly by
    // the time anything renders it.
    void LateUpdate()
    {
        ProtectSurfaceFromClipping();
        CheckCameraCrossing();
    }

    // The teleport is driven from physics at a fixed 50Hz, but the camera renders
    // interpolated between those steps. At speed that leaves a window - up to a whole
    // physics step - where the view has already passed through the opening while the body
    // hasn't been stepped across yet, and every frame drawn in that window shows the room
    // the player just left. So the crossing is tested against the camera as well, once per
    // rendered frame: it's the camera's position that decides what actually gets drawn.
    //
    // Teleporting a touch early is harmless - the body maps through still slightly short
    // of the exit plane and simply continues outward from there - and the shared
    // teleportLockout keeps this and the physics check from both firing.
    void CheckCameraCrossing()
    {
        if (trackedBody == null)
        {
            cameraTracked = false;
            return;
        }

        Camera main = Camera.main;
        if (main == null)
        {
            cameraTracked = false;
            return;
        }

        Vector3 local = transform.InverseTransformPoint(main.transform.position);
        Vector2 size = CurrentSize;
        bool inBounds = Mathf.Abs(local.x) <= size.x * 0.5f
                     && Mathf.Abs(local.y) <= size.y * 0.5f;

        if (cameraTracked && Crossable && Time.time >= teleportLockout && inBounds
            && trackedPrevCameraLocalZ > 0f && local.z <= 0f)
        {
            TryTeleport();
            cameraTracked = false;
            return;
        }

        trackedPrevCameraLocalZ = local.z;
        cameraTracked = true;
    }

    // The camera's near plane sits AHEAD of the camera itself, so it slices into a
    // paper-thin portal surface slightly before the player actually reaches the opening -
    // punching a hole that shows the room behind the portal for the last few centimetres
    // of walking through. Giving the surface real depth toward the viewer for exactly that
    // stretch keeps geometry in front of the near plane the whole way across.
    void ProtectSurfaceFromClipping()
    {
        if (surfaceTransform == null || !IsVisible) return;

        Camera main = Camera.main;
        if (main == null) return;

        float distanceToPlane = Mathf.Abs(
            Vector3.Dot(main.transform.position - transform.position, transform.forward));

        float halfHeight = main.nearClipPlane * Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float halfWidth = halfHeight * main.aspect;
        float nearCornerDistance = new Vector3(halfWidth, halfHeight, main.nearClipPlane).magnitude;

        // Centred on the plane and grown BOTH ways, rather than toward one side. The
        // teleport fires from physics at a fixed 50Hz while the camera renders
        // interpolated, so for up to a physics step the camera can already be past the
        // plane with the body not yet moved - and a slab sitting only on the approach side
        // would be entirely behind the near plane by then, reopening the same hole. A slab
        // straddling the plane always leaves surface further out than the near plane
        // whichever side the camera is really on.
        // Floored well above the near-plane corner. With a small near clip that corner is
        // only a centimetre or two out, which covers the clipping but leaves no margin for
        // the single frame on which the crossing itself happens - the slab has to still
        // surround the camera on the frame it steps across.
        float halfThickness = Mathf.Max(nearCornerDistance, CrossingMargin);

        float thickness = distanceToPlane < halfThickness
            ? halfThickness * 2f
            : SurfaceFlatThickness;

        Vector2 size = CurrentSize;
        surfaceTransform.localScale = new Vector3(size.x, size.y, thickness);
        surfaceTransform.localPosition = Vector3.zero;
    }

    void TryResolveLink()
    {
        if (string.IsNullOrEmpty(linkedSceneName) || !PortalManager.Get().IsSceneReady(linkedSceneName))
            return;

        Portal partner = PortalManager.Get().FindPortal(linkedSceneName, linkedPortalId);
        if (partner == null) return;

        linkedPortal = partner;
        EnsureRenderTexture();

        if (partner.linkedSceneName != gameObject.scene.name || partner.linkedPortalId != portalId)
        {
            Debug.LogWarning($"[Portal] '{gameObject.scene.name}/{portalId}' leads to '{linkedSceneName}/{linkedPortalId}', " +
                $"but that one leads to '{partner.linkedSceneName}/{partner.linkedPortalId}' instead. The two won't open, close " +
                "or come back to each other together - point them at each other.", this);
        }
        if (portalCamera != null) portalCamera.enabled = IsVisible;

        // keep a closed pair in step: whichever side was opened first opens the other
        if (openPartnerWhenLinked)
        {
            openPartnerWhenLinked = false;
            partner.Open();
        }
        else if (!IsVisible && partner.IsVisible)
        {
            Open();
        }
    }

    void EnsureRenderTexture()
    {
        int w = Mathf.Max(4, Mathf.RoundToInt(Screen.width * resolutionScale));
        int h = Mathf.Max(4, Mathf.RoundToInt(Screen.height * resolutionScale));

        if (renderTexture != null && renderTexture.width == w && renderTexture.height == h)
            return;

        ReleaseRenderTexture();

        // HDR format: the portal camera renders the same HDR pipeline the main camera
        // does (bloom, exposure, etc. all operate on pre-tonemapped values). Capturing
        // into an LDR texture here would clip/crush that data before the surface shader
        // ever sees it, which is a big part of why a portal ends up looking like a dull
        // flat video feed instead of a real, correctly-lit view into another room.
        RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR)
            ? RenderTextureFormat.DefaultHDR
            : RenderTextureFormat.Default;

        int msaaSamples = UniversalRenderPipeline.asset != null ? UniversalRenderPipeline.asset.msaaSampleCount : 1;

        renderTexture = new RenderTexture(w, h, 24, format)
        {
            hideFlags = HideFlags.HideAndDontSave,
            antiAliasing = Mathf.Clamp(msaaSamples, 1, 8)
        };

        if (portalCamera != null) portalCamera.targetTexture = renderTexture;
    }

    void ReleaseRenderTexture()
    {
        if (renderTexture == null) return;
        if (portalCamera != null) portalCamera.targetTexture = null;
        renderTexture.Release();
        Destroy(renderTexture);
        renderTexture = null;
    }

    // ---------------------------------------------------------------- rendering

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam != portalCamera || linkedPortal == null) return;

        Camera main = Camera.main;
        if (main == null) return;

        // Position AND rotation both come from the one portal matrix - see PortalMatrix().
        // The projection matrix is cloned outright rather than rebuilt from FOV/aspect, so
        // the portal camera's lens can never drift out of sync with whatever the main
        // camera is doing (dynamic FOV kick from speed, etc.), and so its output stays
        // aligned to the main camera's screen - which the composite shader depends on,
        // since it samples this render texture by screen position.
        Matrix4x4 portalMatrix = PortalMatrix();
        portalCamera.transform.SetPositionAndRotation(
            portalMatrix.MultiplyPoint3x4(main.transform.position),
            portalMatrix.rotation * main.transform.rotation);
        portalCamera.projectionMatrix = main.projectionMatrix;
        portalCamera.cullingMask = main.cullingMask;
        portalCamera.allowHDR = main.allowHDR;

        // Post-processing stays OFF on this camera deliberately: PortalCompositeFeature
        // writes this render texture straight into the MAIN camera's own color buffer,
        // which then goes through its own post stack once, same as everything else in
        // the frame. If this camera also graded/tonemapped its output, that would
        // happen a second time on top - a subtly wrong double pass that's a real
        // contributor to a portal reading as "footage" rather than a real view.
        UniversalAdditionalCameraData portalData = portalCamera.GetUniversalAdditionalCameraData();
        if (portalData != null) portalData.renderPostProcessing = false;

        if (useObliqueClip)
            ApplyObliqueClip(portalCamera);

        PortalSceneEnvironment env = PortalManager.Get().FindEnvironment(linkedSceneName);
        if (env != null)
        {
            pendingEnvSnapshot = EnvSnapshot.Capture();
            env.Apply();
            envSwapped = true;
        }

        // the view through is lit by the far scene's sun, not the one the player is under
        PortalManager.Get().UseSunOf(linkedSceneName);
        sunSwapped = true;
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam != portalCamera) return;

        if (sunSwapped)
        {
            if (PortalManager.Instance != null) PortalManager.Instance.UseSunOf(PortalManager.PlayerSpace);
            sunSwapped = false;
        }

        if (!envSwapped) return;
        pendingEnvSnapshot.Restore();
        envSwapped = false;
    }

    bool sunSwapped;

    void ApplyObliqueClip(Camera cam)
    {
        // Pulls the portal camera's near plane onto the linked portal's own surface, so
        // the destination room's near-side geometry (the floor and walls sitting between
        // that camera and the opening) can't bleed into the view through it. The kept
        // half-space is the one the linked portal faces.
        //
        // Only the depth row of the projection is rewritten - x/y projection is untouched,
        // so the render stays aligned to the main camera's screen and the composite
        // shader's screen-space sampling keeps working.
        Transform exit = linkedPortal.transform;
        Vector3 normal = exit.forward * (flipClipPlane ? -1f : 1f);

        // Right as the player crosses, this camera sits essentially ON the clip plane and
        // an oblique matrix built there is degenerate - it blows the projection out and
        // the view through the opening garbles for a frame or two. Leave the projection
        // alone for that last stretch; there's nothing left to trim by then anyway.
        float cameraToPlane = Vector3.Dot(exit.position - cam.transform.position, normal);
        if (cameraToPlane <= clipPlaneOffset + 0.01f) return;

        float distance = -Vector3.Dot(normal, exit.position) - clipPlaneOffset;
        Vector4 worldPlane = new Vector4(normal.x, normal.y, normal.z, distance);

        Vector4 cameraPlane = Matrix4x4.Transpose(Matrix4x4.Inverse(cam.worldToCameraMatrix)) * worldPlane;

        // The oblique matrix is only well-formed while the clip plane actually cuts through
        // this camera's view frustum. The portal camera renders every frame, so most of the
        // time the linked portal is behind it, off to the side, or edge-on - and there the
        // rebuilt far plane collapses or flips inside-out, which Unity reports from its own
        // frustum-corner un-projection as "Screen position out of view frustum". Same test
        // CalculateObliqueMatrix is built around: take the far corner furthest along the
        // plane's normal and require it to sit clearly past the plane. When it doesn't, the
        // opening can't be meaningfully in view, so the plain projection loses nothing.
        Vector4 farCorner = cam.projectionMatrix.inverse *
            new Vector4(Mathf.Sign(cameraPlane.x), Mathf.Sign(cameraPlane.y), 1f, 1f);
        if (farCorner.w <= 0f) return;
        float farCornerPastPlane = Vector4.Dot(cameraPlane, farCorner) / farCorner.w;
        if (farCornerPastPlane < ObliqueMinFarCornerDistance) return;

        cam.projectionMatrix = cam.CalculateObliqueMatrix(cameraPlane);
    }

    struct EnvSnapshot
    {
        public Material skybox;
        public AmbientMode ambientMode;
        public Color ambientLight;
        public bool fog;
        public Color fogColor;
        public FogMode fogMode;
        public float fogStart, fogEnd, fogDensity;

        public static EnvSnapshot Capture() => new EnvSnapshot
        {
            skybox = RenderSettings.skybox,
            ambientMode = RenderSettings.ambientMode,
            ambientLight = RenderSettings.ambientLight,
            fog = RenderSettings.fog,
            fogColor = RenderSettings.fogColor,
            fogMode = RenderSettings.fogMode,
            fogStart = RenderSettings.fogStartDistance,
            fogEnd = RenderSettings.fogEndDistance,
            fogDensity = RenderSettings.fogDensity
        };

        public void Restore()
        {
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.fog = fog;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogStartDistance = fogStart;
            RenderSettings.fogEndDistance = fogEnd;
            RenderSettings.fogDensity = fogDensity;
        }
    }

    EnvSnapshot pendingEnvSnapshot;
    bool envSwapped;

    // ---------------------------------------------------------------- crossing detection

    void OnTriggerEnter(Collider other)
    {
        if (trackedBody != null) return;
        // a portal from a scene the player isn't in overlaps them without being there
        string space = PortalManager.PlayerSpace;
        if (!string.IsNullOrEmpty(space) && gameObject.scene.name != space) return;

        FirstPersonCharacterController controller = other.attachedRigidbody != null
            ? other.attachedRigidbody.GetComponent<FirstPersonCharacterController>()
            : null;
        if (controller == null) return;

        trackedController = controller;
        trackedBody = other.attachedRigidbody;
        trackedPrevLocalZ = transform.InverseTransformPoint(trackedBody.position).z;
    }

    void OnTriggerExit(Collider other)
    {
        if (other.attachedRigidbody == trackedBody)
        {
            trackedBody = null;
            trackedController = null;
        }
    }

    void FixedUpdate()
    {
        if (trackedBody == null) return;

        Vector3 local = transform.InverseTransformPoint(trackedBody.position);
        Vector2 size = CurrentSize;
        bool inBounds = Mathf.Abs(local.x) <= size.x * 0.5f && Mathf.Abs(local.y) <= size.y * 0.5f;

        if (Crossable && Time.time >= teleportLockout && inBounds && trackedPrevLocalZ > 0f && local.z <= 0f)
            TryTeleport();

        trackedPrevLocalZ = local.z;
    }

    void TryTeleport()
    {
        if (linkedPortal == null)
        {
            Debug.LogWarning($"[Portal] '{portalId}' was reached before its link to " +
                $"'{linkedSceneName}/{linkedPortalId}' finished resolving. Player passed through without teleporting.", this);
            return;
        }

        FirstPersonCharacterController controller = trackedController;
        Rigidbody body = trackedBody;

        Matrix4x4 portalMatrix = PortalMatrix();
        Quaternion rotDelta = portalMatrix.rotation;
        Vector3 newPos = portalMatrix.MultiplyPoint3x4(body.position);
        Quaternion currentYaw = Quaternion.LookRotation(controller.FlatForward, Vector3.up);
        Quaternion newRot = rotDelta * currentYaw;
        Vector3 newVel = rotDelta * body.linearVelocity;

        newPos += linkedPortal.transform.forward * exitPush;

        controller.TeleportTo(newPos, newRot, newVel);

        // A grapple anchored through the opening was stored as the virtual point the
        // player could SEE there - the real target mapped back onto this side. Now that
        // they're through, the same matrix turns that virtual point into the real one, so
        // the rope stays attached to the thing it was always aimed at.
        Grappling grappling = controller.transform.root.GetComponentInChildren<Grappling>(true);
        if (grappling != null) grappling.ApplyPortalTransform(portalMatrix);

        linkedPortal.SeedTracking(controller, body, exitPush);
        teleportLockout = Time.time + teleportCooldown;

        trackedBody = null;
        trackedController = null;

        PortalManager.Get().NotePlayerEntered(linkedPortal.gameObject.scene);
        PlayerTravelled?.Invoke(this, linkedPortal);
    }

    public void SeedTracking(FirstPersonCharacterController controller, Rigidbody body, float assumedLocalZ)
    {
        trackedController = controller;
        trackedBody = body;
        trackedPrevLocalZ = assumedLocalZ;
        teleportLockout = Time.time + teleportCooldown;
    }

    // The portal transform: express a world pose in this portal's local frame, flip it
    // 180 degrees (you go in one face and come out the other), then re-express it in the
    // linked portal's frame.
    //
    // Position and rotation MUST both be derived from this single matrix. They were
    // previously computed from two different transforms - the rotation included the flip
    // and the position deliberately left it out - which is geometrically incoherent: it
    // puts the render eye somewhere that doesn't match the direction it's pointed, so the
    // view through the portal swims and warps as the player moves instead of holding
    // still like a window. (With a linked pair whose rooms are identical untilted copies,
    // this matrix correctly reduces to a pure translation with no rotation at all, so the
    // view through the opening matches the room around it exactly.)
    Matrix4x4 PortalMatrix()
    {
        Matrix4x4 flip = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 180f, 0f), Vector3.one);
        return linkedPortal.transform.localToWorldMatrix * flip * transform.worldToLocalMatrix;
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(portalSize.x, portalSize.y, 0.01f));

        Gizmos.color = Color.blue;
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 1.5f);
        Gizmos.DrawLine(Vector3.forward * 1.5f, Vector3.forward * 1.1f + Vector3.up * 0.2f);
        Gizmos.DrawLine(Vector3.forward * 1.5f, Vector3.forward * 1.1f + Vector3.right * 0.2f);
    }
}
