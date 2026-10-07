# Enemy art and animation

Everything below is optional. The enemies play fine as placeholders. When you're ready, grab these from Mixamo and drop them into the folders.

## Mixamo download settings

| | Format | Skin | FPS | Other |
|---|---|---|---|---|
| Character | FBX for Unity | With Skin | | T-pose |
| Animations | FBX for Unity | Without Skin | 30 | Tick **In Place** on anything that walks/runs/crawls (the NavMesh moves the enemy, not the animation) |

## Naming

Name each animation file `<Enemy>_<Clip>.fbx`, for example `Grunt_Run.fbx`. The character file in `Model/` can have any name.

The animation names in the tables are Mixamo **search terms**. Pick whatever looks right.

## Grunt (`Grunt/Model`, `Grunt/Animations`)

Character: a soldier or SWAT type (search "soldier" or "swat").

| File | Search for | Loops |
|---|---|---|
| Grunt_Idle | rifle idle | yes |
| Grunt_Walk | rifle walk | yes |
| Grunt_Run | rifle run | yes |
| Grunt_AimIdle | rifle aiming idle | yes |
| Grunt_AimWalk | walking while aiming rifle / strafe | yes |
| Grunt_Fire | firing rifle | no |
| Grunt_Dive | dive / roll to the side | no |
| Grunt_Stagger | hit reaction, stumble backwards | no |
| Grunt_Panic | running scared / terrified run (falls back to Grunt_Run if missing) | yes |
| Grunt_Throw | grenade throw / toss (leader only) | no |

Leaders and followers share these files. Scale the leader's model up a little and give it a different material.

Mixamo has no weapons. Parent the placeholder gun to the right hand bone, or swap in a real rifle model.

## Stillwalker (`Stillwalker/Model`, `Stillwalker/Animations`)

Character: anything tall and thin. "Y Bot" or "X Bot" works well with the stone material on it.

| File | Search for | Loops |
|---|---|---|
| Stillwalker_Walk | zombie walk / creepy walk / stiff walk | yes |
| Stillwalker_LungeWindup | crouch, ready / zombie scream (the 0.2s before a launch) | no |
| Stillwalker_Lunge | jump attack / mutant jumping (the launch itself) | no |

The script freezes the animator the instant you look at it and speeds the walk up when it's catching up, so a slow, stiff walk reads best.

## After importing

1. Set each FBX's Rig to **Humanoid** (animations copy the avatar from the character).
2. Make an Animator Controller per enemy. The scripts set these parameters (any that are missing are ignored):
   - Grunt: `Speed` (float), `Alert`, `Aiming`, `Panic` (bools), `Fire`, `Stagger`, `Dive`, `Throw` (triggers)
   - Stillwalker: `Speed` (float), `LungeWindup`, `Lunge` (triggers)
3. Drop the character under the enemy as a child called `Model`, give it the controller, and turn off the `Placeholder` child.
4. For ragdoll deaths, select the Model and use **GameObject > 3D Object > Ragdoll...**. `EnemyRagdoll` picks up the bones by itself.

## Sound (optional)

Each enemy synthesises placeholder sounds at runtime. To replace them, drag clips onto these fields:

| Enemy | Fields |
|---|---|
| Grunt | Shot Sound, Charge Sound |
| Stillwalker | Step Sound, Crack Sound |
