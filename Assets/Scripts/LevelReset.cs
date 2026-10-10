using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Starts a level over when the player comes into it through a portal, so the tutorial and
// the level can be played round and round. Both scenes stay loaded the whole time (each
// one's portals keep the other streamed in), so without this, coming back to a level
// would find it exactly how you left it: goal taken, glass smashed, enemies dead.
//
// What comes back: the LevelGoal, every smashed BreakableWall, the enemies, checkpoints
// and the respawn point, and the clock starts again for this level. Ability pickups stay
// gone, since the player still has what they picked up.
//
// PortalManager calls Enter when the player crosses into a different scene.
public static class LevelReset
{
    // respawn spots made for scenes without a Deathplane that has one
    static readonly Dictionary<string, Transform> madeRespawns = new Dictionary<string, Transform>();

    public static void Enter(Scene scene)
    {
        if (!scene.IsValid())
        {
            return;
        }

        foreach (LevelGoal goal in Object.FindObjectsByType<LevelGoal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (goal.gameObject.scene == scene)
            {
                goal.ResetGoal();
            }
        }

        // only ones that broke: a wall switched off in the editor stays off
        foreach (BreakableWall wall in Object.FindObjectsByType<BreakableWall>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (wall.gameObject.scene == scene && wall.IsShattered)
            {
                wall.Respawn();
            }
        }

        // every enemy, not just this scene's: anything that chased you through goes home too
        if (AIDirector.Instance != null)
        {
            AIDirector.Instance.ResetAll();
        }

        ResetPlayer(scene);
        CourseTimer.Get().StartRun(scene.name);
    }

    // Checkpoints and the respawn point belong to the scene you just left, so dying here
    // would put you back in the other level.
    static void ResetPlayer(Scene scene)
    {
        Checkpoint.ClearLast();

        FirstPersonCharacterController player = PortalManager.FindPlayer();
        PlayerHealth health = player != null ? player.GetComponent<PlayerHealth>() : null;
        if (health == null)
        {
            return;
        }

        Transform respawn = SceneRespawn(scene);
        if (respawn != null)
        {
            health.respawnPoint = respawn;
        }
        health.Heal(health.maxHealth);
    }

    // The level's own respawn spot (the one its Deathplane uses), or else one made at the
    // spot the player starts the scene from.
    static Transform SceneRespawn(Scene scene)
    {
        foreach (Deathplane plane in Object.FindObjectsByType<Deathplane>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (plane.gameObject.scene == scene && plane.RespawnPoint != null)
            {
                return plane.RespawnPoint;
            }
        }

        if (madeRespawns.TryGetValue(scene.name, out Transform made) && made != null)
        {
            return made;
        }

        if (PortalManager.Instance == null || !PortalManager.Instance.TryGetSceneSpawn(scene, out Pose spawn))
        {
            return null;
        }

        GameObject go = new GameObject("Level Respawn");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        madeRespawns[scene.name] = go.transform;
        return go.transform;
    }
}
