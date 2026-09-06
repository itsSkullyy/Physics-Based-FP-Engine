using UnityEngine;
using UnityEngine.Rendering;

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

    [Header("References")]
    public Transform surfaceTransform;
    public MeshRenderer surfaceRenderer;
    public Camera portalCamera;

    [Header("Rendering")]
    [Range(0.25f, 1f)] public float resolutionScale = 1f;
    [Tooltip("Trims geometry behind the linked portal's wall from extreme angles. Off by default - this is a small polish feature, not required to see through the portal, and its sign convention is easy to get wrong for a given portal orientation. Only turn it on once the base view looks correct, then check both this and flipClipPlane.")]
    public bool useObliqueClip = false;
    public bool flipClipPlane = false;
    public float clipPlaneOffset = 0.05f;

    [Header("Teleport")]
    [Tooltip("Extra push along the exit direction so the player doesn't land exactly on the surface plane.")]
    public float exitPush = 0.05f;
    public float teleportCooldown = 0.15f;

    Portal linkedPortal;
    RenderTexture renderTexture;
    Material surfaceMaterial;

    Rigidbody trackedBody;
    FirstPersonCharacterController trackedController;
    float trackedPrevLocalZ;
    float teleportLockout;

    BoxCollider triggerCollider;

    void Awake()
    {
        triggerCollider = GetComponent<BoxCollider>();
        ApplyPortalSize();

        if (surfaceRenderer != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            if (shader != null)
            {
                surfaceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                surfaceRenderer.material = surfaceMaterial;
            }
        }

        if (portalCamera != null)
            portalCamera.enabled = false;
    }

    void OnEnable()
    {
        PortalManager.Get().RegisterPortal(this);
        PortalManager.Get().RequestSceneLoad(linkedSceneName);

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

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
        if (surfaceMaterial != null) Destroy(surfaceMaterial);
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
            // Unity's built-in Quad mesh faces local -Z, not +Z, so the surface is turned
            // 180 degrees here to bring its visible face in line with this portal's own
            // +Z (outward/exit) convention - otherwise the rendered preview only shows on
            // the side you can't actually cross from.
            surfaceTransform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            surfaceTransform.localScale = new Vector3(portalSize.x, portalSize.y, 1f);
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

        renderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.Default)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        if (portalCamera != null) portalCamera.targetTexture = renderTexture;
        if (surfaceMaterial != null) surfaceMaterial.mainTexture = renderTexture;
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

        // Position/rotate exactly where the hand-verified portal math says the main
        // camera would be after stepping through, then clone its projection matrix
        // outright rather than rebuilding one from FOV/aspect/near/far. Cloning the whole
        // matrix means the portal camera's lens can never drift out of sync with whatever
        // the main camera is actually doing (dynamic FOV kick from speed, etc.) - there's
        // nothing left to individually copy wrong.
        portalCamera.transform.SetPositionAndRotation(
            TransformPoint(main.transform.position),
            RotationDelta() * main.transform.rotation);
        portalCamera.projectionMatrix = main.projectionMatrix;
        portalCamera.cullingMask = main.cullingMask;

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
        // -forward here matches the verified reference implementation this technique is
        // taken from; flipClipPlane exists as an escape hatch back to +forward if a given
        // portal orientation ever needs it.
        Vector3 normal = linkedPortal.transform.forward * (flipClipPlane ? 1f : -1f);
        Vector3 pos = linkedPortal.transform.position + normal * clipPlaneOffset;

        Matrix4x4 worldToCamera = cam.worldToCameraMatrix;
        Vector3 cPos = worldToCamera.MultiplyPoint(pos);
        Vector3 cNormal = worldToCamera.MultiplyVector(normal).normalized;
        Vector4 clipPlane = new Vector4(cNormal.x, cNormal.y, cNormal.z, -Vector3.Dot(cPos, cNormal));

        cam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);
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

        Quaternion rotDelta = RotationDelta();
        Vector3 newPos = TransformPoint(body.position);
        Quaternion currentYaw = Quaternion.LookRotation(controller.FlatForward, Vector3.up);
        Quaternion newRot = rotDelta * currentYaw;
        Vector3 newVel = rotDelta * body.linearVelocity;

        newPos += linkedPortal.transform.forward * exitPush;

        controller.TeleportTo(newPos, newRot, newVel);

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

    // Rotation delta for anything that represents a FACING or direction of travel
    // (rotation, velocity, the camera's view basis). Crossing a portal continues your
    // motion outward from the other side, which - numerically - means reversing which way
    // you're pointed relative to the world, unless the two portals happen to be counter-
    // rotated to compensate. Hence the 180-degree flip baked in here.
    Quaternion RotationDelta()
    {
        Matrix4x4 flip = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 180f, 0f), Vector3.one);
        return (linkedPortal.transform.localToWorldMatrix * flip * transform.worldToLocalMatrix).rotation;
    }

    // Maps a world POINT through the portal. Deliberately does NOT include the 180-degree
    // flip that RotationDelta() has: a point that is d units in front of this portal (on
    // the room side) must map to a point d units in front of the linked portal, also on
    // ITS room side - positions are simply re-expressed in the linked portal's frame, not
    // reversed the way facing/velocity need to be. (Using the same flipped transform for
    // positions was the bug behind the camera looking too far away/small from a distance -
    // it put the render eye behind the linked portal's wall instead of in front of it,
    // adding a phantom gap that grew with how far back you actually stood. Actual
    // teleporting never showed the bug because it only happens right at the crossing
    // plane, where that gap is nearly zero either way.)
    Vector3 TransformPoint(Vector3 worldPoint)
    {
        Quaternion relativeRotation = linkedPortal.transform.rotation * Quaternion.Inverse(transform.rotation);
        Vector3 offset = worldPoint - transform.position;
        return linkedPortal.transform.position + relativeRotation * offset;
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
