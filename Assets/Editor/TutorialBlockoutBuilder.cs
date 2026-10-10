using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UnityEngine.SceneManagement;

// Grey-box of the 16-room tutorial from the level design doc, built out of ProBuilder
// cubes the same way the existing obstacles are: Ground layer, non-convex mesh colliders,
// Gridbox prototype materials, wall-run walls red on the WallRun layer and tag, and the
// project's own Grapple Point, BreakableWall and Portal prefabs.
//
// USE: Tools > Tutorial Blockout > Build Blockout Scene. The first run copies Mirror
// Grapple Scene to "Tutorial Blockout" (player, camera, portals and lighting come with
// it), clears the old tutorial geometry out of the copy and builds the new layout under
// one "Tutorial Blockout" object. Running it again rebuilds that object from scratch, so
// tune the numbers below and rerun rather than hand-editing what it made - or stop using
// the builder once you start detailing by hand.
//
// Everything is placed in metres relative to the root at (1900, 0, 0), the same far-off
// spot the tutorial already uses so it never overlaps Grapple Scene when both are loaded.
// x runs along Act 1, the route then turns back along -x for Act 2 and out along +x again
// for Act 3, with the Gauntlet at the end.
public static class TutorialBlockoutBuilder
{
    const string SourceScene = "Assets/Scenes/Mirror Grapple Scene.unity";
    const string BlockoutScene = "Assets/Scenes/Tutorial Blockout.unity";
    const string RootName = "Tutorial Blockout";
    const string Materials = "Assets/Materials/Thirdparty/Ciathyza/Gridbox Prototype Materials/Materials/URP/";
    static readonly Vector3 Origin = new Vector3(1900f, 0f, 0f);

    const float T = 0.5f;          // wall, floor and ceiling thickness
    const float DoorW = 4f;
    const float DoorH = 4f;
    const float Eye = 1.25f;       // player root height above the floor

    static readonly Color Blue = new Color(0.45f, 0.85f, 1f, 1f);
    static readonly Color Orange = new Color(1f, 0.62f, 0.2f, 1f);
    static readonly Color Green = new Color(0.45f, 1f, 0.55f, 1f);
    static readonly Color Gold = new Color(1f, 0.85f, 0.3f, 1f);

    static Material floorMat, wallMat, obstacleMat, wallRunMat, exitMat;
    static int groundLayer, wallRunLayer;
    static GameObject grapplePointPrefab, breakablePrefab;
    static Transform room;          // the room being built
    static float roomBottom;        // its lowest floor, solid floors are filled down to here
    static int built;

    // ---------------------------------------------------------------- menu

    [MenuItem("Tools/Tutorial Blockout/Build Blockout Scene")]
    static void BuildFromMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        string result = Build();
        EditorUtility.DisplayDialog("Tutorial Blockout", result, "OK");
    }

    /// For -batchmode -executeMethod TutorialBlockoutBuilder.BuildFromCommandLine
    public static void BuildFromCommandLine()
    {
        Debug.Log("[TutorialBlockout] " + Build());
    }

    static string Build()
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(BlockoutScene) == null)
        {
            if (!AssetDatabase.CopyAsset(SourceScene, BlockoutScene))
            {
                return "Couldn't copy " + SourceScene + " to " + BlockoutScene + ".";
            }
            AddToBuildSettings(BlockoutScene);
        }

        Scene scene = EditorSceneManager.OpenScene(BlockoutScene, OpenSceneMode.Single);
        if (!LoadAssets(out string problem))
        {
            return problem;
        }

        ClearOldLayout(scene);

        GameObject rootGo = new GameObject(RootName);
        rootGo.transform.position = Origin;
        built = 0;

        BuildAct1(rootGo.transform);
        BuildAct2(rootGo.transform);
        BuildAct3(rootGo.transform);
        BuildGauntlet(rootGo.transform);
        PlacePlayerAtStart(scene);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        return $"Built the tutorial blockout in {BlockoutScene}: {built} pieces in 16 rooms.\n\n" +
               "Press Play in this scene to try it. Rerunning the menu rebuilds the " +
               $"'{RootName}' object from scratch, so hand edits inside it are lost.";
    }

    static void AddToBuildSettings(string path)
    {
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (EditorBuildSettingsScene s in scenes)
        {
            if (s.path == path)
            {
                return;
            }
        }
        scenes.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    static bool LoadAssets(out string problem)
    {
        problem = null;
        floorMat = LoadMaterial("Prototype_512x512_Grey1");
        wallMat = LoadMaterial("Prototype_512x512_Grey3");
        obstacleMat = LoadMaterial("Prototype_512x512_Blue1");
        wallRunMat = LoadMaterial("Prototype_512x512_Red");
        exitMat = LoadMaterial("Prototype_512x512_Yellow");

        groundLayer = LayerMask.NameToLayer("Ground");
        wallRunLayer = 8;
        if (groundLayer < 0)
        {
            problem = "There's no 'Ground' layer, which the player's ground check needs.";
            return false;
        }

        grapplePointPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Grapple Point.prefab");
        breakablePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/BreakableWall.prefab");
        if (grapplePointPrefab == null || breakablePrefab == null)
        {
            problem = "Couldn't find Assets/Prefabs/Grapple Point.prefab or BreakableWall.prefab.";
            return false;
        }
        return true;
    }

    static Material LoadMaterial(string name)
    {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(Materials + name + ".mat");
        return m != null ? m : BuiltinMaterials.defaultMaterial;
    }

    // The copy brings the old one-long-room tutorial with it. Its pieces are cleared out,
    // everything else (player, cameras, portals, lights, JuiceFX...) stays.
    static void ClearOldLayout(Scene scene)
    {
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            string n = go.name;
            bool oldPiece = n == RootName
                || n == "Cube" || (n.StartsWith("Cube (") && n.EndsWith(")"))
                || n.StartsWith("Grapple Point")
                || n == "Axe Pickup" || n == "Grapple Pickup" || n == "Axe Wall"
                // a loose copy of the thrown-axe prefab left in the scene; the axe itself
                // uses the prefab asset, and this one would sit inside the Vaults room
                || n == "AxeThrowPrefab";
            if (oldPiece)
            {
                Object.DestroyImmediate(go);
            }
        }
    }

    static void PlacePlayerAtStart(Scene scene)
    {
        Vector3 spawn = Origin + new Vector3(3f, Eye, 0f);
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

        // Portal B, the way into Grapple Scene, moves onto the Gauntlet's island.
        foreach (Portal p in Object.FindObjectsByType<Portal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p.gameObject.scene != scene || p.portalId != "PortalB")
            {
                continue;
            }
            p.transform.SetPositionAndRotation(Origin + new Vector3(353f, 36f + 3.02f, 66f), Quaternion.Euler(0f, -90f, 0f));
            SerializedObject so = new SerializedObject(p);
            so.FindProperty("portalSize").vector2Value = new Vector2(4.5f, 6f);
            so.ApplyModifiedProperties();
        }
    }

    // ---------------------------------------------------------------- Act 1: feet only

    static void BuildAct1(Transform root)
    {
        // 1 Arrival, 16 x 16 x 8. The arrival portal opens 5 m ahead of the spawn.
        Room(root, "01 Arrival", 0f);
        Shell(0, 16, -8, 8, 0, 8, D('E', 0, 0, exit: true));
        Floor(0, 16, -8, 8, 0);
        Checkpoint("Start", 3, 0, 0, 90);
        Tip("Tip Move", 1, 8, -4, 4, 0, "MOVE", "MOVEMENT", Blue,
            "{move}  Move, mouse to look.",
            "Keep moving and you get faster. Stopping throws your speed away.");
        CorridorX(root, "C1", 16, 20, 0, 0);

        // 2 Run lane, 72 x 8. Ditches 2, 3 and 4 m wide and 1 m deep you can climb out of,
        // a 6 m gap over a pit that needs a run-up, then three gaps back to back.
        Room(root, "02 Run Lane", -5f);
        Shell(20, 92, -4, 4, -5, 6, D('W', 0, 0), D('E', 0, 0, exit: true));
        Floor(20, 30, -4, 4, 0);
        Floor(30, 32, -4, 4, -1);
        Floor(32, 40, -4, 4, 0);
        Floor(40, 43, -4, 4, -1);
        Floor(43, 52, -4, 4, 0);
        Floor(52, 56, -4, 4, -1);
        Floor(56, 62, -4, 4, 0);
        Floor(62, 68, -4, 4, -4);
        FallReset("Pit Reset", 62, 68, -4, 4, -4, -3);
        Floor(68, 74, -4, 4, 0);
        Floor(74, 77, -4, 4, -1.5f);
        Floor(77, 80, -4, 4, 0);
        Floor(80, 83, -4, 4, -1.5f);
        Floor(83, 86, -4, 4, 0);
        Floor(86, 89, -4, 4, -1.5f);
        Floor(89, 92, -4, 4, 0);
        Checkpoint("Run Lane", 20.6f, 0, 0, 90);
        Checkpoint("Run Lane Gaps", 68.6f, 0, 0, 90);
        Tip("Tip Jump", 21, 25, -4, 4, 0, "JUMP", "MOVEMENT", Blue,
            "{jump}  Jump. Hold it to go higher, tap it for a hop.");
        CorridorX(root, "C2", 92, 96, 0, 0);

        // 3 Vaults, 24 x 24. A 0.5, 1.0 and 1.8 m block in a line, then a long 1.8 m block
        // that runs to the exit, which is 1.8 m up. A side lane of mixed blocks is the test.
        Room(root, "03 Vaults", -1f);
        Shell(96, 120, -12, 12, -1, 7, D('W', 0, 0), D('E', 0, 1.8f, exit: true));
        Floor(96, 120, -12, 12, 0);
        Obstacle("Vault 0.5", 100, 101, -4, 4, 0, 0.5f);
        Obstacle("Vault 1.0", 105, 106, -4, 4, 0, 1.0f);
        Obstacle("Vault 1.8", 110, 111, -4, 4, 0, 1.8f);
        Obstacle("Vault Run To Exit", 113, 120, -2, 2, 0, 1.8f);
        Obstacle("Zigzag 1.0", 99, 100, -10, -6, 0, 1.0f);
        Obstacle("Zigzag 0.5", 102, 103, -10, -6, 0, 0.5f);
        Obstacle("Zigzag 1.8", 105, 106, -10, -6, 0, 1.8f);
        Obstacle("Zigzag 1.0 B", 108, 109, -10, -6, 0, 1.0f);
        Obstacle("Zigzag 0.5 B", 111, 112, -10, -6, 0, 0.5f);
        Checkpoint("Vaults", 96.6f, 0, 0, 90);
        Tip("Tip Vault", 96.5f, 99, -4, 4, 0, "VAULT", "MOVEMENT", Blue,
            "Run straight at a ledge to vault it. No need to jump.");
        CorridorX(root, "C3", 120, 124, 0, 1.8f);

        // 4 Slides, 40 x 8. A 10 degree slope with two pipes 1.3 m up you have to slide
        // under, a third on the flat, then a 1 m ledge with a platform 2.2 m above it -
        // too high to vault, so only a slide launch off the ledge gets you up.
        Room(root, "04 Slides", -3f);
        Shell(124, 164, -4, 4, -3, 9, D('W', 0, 1.8f), D('E', 0, 1.5f, exit: true));
        Ramp("Slope", 124, 1.8f, 144, -1.7f, -4, 4, floorMat);
        Floor(144, 152, -4, 4, -1.7f);
        Obstacle("Pipe 1", 129.5f, 130.5f, -4, 4, SlopeY(130) + 1.3f, SlopeY(130) + 1.8f);
        Obstacle("Pipe 2", 135.5f, 136.5f, -4, 4, SlopeY(136) + 1.3f, SlopeY(136) + 1.8f);
        Obstacle("Pipe 3", 147, 148, -4, 4, -0.4f, 0.1f);
        Obstacle("Launch Ledge", 152, 153, -4, 4, -1.7f, -0.7f);
        Floor(153, 164, -4, 4, 1.5f);
        Checkpoint("Slides", 122.5f, 0, 1.8f, 90);
        Tip("Tip Slide", 124.5f, 127, -4, 4, 1.8f, "SLIDE", "MOVEMENT", Blue,
            "{slide}  Slide. Fit under low gaps and pick up speed downhill.");
        Tip("Tip Slide Launch", 144, 148, -4, 4, -1.7f, "SLIDE LAUNCH", "MOVEMENT", Blue,
            "Slide into a low ledge fast and it launches you up.");
        CorridorX(root, "C4", 164, 168, 0, 1.5f);

        // 5 Wall runs, 60 x 14. One wall beside an 8 m gap over a 1 m ditch (safe), one
        // beside a 14 m gap over a pit, then two walls on alternate sides.
        Room(root, "05 Wall Runs", -3f);
        Shell(168, 228, -7, 7, -3, 11.5f, D('W', 0, 1.5f), D('E', 0, 1.5f, exit: true));
        Floor(168, 173, -7, 7, 1.5f);
        Floor(173, 181, -7, 7, 0.5f);
        Floor(181, 186, -7, 7, 1.5f);
        Floor(186, 200, -7, 7, -2.5f);
        Floor(200, 204, -7, 7, 1.5f);
        Floor(204, 220, -7, 7, -2.5f);
        Floor(220, 228, -7, 7, 1.5f);
        FallReset("Pit Reset A", 186, 200, -7, 7, -2.5f, -1.5f);
        FallReset("Pit Reset B", 204, 220, -7, 7, -2.5f, -1.5f);
        WallRunWall("Wall Run Teach", 172, 183, 2.5f, 3f, 0, 7);
        WallRunWall("Wall Run Practise", 185, 201, -3f, -2.5f, -2.5f, 7);
        WallRunWall("Wall Run Twist A", 203, 213, 2.5f, 3f, -2.5f, 7);
        WallRunWall("Wall Run Twist B", 211, 221, -3f, -2.5f, -2.5f, 7);
        Checkpoint("Wall Runs", 168.6f, 0, 1.5f, 90);
        Checkpoint("Wall Runs Twist", 200.6f, 0, 1.5f, 90);
        Tip("Tip Wall Run", 168.5f, 171, -7, 7, 1.5f, "WALL RUN", "MOVEMENT", Blue,
            "Jump alongside a red wall and hold forward to run along it.",
            "{jump}  jumps off it.");
        CorridorX(root, "C5", 228, 232, 0, 1.5f);

        // 6 The shaft, 5 x 5 x 20. Kick up between the walls: a wall kick pushes you 8 m/s
        // across and 7 m/s up, so each crossing of the shaft is worth about 3 m. Ledges on
        // alternating walls every 3 to 3.5 m, the exit 16 m up and close enough above the
        // last ledge to vault into. A yellow stripe runs up to the exit so players look up.
        Room(root, "06 The Shaft", 1f);
        Shell(232, 237, -2.5f, 2.5f, 1f, 21.5f, D('W', 0, 1.5f), D('E', 0, 17.5f, exit: true));
        Floor(232, 237, -2.5f, 2.5f, 1.5f);
        Obstacle("Ledge 3m", 235.8f, 237, -2.5f, 2.5f, 4f, 4.5f);
        Obstacle("Ledge 6m", 232, 233.2f, -2.5f, 2.5f, 7f, 7.5f);
        Obstacle("Ledge 9.5m", 235.8f, 237, -2.5f, 2.5f, 10.5f, 11f);
        Obstacle("Ledge 12.5m", 232, 233.2f, -2.5f, 2.5f, 13.5f, 14f);
        Piece("Look Up Stripe", new Vector3(236.9f, 11.5f, -0.4f), new Vector3(237f, 17.5f, 0.4f), exitMat);
        Checkpoint("Shaft", 232.6f, 0, 1.5f, 90);
        Tip("Tip Wall Kick", 232.2f, 234, -2.5f, 2.5f, 1.5f, "WALL KICK", "MOVEMENT", Blue,
            "{jump}  in the air next to a wall kicks off it. Twice per jump.");
        TipBox("Tip Dart", new Vector3(232, 7.5f, -2.5f), new Vector3(233.2f, 10f, 2.5f), "DART", "MOVEMENT", Blue,
            "{dart}  just after a jump or kick darts forward.",
            "A dart gives you a wall kick back.");
        CorridorX(root, "C6", 237, 241, 0, 17.5f);

        // 7 The Run, 100 m. No cards: a long slide down with pipes, a vault, a ditch, a
        // slide launch, two wall runs over a pit and a vault up to the exit.
        Room(root, "07 The Run", -4f);
        Shell(241, 341, -5, 5, -4, 24, D('W', 0, 17.5f), D('E', 0, 5.2f, exit: true));
        Ramp("Long Slide", 241, 17.5f, 291, 0, -5, 5, floorMat);
        Obstacle("Pipe A", 254.5f, 255.5f, -5, 5, RunSlopeY(255) + 1.3f, RunSlopeY(255) + 1.8f);
        Obstacle("Pipe B", 271.5f, 272.5f, -5, 5, RunSlopeY(272) + 1.3f, RunSlopeY(272) + 1.8f);
        Floor(291, 300, -5, 5, 0);
        Obstacle("Vault 1.0", 295, 296, -5, 5, 0, 1.0f);
        Floor(300, 303, -5, 5, -1.5f);
        Floor(303, 308, -5, 5, 0);
        Obstacle("Launch Ledge", 308, 309, -5, 5, 0, 1.0f);
        Floor(309, 316, -5, 5, 3.2f);
        Floor(316, 332, -5, 5, -3f);
        FallReset("Pit Reset", 316, 332, -5, 5, -3, -2);
        WallRunWall("Run Wall A", 315, 325, 2.5f, 3f, -3, 9);
        WallRunWall("Run Wall B", 323, 333, -3f, -2.5f, -3, 9);
        Floor(332, 341, -5, 5, 3.2f);
        Checkpoint("Run Top", 239.5f, 0, 17.5f, 90);
        Checkpoint("Run Bottom", 291.6f, 0, 0, 90);
        Checkpoint("Run Platform", 309.6f, 0, 3.2f, 90);
        Checkpoint("Run Landing", 332.6f, 0, 3.2f, 90);
        CorridorX(root, "C7", 341, 345, 0, 5.2f);
    }

    static float SlopeY(float x) => Mathf.Lerp(1.8f, -1.7f, (x - 124f) / 20f);
    static float RunSlopeY(float x) => Mathf.Lerp(17.5f, 0f, (x - 241f) / 50f);

    // ---------------------------------------------------------------- Act 2: axe

    static void BuildAct2(Transform root)
    {
        // 8 Axe shrine, 14 x 14. The axe on a plinth, two crates, the exit north sealed by
        // a breakable wall that only the axe opens.
        Room(root, "08 Axe Shrine", 4.2f);
        Shell(345, 359, -7, 7, 4.2f, 13.2f, D('W', 0, 5.2f), D('N', 352, 5.2f, exit: true));
        Floor(345, 359, -7, 7, 5.2f);
        Obstacle("Plinth", 351, 353, -1, 1, 5.2f, 6.2f);
        Pickup("Axe Pickup", AbilityPickup.Ability.Axe, new Vector3(352, 7.6f, 0), "Smash the wall to get out.");
        Breakable("Crate A", new Vector3(348, 5.95f, 4), 1.5f, 1.5f, alongX: true, runThrough: true);
        Breakable("Crate B", new Vector3(356, 5.95f, 4), 1.5f, 1.5f, alongX: true, runThrough: true);
        Breakable("Axe Wall", new Vector3(352, 5.2f + DoorH * 0.5f, 7.25f), DoorW, DoorH, alongX: false, runThrough: false);
        Checkpoint("Axe Shrine", 345.6f, 0, 5.2f, 90);
        CorridorZ(root, "C8", 7, 20, 352, 5.2f);

        // 9 Throw range, 32 x 12, travelling -x. Panels on the north wall at 10, 20 and
        // 30 m, and the exit 2 m up in the far wall behind a breakable window.
        Room(root, "09 Throw Range", 4.2f);
        Shell(323, 355, 20, 32, 4.2f, 15.2f, D('S', 352, 5.2f), D('W', 26, 7.2f, exit: true));
        Floor(323, 355, 20, 32, 5.2f);
        Breakable("Target 10m", new Vector3(343, 8, 31.5f), 2, 2, alongX: false, runThrough: false);
        Breakable("Target 20m", new Vector3(333, 8, 31.5f), 2, 2, alongX: false, runThrough: false);
        Breakable("Target 30m", new Vector3(326, 8, 31.5f), 2, 2, alongX: false, runThrough: false);
        Breakable("Window", new Vector3(322.75f, 7.2f + DoorH * 0.5f, 26), DoorW, DoorH, alongX: true, runThrough: false);
        Checkpoint("Throw Range", 352, 20.6f, 5.2f, 0);
        TipBox("Tip Throw", new Vector3(350, 5.2f, 20), new Vector3(354, 9, 23), "THROW", "AXE", Orange,
            "Hold {primary} to charge a throw, {secondary} throws straight away.",
            "{axePickup}  calls it back.");
        CorridorX(root, "C9", 319, 323, 26, 7.2f);

        // 10 Pogo drop, 12 x 12. Drop 6 m, bounce off the floor up to a 3 m ledge or the
        // 4.5 m exit. Steps along the south wall lead back up to try the drop again.
        Room(root, "10 Pogo Drop", 0.2f);
        Shell(307, 319, 20, 32, 0.2f, 15.2f, D('E', 26, 7.2f), D('W', 30, 5.7f, exit: true));
        Floor(307, 319, 20, 32, 1.2f);
        Obstacle("Entry Ledge", 315.5f, 319, 20, 32, 1.2f, 7.2f);
        Obstacle("Practise Ledge 3m", 311, 313, 27, 29, 1.2f, 4.2f);
        Obstacle("Exit Ledge", 307, 310, 28, 32, 1.2f, 5.7f);
        Obstacle("Step 1", 311, 312.5f, 20, 21.5f, 1.2f, 2.7f);
        Obstacle("Step 2", 312.5f, 314, 20, 21.5f, 1.2f, 4.2f);
        Obstacle("Step 3", 314, 315.5f, 20, 21.5f, 1.2f, 5.7f);
        Checkpoint("Pogo Drop", 318.4f, 26, 7.2f, -90);
        Tip("Tip Pogo", 316, 318, 20, 32, 7.2f, "POGO", "AXE", Orange,
            "Swing at the floor as you land to bounce back up.");
        CorridorX(root, "C10", 303, 307, 30, 5.7f);

        // 11 Glass corridor, 50 x 6. Two walls on the flat to swing through, a downhill
        // slide, then three walls close together to break by running into them.
        Room(root, "11 Glass Corridor", 1.7f);
        Shell(253, 303, 27, 33, 1.7f, 11.7f, D('E', 30, 5.7f), D('W', 30, 2.7f, exit: true));
        Floor(275, 303, 27, 33, 5.7f);
        RampDown("Slide Down", 275, 5.7f, 265, 2.7f, 27, 33, floorMat);
        Floor(253, 265, 27, 33, 2.7f);
        Breakable("Glass A", new Vector3(291, 5.7f + 3, 30), 6, 6, alongX: true, runThrough: true);
        Breakable("Glass B", new Vector3(281, 5.7f + 3, 30), 6, 6, alongX: true, runThrough: true);
        Breakable("Glass C", new Vector3(262, 2.7f + 4.5f, 30), 6, 9, alongX: true, runThrough: true);
        Breakable("Glass D", new Vector3(259, 2.7f + 4.5f, 30), 6, 9, alongX: true, runThrough: true);
        Breakable("Glass E", new Vector3(256, 2.7f + 4.5f, 30), 6, 9, alongX: true, runThrough: true);
        Checkpoint("Glass Corridor", 302.4f, 30, 5.7f, -90);
        Checkpoint("Glass Corridor Slide", 277, 30, 5.7f, -90);
        Tip("Tip Run Through", 276, 279, 27, 33, 5.7f, "RUN-THROUGH", "AXE", Orange,
            "Fast enough, and walls like these break when you run into them.");
    }

    // ---------------------------------------------------------------- Act 3: grapple

    static void BuildAct3(Transform root)
    {
        // 12 Grapple pit, 34 x 28. You drop in from the glass corridor. The grapple sits on
        // the low platform, the only way out is the ledge 11 m up with a point above it.
        Room(root, "12 Grapple Pit", -7f);
        Shell(219, 253, 18, 46, -7, 14, D('E', 30, 2.7f), D('N', 236, 5f, exit: true));
        Floor(219, 253, 18, 46, -6);
        Obstacle("Platform", 231, 241, 28, 36, -6, -5);
        Obstacle("Exit Ledge", 233, 239, 43, 46, -6, 5);
        Pickup("Grapple Pickup", AbilityPickup.Ability.Grapple, new Vector3(236, -3.8f, 32), "Zip up to the ledge above the far wall.");
        GrapplePoint("Point Pit Exit", 236, 7.5f, 44.5f);
        Checkpoint("Grapple Pit", 249, 30, -6, -90);
        CorridorZ(root, "C12", 46, 54, 236, 5f);

        // 13 Zip tower, 12 x 12 x 28. Zip to a ledge at 8 m, then to the ledge at 16 m that
        // runs into the exit.
        Room(root, "13 Zip Tower", 4f);
        Shell(230, 242, 54, 66, 4, 33, D('S', 236, 5f), D('E', 64.25f, 21f, exit: true));
        Floor(230, 242, 54, 66, 5);
        Obstacle("Ledge 8m", 230, 233.5f, 56, 61, 12.5f, 13f);
        Obstacle("Ledge 16m", 230, 242, 62.5f, 66, 20.5f, 21f);
        GrapplePoint("Point 8m", 231.75f, 15.5f, 58.5f);
        GrapplePoint("Point 16m", 236, 26f, 64.5f);
        Checkpoint("Zip Tower", 236, 54.6f, 5, 0);
        CorridorX(root, "C13", 242, 246, 64.25f, 21f);

        // 14 Swing gorge, 50 x 16, 20 m deep. A first swing over a 2 m ditch, a resting
        // platform, then two swings over the deep part with a 4 m wall between them.
        Room(root, "14 Swing Gorge", 1f);
        Shell(246, 296, 56, 72, 1, 37, D('W', 64.25f, 21f), D('E', 64.25f, 21f, exit: true));
        Floor(246, 251, 56, 72, 21);
        Floor(251, 262, 56, 72, 19);
        Floor(262, 266, 56, 72, 21);
        Floor(266, 291, 56, 72, 1.5f);
        FallReset("Gorge Reset", 266, 291, 56, 72, 1.5f, 3f);
        Obstacle("Mid Wall", 277, 278, 56, 72, 1.5f, 25f);
        Floor(291, 296, 56, 72, 21);
        GrapplePoint("Swing 1", 256.5f, 33, 64);
        GrapplePoint("Swing 2", 272, 33, 64);
        GrapplePoint("Swing 3", 284, 33, 64);
        Checkpoint("Swing Gorge", 246.6f, 64.25f, 21, 90);
        Checkpoint("Swing Gorge Platform", 262.6f, 64.25f, 21, 90);
        Tip("Tip Swing", 246.5f, 250, 56, 72, 21, "SWING", "GRAPPLE", Green,
            "{slot2}  then hold  {primary}  to swing.",
            "Scroll reels in and out.  {jump}  jumps off with a boost.");
        CorridorX(root, "C14", 296, 300, 64.25f, 21f);

        // 15 Anchor wall, 16 x 16. Three 5 m steps and no grapple points: throw the axe
        // into each step and zip to it.
        Room(root, "15 Anchor Wall", 20f);
        Shell(300, 316, 56, 72, 20, 45, D('W', 64.25f, 21f), D('E', 64.25f, 36f, exit: true));
        Floor(300, 316, 56, 72, 21);
        Obstacle("Step 5m", 308, 316, 56, 72, 21, 26);
        Obstacle("Step 10m", 311, 316, 56, 72, 26, 31);
        Obstacle("Step 15m", 314, 316, 56, 72, 31, 36);
        Checkpoint("Anchor Wall", 300.6f, 64.25f, 21, 90);
        Tip("Tip Anchor", 300.5f, 304, 56, 72, 21, "ANCHOR", "GRAPPLE", Green,
            "No point to grab? Throw the axe into the wall and  {zip}  to it.");
        CorridorX(root, "C15", 316, 320, 64.25f, 36f);
    }

    // ---------------------------------------------------------------- Act 4: the Gauntlet

    // A 6 m track around a central pit. From the entrance it runs south past the start
    // gate (vault blocks), east (slide pipe, a gap, a glass wall), north (one long wall
    // run over a gap), west (a zip across a gap), then south again over an anchor block
    // towards the entrance. Just before it, a swing reaches the island with Portal B. Falling
    // in the pit puts you back at the entrance, which also restarts the clock.
    static void BuildGauntlet(Transform root)
    {
        Room(root, "16 The Gauntlet", 20f);
        Shell(320, 382, 41, 92, 20, 56, D('W', 64.25f, 36f));
        // the pit floor runs under the whole arena, so every gap in the track lands in it
        Floor(320, 382, 41, 92, 22);
        FallReset("Pit Reset", 320, 382, 41, 92, 22, 24);

        // west strip, entrance and start gate
        Floor(320, 326, 47, 92, 36);
        Obstacle("Vault 1.0", 320, 326, 55, 56, 36, 37);
        Obstacle("Vault 1.8", 320, 326, 50, 51, 36, 37.8f);
        // south strip, with a 4 m gap
        Floor(320, 345, 41, 47, 36);
        Floor(349, 382, 41, 47, 36);
        Obstacle("Pipe", 334.5f, 335.5f, 41, 47, 37.3f, 37.8f);
        Breakable("Glass", new Vector3(362, 39, 44), 6, 6, alongX: true, runThrough: true);
        // east strip, a 16 m gap with a wall-run wall on the outside
        Floor(376, 382, 47, 55, 36);
        Floor(376, 382, 71, 86, 36);
        WallRunWall("Gauntlet Wall Run", 381.5f, 382, 54, 72, 30, 44);
        // north strip, a 12 m gap with a zip point over it
        Floor(362, 382, 86, 92, 36);
        Floor(320, 350, 86, 92, 36);
        GrapplePoint("Zip Gap", 356, 41, 89);
        // anchor block across the west strip, 5 m tall, no points
        Obstacle("Anchor Block", 320, 326, 74, 78, 36, 41);
        // the island and the swing to it
        Obstacle("Island", 345, 357, 60, 72, 22, 36);
        // north of the entrance, so coming round the loop you swing off before the start gate
        GrapplePoint("Swing To Island", 336, 47, 70);

        GameObject start = Trigger("Start Gate", new Vector3(320, 36, 57), new Vector3(326, 40, 59));
        start.AddComponent<StartZone>();
        GameObject finish = Trigger("Finish", new Vector3(345, 36, 60), new Vector3(347, 40, 72));
        finish.AddComponent<FinishZone>();

        Checkpoint("Gauntlet", 323, 64.25f, 36, 180);
        Tip("Tip Gauntlet", 320, 326, 61, 67, 36, "THE GAUNTLET", "TEST", Gold,
            "Every move you've learned, against the clock.",
            "Go round, then swing to the island. Portal B is waiting there.");
    }

    // ---------------------------------------------------------------- building blocks

    struct Door
    {
        public char side;      // W/E = the -x/+x walls, S/N = the -z/+z walls
        public float at;       // centre along the wall (z for W/E, x for S/N)
        public float sill;
        public bool exit;
    }

    // a door on one wall: "at" is where its middle is along that wall (z for W/E, x for S/N)
    static Door D(char side, float at, float sill, bool exit = false) =>
        new Door { side = side, at = at, sill = sill, exit = exit };

    static void Room(Transform root, string name, float bottom)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(root, false);
        room = go.transform;
        roomBottom = bottom;
    }

    // Four walls round the floor plan and an invisible ceiling (collider only, so light
    // still gets in for the blockout). Doors are holes in the walls; an exit's lintel is
    // yellow so the way on stands out.
    static void Shell(float x0, float x1, float z0, float z1, float yb, float yt, params Door[] doors)
    {
        Wall('W', x0 - T, x0, z0 - T, z1 + T, yb, yt, doors);
        Wall('E', x1, x1 + T, z0 - T, z1 + T, yb, yt, doors);
        Wall('S', x0, x1, z0 - T, z0, yb, yt, doors);
        Wall('N', x0, x1, z1, z1 + T, yb, yt, doors);
        Piece("Ceiling", new Vector3(x0 - T, yt, z0 - T), new Vector3(x1 + T, yt + T, z1 + T), wallMat, visible: false);
    }

    static void Wall(char side, float xa, float xb, float za, float zb, float yb, float yt, Door[] doors)
    {
        bool alongZ = side == 'W' || side == 'E';
        float a0 = alongZ ? za : xa;
        float a1 = alongZ ? zb : xb;

        List<Door> mine = new List<Door>();
        foreach (Door d in doors)
        {
            if (d.side == side)
            {
                mine.Add(d);
            }
        }
        mine.Sort((p, q) => p.at.CompareTo(q.at));

        float cursor = a0;
        foreach (Door d in mine)
        {
            float o0 = d.at - DoorW * 0.5f;
            float o1 = d.at + DoorW * 0.5f;
            WallPiece("Wall " + side, alongZ, xa, xb, za, zb, cursor, o0, yb, yt, wallMat);
            WallPiece("Below Door " + side, alongZ, xa, xb, za, zb, o0, o1, yb, d.sill, wallMat);
            WallPiece(d.exit ? "Exit Lintel " + side : "Above Door " + side, alongZ, xa, xb, za, zb, o0, o1,
                d.sill + DoorH, yt, d.exit ? exitMat : wallMat);
            cursor = o1;
        }
        WallPiece("Wall " + side, alongZ, xa, xb, za, zb, cursor, a1, yb, yt, wallMat);
    }

    static void WallPiece(string name, bool alongZ, float xa, float xb, float za, float zb,
                          float a0, float a1, float y0, float y1, Material mat)
    {
        if (a1 - a0 < 0.01f || y1 - y0 < 0.01f)
        {
            return;
        }
        Vector3 min = alongZ ? new Vector3(xa, y0, a0) : new Vector3(a0, y0, za);
        Vector3 max = alongZ ? new Vector3(xb, y1, a1) : new Vector3(a1, y1, zb);
        Piece(name, min, max, mat);
    }

    // solid from the room's bottom up to the walking surface, so pits have real sides
    static void Floor(float x0, float x1, float z0, float z1, float top)
    {
        Piece("Floor", new Vector3(x0, Mathf.Min(roomBottom, top - T), z0), new Vector3(x1, top, z1), floorMat);
    }

    static void Obstacle(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), obstacleMat);
    }

    // red, on the WallRun layer and tag, like the walls in the current tutorial
    static void WallRunWall(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        GameObject go = Piece(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), wallRunMat);
        if (go != null)
        {
            go.layer = wallRunLayer;
            go.tag = "WallRun";
        }
    }

    static void CorridorX(Transform root, string name, float x0, float x1, float zc, float floorY)
    {
        Room(root, name, floorY - T);
        float z0 = zc - DoorW * 0.5f, z1 = zc + DoorW * 0.5f;
        Floor(x0, x1, z0, z1, floorY);
        Piece("Wall S", new Vector3(x0, floorY, z0 - T), new Vector3(x1, floorY + DoorH, z0), wallMat);
        Piece("Wall N", new Vector3(x0, floorY, z1), new Vector3(x1, floorY + DoorH, z1 + T), wallMat);
        Piece("Ceiling", new Vector3(x0, floorY + DoorH, z0 - T), new Vector3(x1, floorY + DoorH + T, z1 + T), wallMat, visible: false);
    }

    static void CorridorZ(Transform root, string name, float z0, float z1, float xc, float floorY)
    {
        Room(root, name, floorY - T);
        float x0 = xc - DoorW * 0.5f, x1 = xc + DoorW * 0.5f;
        Floor(x0, x1, z0, z1, floorY);
        Piece("Wall W", new Vector3(x0 - T, floorY, z0), new Vector3(x0, floorY + DoorH, z1), wallMat);
        Piece("Wall E", new Vector3(x1, floorY, z0), new Vector3(x1 + T, floorY + DoorH, z1), wallMat);
        Piece("Ceiling", new Vector3(x0 - T, floorY + DoorH, z0), new Vector3(x1 + T, floorY + DoorH + T, z1), wallMat, visible: false);
    }

    // A ProBuilder cube between two corners, set up like the project's other level pieces:
    // Ground layer, non-convex mesh collider, static.
    static GameObject Piece(string name, Vector3 min, Vector3 max, Material mat, bool visible = true)
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
        FinishPiece(pb, mat, visible);
        return go;
    }

    // a slab whose top surface runs from (xa, ya) to (xb, yb) along x
    static void Ramp(string name, float xa, float ya, float xb, float yb, float z0, float z1, Material mat)
    {
        float dx = xb - xa, dy = yb - ya;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
        Quaternion rot = Quaternion.Euler(0f, 0f, angle);

        ProBuilderMesh pb = ShapeGenerator.GenerateCube(PivotLocation.Center, new Vector3(len, T, z1 - z0));
        GameObject go = pb.gameObject;
        go.name = name;
        go.transform.SetParent(room, false);
        Vector3 topMid = new Vector3((xa + xb) * 0.5f, (ya + yb) * 0.5f, (z0 + z1) * 0.5f);
        go.transform.localRotation = rot;
        go.transform.localPosition = topMid - rot * Vector3.up * (T * 0.5f);
        FinishPiece(pb, mat, true);
    }

    // same, for a slope met while travelling -x
    static void RampDown(string name, float xHigh, float yHigh, float xLow, float yLow, float z0, float z1, Material mat)
    {
        Ramp(name, xLow, yLow, xHigh, yHigh, z0, z1, mat);
    }

    static void FinishPiece(ProBuilderMesh pb, Material mat, bool visible)
    {
        pb.SetMaterial(pb.faces, mat);
        pb.ToMesh();
        pb.Refresh();

        GameObject go = pb.gameObject;
        go.layer = groundLayer;

        MeshCollider col = go.AddComponent<MeshCollider>();
        col.convex = false;
        col.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;

        MeshRenderer r = go.GetComponent<MeshRenderer>();
        if (r != null)
        {
            r.enabled = visible;
        }

        GameObjectUtility.SetStaticEditorFlags(go,
            StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic |
            StaticEditorFlags.OccluderStatic | StaticEditorFlags.ReflectionProbeStatic);
        built++;
    }

    // ---------------------------------------------------------------- gameplay pieces

    static void GrapplePoint(string name, float x, float y, float z)
    {
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(grapplePointPrefab, room.gameObject.scene);
        go.name = name;
        go.transform.SetParent(room, false);
        go.transform.localPosition = new Vector3(x, y, z);
        built++;
    }

    // The BreakableWall prefab is a 10.3 m wide, 10.2 m tall slab with its pivot off to one
    // side. It's scaled to the opening and shifted so its middle lands on "centre".
    // alongX = you walk through it travelling along x (the wall faces x).
    static void Breakable(string name, Vector3 centre, float width, float height, bool alongX, bool runThrough)
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

    // a tip zone the full height of a doorway, from x0 to x1 and z0 to z1, standing on "floor"
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

    // You come back standing here, facing "yaw" (90 = +x). The trigger spans a doorway.
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

    static void FallReset(string name, float x0, float x1, float z0, float z1, float y0, float y1)
    {
        GameObject go = Trigger(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
        go.AddComponent<FallReset>();
    }
}
