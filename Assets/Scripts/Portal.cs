using System.Collections.Generic;
using UnityEngine;
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

    // Read by PortalCompositeFeature, which composites every ready portal's render
    // texture directly into whichever camera it's rendering for - stencil-clipped to
    // the surface silhouette - instead of this portal showing it via a textured quad in
    // the normal render queue. PortalCameras lets that feature skip re-compositing
    // portals visible from within another portal's own camera (no recursive portals).
    public static readonly List<Portal> ActivePortals = new List<Portal>();
    public static readonly HashSet<Camera> PortalCameras = new HashSet<Camera>();

    public bool IsRenderReady => linkedPortal != null && renderTexture != null;
    public RenderTexture PortalRenderTexture => renderTexture;
    public Matrix4x4 SurfaceMatrix => surfaceTransform != null ? surfaceTransform.localToWorldMatrix : Matrix4x4.identity;
    public Mesh SurfaceMesh =>
        surfaceTransform != null && surfaceTransform.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;

    public bool IsLinked => linkedPortal != null;

    /// Maps a pose on this portal's side into the linked portal's space. Apply it to
    /// anything that travels through - a body's position/rotation/velocity, or a world
    /// point something still on this side is anchored to.
    public Matrix4x4 TeleportMatrix => linkedPortal != null ? PortalMatrix() : Matrix4x4.identity;

    /// Does the segment from->to pass through this portal's opening, entering from the
    /// approach (local +Z) side? t is how far along the segment the crossing happens.
    public bool TryGetSegmentCrossing(Vector3 from, Vector3 to, out float t)
    {
        t = 0f;
        if (linkedPortal == null) return false;

        Vector3 localFrom = transform.InverseTransformPoint(from);
        Vector3 localTo = transform.InverseTransformPoint(to);

        // Same front-to-back convention the player's own crossing test uses, so a thing
        // that just came OUT of a portal moving away can never immediately re-enter it.
        if (localFrom.z <= 0f || localTo.z > 0f) return false;

        float span = localFrom.z - localTo.z;
        if (span <= Mathf.Epsilon) return false;

        t = localFrom.z / span;
        Vector3 crossing = Vector3.Lerp(localFrom, localTo, t);
        return Mathf.Abs(crossing.x) <= portalSize.x * 0.5f
            && Mathf.Abs(crossing.y) <= portalSize.y * 0.5f;
    }

    /// First portal the segment passes through, if any, with the transform to apply on
    /// the far side. Used by things that move themselves rather than relying on trigger
    /// callbacks - a thrown axe flies with its colliders disabled, and an aim ray has no
    /// collider at all.
    public static Portal FindSegmentCrossing(Vector3 from, Vector3 to, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.identity;

        Portal nearest = null;
        float nearestT = float.MaxValue;

        for (int i = 0; i < ActivePortals.Count; i++)
        {
            Portal portal = ActivePortals[i];
            if (portal == null) continue;
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

    void Awake()
    {
        triggerCollider = GetComponent<BoxCollider>();
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
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        ActivePortals.Remove(this);
        if (portalCamera != null) PortalCameras.Remove(portalCamera);

        if (PortalManager.Instance != null)
        {
            PortalManager.Instance.UnregisterPortal(this);
            PortalManager.Instance.ReleaseSceneLoad(linkedSceneName);
        }

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
        if (surfaceTransform != null)
        {
            // A box, not a quad: ProtectSurfaceFromClipping() needs real depth to grow
            // into while the player is mid-crossing. Its facing doesn't matter (the
            // composite shader samples by screen position, not by the mesh's own UVs), so
            // no orientation fixup is needed here.
            surfaceTransform.localRotation = Quaternion.identity;
            surfaceTransform.localScale = new Vector3(portalSize.x, portalSize.y, SurfaceFlatThickness);
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
            frameTop.localPosition = new Vector3(0f, portalSize.y * 0.5f + bw * 0.5f, 0f);
            frameTop.localScale = new Vector3(portalSize.x + bw * 2f, bw, bd);
        }
        if (frameBottom != null)
        {
            frameBottom.localPosition = new Vector3(0f, -(portalSize.y * 0.5f + bw * 0.5f), 0f);
            frameBottom.localScale = new Vector3(portalSize.x + bw * 2f, bw, bd);
        }
        if (frameLeft != null)
        {
            frameLeft.localPosition = new Vector3(-(portalSize.x * 0.5f + bw * 0.5f), 0f, 0f);
            frameLeft.localScale = new Vector3(bw, portalSize.y, bd);
        }
        if (frameRight != null)
        {
            frameRight.localPosition = new Vector3(portalSize.x * 0.5f + bw * 0.5f, 0f, 0f);
            frameRight.localScale = new Vector3(bw, portalSize.y, bd);
        }

        BoxCollider box = triggerCollider != null ? triggerCollider : GetComponent<BoxCollider>();
        if (box != null)
        {
            box.isTrigger = true;
            box.center = Vector3.zero;
            box.size = new Vector3(portalSize.x, portalSize.y, Mathf.Max(0.05f, triggerDepth));
        }
    }

    void Update()
    {
        if (linkedPortal == null)
            TryResolveLink();
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
        bool inBounds = Mathf.Abs(local.x) <= portalSize.x * 0.5f
                     && Mathf.Abs(local.y) <= portalSize.y * 0.5f;

        if (cameraTracked && Time.time >= teleportLockout && inBounds
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
        if (surfaceTransform == null) return;

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

        surfaceTransform.localScale = new Vector3(portalSize.x, portalSize.y, thickness);
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
        if (portalCamera != null) portalCamera.enabled = true;
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
    }

    void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (cam != portalCamera || !envSwapped) return;
        pendingEnvSnapshot.Restore();
        envSwapped = false;
    }

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
        bool inBounds = Mathf.Abs(local.x) <= portalSize.x * 0.5f && Mathf.Abs(local.y) <= portalSize.y * 0.5f;

        if (Time.time >= teleportLockout && inBounds && trackedPrevLocalZ > 0f && local.z <= 0f)
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
