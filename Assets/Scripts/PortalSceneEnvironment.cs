using UnityEngine;
using UnityEngine.Rendering;

// Optional. Drop one of these anywhere in a scene to record that scene's skybox/ambient/
// fog for correct portal previews. Without it, a portal looking into this scene just
// borrows whatever RenderSettings the currently active scene has, which can look
// slightly wrong (mismatched sky or fog) if the two scenes differ. Not required - most
// scenes can skip this entirely.
public class PortalSceneEnvironment : MonoBehaviour
{
    public Material skybox;
    public AmbientMode ambientMode = AmbientMode.Skybox;
    public Color ambientLight = Color.gray;
    public bool fog;
    public Color fogColor = Color.gray;
    public FogMode fogMode = FogMode.Linear;
    public float fogStartDistance;
    public float fogEndDistance = 300f;
    public float fogDensity = 0.01f;

    void OnEnable()
    {
        PortalManager.Get().RegisterEnvironment(this);
    }

    void OnDisable()
    {
        if (PortalManager.Instance != null)
            PortalManager.Instance.UnregisterEnvironment(this);
    }

    [ContextMenu("Capture Current RenderSettings")]
    void CaptureFromRenderSettings()
    {
        skybox = RenderSettings.skybox;
        ambientMode = RenderSettings.ambientMode;
        ambientLight = RenderSettings.ambientLight;
        fog = RenderSettings.fog;
        fogColor = RenderSettings.fogColor;
        fogMode = RenderSettings.fogMode;
        fogStartDistance = RenderSettings.fogStartDistance;
        fogEndDistance = RenderSettings.fogEndDistance;
        fogDensity = RenderSettings.fogDensity;
    }

    public void Apply()
    {
        RenderSettings.skybox = skybox;
        RenderSettings.ambientMode = ambientMode;
        RenderSettings.ambientLight = ambientLight;
        RenderSettings.fog = fog;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogMode = fogMode;
        RenderSettings.fogStartDistance = fogStartDistance;
        RenderSettings.fogEndDistance = fogEndDistance;
        RenderSettings.fogDensity = fogDensity;
    }
}
