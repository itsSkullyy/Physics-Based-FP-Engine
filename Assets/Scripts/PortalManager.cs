using System.Collections;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;

// Cross-scene portal support. Handles additive scene streaming (ref-counted, with an
// unload grace delay so walking back and forth near a portal doesn't thrash) and a
// registry so two Portal instances living in different scenes can find each other's
// Transform once both scenes are loaded. Also keeps exactly one player/camera alive
// across scene loads: every existing scene already has its own Player rig, so loading a
// second scene additively would otherwise spawn a second player and a second camera.
//
// Auto-spawned the first time any Portal needs it - nothing to place by hand.
[DefaultExecutionOrder(-80)]
public class PortalManager : MonoBehaviour
{
    public static PortalManager Instance { get; private set; }

    [Tooltip("Seconds an additively-loaded scene stays loaded after its last portal releases it.")]
    public float unloadDelay = 4f;

    public static PortalManager Get()
    {
        if (Instance != null) return Instance;

        PortalManager found = FindFirstObjectByType<PortalManager>();
        if (found != null) { Instance = found; return Instance; }

        GameObject go = new GameObject("PortalManager");
        Instance = go.AddComponent<PortalManager>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded += OnSceneLoaded;
        ResolvePersistentObjects();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ---------------------------------------------------------------- player/camera persistence

    GameObject persistentPlayer;
    GameObject persistentCamera;
    GameObject persistentVcam;

    // The real render Camera (with the Cinemachine Brain) and the CinemachineCamera
    // (the virtual camera doing the actual tracking) are not children of the player rig -
    // in this project's scenes they're both separate root objects that merely start out
    // near the player - so all three are resolved and persisted independently.
    void ResolvePersistentObjects()
    {
        if (persistentPlayer == null)
        {
            FirstPersonCharacterController player = FindFirstObjectByType<FirstPersonCharacterController>();
            if (player != null)
            {
                persistentPlayer = player.transform.root.gameObject;
                DontDestroyOnLoad(persistentPlayer);
            }
        }

        if (persistentCamera == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                GameObject camRoot = cam.transform.root.gameObject;
                if (camRoot != persistentPlayer)
                {
                    persistentCamera = camRoot;
                    DontDestroyOnLoad(persistentCamera);
                }
            }
        }

        if (persistentVcam == null)
        {
            CinemachineVirtualCameraBase vcam = FindFirstObjectByType<CinemachineVirtualCameraBase>();
            if (vcam != null)
            {
                GameObject vcamRoot = vcam.transform.root.gameObject;
                if (vcamRoot != persistentPlayer && vcamRoot != persistentCamera)
                {
                    persistentVcam = vcamRoot;
                    DontDestroyOnLoad(persistentVcam);
                }
            }
        }
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Additive) return;

        ResolvePersistentObjects();
        DisableDuplicatePlayerObjects(scene);
    }

    void DisableDuplicatePlayerObjects(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == persistentPlayer || root == persistentCamera || root == persistentVcam) continue;

            FirstPersonCharacterController dupPlayer = root.GetComponentInChildren<FirstPersonCharacterController>(true);
            if (dupPlayer != null)
            {
                Debug.Log($"[PortalManager] Disabling duplicate player '{root.name}' found in additively loaded scene '{scene.name}'.", root);
                root.SetActive(false);
                continue;
            }

            CinemachineVirtualCameraBase dupVcam = root.GetComponentInChildren<CinemachineVirtualCameraBase>(true);
            if (dupVcam != null)
            {
                Debug.Log($"[PortalManager] Disabling duplicate virtual camera '{root.name}' found in additively loaded scene '{scene.name}'.", root);
                root.SetActive(false);
                continue;
            }

            AudioListener[] listeners = root.GetComponentsInChildren<AudioListener>(true);
            foreach (AudioListener listener in listeners)
                listener.enabled = false;

            Camera[] cams = root.GetComponentsInChildren<Camera>(true);
            foreach (Camera cam in cams)
            {
                if (!cam.CompareTag("MainCamera")) continue;
                Debug.Log($"[PortalManager] Disabling duplicate main camera '{cam.name}' found in additively loaded scene '{scene.name}'.", cam);
                cam.gameObject.SetActive(false);
            }
        }
    }

    // ---------------------------------------------------------------- scene streaming

    class SceneRef
    {
        public int count;
        public bool loaded;
        public bool loading;
        public Coroutine unloadRoutine;
    }

    readonly Dictionary<string, SceneRef> sceneRefs = new Dictionary<string, SceneRef>();

    public bool IsSceneReady(string sceneName) =>
        !string.IsNullOrEmpty(sceneName) && sceneRefs.TryGetValue(sceneName, out SceneRef r) && r.loaded;

    public void RequestSceneLoad(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;

        if (!sceneRefs.TryGetValue(sceneName, out SceneRef r))
        {
            r = new SceneRef();
            sceneRefs[sceneName] = r;
        }

        r.count++;

        if (r.unloadRoutine != null)
        {
            StopCoroutine(r.unloadRoutine);
            r.unloadRoutine = null;
        }

        if (!r.loaded && !r.loading)
            StartCoroutine(LoadRoutine(sceneName, r));
    }

    public void ReleaseSceneLoad(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;
        if (!sceneRefs.TryGetValue(sceneName, out SceneRef r)) return;

        r.count = Mathf.Max(0, r.count - 1);
        if (r.count == 0 && r.loaded && r.unloadRoutine == null)
            r.unloadRoutine = StartCoroutine(UnloadRoutine(sceneName, r));
    }

    IEnumerator LoadRoutine(string sceneName, SceneRef r)
    {
        r.loading = true;

        if (SceneManager.GetSceneByName(sceneName).isLoaded)
        {
            r.loaded = true;
            r.loading = false;
            yield break;
        }

        AsyncOperation op = null;
        try
        {
            op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PortalManager] Could not load scene '{sceneName}': {e.Message}. Is it added to Build Settings?", this);
        }

        if (op == null)
        {
            r.loading = false;
            yield break;
        }

        while (!op.isDone) yield return null;

        r.loaded = true;
        r.loading = false;
    }

    IEnumerator UnloadRoutine(string sceneName, SceneRef r)
    {
        yield return new WaitForSeconds(unloadDelay);

        if (r.count > 0) { r.unloadRoutine = null; yield break; }

        Scene scene = SceneManager.GetSceneByName(sceneName);
        if (scene.isLoaded)
        {
            UnregisterScenePortals(sceneName);
            yield return SceneManager.UnloadSceneAsync(scene);
        }

        r.loaded = false;
        r.unloadRoutine = null;
    }

    // ---------------------------------------------------------------- portal registry

    readonly Dictionary<(string scene, string id), Portal> portals = new Dictionary<(string, string), Portal>();

    public void RegisterPortal(Portal portal)
    {
        portals[(portal.gameObject.scene.name, portal.portalId)] = portal;
    }

    public void UnregisterPortal(Portal portal)
    {
        var key = (portal.gameObject.scene.name, portal.portalId);
        if (portals.TryGetValue(key, out Portal current) && current == portal)
            portals.Remove(key);
    }

    public Portal FindPortal(string sceneName, string portalId)
    {
        portals.TryGetValue((sceneName, portalId), out Portal p);
        return p;
    }

    void UnregisterScenePortals(string sceneName)
    {
        List<(string, string)> toRemove = new List<(string, string)>();
        foreach (var kvp in portals)
            if (kvp.Key.scene == sceneName) toRemove.Add(kvp.Key);
        foreach (var key in toRemove)
            portals.Remove(key);
    }

    // ---------------------------------------------------------------- environment registry

    readonly Dictionary<string, PortalSceneEnvironment> environments = new Dictionary<string, PortalSceneEnvironment>();

    public void RegisterEnvironment(PortalSceneEnvironment env)
    {
        environments[env.gameObject.scene.name] = env;
    }

    public void UnregisterEnvironment(PortalSceneEnvironment env)
    {
        string key = env.gameObject.scene.name;
        if (environments.TryGetValue(key, out PortalSceneEnvironment current) && current == env)
            environments.Remove(key);
    }

    public PortalSceneEnvironment FindEnvironment(string sceneName)
    {
        environments.TryGetValue(sceneName, out PortalSceneEnvironment env);
        return env;
    }
}
