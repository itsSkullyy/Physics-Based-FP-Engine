using System;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

// ML-Agents (PPO + LSTM) policy for where the Stillwalker goes. It picks a direction and
// whether to hurry, both while stalking and while running away hurt. The rules (freezing
// when seen, launching, cracking, dodging) stay in Stillwalker.cs.
//
// It sees how the player is moving, how they've been playing for the last few seconds
// (PlayerProfile), where it could run and hide, and incoming axes. The LSTM in the
// trainer config gives it a memory, so it can pick up on a player's habits.
//
// Training episodes are a mix of two fake players: F8 recordings of real runs (how
// players actually move) and StillwalkerHunterBot (a player that hunts it down). Both
// throw simulated axes. Some episodes start it already cracked so it practises escaping.
[RequireComponent(typeof(Stillwalker))]
public class StillwalkerAgent : Agent, IStillwalkerPolicy
{
    public const int ObservationSize = 51;

    [Header("In Game")]
    [Tooltip("Drive the Stillwalker with the trained model in normal play. Needs a model on Behavior Parameters.")]
    public bool driveInGame = true;
    public int decisionPeriod = 5;

    [Header("Training")]
    public bool trainingMode = false;
    [Tooltip("A PlayerMotionTracker in Playback mode, replaying a recorded run.")]
    public PlayerMotionTracker ghost;
    [Tooltip("Optional. Each episode replays a random one of these (F8 recordings).")]
    public TextAsset[] recordings;
    public float spawnRadius = 18f;
    public float episodeLength = 30f;
    [Tooltip("Share of episodes where the hunter bot plays instead of a recording. The trainer config can override it (hunter_chance).")]
    [Range(0f, 1f)] public float hunterChance = 0.5f;
    [Tooltip("Share of episodes that start already cracked (hurt_start_chance).")]
    [Range(0f, 1f)] public float hurtStartChance = 0.4f;
    [Tooltip("How good the hunter bot is, 0 to 1 (hunter_skill). The trainer config ramps it up as training goes.")]
    [Range(0f, 1f)] public float hunterSkill = 1f;

    [Header("Simulated Axe Throws")]
    public bool simulateAxe = true;
    public float axeSpeed = 35f;
    public float axeRange = 30f;
    public float axeIntervalMin = 2.5f;
    public float axeIntervalMax = 6f;
    [Tooltip("The axe hits if the Stillwalker is still within this far of where it was aimed.")]
    public float axeHitRadius = 0.9f;

    [Header("Rewards")]
    public float landedInRangeReward = 0.3f;
    public float landedOutOfRangeReward = -0.1f;
    public float launchHitReward = 1f;
    public float launchMissedReward = -0.2f;
    public float contactHitReward = 0.5f;
    public float crackedReward = -0.4f;
    public float shatteredReward = -1f;
    [Tooltip("Per decision while the player is looking at it. Teaches it to stay out of view.")]
    public float seenPenalty = -0.002f;
    [Tooltip("Cracks healed: it got away. Keep heal + 3s of safe rewards below the crack penalty, or it learns to get hit on purpose.")]
    public float healedReward = 0.25f;
    [Tooltip("Per decision while hurt, far enough away and out of sight.")]
    public float safeWhileHurtReward = 0.004f;
    [Tooltip("Per decision while hurt and within 8m of the player.")]
    public float closeWhileHurtPenalty = -0.006f;
    [Tooltip("The hurt rewards are multiplied by this when it's one hit from death.")]
    public float oneHitAwayScale = 2.5f;
    [Tooltip("A simulated axe throw missed it.")]
    public float axeDodgedReward = 0.15f;

    Stillwalker body;
    Vector3 lastMove;
    bool lastFast;
    float episodeTimer;
    float pogoCooldown;
    int fixedCount;
    bool endAfterDeath;
    bool beginPending;
    float offMeshTime;
    // made in Initialize, Unity won't create NavMeshPaths in a field initializer
    NavMeshPath spawnPath;

    StillwalkerHunterBot hunter;
    bool hunterEpisode;

    bool axeInFlight;
    Vector3 axeAim;
    float axeArrive;
    float nextAxe;

    public bool PolicyActive => isActiveAndEnabled && (trainingMode || driveInGame);
    public bool Training => trainingMode;

    protected override void Awake()
    {
        base.Awake();
        ConfigureBehaviorParameters();

        // an old model trained on fewer observations would throw every step, so fall back
        // to the hand-written rules instead
        if (!trainingMode && !ModelFits(out string why))
        {
            Debug.LogWarning($"[StillwalkerAgent] {name}: {why} Using the hand-written rules until a retrained model is assigned.", this);
            enabled = false;
        }
    }

    [ContextMenu("Configure Behavior Parameters")]
    void ConfigureBehaviorParameters()
    {
        BehaviorParameters bp = GetComponent<BehaviorParameters>();
        if (bp == null) return;

        if (string.IsNullOrEmpty(bp.BehaviorName) || bp.BehaviorName == "My Behavior")
            bp.BehaviorName = "Stillwalker";
        bp.BrainParameters.VectorObservationSize = ObservationSize;
        bp.BrainParameters.ActionSpec = new ActionSpec(2, new[] { 2 });

        // deterministic in game, otherwise it samples and looks twitchy
        bp.DeterministicInference = !trainingMode;
    }

    bool ModelFits(out string why)
    {
        why = null;
        BehaviorParameters bp = GetComponent<BehaviorParameters>();
        if (bp == null || bp.BehaviorType == BehaviorType.HeuristicOnly) return true;
        if (bp.Model == null)
        {
            why = "No model on Behavior Parameters.";
            return false;
        }

        try
        {
            Model model = ModelLoader.Load(bp.Model);
            int want = ObservationSize * Mathf.Max(1, bp.BrainParameters.NumStackedVectorObservations);
            foreach (Model.Input input in model.inputs)
            {
                if (input.name != "obs_0") continue;
                int[] shape = input.shape.ToIntArray();
                int got = shape.Length > 0 ? shape[shape.Length - 1] : -1;
                if (got != want)
                {
                    why = $"Model '{bp.Model.name}' takes {got} observations but the agent now sends {want} (it was trained on an older version).";
                    return false;
                }
            }
        }
        catch (Exception e)
        {
            why = $"Couldn't read model '{bp.Model.name}': {e.Message}";
            return false;
        }
        return true;
    }

    public override void Initialize()
    {
        body = GetComponent<Stillwalker>();
        body.Policy = this;

        if (trainingMode && ghost != null)
        {
            body.targetOverride = ghost;
            hunter = new StillwalkerHunterBot();
            spawnPath = new NavMeshPath();
        }
    }

    // ---------------------------------------------------------------- episodes

    public override void OnEpisodeBegin()
    {
        if (!trainingMode || ghost == null) return;

        // ML-Agents runs at execution order -50, so this can be called before the Stillwalker
        // has woken up. Wait for it, FixedUpdate starts the episode once it's ready.
        if (body == null || body.Brain == null)
        {
            beginPending = true;
            return;
        }
        beginPending = false;

        EnvironmentParameters env = Academy.Instance.EnvironmentParameters;
        float hunterShare = env.GetWithDefault("hunter_chance", hunterChance);
        float hurtShare = env.GetWithDefault("hurt_start_chance", hurtStartChance);
        float skill = env.GetWithDefault("hunter_skill", hunterSkill);

        // a recording either plays this episode or just gives the hunter a spot in the arena to start from
        ghost.SetScripted(false);
        if (recordings != null && recordings.Length > 0)
        {
            TextAsset pick = recordings[Random.Range(0, recordings.Length)];
            if (pick != null && pick != ghost.playbackData) ghost.SetPlayback(pick);
        }
        ghost.RestartPlayback(Random.value);

        // the recording can be mid-swing or up a wall, so start from the floor under it
        Vector3 floor = FloorUnder(ghost.PlaybackCenter);
        Vector3 start = floor + Vector3.up * ghost.playbackFeetOffset;

        hunterEpisode = hunter != null && Random.value < hunterShare;
        if (hunterEpisode)
        {
            ghost.SetScripted(true);
            hunter.Reset(start, ghost.playbackFeetOffset, skill);
            hunter.Step(0f, body, ghost);
        }

        int cracks = Random.value < hurtShare ? Random.Range(1, body.cracksToShatter) : 0;
        body.PlaceForTraining(PickSpawn(floor), cracks);

        episodeTimer = 0f;
        offMeshTime = 0f;
        endAfterDeath = false;
        pogoCooldown = 0f;
        axeInFlight = false;
        nextAxe = Time.time + Random.Range(axeIntervalMin, axeIntervalMax);
    }

    static Vector3 FloorUnder(Vector3 point)
    {
        if (NavMesh.SamplePosition(point, out NavMeshHit hit, 40f, NavMesh.AllAreas)) return hit.position;
        return point;
    }

    // a spot on the NavMesh 4 to spawnRadius metres away that can actually be walked to
    // from the floor (not on a roof, inside a wall or in the next arena)
    Vector3 PickSpawn(Vector3 floor)
    {
        for (int i = 0; i < 12; i++)
        {
            Vector2 r = Random.insideUnitCircle.normalized * Random.Range(4f, spawnRadius);
            if (!NavMesh.SamplePosition(floor + new Vector3(r.x, 0f, r.y), out NavMeshHit hit, 3f, NavMesh.AllAreas))
                continue;
            if (NavMesh.CalculatePath(floor, hit.position, NavMesh.AllAreas, spawnPath)
                && spawnPath.status == NavMeshPathStatus.PathComplete)
                return hit.position;
        }
        return floor;
    }

    // ---------------------------------------------------------------- observations (51)

    public override void CollectObservations(VectorSensor sensor)
    {
        StillwalkerSenses s = body.Senses();

        sensor.AddObservation(s.toPlayerLocal / 20f);
        sensor.AddObservation(s.toPredictedLocal / 20f);
        sensor.AddObservation(s.playerVelocityLocal / 20f);
        sensor.AddObservation(s.playerHeightAbove / 5f);
        sensor.AddObservation(s.playerVerticalSpeed / 15f);
        sensor.AddObservation(s.playerOnGround);
        sensor.AddObservation(Mathf.Clamp01(s.playerGroundedTime / 2f));
        sensor.AddObservation(Mathf.Clamp01(s.playerAirTime / 2f));
        sensor.AddObservation(Mathf.Clamp01(s.distance / 40f));

        sensor.AddObservation(s.playerSwinging);
        sensor.AddObservation(s.playerZipping);
        sensor.AddObservation(s.playerWallRunning);
        sensor.AddObservation(s.playerSliding);

        sensor.AddObservation(s.seen);
        sensor.AddObservation(s.lookAngle01);
        sensor.AddObservation(s.exposed);

        sensor.AddObservation(s.zipAtMe);
        sensor.AddObservation(s.axeThreat01);

        sensor.AddObservation(s.cracks / (float)Mathf.Max(1, body.cracksToShatter));
        sensor.AddObservation(s.oneHitAway);
        sensor.AddObservation(s.healProgress01);
        sensor.AddObservation(s.launchReady);
        sensor.AddObservation(s.myVelocityLocal.x / 20f);
        sensor.AddObservation(s.myVelocityLocal.z / 20f);

        for (int i = 0; i < 8; i++) sensor.AddObservation(s.escapeRoom01[i]);
        for (int i = 0; i < 8; i++) sensor.AddObservation(s.escapeHidden[i]);

        sensor.AddObservation(s.profile.speed01);
        sensor.AddObservation(s.profile.airborne01);
        sensor.AddObservation(s.profile.watching01);
        sensor.AddObservation(s.profile.approach01);
        sensor.AddObservation(s.profile.aggression01);
    }

    // ---------------------------------------------------------------- actions

    public override void OnActionReceived(ActionBuffers actions)
    {
        var c = actions.ContinuousActions;
        Vector3 local = new Vector3(Mathf.Clamp(c[0], -1f, 1f), 0f, Mathf.Clamp(c[1], -1f, 1f));
        lastMove = transform.TransformDirection(local);
        lastFast = actions.DiscreteActions[0] == 1;

        if (trainingMode) ShapeRewards();
    }

    void ShapeRewards()
    {
        if (body.Seen) AddReward(seenPenalty);
        if (!body.Hurt) return;

        float scale = body.OneHitAway ? oneHitAwayScale : 1f;
        Vector3 d = ghost.Feet - transform.position;
        d.y = 0f;
        float dist = d.magnitude;
        float wanted = body.retreatDistance + body.retreatPerCrack * body.Cracks;

        if (dist > wanted * 0.8f && !body.Exposed) AddReward(safeWhileHurtReward * scale);
        if (dist < 8f) AddReward(closeWhileHurtPenalty * scale);
    }

    // Heuristic Only baseline: run when hurt, otherwise hold the stalking band
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        StillwalkerSenses s = body.Senses();
        Vector3 to = s.toPlayerLocal;
        to.y = 0f;

        Vector3 dir;
        bool fast;
        if (s.cracks > 0)
        {
            dir = to.sqrMagnitude > 0.01f ? -to.normalized : Vector3.zero;
            fast = true;
        }
        else
        {
            float want = (body.stalkMin + body.stalkMax) * 0.5f;
            dir = to.sqrMagnitude > 0.01f ? to.normalized * Mathf.Sign(s.distance - want) : Vector3.zero;
            fast = s.distance > body.stalkMax;
        }

        var c = actionsOut.ContinuousActions;
        c[0] = dir.x;
        c[1] = dir.z;
        var d = actionsOut.DiscreteActions;
        d[0] = fast ? 1 : 0;
    }

    public void Decide(in StillwalkerSenses senses, out Vector3 moveDirWorld, out bool fast)
    {
        moveDirWorld = lastMove;
        fast = lastFast;
    }

    public void OnStillwalkerEvent(StillwalkerEvent e)
    {
        if (!trainingMode) return;

        switch (e)
        {
            case StillwalkerEvent.PlayerLandedInRange: AddReward(landedInRangeReward); break;
            case StillwalkerEvent.PlayerLandedOutOfRange: AddReward(landedOutOfRangeReward); break;
            case StillwalkerEvent.LaunchHit: AddReward(launchHitReward); break;
            case StillwalkerEvent.LaunchMissed: AddReward(launchMissedReward); break;
            case StillwalkerEvent.ContactHit: AddReward(contactHitReward); break;
            case StillwalkerEvent.Cracked: AddReward(crackedReward); break;
            case StillwalkerEvent.Healed: AddReward(healedReward); break;
            case StillwalkerEvent.Shattered:
                // EndEpisode respawns it straight away (OnEpisodeBegin runs inside the call), but
                // this fires halfway through Stillwalker.OnDeath, which would then hide and
                // disable the fresh one. Ending it next step lets the death finish first.
                AddReward(shatteredReward);
                endAfterDeath = true;
                break;
        }
    }

    // ---------------------------------------------------------------- stepping

    // runs before PlayerMotionTracker (order 50), so the hunter's move is sampled this step
    void FixedUpdate()
    {
        if (!PolicyActive) return;

        if (++fixedCount % Mathf.Max(1, decisionPeriod) == 0)
            RequestDecision();

        if (!trainingMode || ghost == null) return;

        if (beginPending)
        {
            OnEpisodeBegin();
            return;
        }

        if (endAfterDeath)
        {
            endAfterDeath = false;
            EndEpisode();
            return;
        }

        // Safety net: if it ever ends up off the NavMesh (launches take it off for under a
        // second) it can't move, so restart rather than burn the rest of the episode.
        // EpisodeInterrupted means it isn't blamed for it.
        offMeshTime = body.IsDead || body.OnNavMesh ? 0f : offMeshTime + Time.fixedDeltaTime;
        if (offMeshTime > 2f)
        {
            Debug.LogWarning($"[StillwalkerAgent] {name} got stuck off the NavMesh at {transform.position}, restarting the episode.", this);
            EpisodeInterrupted();
            return;
        }

        episodeTimer += Time.fixedDeltaTime;
        if (episodeTimer > episodeLength)
        {
            EndEpisode();
            return;
        }

        if (hunterEpisode) hunter.Step(Time.fixedDeltaTime, body, ghost);
        SimulatePogo();
        SimulateAxe();
    }

    // the ghost can't attack, so falling onto it counts as a pogo hit
    void SimulatePogo()
    {
        pogoCooldown -= Time.fixedDeltaTime;
        if (pogoCooldown > 0f || ghost.IsGrounded) return;

        Vector3 d = ghost.Feet - transform.position;
        float height = d.y;
        d.y = 0f;
        if (d.magnitude < 1f && height > 1.2f && height < 3.5f && ghost.Velocity.y < -body.minFallSpeed)
        {
            pogoCooldown = 0.4f;
            body.TakeHit(new EnemyHit
            {
                kind = HitKind.Melee,
                point = body.HeadPosition,
                direction = Vector3.down,
                force = 1f
            });
            if (hunterEpisode) hunter.Bounce();
        }
    }

    // A throw is aimed where it'll be when the axe arrives (better hunters lead it more).
    // It hits if the Stillwalker is still near that spot and in the open when it lands.
    void SimulateAxe()
    {
        body.TrainingAxeThreat = 0f;
        if (!simulateAxe || body.IsDead) return;

        if (axeInFlight)
        {
            float left = axeArrive - Time.time;
            body.TrainingAxeThreat = 1f - Mathf.Clamp01(left / 1.2f);
            if (left > 0f) return;

            axeInFlight = false;
            Vector3 miss = body.ChestPosition - axeAim;
            miss.y = 0f;
            if (miss.magnitude < axeHitRadius && body.Exposed)
            {
                body.TakeHit(new EnemyHit
                {
                    kind = HitKind.Thrown,
                    point = body.ChestPosition,
                    direction = (body.ChestPosition - ghost.CameraPosition).normalized,
                    force = 1f
                });
            }
            else AddReward(axeDodgedReward);

            nextAxe = Time.time + Random.Range(axeIntervalMin, axeIntervalMax) * AxeIntervalScale();
            return;
        }

        if (Time.time < nextAxe) return;

        Vector3 to = body.ChestPosition - ghost.CameraPosition;
        if (to.magnitude > axeRange || !body.Exposed || ghost.LookAngleTo(body.ChestPosition) > 60f) return;

        float skill = hunterEpisode ? hunter.Skill : 0.6f;
        float flight = to.magnitude / axeSpeed;
        axeAim = body.ChestPosition + body.Velocity * flight * skill * Random.Range(0.6f, 1.1f);
        axeArrive = Time.time + flight;
        axeInFlight = true;
    }

    float AxeIntervalScale()
    {
        if (!hunterEpisode) return 1.3f;
        switch (hunter.CurrentStyle)
        {
            case StillwalkerHunterBot.Style.Kiter: return 0.55f;
            case StillwalkerHunterBot.Style.Diver: return 1.5f;
            default: return 1f;
        }
    }
}
