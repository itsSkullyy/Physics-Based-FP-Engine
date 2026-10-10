using System.Collections;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// Cross-scene portal support. Handles additive scene streaming (ref-counted, with an
// unload grace delay so walking back and forth near a portal doesn't thrash) and a
// registry so two Portal instances living in different scenes can find each other's
// Transform once both scenes are loaded. Also keeps exactly one player/camera alive
// across scene loads: every existing scene already has its own Player rig, so loading a
// second scene additively would otherwise spawn a second player and a second camera.
//
// Auto-spawned the first time any Portal needs it - nothing to place by hand.
//
// Restarting has to go through ReloadScene/RestartCurrentScene rather than a plain
// SceneManager.LoadScene: the player and cameras live in DontDestroyOnLoad, so a plain
// reload would bring the scene's own Player up next to the old one.
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
        startScene = SceneManager.GetActiveScene();
        PlayerSceneName = startScene.name;
    }

    Scene startScene;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;

        foreach (List<NavMeshDataInstance> list in builtNavMeshes.Values)
            foreach (NavMeshDataInstance instance in list)
                instance.Remove();
        builtNavMeshes.Clear();
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

    /// The one live player. A scene streamed in through a portal brings its own Player rig,
    /// and that copy is awake for a moment before it gets switched off, so a plain
    /// FindFirstObjectByType can land on the copy. Anything that grabs the player in Awake
    /// should come through here.
    public static FirstPersonCharacterController FindPlayer()
    {
        if (Instance != null && Instance.persistentPlayer != null)
        {
            FirstPersonCharacterController c = Instance.persistentPlayer.GetComponentInChildren<FirstPersonCharacterController>();
            if (c != null) return c;
        }
        return FindFirstObjectByType<FirstPersonCharacterController>();
    }

    /// Scene the player is standing in. The active scene never changes when walking through
    /// a portal, so this is tracked separately for restarts.
    public string PlayerSceneName { get; private set; }

    public void NotePlayerEntered(Scene scene)
    {
        if (scene.IsValid()) PlayerSceneName = scene.name;
    }

    /// Restarts whichever scene the player is in right now (the level, or the tutorial).
    public static void RestartCurrentScene()
    {
        string scene = Instance != null && !string.IsNullOrEmpty(Instance.PlayerSceneName)
            ? Instance.PlayerSceneName
            : SceneManager.GetActiveScene().name;
        ReloadScene(scene);
    }

    /// Fresh load of one scene, throwing away the carried-over player, cameras and this
    /// manager first so the new scene starts exactly like pressing Play on it.
    public static void ReloadScene(string sceneName)
    {
        Time.timeScale = 1f;

        if (Instance != null)
        {
            if (Instance.persistentPlayer != null) Destroy(Instance.persistentPlayer);
            if (Instance.persistentCamera != null) Destroy(Instance.persistentCamera);
            if (Instance.persistentVcam != null) Destroy(Instance.persistentVcam);
            Destroy(Instance.gameObject);
            Instance = null;
        }

        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode == LoadSceneMode.Single)
        {
            OnSingleSceneLoaded(scene);
            return;
        }

        ResolvePersistentObjects();
        DisableDuplicatePlayerObjects(scene);
        StartCoroutine(EnsureNavMesh(scene));
    }

    // Something loaded a scene the normal way while this manager was carrying a player over.
    // If the new scene has its own player, the carried-over one is stale: drop it and start
    // tracking the new one, and forget every scene load from before.
    void OnSingleSceneLoaded(Scene scene)
    {
        // the scene this manager woke up in finishing its own load, nothing is stale
        if (scene == startScene)
        {
            StartCoroutine(EnsureNavMesh(scene));
            return;
        }
        startScene = scene;

        bool sceneHasOwnPlayer = false;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<FirstPersonCharacterController>(true) != null)
            {
                sceneHasOwnPlayer = true;
                break;
            }
        }

        if (sceneHasOwnPlayer && persistentPlayer != null && persistentPlayer.scene != scene)
        {
            Destroy(persistentPlayer);
            if (persistentCamera != null) Destroy(persistentCamera);
            if (persistentVcam != null) Destroy(persistentVcam);
            persistentPlayer = persistentCamera = persistentVcam = null;
        }

        StopAllCoroutines();
        sceneRefs.Clear();
        ResolvePersistentObjects();
        PlayerSceneName = scene.name;

        // The new scene's portals asked for their linked scenes before this ran, against the
        // old bookkeeping, so ask again.
        foreach (Portal portal in Portal.ActivePortals)
        {
            if (portal != null && portal.gameObject.scene == scene)
                RequestSceneLoad(portal.linkedSceneName);
        }

        StartCoroutine(EnsureNavMesh(scene));
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

    // ---------------------------------------------------------------- runtime navmesh

    readonly Dictionary<string, List<NavMeshDataInstance>> builtNavMeshes = new Dictionary<string, List<NavMeshDataInstance>>();

    // Enemies follow the player through portals, which needs NavMesh on both sides. A scene
    // with portals but nothing baked under them (the tutorial, at the moment) gets a NavMesh
    // built from its colliders when it loads, so nothing has to be baked by hand for it.
    IEnumerator EnsureNavMesh(Scene scene)
    {
        // one frame so the scene's portals have registered and duplicates are switched off
        yield return null;
        if (!scene.isLoaded || builtNavMeshes.ContainsKey(scene.name)) yield break;

        // a scene with its own bake is left alone, even if a portal sits off the edge of it
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Unity.AI.Navigation.NavMeshSurface surface in root.GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>())
            {
                if (surface.navMeshData != null) yield break;
            }
        }

        bool needsMesh = false;
        foreach (Portal portal in Portal.ActivePortals)
        {
            if (portal == null || portal.gameObject.scene != scene || !portal.enemiesCanPass) continue;
            if (!portal.HasNavMeshInFront()) needsMesh = true;
        }
        if (!needsMesh) yield break;

        if (!SceneBounds(scene, out Bounds bounds)) yield break;
        bounds.Expand(4f);

        List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(bounds, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
            new List<NavMeshBuildMarkup>(), sources);
        sources.RemoveAll(IsNotLevelGeometry);

        List<NavMeshDataInstance> instances = new List<NavMeshDataInstance>();
        builtNavMeshes[scene.name] = instances;

        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            NavMeshBuildSettings settings = NavMesh.GetSettingsByIndex(i);
            NavMeshData data = new NavMeshData(settings.agentTypeID);
            instances.Add(NavMesh.AddNavMeshData(data));
            yield return NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, sources, bounds);
        }

        Debug.Log($"[PortalManager] '{scene.name}' had no NavMesh by its portals, so one was built at load for enemies to follow you into it. Baking a NavMeshSurface there skips this.");
    }

    static bool SceneBounds(Scene scene, out Bounds bounds)
    {
        bounds = default;
        bool any = false;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (!root.activeInHierarchy) continue;
            foreach (Collider c in root.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger) continue;
                // kill planes and the like are huge and would pull the other scene in too
                if (c.bounds.size.x > 1000f || c.bounds.size.z > 1000f) continue;
                if (!any) { bounds = c.bounds; any = true; }
                else bounds.Encapsulate(c.bounds);
            }
        }
        return any;
    }

    // Only static level geometry. Anything that moves, the player, enemies and the portals'
    // own trigger volumes would leave holes or bumps in the mesh.
    static bool IsNotLevelGeometry(NavMeshBuildSource source)
    {
        Collider c = source.component as Collider;
        if (c == null) return false;
        if (c.isTrigger) return true;
        if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) return true;
        if (c.GetComponentInParent<NavMeshAgent>() != null) return true;
        if (c.GetComponentInParent<FirstPersonCharacterController>() != null) return true;
        if (c.GetComponentInParent<Portal>() != null) return true;
        return false;
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

        Scene existing = SceneManager.GetSceneByName(sceneName);
        if (existing.IsValid())
        {
            // already loaded, or already on its way in from an earlier request
            while (existing.IsValid() && !existing.isLoaded) yield return null;
            r.loaded = existing.isLoaded;
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
            if (builtNavMeshes.TryGetValue(sceneName, out List<NavMeshDataInstance> built))
            {
                foreach (NavMeshDataInstance instance in built) instance.Remove();
                builtNavMeshes.Remove(sceneName);
            }
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
