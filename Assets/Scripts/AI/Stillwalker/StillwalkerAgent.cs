using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

// ML-Agents (PPO) policy for where the Stillwalker stalks. It only picks a direction and
// whether to hurry, the rest of the Stillwalker's rules stay in Stillwalker.cs.
[RequireComponent(typeof(Stillwalker))]
public class StillwalkerAgent : Agent, IStillwalkerPolicy
{
    public const int ObservationSize = 15;

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

    Stillwalker body;
    Vector3 lastMove;
    bool lastFast;
    float episodeTimer;
    float pogoCooldown;
    int fixedCount;

    public bool PolicyActive => isActiveAndEnabled && (trainingMode || driveInGame);

    protected override void Awake()
    {
        base.Awake();
        ConfigureBehaviorParameters();
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

    public override void Initialize()
    {
        body = GetComponent<Stillwalker>();
        body.Policy = this;

        if (trainingMode && ghost != null)
            body.targetOverride = ghost;
    }

    public override void OnEpisodeBegin()
    {
        if (!trainingMode || ghost == null) return;

        if (recordings != null && recordings.Length > 0)
        {
            TextAsset pick = recordings[Random.Range(0, recordings.Length)];
            if (pick != null && pick != ghost.playbackData) ghost.SetPlayback(pick);
        }
        ghost.RestartPlayback(Random.value);
        Vector2 r = Random.insideUnitCircle.normalized * Random.Range(4f, spawnRadius);
        body.PlaceForTraining(ghost.Feet + new Vector3(r.x, 0f, r.y));
        episodeTimer = 0f;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        StillwalkerSenses s = body.Senses();
        sensor.AddObservation(s.toPlayerLocal / 20f);
        sensor.AddObservation(s.toPredictedLocal / 20f);
        sensor.AddObservation(s.playerHeightAbove / 5f);
        sensor.AddObservation(s.playerVerticalSpeed / 15f);
        sensor.AddObservation(s.playerOnGround ? 1f : 0f);
        sensor.AddObservation(Mathf.Clamp01(s.playerGroundedTime / 2f));
        sensor.AddObservation(Mathf.Clamp01(s.playerAirTime / 2f));
        sensor.AddObservation(s.cracks / 4f);
        sensor.AddObservation(s.launchReady ? 1f : 0f);
        sensor.AddObservation(s.seen ? 1f : 0f);
        sensor.AddObservation(Mathf.Clamp01(s.distance / 40f));
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        var c = actions.ContinuousActions;
        Vector3 local = new Vector3(Mathf.Clamp(c[0], -1f, 1f), 0f, Mathf.Clamp(c[1], -1f, 1f));
        lastMove = transform.TransformDirection(local);
        lastFast = actions.DiscreteActions[0] == 1;

        if (trainingMode && body.Seen) AddReward(seenPenalty);
    }

    // Heuristic Only baseline
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        StillwalkerSenses s = body.Senses();
        Vector3 to = s.toPlayerLocal;
        to.y = 0f;
        float want = (body.stalkMin + body.stalkMax) * 0.5f;
        Vector3 dir = to.sqrMagnitude > 0.01f ? to.normalized * Mathf.Sign(s.distance - want) : Vector3.zero;

        var c = actionsOut.ContinuousActions;
        c[0] = dir.x;
        c[1] = dir.z;
        var d = actionsOut.DiscreteActions;
        d[0] = s.distance > body.stalkMax ? 1 : 0;
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
            case StillwalkerEvent.Shattered:
                AddReward(shatteredReward);
                EndEpisode();
                break;
        }
    }

    void FixedUpdate()
    {
        if (!PolicyActive) return;

        if (++fixedCount % Mathf.Max(1, decisionPeriod) == 0)
            RequestDecision();

        if (!trainingMode || ghost == null) return;

        episodeTimer += Time.fixedDeltaTime;
        if (episodeTimer > episodeLength)
        {
            EndEpisode();
            return;
        }

        SimulatePogo();
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
        }
    }
}
