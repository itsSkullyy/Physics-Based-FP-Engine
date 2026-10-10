using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.SceneManagement;

// Grey-box of the 16-room tutorial from the level design doc.
//
// Every room is an inverted-hull ProBuilder cube made with RoomBuilder.ConvertToRoom, the
// same as Tools > ProBuilder Rooms > Convert Selection To Room, with real doorway holes
// cut into it. Corridors are inverted hulls too, open at both ends. Inside the rooms,
// platforms and obstacles are solid ProBuilder cubes on the Ground layer like the existing
// ones: light grey to stand on, blue obstacles, red wall-run walls (WallRun layer + tag),
// orange pogo surfaces (PogoSurface), glass breakables, brown axe targets. Grapple points
// are the Grapple Point prefab with a green light.
//
// No pit kills you. Every pit has 2 m steps (vaultable) back up, so falling costs a climb.
//
// USE: Tools > Tutorial Blockout > Build Blockout Scene. It builds into the Tutorial
// Blockout scene (a copy of Mirror Grapple Scene), replacing everything under the
// "Tutorial Blockout" object. It also wires Grapple Scene to it (the arrival portal the
// tutorial's exit leads to, and the level's exit pointed back here instead of at Mirror
// Grapple Scene) and puts the tutorial first in Build Settings with Mirror taken out.
// Tools > Tutorial Blockout > Link Portals And Build Order does just that wiring.
//
// Each room is laid out in its own frame: you come in through the -x wall at x = 0, z = 0,
// standing on y = 0, and x runs forward. Rooms are chained exit to entry by 6 m corridors,
// turning left (N exit) or right (S exit) as they go, so a room can be resized without
// moving the ones after it by hand.
public static class TutorialBlockoutBuilder
{
    // Scenes are found by name, so they can live in any folder (Assets/Scenes/In Build...).
    const string SourceSceneName = "Mirror Grapple Scene";
    const string BlockoutSceneName = "Tutorial Blockout";
    const string LevelSceneName = "Grapple Scene";
    const string RootName = "Tutorial Blockout";
    const string Materials = "Assets/Materials/Thirdparty/Ciathyza/Gridbox Prototype Materials/Materials/URP/";
    const string GlassPath = "Assets/Materials/Blockout Glass.mat";
    static readonly Vector3 Origin = new Vector3(1900f, 0f, 0f);

    const float DoorW = 4f;
    const float DoorH = 4f;
    const float CorridorLength = 6f;
    const float Eye = 1.25f;        // player root height above the floor
    const float Rise = 2f;          // tallest step you can always vault

    static readonly Color Blue = new Color(0.45f, 0.85f, 1f, 1f);
    static readonly Color Orange = new Color(1f, 0.62f, 0.2f, 1f);
    static readonly Color Green = new Color(0.45f, 1f, 0.55f, 1f);
    static readonly Color Gold = new Color(1f, 0.85f, 0.3f, 1f);
    static readonly Color ExitLight = new Color(1f, 0.85f, 0.45f, 1f);

    static Material hullMat, floorMat, obstacleMat, wallRunMat, exitMat, pogoMat, targetMat, glassMat;
    static int groundLayer, wallRunLayer;
    static GameObject grapplePointPrefab, breakablePrefab, portalPrefab;
    static int built;

    // the room being built
    static Transform root, room;
    static float rL, rZ0, rZ1, rY0, rY1;
    static bool rHasEntry;
    static char exitSide;
    static float exitAt, exitSill;

    // where the next room's entry goes
    static Vector3 cursorPos;
    static float cursorYaw;

    // ---------------------------------------------------------------- menu

    [MenuItem("Tools/Tutorial Blockout/Build Blockout Scene")]
    static void BuildFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        EditorUtility.DisplayDialog("Tutorial Blockout", Build(), "OK");
    }

    // Just the wiring between the tutorial and the level, without rebuilding the rooms.
    [MenuItem("Tools/Tutorial Blockout/Link Portals And Build Order")]
    static void LinkFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        string blockout = FindScene(BlockoutSceneName);
        if (blockout == null)
        {
            EditorUtility.DisplayDialog("Tutorial Blockout", $"There's no '{BlockoutSceneName}' scene yet. Build it first.", "OK");
            return;
        }
        if (!LoadAssets(out string problem))
        {
            EditorUtility.DisplayDialog("Tutorial Blockout", problem, "OK");
            return;
        }
        string note = LinkLevel() + "\n\n" + SetBuildOrder(blockout);
        EditorUtility.DisplayDialog("Tutorial Blockout", note, "OK");
    }

    /// For -batchmode -executeMethod TutorialBlockoutBuilder.BuildFromCommandLine
    public static void BuildFromCommandLine()
    {
        Debug.Log("[TutorialBlockout] " + Build());
    }

    static string Build()
    {
        string BlockoutScene = FindScene(BlockoutSceneName);
        if (BlockoutScene == null)
        {
            string source = FindScene(SourceSceneName);
            if (source == null)
            {
                return $"Couldn't find a '{BlockoutSceneName}' scene, or '{SourceSceneName}' to copy one from.";
            }
            string folder = System.IO.Path.GetDirectoryName(FindScene(LevelSceneName) ?? source).Replace('\\', '/');
            BlockoutScene = folder + "/" + BlockoutSceneName + ".unity";
            if (!AssetDatabase.CopyAsset(source, BlockoutScene))
            {
                return "Couldn't copy " + source + " to " + BlockoutScene + ".";
            }
        }
        if (!LoadAssets(out string problem))
        {
            return problem;
        }

        Scene scene = EditorSceneManager.OpenScene(BlockoutScene, OpenSceneMode.Single);
        ClearOldLayout(scene);

        root = new GameObject(RootName).transform;
        root.position = Origin;
        cursorPos = Origin;
        cursorYaw = 0f;
        built = 0;

        Room01Arrival();
        Room02JumpYard();
        Room03VaultCourtyard();
        Room04Slides();
        Room05WallRunPit();
        Room06Tower();
        Room07TheRun();
        Room08AxeShrine();
        Room09ButtonDoor();
        Room10PogoHall();
        Room11GlassRun();
        Room12TheWell();
        Room13ZipCanyon();
        Room14SwingGorge();
        Room15AnchorChasm();
        Room16TheAscent();

        PlacePlayerAtStart(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        string levelNote = LinkLevel() + "\n\n" + SetBuildOrder(BlockoutScene);

        return $"Built the tutorial blockout in {BlockoutScene}: {built} pieces in 16 rooms.\n\n{levelNote}\n\n" +
               "Press Play in Tutorial Blockout to try it. Rebuilding replaces everything " +
               $"under '{RootName}', so hand edits inside it are lost.";
    }

    // the scene asset whose file is exactly "<name>.unity", wherever it is under Assets
    static string FindScene(string name)
    {
        foreach (string guid in AssetDatabase.FindAssets(name + " t:Scene", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == name)
            {
                return path;
            }
        }
        return null;
    }

    // The tutorial is where the game starts, so it goes first, then the level. The old
    // tutorial it replaces comes out: left in, anything still pointing at it would stream
    // it in on top of the new one, since every loaded scene shares the same world.
    static string SetBuildOrder(string blockoutPath)
    {
        string levelPath = FindScene(LevelSceneName);
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>();
        scenes.Add(new EditorBuildSettingsScene(blockoutPath, true));
        if (levelPath != null)
        {
            scenes.Add(new EditorBuildSettingsScene(levelPath, true));
        }
        bool removedOld = false;
        foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
        {
            if (s.path == blockoutPath || s.path == levelPath)
            {
                continue;
            }
            if (System.IO.Path.GetFileNameWithoutExtension(s.path) == SourceSceneName)
            {
                removedOld = true;
                continue;
            }
            scenes.Add(s);
        }
        EditorBuildSettings.scenes = scenes.ToArray();
        return $"Build Settings: {BlockoutSceneName} first, then {LevelSceneName}" +
               (removedOld ? $", and {SourceSceneName} taken out (the file is still there)." : ".");
    }

    static bool LoadAssets(out string problem)
    {
        problem = null;
        hullMat = LoadMaterial("Prototype_512x512_Grey4");
        floorMat = LoadMaterial("Prototype_512x512_Grey1");
        obstacleMat = LoadMaterial("Prototype_512x512_Blue1");
        wallRunMat = LoadMaterial("Prototype_512x512_Red");
        exitMat = LoadMaterial("Prototype_512x512_Yellow");
        pogoMat = LoadMaterial("Prototype_512x512_Orange");
        targetMat = LoadMaterial("Prototype_512x512_Brown");
        glassMat = GlassMaterial();

        groundLayer = LayerMask.NameToLayer("Ground");
        wallRunLayer = 8;
        if (groundLayer < 0)
        {
            problem = "There's no 'Ground' layer, which the player's ground check needs.";
            return false;
        }

        grapplePointPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Grapple Point.prefab");
        breakablePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BreakableWall.prefab");
        portalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Portal.prefab");
        if (grapplePointPrefab == null || breakablePrefab == null || portalPrefab == null)
        {
            problem = "Couldn't find the Grapple Point, BreakableWall or Portal prefab in Assets/Prefabs.";
            return false;
        }
        return true;
    }

    static Material LoadMaterial(string name)
    {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(Materials + name + ".mat");
        return m != null ? m : BuiltinMaterials.defaultMaterial;
    }

    // URP Lit, transparent, faintly blue. Made once and kept as an asset so shards and the
    // scene keep referencing it.
    static Material GlassMaterial()
    {
        Material glass = AssetDatabase.LoadAssetAtPath<Material>(GlassPath);
        if (glass != null)
        {
            return glass;
        }

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            return BuiltinMaterials.defaultMaterial;
        }

        glass = new Material(lit) { name = "Blockout Glass" };
        glass.SetFloat("_Surface", 1f);
        glass.SetFloat("_Blend", 0f);
        glass.SetFloat("_ZWrite", 0f);
        glass.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glass.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glass.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        glass.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glass.SetFloat("_Smoothness", 0.95f);
        glass.SetColor("_BaseColor", new Color(0.7f, 0.9f, 1f, 0.22f));
        glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        glass.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        glass.SetOverrideTag("RenderType", "Transparent");

        System.IO.Directory.CreateDirectory("Assets/Materials");
        AssetDatabase.CreateAsset(glass, GlassPath);
        return glass;
    }

    // The copy brings the old one-long-room tutorial and Portal B with it. Those go, the
    // player, cameras, lights, the arrival portal and services stay.
    static void ClearOldLayout(Scene scene)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            string n = go.name;
            Portal portal = go.GetComponent<Portal>();
            bool oldPiece = n == RootName
                || n == "Cube" || (n.StartsWith("Cube (") && n.EndsWith(")"))
                || n.StartsWith("Grapple Point")
                || n == "Axe Pickup" || n == "Grapple Pickup" || n == "Axe Wall"
                // a loose copy of the thrown-axe prefab; the axe itself uses the asset
                || n == "AxeThrowPrefab"
                // the old way into the level, replaced by the exit portal at the end
                || (portal != null && portal.portalId == "PortalB");
            if (oldPiece)
            {
                Object.DestroyImmediate(go);
            }
        }
    }

    static void PlacePlayerAtStart(Scene scene)
    {
        Vector3 spawn = Origin + new Vector3(4f, Eye, 0f);
        Quaternion facing = Quaternion.Euler(0f, 90f, 0f);

        foreach (GameObject go in scene.GetRootGameObjects())
        {
            bool isPlayer = go.GetComponentInChildren<FirstPersonCharacterController>(true) != null;
            bool isCamera = go.GetComponentInChildren<Camera>(true) != null && go.GetComponentInChildren<Portal>(true) == null;
            bool isVcam = go.GetComponentInChildren<Unity.Cinemachine.CinemachineVirtualCameraBase>(true) != null;
            if (isPlayer || go.name == "RespawnPoint")
            {
                go.transform.SetPositionAndRotation(spawn, facing);
            }
            else if (isCamera || isVcam)
            {
                go.transform.SetPositionAndRotation(spawn + Vector3.up * 0.6f, facing);
            }
        }
    }

    // The two ways between the tutorial and Grapple Scene, each pair pointing at each other:
    //   tutorial TutorialExit (end of the tutorial) <-> level TutorialArrival (level start)
    //   level ExitPortal (end of the level)         <-> tutorial ArrivalPortal (tutorial start)
    // The level's exit used to lead to the old tutorial; it's moved over to this one.
    static string LinkLevel()
    {
        string levelPath = FindScene(LevelSceneName);
        if (levelPath == null)
        {
            return $"Couldn't find '{LevelSceneName}', so the portals between it and the tutorial weren't linked.";
        }
        Scene level = EditorSceneManager.OpenScene(levelPath, OpenSceneMode.Additive);
        List<string> notes = new List<string>();

        bool hasArrival = false;
        foreach (Portal p in Object.FindObjectsByType<Portal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p.gameObject.scene != level)
            {
                continue;
            }
            if (p.portalId == "TutorialArrival")
            {
                hasArrival = true;
            }
            if (p.linkedSceneName == SourceSceneName && p.gameObject.activeInHierarchy)
            {
                SerializedObject link = new SerializedObject(p);
                link.FindProperty("linkedSceneName").stringValue = BlockoutSceneName;
                link.ApplyModifiedProperties();
                notes.Add($"{LevelSceneName}'s '{p.name}' now leads to {BlockoutSceneName}/{p.linkedPortalId} instead of {SourceSceneName}.");
            }
        }

        if (!hasArrival)
        {
            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(portalPrefab, level);
            go.name = "Tutorial Arrival Portal";
            go.transform.position = new Vector3(0f, 3f, 5f);
            Portal portal = go.GetComponent<Portal>();
            SerializedObject so = new SerializedObject(portal);
            so.FindProperty("portalId").stringValue = "TutorialArrival";
            so.FindProperty("linkedSceneName").stringValue = BlockoutSceneName;
            so.FindProperty("linkedPortalId").stringValue = "TutorialExit";
            so.FindProperty("portalSize").vector2Value = new Vector2(4.5f, 6f);
            so.FindProperty("startClosed").boolValue = true;
            so.FindProperty("openPlacement").enumValueIndex = (int)Portal.OpenPlacement.AtSceneStart;
            so.ApplyModifiedProperties();
            notes.Add($"Added the TutorialArrival portal to {LevelSceneName} (it opens ahead of the level's start).");
        }

        if (notes.Count == 0)
        {
            EditorSceneManager.CloseScene(level, true);
            return $"{LevelSceneName}'s portals already lead to the tutorial.";
        }
        EditorSceneManager.MarkSceneDirty(level);
        EditorSceneManager.SaveScene(level);
        EditorSceneManager.CloseScene(level, true);
        return string.Join("\n", notes);
    }

    // ================================================================ Act 1: feet only

    // 1 Arrival. A calm room to look around in. The arrival portal opens 5 m ahead of you.
    static void Room01Arrival()
    {
        BeginRoom("01 Arrival", 24, -12, 12, 0, 10, entry: false);
        Checkpoint("Start", 4, 0, 0, 90);
        Tip("Tip Move", 2, 9, -4, 4, 0, "MOVE", "MOVEMENT", Blue,
            "{move}  Move, mouse to look.",
            "Keep moving and you get faster. Stopping throws your speed away.");
        Exit('E', 0, 0);
        EndRoom();
    }

    // 2 Jump yard. Platforms over a 4 m trench: gaps of 2, 3 and 4 m, steps up and down, a
    // 12 m runway into a 6 m gap, then zigzag stepping stones. Fall in and climb the steps
    // back up to the platform you jumped from.
    static void Room02JumpYard()
    {
        BeginRoom("02 Jump Yard", 100, -7, 7, -4, 10);
        Floor(0, 12, -7, 7, 0);
        Floor(14, 20, -7, 7, 0);
        Floor(23, 29, -7, 7, 0);
        Floor(33, 40, -7, 7, 1);
        Floor(43, 48, -7, 7, 0);
        Floor(51, 55, -7, 7, 1.5f);
        Floor(58, 70, -7, 7, 0.5f);
        Floor(76, 80, -7, 7, 0.5f);
        Floor(83, 85.5f, 1, 4.5f, 0.5f);
        Floor(88, 90.5f, -4.5f, -1, 0.5f);
        Floor(93, 95.5f, 1, 4.5f, 0.5f);
        Floor(97.5f, 100, -7, 7, 0.5f);
        ClimbBack(12, 5, 7, 0);
        ClimbBack(20, 5, 7, 0);
        ClimbBack(29, 5, 7, 0);
        ClimbBack(40, 5, 7, 1);
        ClimbBack(48, 5, 7, 0);
        ClimbBack(55, 5, 7, 1.5f);
        ClimbBack(70, 5, 7, 0.5f);
        ClimbBack(80, 5, 7, 0.5f);
        Checkpoint("Jump Yard", 1.5f, 0, 0, 90);
        Checkpoint("Runway", 59, 0, 0.5f, 90);
        Tip("Tip Jump", 1, 6, -7, 7, 0, "JUMP", "MOVEMENT", Blue,
            "{jump}  Jump. Hold it to go higher, tap it for a hop.");
        Tip("Tip Run Up", 58.5f, 62, -7, 7, 0.5f, "RUN-UP", "MOVEMENT", Blue,
            "Long gap ahead. Use the whole runway and hold  {jump}.");
        Exit('E', 0, 0.5f);
        EndRoom();
    }

    // 3 Vault courtyard. Vaulting is how you climb: up three 1.8 m terraces, then low
    // barriers you vault at speed (low vaults keep your speed) into a 6 m gap to the exit.
    // Miss and you land on the terrace below and vault straight back up.
    static void Room03VaultCourtyard()
    {
        BeginRoom("03 Vault Courtyard", 40, -12, 12, 0, 14);
        Block("Crate", 6, 7, -3, 3, 0, 1);
        Floor(14, 40, -12, 12, 1.8f);
        Floor(18, 40, -12, 12, 3.6f);
        Floor(22, 30, -12, 12, 5.4f);
        Block("Barrier 1", 24.5f, 25, -12, 12, 5.4f, 6f);
        Block("Barrier 2", 27, 27.5f, -12, 12, 5.4f, 6f);
        Floor(36, 40, -12, 12, 6f);
        Checkpoint("Vault Courtyard", 1.5f, 0, 0, 90);
        Checkpoint("Top Terrace", 22.6f, 0, 5.4f, 90);
        Tip("Tip Vault", 1, 5, -6, 6, 0, "VAULT", "MOVEMENT", Blue,
            "Run straight at a ledge to vault it. No need to jump.",
            "Ledges up to about your height are climbable this way.");
        Tip("Tip Keep Speed", 22.2f, 24, -12, 12, 5.4f, "KEEP YOUR SPEED", "MOVEMENT", Blue,
            "Vault low walls instead of jumping them. You keep your speed for the gap.");
        Exit('E', 0, 6f);
        EndRoom();
    }

    // 4 Slides. Down a slope, under two pipes 1.6 m off the floor (too low to stand under,
    // too high to vault), then a 1 m ledge with a platform 2.2 m above it: only a slide
    // launch off the ledge gets you up.
    static void Room04Slides()
    {
        BeginRoom("04 Slides", 56, -5, 5, -5, 8);
        Floor(0, 6, -5, 5, 0);
        Ramp("Slope", 6, 0, 30, -4.2f, -5, 5);
        Floor(30, 44, -5, 5, -4.2f);
        Block("Pipe 1", 33, 34, -5, 5, -2.6f, -2f);
        Block("Pipe 2", 38, 39, -5, 5, -2.6f, -2f);
        Block("Launch Ledge", 44, 45, -5, 5, -4.2f, -3.2f);
        Floor(45, 56, -5, 5, -1f);
        Checkpoint("Slides", 1.5f, 0, 0, 90);
        Checkpoint("Slide Flat", 30.6f, 0, -4.2f, 90);
        Tip("Tip Slide", 1, 5, -5, 5, 0, "SLIDE", "MOVEMENT", Blue,
            "{slide}  Slide. Fit under low gaps and pick up speed downhill.");
        Tip("Tip Slide Launch", 40, 43.5f, -5, 5, -4.2f, "SLIDE LAUNCH", "MOVEMENT", Blue,
            "Slide into a low ledge fast and it launches you up.");
        Exit('N', 51, -1f);
        EndRoom();
    }

    // 5 Wall-run pit. A 12 m pit under everything. One wall across the first gap to a
    // pillar, then two walls on alternate sides across 32 m to the exit ledge. Fall and you
    // climb the steps in the pit up to the pillar or the exit ledge.
    static void Room05WallRunPit()
    {
        BeginRoom("05 Wall Run Pit", 72, -9, 9, -12, 14);
        Floor(0, 6, -9, 9, 0);
        Floor(18, 26, -9, 9, 0);
        Floor(58, 72, -9, 9, 0);
        // the walls hang above the pit floor so it stays one space you can walk around
        WallRun("Wall Run Teach", 5, 19, 4, 4.5f, -4, 8);
        WallRun("Wall Run A", 25, 42, -4.5f, -4, -4, 8);
        WallRun("Wall Run B", 40, 57, 4, 4.5f, -4, 8);
        Stairs(18, +1, -9, -6, -12, 0);
        Stairs(58, +1, -9, -6, -12, 0);
        Checkpoint("Wall Run Pit", 1.5f, 0, 0, 90);
        Checkpoint("Pillar", 18.6f, 0, 0, 90);
        Tip("Tip Wall Run", 1, 5, -9, 9, 0, "WALL RUN", "MOVEMENT", Blue,
            "Jump alongside a red wall and hold forward to run along it.",
            "{jump}  jumps off it.");
        Exit('E', 0, 0);
        EndRoom();
    }

    // 6 The tower. 16 x 16, climbing to an exit 18.6 m up. Vault up two blocks, jump a 4 m
    // gap along the walls, kick up a 4 m chimney, then ledges to the exit. Falling just
    // drops you to a lower ledge or the floor to climb again.
    static void Room06Tower()
    {
        BeginRoom("06 The Tower", 16, -8, 8, 0, 26);
        Block("Step", 4, 8, 5, 8, 0, 1.8f);
        Floor(8, 16, 6, 8, 3.6f);
        Ledge("Ledge East", 14, 16, -2, 6, 5.4f);
        Ledge("Ledge South", 0, 16, -8, -6, 5.4f);
        Block("Chimney Wall", 4, 5, -6, 0, 0, 11.4f);
        Ledge("Ledge Chimney Top", 0, 1.5f, -6, 0, 11.4f);
        Ledge("Ledge West", 0, 1.5f, 2, 8, 13.2f);
        Ledge("Ledge North", 4, 16, 6.5f, 8, 15f);
        Ledge("Ledge Exit", 14.5f, 16, -8, 5, 16.8f);
        Checkpoint("Tower", 1.5f, 0, 0, 90);
        TipBox("Tip Wall Kick", new Vector3(0, 5.4f, -8), new Vector3(4, 9, -6), "WALL KICK", "MOVEMENT", Blue,
            "{jump}  in the air next to a wall kicks off it. Twice per jump.",
            "Kick between these two walls to climb the chimney.");
        TipBox("Tip Dart", new Vector3(0, 13.2f, 2), new Vector3(1.5f, 16, 8), "DART", "MOVEMENT", Blue,
            "{dart}  just after a jump or kick darts forward.",
            "A dart gives you a wall kick back.");
        Exit('E', -4, 18.6f);
        EndRoom();
    }

    // 7 The Run. 150 m, no cards: a long slide with pipes, a crate, two trenches, a slide
    // launch, two wall runs over a 22 m pit, and a vault climb to the exit.
    static void Room07TheRun()
    {
        BeginRoom("07 The Run", 150, -6, 6, -22, 10);
        Floor(0, 6, -6, 6, 0);
        Ramp("Slide 1", 6, 0, 26, -5, -6, 6);
        Floor(26, 34, -6, 6, -5);
        Block("Pipe A", 29, 30, -6, 6, -3.4f, -2.8f);
        Ramp("Slide 2", 34, -5, 54, -10, -6, 6);
        Floor(54, 66, -6, 6, -10);
        Block("Pipe B", 58, 59, -6, 6, -8.4f, -7.8f);
        Floor(66, 70, -6, 6, -14);
        Floor(70, 78, -6, 6, -10);
        Block("Crate", 73, 74, -6, 6, -10, -9);
        Floor(78, 82, -6, 6, -14);
        Floor(82, 93, -6, 6, -10);
        Block("Launch Ledge", 93, 94, -6, 6, -10, -9);
        Floor(94, 104, -6, 6, -6.8f);
        Floor(130, 143, -6, 6, -6.8f);
        Floor(143, 150, -6, 6, -4.8f);
        ClimbBack(66, 4, 6, -10, -14);
        ClimbBack(78, 4, 6, -10, -14);
        WallRun("Run Wall A", 103, 117, 3, 3.5f, -12, 0);
        WallRun("Run Wall B", 115, 131, -3.5f, -3, -12, 0);
        Stairs(130, +1, -6, -4, -22, -6.8f);
        Checkpoint("The Run", 1.5f, 0, 0, 90);
        Checkpoint("Run Flat", 54.6f, 0, -10, 90);
        Checkpoint("Run Launch", 82.6f, 0, -10, 90);
        Checkpoint("Run Landing", 130.6f, 0, -6.8f, 90);
        Exit('E', 0, -2.8f);
        EndRoom();
    }

    // ================================================================ Act 2: axe

    // 8 Axe shrine. The axe on a lit plinth, two glass crates, and the exit sealed by a
    // glass wall only the axe opens.
    static void Room08AxeShrine()
    {
        BeginRoom("08 Axe Shrine", 20, -10, 10, 0, 12);
        Block("Plinth", 9, 11, -1, 1, 0, 1);
        Pickup("Axe Pickup", AbilityPickup.Ability.Axe, new Vector3(10, 2.4f, 0), "Smash the glass to get out.");
        PointLight("Plinth Light", new Vector3(10, 7, 0), new Color(1f, 0.8f, 0.6f), 12, 2.5f);
        Glass("Glass Crate A", new Vector3(6, 0.75f, 6), 1.5f, 1.5f, alongX: true, runThrough: true);
        Glass("Glass Crate B", new Vector3(14, 0.75f, -6), 1.5f, 1.5f, alongX: true, runThrough: true);
        Glass("Exit Glass", new Vector3(19.8f, DoorH * 0.5f, 0), DoorW, DoorH, alongX: true, runThrough: false);
        Checkpoint("Axe Shrine", 1.5f, 0, 0, 90);
        Exit('E', 0, 0);
        EndRoom();
    }

    // 9 Button door. A red button on a pillar in the middle of a 6 m pit, out of reach on
    // foot. Stick the axe in it and the door on the raised platform in the far corner
    // opens; the way there is a run of platforms along the north side. Walk through the
    // door and the axe comes flying back after you, squeezing under it as it shuts.
    static void Room09ButtonDoor()
    {
        BeginRoom("09 Button Door", 44, -14, 14, -6, 16);
        Floor(0, 10, -14, 14, 0);
        Floor(18, 22, -2, 2, 4);
        Floor(12, 14, 8, 12, 0.5f);
        Floor(17, 19, 8, 12, 1f);
        Floor(22, 24, 8, 12, 1.5f);
        Floor(27, 30, 8, 12, 1f);
        Floor(30, 44, -14, 14, 0);
        Floor(38, 44, -14, -4, 2);
        Stairs(10, -1, 12, 14, -6, 0);
        Stairs(30, +1, 12, 14, -6, 0);
        Glass("Target A", new Vector3(5, 3, -13.8f), 2, 2, alongX: false, runThrough: false);
        Glass("Target B", new Vector3(8.5f, 3, -13.8f), 2, 2, alongX: false, runThrough: false);

        GameObject button = Block("Button", 17.6f, 18, -1.5f, 1.5f, 0.5f, 3.5f);
        button.GetComponent<MeshRenderer>().sharedMaterial = wallRunMat;
        AxeButton axeButton = button.AddComponent<AxeButton>();
        axeButton.face = button.GetComponent<MeshRenderer>();

        GameObject door = MovingBlock("Axe Door", 43.6f, 44, -10, -6, 2, 2 + DoorH);
        AxeDoor axeDoor = door.AddComponent<AxeDoor>();
        axeDoor.buttons = new[] { axeButton };

        GameObject zone = Trigger("Axe Recall Zone", new Vector3(44.5f, 2, -10), new Vector3(47, 6, -6));
        zone.AddComponent<AxeRecallZone>().buttons = new[] { axeButton };

        Checkpoint("Button Door", 1.5f, 0, 0, 90);
        Tip("Tip Throw", 1, 5, -6, 6, 0, "THROW", "AXE", Orange,
            "Hold {primary} to charge a throw, {secondary} throws straight away.",
            "{axePickup}  calls it back. Try the glass targets.");
        Tip("Tip Button", 6, 9.5f, -6, 6, 0, "AXE BUTTON", "AXE", Orange,
            "Stick the axe in the red button to hold the door open.",
            "Walk through and the axe follows you.");
        Exit('E', -8, 2);
        EndRoom();
    }

    // 10 Pogo hall. Only orange surfaces bounce you. Drop onto a pad and pogo to a 4 m
    // ledge, drop further onto a deeper pad and pogo to 8 m, then climb an orange shaft by
    // bouncing off its walls to the exit 20 m up. Every pit has steps back to its ledge.
    static void Room10PogoHall()
    {
        BeginRoom("10 Pogo Hall", 48, -12, 12, -6, 28);
        Floor(0, 8, -12, 12, 0);
        Floor(8, 16, -12, 12, -4);
        Pogo("Pogo Pad 1", 8.5f, 15.5f, -5, 5, -4, -3.6f);
        Floor(16, 24, -12, 12, 4);
        Floor(24, 32, -12, 12, -6);
        Pogo("Pogo Pad 2", 24.5f, 31.5f, -5, 5, -6, -5.6f);
        Floor(32, 48, -12, 12, 8);
        Pogo("Pogo Wall West", 39.6f, 40, -4, 4, 10.6f, 24);
        Pogo("Pogo Wall East", 44, 44.4f, -4, 4, 8, 19);
        Floor(44.4f, 48, -12, 12, 20);
        ClimbBack(8, 10, 12, 0, -4);
        ClimbBack(24, 10, 12, 4, -6);
        Checkpoint("Pogo Hall", 1.5f, 0, 0, 90);
        Tip("Tip Pogo", 1, 6, -12, 12, 0, "POGO", "AXE", Orange,
            "Only orange surfaces bounce you.",
            "Swing at the orange pad as you land to bounce back up.");
        Tip("Tip Pogo Wall", 33, 38, -4, 4, 8, "POGO WALLS", "AXE", Orange,
            "Swing at an orange wall to bounce off it.",
            "Bounce side to side to climb the shaft.");
        Exit('E', 0, 20);
        EndRoom();
    }

    // 11 Glass run. Glass panes stand at the ends of platforms over a pit: smash one as you
    // reach the edge and it launches you across the gap. Then a slide down through three
    // panes close together, fast enough to break them just by running into them.
    static void Room11GlassRun()
    {
        BeginRoom("11 Glass Run", 72, -8, 8, -10, 12);
        Floor(0, 8, -8, 8, 0);
        Floor(14, 22, -8, 8, 0);
        Floor(30, 36, -8, 8, 1);
        Floor(40, 48, -8, 8, 3);
        Ramp("Glass Slide", 48, 3, 60, -1, -8, 8);
        Floor(60, 72, -8, 8, -1);
        Glass("Edge Glass 1", new Vector3(7.7f, 2, 0), 16, 4, alongX: true, runThrough: true);
        Glass("Edge Glass 2", new Vector3(21.7f, 2, 0), 16, 4, alongX: true, runThrough: true);
        Glass("Slide Glass 1", new Vector3(61, 3, 0), 16, 8, alongX: true, runThrough: true);
        Glass("Slide Glass 2", new Vector3(63.5f, 3, 0), 16, 8, alongX: true, runThrough: true);
        Glass("Slide Glass 3", new Vector3(66, 3, 0), 16, 8, alongX: true, runThrough: true);
        ClimbBack(8, 6, 8, 0, -10);
        ClimbBack(22, 6, 8, 0, -10);
        ClimbBack(36, 6, 8, 1, -10);
        Checkpoint("Glass Run", 1.5f, 0, 0, 90);
        Tip("Tip Smash", 1, 5, -8, 8, 0, "SMASH", "AXE", Orange,
            "Swing at glass as you reach the edge and it launches you across.");
        Tip("Tip Run Through", 42, 46, -8, 8, 3, "RUN-THROUGH", "AXE", Orange,
            "Fast enough, and glass breaks when you run into it.");
        Exit('E', 0, -1);
        EndRoom();
    }

    // ================================================================ Act 3: grapple

    // 12 The Well. Drop 28 m into a well with the grapple at the bottom, then zip up a
    // ladder of lit points with a ledge under each, round the walls to the exit.
    static void Room12TheWell()
    {
        BeginRoom("12 The Well", 24, -12, 12, -28, 8);
        Floor(0, 3, -4, 4, 0);
        Block("Plinth", 11, 13, -1, 1, -28, -27);
        Pickup("Grapple Pickup", AbilityPickup.Ability.Grapple, new Vector3(12, -25.6f, 0), "Zip from ledge to ledge to climb out.");
        Ledge("Ledge 1", 6, 18, -12, -9, -20);
        Ledge("Ledge 2", 21, 24, -8, 8, -12);
        Ledge("Ledge 3", 6, 18, 9, 12, -4);
        Ledge("Exit Ledge", 21, 24, -10, -2, 4);
        GPoint("Point 1", 12, -17.5f, -10.5f);
        GPoint("Point 2", 22.5f, -9.5f, 0);
        GPoint("Point 3", 12, -1.5f, 10.5f);
        GPoint("Point Exit", 22, 6.5f, -6);
        Checkpoint("Well", 8, 0, -28, 90);
        Exit('E', -6, 4);
        EndRoom();
    }

    // 13 Zip canyon. Zip island to island over a 14 m canyon, then zip to the point beside
    // the red wall and drop straight into a wall run along it to the exit ledge. Fall and
    // the steps in the canyon take you back up to the start to try again.
    static void Room13ZipCanyon()
    {
        BeginRoom("13 Zip Canyon", 64, -12, 12, -14, 14);
        Floor(0, 8, -12, 12, 0);
        Floor(18, 24, -4, 4, 2);
        Floor(34, 40, 2, 10, 4);
        Floor(56, 64, -12, 12, 4);
        WallRun("Canyon Wall", 42, 58, 11.5f, 12, -14, 12);
        GPoint("Zip 1", 21, 6.5f, 0);
        GPoint("Zip 2", 37, 8.5f, 6);
        GPoint("Zip To Wall Run", 44, 9, 9.5f);
        ClimbBack(8, -12, -9, 0, -14);
        Checkpoint("Zip Canyon", 1.5f, 0, 0, 90);
        Tip("Tip Zip Wall Run", 34.5f, 39.5f, 2, 10, 4, "ZIP TO WALL RUN", "GRAPPLE", Green,
            "Zip to a point beside a red wall to land straight in a wall run.");
        Exit('E', 0, 4);
        EndRoom();
    }

    // 14 Swing gorge. 90 m over a 16 m deep gorge. Each swing point hangs 12 m above the
    // ledges, halfway across its gap, 14 m apart - one rope length - so letting go at the
    // top of one swing carries you to the next. A wall hanging 6 m above the ledges near the
    // end needs a reel in to clear. Fall and the steps take you back up to the ledge you
    // swung from.
    static void Room14SwingGorge()
    {
        BeginRoom("14 Swing Gorge", 90, -10, 10, -16, 24);
        Floor(0, 6, -10, 10, 0);
        Floor(20, 26, -10, 10, 0);
        Floor(54, 60, -10, 10, 0);
        Floor(82, 90, -10, 10, 0);
        Block("Gorge Wall", 66, 67, -10, 10, -4, 6);
        GPoint("Swing 1", 13, 12, 0);
        GPoint("Swing 2", 33, 12, 0);
        GPoint("Swing 3", 47, 12, 0);
        GPoint("Swing 4", 64, 14, 0);
        GPoint("Swing 5", 76, 12, 0);
        ClimbBack(6, -10, -7, 0, -16);
        ClimbBack(26, -10, -7, 0, -16);
        ClimbBack(60, -10, -7, 0, -16);
        Checkpoint("Swing Gorge", 1.5f, 0, 0, 90);
        Tip("Tip Swing", 1, 5, -10, 10, 0, "SWING", "GRAPPLE", Green,
            "{slot2}  then hold  {primary}  to swing. Let go at the top of the swing.",
            "{jump}  jumps off with a boost.");
        Tip("Tip Reel", 55, 59, -10, 10, 0, "REEL", "GRAPPLE", Green,
            "Scroll to reel in and lift yourself over the wall.");
        Exit('E', 0, 0);
        EndRoom();
    }

    // 15 Anchor chasm. A 36 m chasm with nothing to grab. The only way over is to throw
    // the axe into the brown board above the exit and zip to it. Fall in and it's a long
    // climb back up to where you started.
    static void Room15AnchorChasm()
    {
        BeginRoom("15 Anchor Chasm", 56, -15, 15, -24, 30);
        Floor(0, 10, -15, 15, 0);
        Floor(46, 56, -15, 15, 0);
        Target("Target Board", 55.6f, 56, -4, 4, 6, 12);
        PointLight("Board Light", new Vector3(52, 10, 0), new Color(1f, 0.8f, 0.5f), 12, 2f);
        ClimbBack(10, -15, -12, 0, -24);
        Checkpoint("Anchor Chasm", 1.5f, 0, 0, 90);
        Tip("Tip Anchor", 1, 6, -15, 15, 0, "ANCHOR", "GRAPPLE", Green,
            "Nothing to grab? Throw the axe into the board and  {zip}  to it.");
        Exit('E', 0, 0);
        EndRoom();
    }

    // ================================================================ Act 4: the Ascent

    // 16 The Ascent. One long run through everything: slide, pipe, a glass launch, a pogo
    // pad, two wall runs, a zip up, a swing, and a 10 m wall you anchor up. The goal is on
    // the summit; grabbing it gives your time and opens the portal into the level.
    // Shortcut for the good: from the zip ledge, throw the axe straight into the summit
    // board and zip up, skipping the swing.
    static void Room16TheAscent()
    {
        BeginRoom("16 The Ascent", 150, -15, 15, -20, 30);
        Floor(0, 8, -15, 15, 0);
        Ramp("Slide", 8, 0, 28, -6, -15, 15);
        Floor(28, 36, -15, 15, -6);
        Block("Pipe", 31, 32, -15, 15, -4.4f, -3.8f);
        Glass("Launch Glass", new Vector3(35.7f, -4, 0), 30, 4, alongX: true, runThrough: true);
        Floor(44, 52, -15, 15, -6);
        Floor(52, 60, -15, 15, -12);
        Pogo("Pogo Pad", 52.5f, 59.5f, -6, 6, -12, -11.6f);
        Floor(60, 66, -15, 15, -2);
        WallRun("Ascent Wall A", 65, 80, 4, 4.5f, -8, 6);
        WallRun("Ascent Wall B", 78, 93, -4.5f, -4, -8, 6);
        Floor(92, 98, -15, 15, -2);
        Floor(106, 112, -15, 15, 4);
        GPoint("Zip Up", 109, 7, 0);
        GPoint("Swing", 120, 16, 0);
        Floor(128, 134, -15, 15, 4);
        Floor(134, 150, -15, 15, 14);
        Target("Summit Board", 133.6f, 134, -4, 4, 10, 14);
        // every fall climbs back to the platform you left, so a miss costs a retry
        ClimbBack(36, -15, -12, -6, -20);
        ClimbBack(52, 12, 15, -6, -12);
        ClimbBack(66, -15, -12, -2, -20);
        ClimbBack(98, -15, -12, -2, -20);
        ClimbBack(112, -15, -12, 4, -20);
        Checkpoint("Ascent", 1.5f, 0, 0, 90);
        Checkpoint("Ascent Middle", 92.6f, 0, -2, 90);

        GameObject exitPortal = ExitPortal(new Vector3(146, 17.02f, 0));
        Goal(new Vector3(142, 15.6f, 0), exitPortal.GetComponent<Portal>());

        Tip("Tip Ascent", 1, 6, -15, 15, 0, "THE ASCENT", "FINAL", Gold,
            "Everything you've learned, start to finish. The goal's at the top.");
        EndRoom();
    }

    // ================================================================ rooms and chaining

    static void BeginRoom(string name, float length, float z0, float z1, float y0, float y1, bool entry = true)
    {
        room = new GameObject(name).transform;
        room.SetParent(root, true);
        room.SetPositionAndRotation(cursorPos, Quaternion.Euler(0f, cursorYaw, 0f));
        rL = length;
        rZ0 = z0;
        rZ1 = z1;
        rY0 = y0;
        rY1 = y1;
        rHasEntry = entry;
        exitSide = '\0';
    }

    static void Exit(char side, float at, float sill)
    {
        exitSide = side;
        exitAt = at;
        exitSill = sill;
    }

    // Builds the room's hull, the corridor out of it, and moves the cursor to the far end
    // of that corridor, turned to face the way it goes.
    static void EndRoom()
    {
        List<HullDoor> doors = new List<HullDoor>();
        if (rHasEntry)
        {
            doors.Add(new HullDoor { side = 'W', at = 0, sill = 0, exit = false });
        }
        if (exitSide != '\0')
        {
            doors.Add(new HullDoor { side = exitSide, at = exitAt, sill = exitSill, exit = true });
        }
        Hull("Room Hull", 0, rL, rY0, rY1, rZ0, rZ1, doors, open: null);

        if (exitSide == '\0')
        {
            return;
        }

        Vector3 doorPoint;
        Vector3 dir;
        float turn;
        switch (exitSide)
        {
            case 'N': doorPoint = new Vector3(exitAt, exitSill, rZ1); dir = Vector3.forward; turn = -90f; break;
            case 'S': doorPoint = new Vector3(exitAt, exitSill, rZ0); dir = Vector3.back; turn = 90f; break;
            default: doorPoint = new Vector3(rL, exitSill, exitAt); dir = Vector3.right; turn = 0f; break;
        }

        // exit light just inside the door: the way on is always the lit opening
        PointLight("Exit Light", doorPoint - dir * 1.5f + Vector3.up * (DoorH + 0.5f), ExitLight, 10, 2f);
        Corridor(doorPoint, dir);

        cursorPos = room.TransformPoint(doorPoint + dir * CorridorLength);
        cursorYaw += turn;
    }

    struct HullDoor
    {
        public char side;
        public float at, sill;
        public bool exit;
    }

    // An inverted-hull box from (x0, y0, z0) to (x1, y1, z1) in the room's frame with door
    // holes cut through its walls. Built outward-facing, then handed to RoomBuilder, which
    // flips it inside out and sets it up like any other room (Ground layer, non-convex
    // collider, two-sided shadows, static). "open" lists sides with no face at all
    // (W/E/S/N, D = floor, U = ceiling).
    static void Hull(string name, float x0, float x1, float y0, float y1, float z0, float z1,
                     List<HullDoor> doors, string open)
    {
        List<Vector3> verts = new List<Vector3>();
        List<Face> faces = new List<Face>();
        List<Face> lintels = new List<Face>();

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, bool lintel)
        {
            if ((a - b).sqrMagnitude < 1e-4f || (b - c).sqrMagnitude < 1e-4f)
            {
                return;
            }
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                (b, d) = (d, b);
            }
            int i = verts.Count;
            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            verts.Add(d);
            Face f = new Face(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            faces.Add(f);
            if (lintel)
            {
                lintels.Add(f);
            }
        }

        bool Has(char side) => open == null || open.IndexOf(side) < 0;

        if (Has('D'))
        {
            Quad(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1), Vector3.down, false);
        }
        if (Has('U'))
        {
            Quad(new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), Vector3.up, false);
        }

        // a wall as rectangles in (u along the wall, y), with each door's hole left out
        void WallFace(char side, float u0, float u1, System.Func<float, float, Vector3> at, Vector3 outward)
        {
            if (!Has(side))
            {
                return;
            }
            List<HullDoor> mine = doors.FindAll(d => d.side == side);
            mine.Sort((p, q) => p.at.CompareTo(q.at));

            void Rect(float a0, float a1, float b0, float b1, bool lintel)
            {
                if (a1 - a0 < 0.01f || b1 - b0 < 0.01f)
                {
                    return;
                }
                Quad(at(a0, b0), at(a1, b0), at(a1, b1), at(a0, b1), outward, lintel);
            }

            float cursor = u0;
            foreach (HullDoor d in mine)
            {
                float o0 = d.at - DoorW * 0.5f, o1 = d.at + DoorW * 0.5f;
                Rect(cursor, o0, y0, y1, false);
                Rect(o0, o1, y0, d.sill, false);
                Rect(o0, o1, d.sill + DoorH, y1, d.exit);
                cursor = o1;
            }
            Rect(cursor, u1, y0, y1, false);
        }

        WallFace('W', z0, z1, (u, y) => new Vector3(x0, y, u), Vector3.left);
        WallFace('E', z0, z1, (u, y) => new Vector3(x1, y, u), Vector3.right);
        WallFace('S', x0, x1, (u, y) => new Vector3(u, y, z0), Vector3.back);
        WallFace('N', x0, x1, (u, y) => new Vector3(u, y, z1), Vector3.forward);

        ProBuilderMesh pb = ProBuilderMesh.Create(verts, faces);
        GameObject go = pb.gameObject;
        go.name = name;
        go.transform.SetParent(room, false);
        pb.SetMaterial(faces, hullMat);
        if (lintels.Count > 0)
        {
            pb.SetMaterial(lintels, exitMat);
        }
        pb.ToMesh();
        pb.Refresh();
        RoomBuilder.ConvertToRoom(pb, groundLayer);
        built++;
    }

    // 6 m hull from a door out along "dir" (in the room's frame), open at both ends.
    static void Corridor(Vector3 doorPoint, Vector3 dir)
    {
        Vector3 far = doorPoint + dir * CorridorLength;
        float y0 = doorPoint.y, y1 = doorPoint.y + DoorH;
        bool alongX = Mathf.Abs(dir.x) > 0.5f;
        List<HullDoor> none = new List<HullDoor>();
        if (alongX)
        {
            Hull("Corridor", Mathf.Min(doorPoint.x, far.x), Mathf.Max(doorPoint.x, far.x), y0, y1,
                 doorPoint.z - DoorW * 0.5f, doorPoint.z + DoorW * 0.5f, none, open: "WE");
        }
        else
        {
            Hull("Corridor", doorPoint.x - DoorW * 0.5f, doorPoint.x + DoorW * 0.5f, y0, y1,
                 Mathf.Min(doorPoint.z, far.z), Mathf.Max(doorPoint.z, far.z), none, open: "SN");
        }
    }

    // ================================================================ pieces

    // walkable block, solid from the room's floor up to "top", so pits have real sides
    static GameObject Floor(float x0, float x1, float z0, float z1, float top)
    {
        return Piece("Floor", new Vector3(x0, rY0, z0), new Vector3(x1, top, z1), floorMat);
    }

    // a shelf on a wall, 0.5 m thick
    static GameObject Ledge(string name, float x0, float x1, float z0, float z1, float top)
    {
        return Piece(name, new Vector3(x0, top - 0.5f, z0), new Vector3(x1, top, z1), floorMat);
    }

    static GameObject Block(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        return Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), obstacleMat);
    }

    static void WallRun(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        GameObject go = Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), wallRunMat);
        go.layer = wallRunLayer;
        go.tag = "WallRun";
    }

    // orange, and the axe bounces you off it
    static void Pogo(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        GameObject go = Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), pogoMat);
        go.AddComponent<PogoSurface>();
    }

    // brown: something to throw the axe into
    static void Target(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), targetMat);
    }

    // 2 m steps (or less) climbing from "bottom" to "top" at the face x = faceX, in a strip
    // from z0 to z1. dir +1: the steps sit at x < faceX and climb as x increases; dir -1:
    // they sit at x > faceX and climb as x decreases.
    static void Stairs(float faceX, int dir, float z0, float z1, float bottom, float top)
    {
        int risers = Mathf.CeilToInt((top - bottom) / Rise - 0.001f);
        float rise = (top - bottom) / risers;
        for (int k = risers - 1; k >= 1; k--)
        {
            int fromFace = risers - 1 - k;
            float near = faceX - dir * (fromFace * 1.5f);
            float farX = near - dir * 1.5f;
            Piece("Step", new Vector3(Mathf.Min(near, farX), rY0, z0),
                  new Vector3(Mathf.Max(near, farX), bottom + k * rise, z1), floorMat);
        }
    }

    // steps in the gap right after the platform ending at faceX, back up onto it
    static void ClimbBack(float faceX, float z0, float z1, float top, float bottom = float.NaN)
    {
        Stairs(faceX, -1, z0, z1, float.IsNaN(bottom) ? rY0 : bottom, top);
    }

    // a slab whose top runs from (xa, ya) to (xb, yb) along x
    static void Ramp(string name, float xa, float ya, float xb, float yb, float z0, float z1)
    {
        const float thick = 0.5f;
        float dx = xb - xa, dy = yb - ya;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        Quaternion rot = Quaternion.Euler(0f, 0f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);

        ProBuilderMesh pb = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(len, thick, z1 - z0));
        GameObject go = pb.gameObject;
        go.name = name;
        go.transform.SetParent(room, false);
        go.transform.localRotation = rot;
        go.transform.localPosition = new Vector3((xa + xb) * 0.5f, (ya + yb) * 0.5f, (z0 + z1) * 0.5f)
                                     - rot * Vector3.up * (thick * 0.5f);
        FinishPiece(pb, floorMat, true);
    }

    // A ProBuilder cube between two corners, set up like the project's other level pieces:
    // Ground layer, non-convex mesh collider, static.
    static GameObject Piece(string name, Vector3 min, Vector3 max, Material mat, bool isStatic = true)
    {
        Vector3 size = max - min;
        if (size.x < 0.01f || size.y < 0.01f || size.z < 0.01f)
        {
            return null;
        }

        ProBuilderMesh pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size);
        GameObject go = pb.gameObject;
        go.name = name;
        go.transform.SetParent(room, false);
        go.transform.localPosition = (min + max) * 0.5f;
        FinishPiece(pb, mat, isStatic);
        return go;
    }

    // not static: it moves
    static GameObject MovingBlock(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        return Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), obstacleMat, isStatic: false);
    }

    static void FinishPiece(ProBuilderMesh pb, Material mat, bool isStatic)
    {
        pb.SetMaterial(pb.faces, mat);
        pb.ToMesh();
        pb.Refresh();

        GameObject go = pb.gameObject;
        go.layer = groundLayer;

        MeshCollider col = go.AddComponent<MeshCollider>();
        col.convex = false;
        col.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;

        if (isStatic)
        {
            GameObjectUtility.SetStaticEditorFlags(go,
                StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.OccluderStatic | StaticEditorFlags.ReflectionProbeStatic);
        }
        built++;
    }

    // ---------------------------------------------------------------- gameplay pieces

    static void GPoint(string name, float x, float y, float z)
    {
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(grapplePointPrefab, room.gameObject.scene);
        go.name = name;
        go.transform.SetParent(room, false);
        go.transform.localPosition = new Vector3(x, y, z);
        // lit, so the points read from across the room
        Light light = new GameObject("Light").AddComponent<Light>();
        light.transform.SetParent(go.transform, false);
        light.type = LightType.Point;
        light.color = Green;
        light.range = 6f;
        light.intensity = 2f;
        built++;
    }

    static void PointLight(string name, Vector3 local, Color color, float range, float intensity)
    {
        Light light = new GameObject(name).AddComponent<Light>();
        light.transform.SetParent(room, false);
        light.transform.localPosition = local;
        light.type = LightType.Point;
        light.color = color;
        light.range = range;
        light.intensity = intensity;
        light.shadows = LightShadows.None;
    }

    // The BreakableWall prefab (a 10.3 x 10.2 m slab with its pivot off to one side),
    // scaled to size, centred on "centre", and given the glass material.
    // alongX = you go through it travelling along x.
    static void Glass(string name, Vector3 centre, float width, float height, bool alongX, bool runThrough)
    {
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(breakablePrefab, room.gameObject.scene);
        go.name = name;
        go.transform.SetParent(room, false);

        Bounds local = LocalMeshBounds(go);
        Vector3 scale = new Vector3(1f, height / local.size.y, width / local.size.z);
        Quaternion rot = alongX ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
        go.transform.localScale = scale;
        go.transform.localRotation = rot;
        go.transform.localPosition = centre - rot * Vector3.Scale(local.center, scale);

        MeshRenderer r = go.GetComponent<MeshRenderer>();
        if (r != null)
        {
            r.sharedMaterial = glassMat;
        }

        BreakableWall wall = go.GetComponent<BreakableWall>();
        if (wall != null)
        {
            SerializedObject so = new SerializedObject(wall);
            so.FindProperty("breakOnHighSpeed").boolValue = runThrough;
            so.ApplyModifiedProperties();
        }
        built++;
    }

    static Bounds LocalMeshBounds(GameObject go)
    {
        ProBuilderMesh pb = go.GetComponent<ProBuilderMesh>();
        if (pb != null && pb.positions.Count > 0)
        {
            Bounds b = new Bounds(pb.positions[0], Vector3.zero);
            foreach (Vector3 p in pb.positions)
            {
                b.Encapsulate(p);
            }
            return b;
        }
        // the prefab's measured shape, in case it ever stops being a ProBuilder mesh
        return new Bounds(new Vector3(1.885f, -0.28f, 1.915f), new Vector3(0.91f, 10.18f, 10.31f));
    }

    static void Pickup(string name, AbilityPickup.Ability ability, Vector3 pos, string hint)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(room, false);
        go.transform.localPosition = pos;
        go.AddComponent<SphereCollider>().isTrigger = true;
        AbilityPickup pickup = go.AddComponent<AbilityPickup>();
        pickup.ability = ability;
        pickup.levelHint = hint;
        built++;
    }

    static GameObject Trigger(string name, Vector3 min, Vector3 max)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(room, false);
        go.transform.localPosition = (min + max) * 0.5f;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = max - min;
        built++;
        return go;
    }

    // a tip zone the full height of a doorway, from x0 to x1 and z0 to z1, on "floor"
    static void Tip(string name, float x0, float x1, float z0, float z1, float floor,
                    string title, string subtitle, Color accent, params string[] lines)
    {
        TipBox(name, new Vector3(x0, floor, z0), new Vector3(x1, floor + 4f, z1), title, subtitle, accent, lines);
    }

    static void TipBox(string name, Vector3 min, Vector3 max, string title, string subtitle, Color accent, params string[] lines)
    {
        GameObject go = Trigger(name, min, max);
        TutorialTip tip = go.AddComponent<TutorialTip>();
        tip.title = title;
        tip.subtitle = subtitle;
        tip.accent = accent;
        tip.lines = lines;
    }

    // You come back standing here, facing "yaw" in the room's frame (90 = forward).
    static void Checkpoint(string name, float x, float z, float floor, float yaw)
    {
        GameObject go = new GameObject("Checkpoint " + name);
        go.transform.SetParent(room, false);
        go.transform.localPosition = new Vector3(x, floor + Eye, z);
        go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = new Vector3(0f, 0.6f, 0f);
        box.size = new Vector3(DoorW + 2f, 3.5f, 1.2f);
        go.AddComponent<Checkpoint>();
        built++;
    }

    // Closed until the goal is grabbed, then it opens floating in front of the player and
    // leads to the TutorialArrival portal in Grapple Scene.
    static GameObject ExitPortal(Vector3 local)
    {
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(portalPrefab, room.gameObject.scene);
        go.name = "Tutorial Exit Portal";
        go.transform.SetParent(room, false);
        go.transform.localPosition = local;
        go.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
        SerializedObject so = new SerializedObject(go.GetComponent<Portal>());
        so.FindProperty("portalId").stringValue = "TutorialExit";
        so.FindProperty("linkedSceneName").stringValue = "Grapple Scene";
        so.FindProperty("linkedPortalId").stringValue = "TutorialArrival";
        so.FindProperty("portalSize").vector2Value = new Vector2(4.5f, 6f);
        so.FindProperty("startClosed").boolValue = true;
        so.FindProperty("openPlacement").enumValueIndex = (int)Portal.OpenPlacement.InFrontOfPlayer;
        so.ApplyModifiedProperties();
        built++;
        return go;
    }

    // The same LevelGoal as Grapple Scene: stops the clock, ranks the time, opens the exit.
    // Rank times are for a first run of about ten minutes.
    static void Goal(Vector3 local, Portal exit)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Tutorial Goal";
        go.transform.SetParent(room, false);
        go.transform.localPosition = local;
        go.transform.localScale = Vector3.one * 0.8f;
        go.GetComponent<MeshRenderer>().sharedMaterial = exitMat;
        go.GetComponent<BoxCollider>().isTrigger = true;
        LevelGoal goal = go.AddComponent<LevelGoal>();
        goal.exitPortal = exit;
        goal.sTime = 360f;
        goal.aTime = 480f;
        goal.bTime = 600f;
        goal.cTime = 780f;
        goal.dTime = 960f;
        PointLight("Goal Light", local + Vector3.up * 2f, Gold, 10, 3f);
        built++;
    }
}
