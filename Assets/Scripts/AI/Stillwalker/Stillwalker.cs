using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Statue when looked at, follows you when not, launches at you when you land in range.
// Only pogo hits and thrown axes crack it, 4 cracks to kill, they heal after 3s.
// Looking at it stops it walking, not reacting: it still launches, flinches and dodges while watched.
// Cracked = it flinches away and retreats until the cracks heal, further the more cracked it is.
// One hit from death it bolts: full speed, ignores being watched, no more launches.
// Glowing red cracks (StillwalkerCracks) show how close it is to breaking.
// You can zip at it but it sidesteps and swats you. Zip it while it's stuck after a launch to get popped over it.
// HSM: Dormant / Hunting{Stalking{Moving, Watched}, Retreating{Fleeing, Frozen}, Dodging,
//      Launching{Windup, Flight}, Recovering} / Shattered
// Once awake it always knows where you are and paths to you around walls. The longer you go
// without looking at it the closer it creeps, and when you're on the move it runs ahead to a
// corner on your route (EQS AmbushPos) and waits to jump you as you come round.
// Stalking and fleeing are steered by the ML policy up close if there is one, otherwise by
// utility (CloseIn / CatchUp / Ambush / Lurk / HoldBack) and an EQS flee query.
[RequireComponent(typeof(NavMeshAgent))]
public class Stillwalker : EnemyAgent, IZipTarget
{
    [Header("Wake")]
    [Tooltip("Wakes when you come this close (through walls), when it sees you, or when it hears you.")]
    public float wakeRadius = 25f;

    [Header("Watched")]
    [Tooltip("Within this many degrees of the centre of your screen counts as looking at it.")]
    public float seenAngle = 35f;
    public float seenRange = 70f;

    [Header("Stalking")]
    public float creepSpeed = 2.2f;
    [Tooltip("Distance band it tries to stay in.")]
    public float stalkMin = 9f;
    public float stalkMax = 16f;

    [Header("Closing In")]
    [Tooltip("How close it creeps once you've gone Close In Time without looking at it.")]
    public float closeInMin = 5f;
    public float closeInTime = 8f;
    [Tooltip("How much of that build-up it keeps each time you spot it.")]
    [Range(0f, 1f)] public float spottedTensionKeep = 0.5f;
    [Tooltip("Speed while you're facing away or it's out of your sight.")]
    public float sneakSpeed = 5f;
    [Tooltip("Degrees off the centre of your view that count as facing away from it.")]
    public float lookAwayAngle = 100f;
    [Tooltip("Within this (and unseen) it pounces without waiting for you to land.")]
    public float pounceRange = 7f;
    [Tooltip("Plays a stone scrape the first time it gets this close behind you.")]
    public float creepCueDistance = 9f;
    public float creepCueCooldown = 8f;

    [Header("Ambush")]
    [Tooltip("Seconds ahead along your route it looks for a hiding spot.")]
    public float ambushLookahead = 2.5f;
    [Tooltip("You need to be moving at least this fast for it to try to cut you off.")]
    public float ambushMinPlayerSpeed = 5f;
    public float ambushMaxDistance = 45f;
    [Tooltip("Jumps you from its hiding spot once you're this close, landing or not.")]
    public float ambushLaunchRange = 12f;
    public float ambushMaxTravel = 4f;
    public float ambushMaxWait = 6f;
    public float ambushCooldown = 4f;

    [Header("Chase")]
    [Tooltip("Speed when you're just past its range band.")]
    public float catchUpSpeed = 10f;
    [Tooltip("Extra speed for every metre you are beyond the band.")]
    public float speedPerMetre = 0.6f;
    public float maxChaseSpeed = 26f;
    [Tooltip("The ML policy only steers within this distance (what it was trained at). Beyond it the chase rule runs.")]
    public float policyMaxDistance = 22f;

    [Header("Launch")]
    public float launchRange = 20f;
    public float launchWindup = 0.2f;
    public float launchSpeed = 24f;
    public float launchArc = 2.5f;
    public float launchCooldown = 2.5f;
    [Tooltip("Standing on the ground in range this long triggers a launch even without a fresh landing.")]
    public float groundedTooLong = 1.2f;
    public float launchDamage = 55f;
    public float impactRadius = 1.6f;
    public float knockback = 11f;
    public float knockUp = 6f;
    [Tooltip("Stuck in place after a launch. A good moment to hit it.")]
    public float recoverTime = 1f;

    [Header("Contact")]
    public float contactDamage = 25f;
    public float contactRadius = 0.55f;

    [Header("Cracks")]
    public int cracksToShatter = 4;
    [Tooltip("Cracks heal if this long passes without a new one.")]
    public float crackHealDelay = 3f;
    [Tooltip("Melee only cracks it if you're falling at least this fast.")]
    public float minFallSpeed = 1.5f;
    public int shatterPieces = 16;
    public float pieceLifetime = 5f;

    [Header("Hurt")]
    [Tooltip("How far it jerks away when cracked, plus flinchPerCrack for each crack it has.")]
    public float flinchDistance = 3f;
    public float flinchPerCrack = 1.5f;
    [Tooltip("Retreat speed, plus fleeSpeedPerCrack for each crack.")]
    public float fleeSpeed = 8f;
    public float fleeSpeedPerCrack = 2f;
    [Tooltip("Distance it wants from you while cracked, plus retreatPerCrack for each crack.")]
    public float retreatDistance = 16f;
    public float retreatPerCrack = 8f;
    [Tooltip("While retreating it only launches if you land this close.")]
    public float cornerDistance = 6f;

    [Header("One Hit From Death")]
    [Tooltip("One crack from shattering it runs flat out, even while you look at it, and stops launching.")]
    public bool boltWhenOneHitAway = true;
    public float boltSpeed = 18f;

    [Header("Policy")]
    [Tooltip("While hurt the policy steers out to this distance (training covers it).")]
    public float policyMaxRetreatDistance = 50f;
    [Tooltip("Policy moves shorter than this count as standing still.")]
    public float policyDeadzone = 0.25f;
    [Tooltip("If the policy stands still this long while you can't see it, the rules take over for Policy Bench Time.")]
    public float policyStallTime = 0.6f;
    public float policyBenchTime = 2.5f;
    [Tooltip("The model was trained to hold about 12m. Once it's been unseen long enough to want to be closer than this, the rules take over and creep in.")]
    public float policyHandOverDistance = 10f;
    [Tooltip("How far the escape probes look in each of the 8 directions.")]
    public float escapeProbeLength = 12f;
    [Tooltip("Seconds the player profile averages over.")]
    public float profileWindow = 6f;

    [Header("Dodging")]
    public float dodgeDistance = 3.5f;
    public float dodgeTime = 0.18f;
    [Tooltip("Sidesteps a zip aimed at it once you get this close.")]
    public float zipDodgeDistance = 6f;
    [Tooltip("Damage when it swats you after a zip, or when you zip into it and it had no room to dodge.")]
    public float zipSwatDamage = 45f;
    public float zipSwatRadius = 4f;
    public float zipArrivalRadius = 1.4f;
    [Tooltip("Upward speed when you zip into it while it's stuck after a launch.")]
    public float zipBounceUp = 13f;
    [Tooltip("Chance per crack it already has to sidestep a pogo coming down on it.")]
    [Range(0f, 1f)] public float pogoDodgeChancePerCrack = 0.34f;
    [Tooltip("Never more likely than this, so a pogo always has a chance.")]
    [Range(0f, 1f)] public float maxPogoDodgeChance = 0.8f;
    public float pogoDodgeCooldown = 2.5f;

    [Header("Landing Marker")]
    public Color markerColor = new Color(1f, 0.15f, 0.1f, 0.9f);

    [Header("Visuals (auto-found)")]
    public Transform visualRoot;
    public Transform head;
    public Renderer[] eyes;
    public Color eyeColor = new Color(1f, 0.15f, 0.1f, 1f);

    [Header("Audio (optional, synthesised if empty)")]
    public AudioClip stepSound;
    public AudioClip crackSound;
    public AudioClip launchSound;

    public IStillwalkerPolicy Policy { get; set; }
    public event Action<StillwalkerEvent> Events;

    public int Cracks { get; private set; }
    public bool Seen => Board.Get(BB.Seen, false);
    public bool LaunchReady => Time.time - lastLaunchTime > launchCooldown;
    public bool Hurt => Cracks > 0;
    public bool OneHitAway => Hurt && Cracks >= cracksToShatter - 1;
    bool Desperate => boltWhenOneHitAway && OneHitAway;
    public bool Exposed { get; private set; }
    public PlayerProfile Profile => profile;

    // set by the training agent for its simulated axe throws
    public float TrainingAxeThreat { get; set; }

    HState dormant, hunting, stalking, moving, watched, retreating, fleeing, frozen, dodging,
           launching, windup, flight, recovering, shattered;

    enum DodgeKind { Flinch, Zip, Pogo }
    DodgeKind dodgeKind;
    Vector3 dodgeAway;
    Vector3 dodgeFrom, dodgeTo;
    bool zipDodging;
    float swatUntil = -99f;
    bool swatted;
    float nextPogoRoll;

    EQSQuery fleeQuery;
    float nextFleeQuery;

    StillwalkerCracks crackFX;
    PlayerProfile profile;
    float lastProfileDist = -1f;
    readonly float[] escapeRoom = new float[8];
    readonly bool[] escapeHidden = new bool[8];
    StillwalkerSenses cachedSenses;
    float cachedSensesTime = -1f;
    Vector3 lastNavPos;
    bool hasNavPos;

    // path distance and closing in
    NavMeshPath navPath;
    float pathDist;
    float nextPathTime;
    float tension;
    bool wasSeen;
    bool heardPlayer;
    float nextCreepCue;

    // ambush
    EQSQuery ambushQuery;
    Vector3 ambushCenter;
    bool hasAmbush;
    float ambushStartedAt = -1f;
    float ambushArrivedAt = -1f;
    float nextAmbushTime;
    float nextAmbushSearch;

    // policy hand-over
    float policyStill;
    float policyBenchedUntil;
    bool rulesSteering = true;

    UtilityBrain utility;
    UtilityAction uCreep, uCatchUp, uHoldBack, uAmbush, uLurk;
    readonly Dictionary<UtilityAction, BehaviourTree> intentTrees = new Dictionary<UtilityAction, BehaviourTree>();
    BehaviourTree activeTree;

    float lastLaunchTime = -99f;
    float lastCrackTime = -99f;
    float nextContactTime;
    float stepSoundTimer;
    Vector3 policyDir;
    bool hitThisLaunch;
    Vector3 flightFrom, flightTo;
    float flightTime;
    PlayerMotionTracker subscribed;

    Quaternion headBaseLocal;
    Quaternion visualBaseLocal;
    Material[] eyeMats;
    float eyeGlow;
    float crackShake;
    LineRenderer marker;

    public override Vector3 HeadPosition => head != null ? head.position : transform.position + Vector3.up * 2f;
    public override Vector3 ChestPosition => transform.position + Vector3.up * 1.3f;

    Vector3 LaunchTarget
    {
        get => Board.Get(BB.LaunchTarget, transform.position);
        set => Board.Set(BB.LaunchTarget, value);
    }

    // ---------------------------------------------------------------- setup

    protected override void Awake()
    {
        base.Awake();
        sightAngle = 360f;

        if (visualRoot == null)
        {
            visualRoot = transform.Find("Model");
            if (visualRoot == null) visualRoot = transform.Find("Placeholder");
        }
        if (visualRoot != null) visualBaseLocal = visualRoot.localRotation;

        if (head == null)
        {
            if (anim != null && anim.isHuman) head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (head == null && visualRoot != null) head = visualRoot.Find("Head");
        }
        if (head != null) headBaseLocal = head.localRotation;

        if (eyes == null || eyes.Length == 0)
        {
            var found = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(r);
            eyes = found.ToArray();
        }
        eyeMats = new Material[eyes.Length];
        for (int i = 0; i < eyes.Length; i++)
        {
            eyeMats[i] = EnemyVisuals.Unlit(eyeColor);
            eyes[i].sharedMaterial = eyeMats[i];
        }

        crackFX = GetComponent<StillwalkerCracks>();
        if (crackFX == null) crackFX = gameObject.AddComponent<StillwalkerCracks>();
        crackFX.Setup(transform, visualRoot, cracksToShatter);
        ResetProfile();

        marker = EnemyVisuals.MakeLine(null, "StillwalkerMarker", markerColor, 0.08f);
        marker.positionCount = 33;
        marker.loop = false;

        navPath = new NavMeshPath();

        if (nav != null)
        {
            nav.acceleration = 60f;
            nav.angularSpeed = 0f;
            nav.stoppingDistance = 0.5f;
            nav.autoBraking = true;
        }
    }

    protected override void Start()
    {
        base.Start();
        Subscribe();
        GruntBase.MatchGrappleTarget(director != null ? director.Grapple : null, gameObject);
    }

    protected override void OnDestroy()
    {
        if (subscribed != null) subscribed.Landed -= OnPlayerLanded;
        if (marker != null) Destroy(marker.gameObject);
        base.OnDestroy();
    }

    void Subscribe()
    {
        if (player == subscribed) return;
        if (subscribed != null) subscribed.Landed -= OnPlayerLanded;
        subscribed = player;
        if (subscribed != null) subscribed.Landed += OnPlayerLanded;
    }

    void OnPlayerLanded()
    {
        Board.Set(BB.LandedAt, Time.time);
        if (Brain == null || !Brain.IsInState(hunting)) return;

        bool inRange = DistToPlayer() <= launchRange;
        Emit(inRange ? StillwalkerEvent.PlayerLandedInRange : StillwalkerEvent.PlayerLandedOutOfRange);
    }

    // ---------------------------------------------------------------- brain

    protected override HStateMachine BuildBrain()
    {
        DebugQueries.Clear();
        BuildUtility();
        BuildTrees();
        BuildFleeQuery();
        BuildAmbushQuery();

        HState root = new HState("Stillwalker");
        dormant = root.Add(new HState("Dormant"), initial: true);
        hunting = root.Add(new HState("Hunting"));
        stalking = hunting.Add(new HState("Stalking"), initial: true);
        moving = stalking.Add(new HState("Moving"), initial: true);
        watched = stalking.Add(new HState("Watched"));
        retreating = hunting.Add(new HState("Retreating"));
        fleeing = retreating.Add(new HState("Fleeing"), initial: true);
        frozen = retreating.Add(new HState("Frozen"));
        dodging = hunting.Add(new HState("Dodging"));
        launching = hunting.Add(new HState("Launching"));
        windup = launching.Add(new HState("Windup"), initial: true);
        flight = launching.Add(new HState("Flight"));
        recovering = hunting.Add(new HState("Recovering"));
        shattered = root.Add(new HState("Shattered"));

        dormant.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.15f; heardPlayer = false; };
        // once awake it never goes back to sleep and always knows where you are
        dormant.To(hunting, () => heardPlayer || PlayerVisible || DistToPlayer() < wakeRadius);

        stalking.To(retreating, () => Hurt);
        stalking.To(launching, ShouldLaunch);

        moving.OnEnter = () => { utility.Clear(); utility.Tick(0f, true); SwitchTree(); AnimSpeed(1f); eyeGlow = 0.8f; };
        moving.OnTick = TickMoving;
        moving.OnExit = () => { activeTree?.Abort(); activeTree = null; StopMoving(); };
        moving.To(watched, () => Seen);

        watched.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = InAmbush ? 0.3f : 0.5f; };
        watched.To(moving, () => !Seen);

        // cracked: get away until the cracks heal, still a statue while you watch unless it's nearly broken
        retreating.OnEnter = EndAmbush;
        retreating.To(stalking, () => !Hurt);
        retreating.To(launching, () => !Desperate && ShouldLaunch() && DistToPlayer() < cornerDistance);

        fleeing.OnEnter = () => { nextFleeQuery = 0f; AnimSpeed(1f); };
        fleeing.OnTick = TickFleeing;
        fleeing.OnExit = StopMoving;
        fleeing.To(frozen, () => Seen && !Desperate);

        frozen.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.6f; };
        frozen.To(fleeing, () => !Seen || Desperate);

        dodging.OnEnter = StartDodge;
        dodging.OnTick = TickDodge;
        dodging.OnExit = () => zipDodging = false;
        dodging.To(retreating, () => dodging.TimeInState >= dodgeTime && Hurt);
        dodging.To(stalking, () => dodging.TimeInState >= dodgeTime);

        launching.OnExit = () =>
        {
            lastLaunchTime = Time.time;
            marker.enabled = false;
        };

        windup.OnEnter = () =>
        {
            StopMoving();
            AnimSpeed(1f);
            AnimTrigger("LungeWindup");
            eyeGlow = 2f;
            hitThisLaunch = false;
            EndAmbush();
            EnemySounds.PlayAt(launchSound != null ? launchSound : EnemySounds.StoneCrack, transform.position, 1f, 0.55f);
        };
        windup.OnTick = dt =>
        {
            LaunchTarget = PredictedStrike();
            FaceTowards(LaunchTarget, 900f);
            ShowMarker(LaunchTarget, 1f);
        };
        windup.To(flight, () => windup.TimeInState >= launchWindup);

        flight.OnEnter = StartFlight;
        flight.OnTick = TickFlight;
        flight.To(recovering, () => flight.TimeInState >= flightTime);
        flight.OnExit = Impact;

        // stuck after landing, best time to pogo it
        recovering.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.25f; };
        recovering.To(retreating, () => recovering.TimeInState >= recoverTime && Hurt);
        recovering.To(stalking, () => recovering.TimeInState >= recoverTime);

        shattered.OnEnter = () => { StopMoving(); AnimSpeed(0f); marker.enabled = false; };

        return new HStateMachine(root);
    }

    bool ShouldLaunch()
    {
        if (!LaunchReady || player.IsDead || !player.OnGround || !PlayerVisible) return false;

        float dist = DistToPlayer();
        if (!TrainingRules)
        {
            // you walked round the corner it was waiting behind
            if (InAmbush && dist <= ambushLaunchRange) return true;
            // crept right up behind you
            if (!Seen && dist <= pounceRange && Brain.IsInState(moving)) return true;
        }

        if (dist > launchRange) return false;

        bool justLanded = Time.time - Board.Get(BB.LandedAt, -99f) < 0.25f;
        return justLanded || player.GroundedTime > groundedTooLong;
    }

    float DistToPlayer()
    {
        Vector3 d = player.Feet - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    bool PolicyReady => Policy != null && Policy.PolicyActive && !DebugIgnorePolicy;
    bool PolicyRetreats => PolicyReady && DistToPlayer() <= policyMaxRetreatDistance;
    bool TrainingRules => Policy != null && Policy.Training;

    // The policy was trained on open ground at close range. Behind walls, on a long detour,
    // while ambushing or when it has stalled, the rules drive instead.
    bool PolicyInControl
    {
        get
        {
            if (!PolicyReady || DistToPlayer() > policyMaxDistance) return false;
            if (TrainingRules) return true;
            return PlayerVisible && !hasAmbush && Time.time >= policyBenchedUntil
                   && PathDist <= policyMaxDistance * 1.4f && WantDistance() >= policyHandOverDistance;
        }
    }

    float ChaseSpeed()
    {
        float beyond = Mathf.Max(0f, PathDist - stalkMax);
        return Mathf.Min(maxChaseSpeed, catchUpSpeed + beyond * speedPerMetre);
    }

    // walking distance on the NavMesh, so a wall between you counts as the long way round
    public float PathDist => pathDist > 0f ? pathDist : DistToPlayer();

    void UpdatePathDistance()
    {
        if (Time.time < nextPathTime) return;
        nextPathTime = Time.time + 0.25f;

        float straight = DistToPlayer();
        pathDist = straight;
        if (!NavReady || navPath == null) return;
        if (!NavMesh.SamplePosition(player.Feet, out NavMeshHit hit, 4f, NavMesh.AllAreas)) return;
        if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, navPath)) return;
        if (navPath.status == NavMeshPathStatus.PathInvalid) return;

        Vector3[] corners = navPath.corners;
        float length = 0f;
        for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        if (corners.Length > 0) length += Vector3.Distance(corners[corners.Length - 1], hit.position);
        pathDist = Mathf.Max(straight, length);
        Board.Set(BB.PathDistance, Mathf.Round(pathDist));
    }

    // how far away it wants to be: the stalking band, shrinking the longer you ignore it
    float WantDistance()
    {
        float band = (stalkMin + stalkMax) * 0.5f;
        if (TrainingRules) return band;
        return Mathf.Lerp(band, closeInMin, Mathf.Clamp01(tension / Mathf.Max(0.1f, closeInTime)));
    }

    float Gap => PathDist - WantDistance();

    bool FacingAway => player.LookAngleTo(ChestPosition) > lookAwayAngle;

    float CloseInSpeed() => FacingAway || !Exposed ? sneakSpeed : creepSpeed;

    void UpdateTension(float dt)
    {
        if (Seen)
        {
            if (!wasSeen) tension *= spottedTensionKeep;
        }
        else tension = Mathf.Min(closeInTime, tension + dt);
        wasSeen = Seen;
    }

    // ---------------------------------------------------------------- utility

    void BuildUtility()
    {
        utility = new UtilityBrain { Interval = 0.3f, Stickiness = 1.15f };

        // follow the path in until it's as close as it wants to be
        uCreep = utility.Add(new UtilityAction("CloseIn"))
            .When(() => Gap > 1f)
            .Consider("Gap", () => Mathf.Clamp01(Gap / 10f), Curves.Rising(0.15f, 14f));

        uCatchUp = utility.Add(new UtilityAction("CatchUp", 1.3f))
            .Consider("TooFar", () => Mathf.InverseLerp(stalkMin, stalkMax * 2.5f, PathDist), Curves.Rising(0.3f, 10f));

        // close enough for now, keep still and wait for you to look away
        uLurk = utility.Add(new UtilityAction("Lurk", 0.6f))
            .When(() => Gap <= 1f);

        uAmbush = utility.Add(new UtilityAction("Ambush", 1.5f))
            .When(() => hasAmbush);

        uHoldBack = utility.Add(new UtilityAction("HoldBack", 1.2f))
            .When(() => !LaunchReady)
            .Consider("TooClose", () => DistToPlayer() / Mathf.Max(1f, closeInMin), Curves.Falling(0.5f, 12f));
    }

    void BuildTrees()
    {
        intentTrees.Clear();

        intentTrees[uCreep] = new BehaviourTree(BT.Action("CloseIn", () => Stalk(PathTarget(), CloseInSpeed())));
        intentTrees[uCatchUp] = new BehaviourTree(BT.Action("CatchUp", () => Stalk(PathTarget(), ChaseSpeed())));
        intentTrees[uLurk] = new BehaviourTree(BT.Action("Lurk", Lurk));
        intentTrees[uAmbush] = new BehaviourTree(BT.Action("Ambush", TickAmbush, StartAmbush));
        intentTrees[uHoldBack] = new BehaviourTree(BT.Action("BackOff", () => Stalk(StalkPoint(), creepSpeed * 1.8f)));
    }

    Vector3 StalkPoint()
    {
        Vector3 away = transform.position - player.Feet;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -player.CameraForward;
        float want = (stalkMin + stalkMax) * 0.5f;
        Vector3 p = player.Feet + away.normalized * want;
        Board.Set(BB.StalkTarget, p);
        return p;
    }

    // where you're standing, on the NavMesh, so the agent paths round walls to get there
    Vector3 PathTarget()
    {
        Vector3 p = NavMesh.SamplePosition(player.Feet, out NavMeshHit hit, 4f, NavMesh.AllAreas) ? hit.position : player.Feet;
        Board.Set(BB.StalkTarget, p);
        return p;
    }

    BTStatus Lurk()
    {
        StopMoving();
        FaceTowards(player.Feet, 200f);
        return BTStatus.Running;
    }

    BTStatus Stalk(Vector3 target, float speed)
    {
        MoveTo(target, speed);
        FaceTowards(player.Feet, 360f);
        return BTStatus.Running;
    }

    void SwitchTree()
    {
        activeTree?.Abort();
        activeTree = utility.Current != null && intentTrees.TryGetValue(utility.Current, out BehaviourTree t) ? t : null;
    }

    void TickMoving(float dt)
    {
        SearchForAmbush();

        // only use the policy within the distances it was trained at
        if (PolicyInControl && SteerByPolicy(dt, ChaseSpeed(), creepSpeed, false))
        {
            rulesSteering = false;
        }
        else
        {
            if (!rulesSteering)
            {
                rulesSteering = true;
                utility.Tick(0f, true);
                SwitchTree();
            }
            if (utility.Tick(dt) || activeTree == null) SwitchTree();
            if (activeTree != null && activeTree.Tick() != BTStatus.Running)
                if (utility.Tick(0f, true)) SwitchTree();
        }

        MoveFeedback(dt);
    }

    // false = the policy stalled out of sight and the rules should drive for a bit
    bool SteerByPolicy(float dt, float fastSpeed, float slowSpeed, bool forceFast)
    {
        Policy.Decide(Senses(), out Vector3 dir, out bool fast);
        dir.y = 0f;
        if (dir.magnitude > policyDeadzone)
        {
            policyStill = 0f;
            // smooth between decisions so it doesn't jitter
            policyDir = policyDir.sqrMagnitude > 0.001f
                ? Vector3.Slerp(policyDir, dir.normalized, 1f - Mathf.Exp(-10f * dt))
                : dir.normalized;
            MoveTo(transform.position + policyDir * 3f, fast || forceFast ? fastSpeed : slowSpeed);
        }
        else
        {
            policyDir = Vector3.zero;
            if (!TrainingRules && !Seen && !Hurt)
            {
                policyStill += dt;
                if (policyStill >= policyStallTime)
                {
                    policyStill = 0f;
                    policyBenchedUntil = Time.time + policyBenchTime;
                    return false;
                }
            }
            StopMoving();
        }
        FaceTowards(player.Feet, 360f);
        return true;
    }

    void MoveFeedback(float dt)
    {
        float speed = Velocity.magnitude;
        stepSoundTimer -= dt;
        if (speed > 0.5f && stepSoundTimer <= 0f)
        {
            stepSoundTimer = Mathf.Lerp(0.6f, 0.15f, speed / maxChaseSpeed);
            EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 0.8f,
                Mathf.Lerp(0.8f, 1.3f, speed / maxChaseSpeed));
        }
        AnimSpeed(Mathf.Clamp(speed / creepSpeed, 0.5f, 4f));
        AnimFloat("Speed", speed);
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    // cached per step, the escape probes are a few raycasts
    public StillwalkerSenses Senses()
    {
        if (cachedSensesTime == Time.time) return cachedSenses;
        cachedSensesTime = Time.time;

        Vector3 predicted;
        if (!player.IsGrounded && player.PredictLanding(out Vector3 land, out _)) predicted = land;
        else predicted = player.Predict(0.35f) - Vector3.up * player.FeetOffset;

        ProbeEscapes();

        cachedSenses = new StillwalkerSenses
        {
            toPlayerLocal = transform.InverseTransformDirection(player.Feet - transform.position),
            toPredictedLocal = transform.InverseTransformDirection(predicted - transform.position),
            playerVelocityLocal = transform.InverseTransformDirection(player.Velocity),
            playerHeightAbove = player.Feet.y - transform.position.y,
            playerVerticalSpeed = player.Velocity.y,
            playerOnGround = player.OnGround,
            playerGroundedTime = player.GroundedTime,
            playerAirTime = player.AirTime,
            distance = DistToPlayer(),

            playerSwinging = player.IsSwinging,
            playerZipping = player.IsZipping,
            playerWallRunning = player.IsWallRunning,
            playerSliding = player.IsCrouching,

            seen = Seen,
            lookAngle01 = player.LookAngleTo(ChestPosition) / 180f,
            exposed = Exposed,

            zipAtMe = ZipAimedAtMe(),
            axeThreat01 = AxeThreat(),

            cracks = Cracks,
            oneHitAway = OneHitAway,
            healProgress01 = Hurt ? Mathf.Clamp01((Time.time - lastCrackTime) / crackHealDelay) : 1f,
            launchReady = LaunchReady,
            myVelocityLocal = transform.InverseTransformDirection(Velocity),

            escapeRoom01 = escapeRoom,
            escapeHidden = escapeHidden,
            profile = profile
        };
        return cachedSenses;
    }

    // how much room there is to run in 8 directions, and whether 6m that way is out of your sight
    void ProbeEscapes()
    {
        for (int i = 0; i < 8; i++)
        {
            Vector3 dir = transform.rotation * (Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward);
            dir.y = 0f;
            dir.Normalize();

            float room = escapeProbeLength;
            if (NavReady && NavMesh.Raycast(transform.position, transform.position + dir * escapeProbeLength,
                    out NavMeshHit hit, NavMesh.AllAreas))
                room = hit.distance;

            escapeRoom[i] = room / escapeProbeLength;
            Vector3 spot = transform.position + dir * Mathf.Min(6f, room) + Vector3.up * 1.3f;
            escapeHidden[i] = !EQSTest.Clear(player.CameraPosition, spot, IgnoreForSight);
        }
    }

    bool ZipAimedAtMe()
    {
        Grappling g = director != null ? director.Grapple : null;
        return g != null && g.IsZipping && ReferenceEquals(g.ZipTarget, this);
    }

    // 1 = an axe is about to hit it
    float AxeThreat()
    {
        float best = Mathf.Clamp01(TrainingAxeThreat);
        IReadOnlyList<ThrownAxe> axes = ThrownAxe.Live;
        for (int i = 0; i < axes.Count; i++)
        {
            ThrownAxe axe = axes[i];
            if (axe == null || !axe.IsFlying) continue;

            Vector3 v = axe.Velocity;
            float speed = v.magnitude;
            if (speed < 1f) continue;

            Vector3 to = ChestPosition - axe.HeadPosition;
            float along = Vector3.Dot(to, v / speed);
            if (along <= 0f || along > 35f) continue;
            if ((to - v / speed * along).magnitude > 2.5f) continue;

            best = Mathf.Max(best, 1f - Mathf.Clamp01(along / speed / 1.2f));
        }
        return best;
    }

    // ---------------------------------------------------------------- player profile

    void ResetProfile()
    {
        profile = new PlayerProfile { approach01 = 0.5f };
        lastProfileDist = -1f;
    }

    void UpdateProfile(float dt)
    {
        if (dt <= 0f) return;
        float k = 1f - Mathf.Exp(-dt / Mathf.Max(0.1f, profileWindow));

        float dist = DistToPlayer();
        float closing = lastProfileDist >= 0f ? (lastProfileDist - dist) / dt : 0f;
        lastProfileDist = dist;

        profile.speed01 = Mathf.Lerp(profile.speed01, Mathf.Clamp01(player.Speed / 20f), k);
        profile.airborne01 = Mathf.Lerp(profile.airborne01, player.IsGrounded ? 0f : 1f, k);
        profile.watching01 = Mathf.Lerp(profile.watching01, Seen ? 1f : 0f, k);
        profile.approach01 = Mathf.Lerp(profile.approach01, Mathf.Clamp01(0.5f + closing / 30f), k);

        // jumps up fast when you come down on it, forgets slowly
        profile.aggression01 = ComingDownOnMe()
            ? Mathf.MoveTowards(profile.aggression01, 1f, dt * 1.5f)
            : Mathf.MoveTowards(profile.aggression01, 0f, dt / (profileWindow * 2f));
    }

    bool ComingDownOnMe()
    {
        if (player.IsGrounded || player.Velocity.y > 0f) return false;
        Vector3 d = player.Feet - transform.position;
        float height = d.y;
        d.y = 0f;
        return d.magnitude < 4f && height > 0.5f && height < 8f;
    }

    // ---------------------------------------------------------------- ambush

    bool InAmbush => hasAmbush && ambushArrivedAt >= 0f;

    // somewhere out of your sight that looks onto where you'll be in a couple of seconds,
    // and that it can reach first: a corner on your route
    void BuildAmbushQuery()
    {
        ambushQuery = new EQSQuery("AmbushPos", EQSGen.Ring(() => ambushCenter, 3f, 11f, 3, 10))
            .Add(EQSTest.OnNavMesh(2f))
            .Add(EQSTest.LineOfSight(() => player.CameraPosition, 1.3f, false, IgnoreForSight))
            .Add(EQSTest.LineOfSight(() => ambushCenter + Vector3.up, 1.3f, true, IgnoreForSight))
            .Add(EQSTest.Custom("GetsThereFirst", i =>
            {
                float mine = Vector3.Distance(i.Point, transform.position) / Mathf.Max(1f, maxChaseSpeed * 0.7f);
                float yours = Vector3.Distance(i.Point, player.Feet) / Mathf.Max(1f, player.Speed);
                return mine < yours ? 1f : -1f;
            }, true))
            .Add(EQSTest.Custom("NearRoute", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, ambushCenter) / 12f)), 1.5f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / ambushMaxDistance)), 0.5f);

        DebugQueries.Add(ambushQuery);
    }

    void SearchForAmbush()
    {
        if (TrainingRules || hasAmbush || Hurt || Time.time < nextAmbushTime || Time.time < nextAmbushSearch) return;
        nextAmbushSearch = Time.time + 0.6f;

        if (player.Speed < ambushMinPlayerSpeed || !player.IsGrounded) return;
        float dist = PathDist;
        if (dist < ambushLaunchRange || dist > ambushMaxDistance) return;

        Vector3 flat = player.Velocity;
        flat.y = 0f;
        ambushCenter = player.Feet + flat.normalized * Mathf.Clamp(flat.magnitude * ambushLookahead, 6f, 25f);
        if (!NavMesh.SamplePosition(ambushCenter, out NavMeshHit onMesh, 3f, NavMesh.AllAreas)) return;
        ambushCenter = onMesh.position;

        EQSItem best = ambushQuery.Run();
        if (best == null) return;

        hasAmbush = true;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
        Board.Set(BB.AmbushPos, best.Point);
    }

    void StartAmbush()
    {
        if (ambushStartedAt < 0f) ambushStartedAt = Time.time;
    }

    BTStatus TickAmbush()
    {
        if (!hasAmbush) return BTStatus.Failure;
        Vector3 spot = Board.Get(BB.AmbushPos, transform.position);

        if (ambushArrivedAt < 0f)
        {
            Vector3 d = spot - transform.position;
            d.y = 0f;
            if (d.magnitude > 1.2f)
            {
                if (Time.time - ambushStartedAt > ambushMaxTravel)
                {
                    EndAmbush();
                    return BTStatus.Failure;
                }
                MoveTo(spot, Mathf.Max(sneakSpeed, ChaseSpeed()));
                FaceTowards(player.Feet, 360f);
                return BTStatus.Running;
            }
            ambushArrivedAt = Time.time;
        }

        // in position: dead still, eyes dim, watching the corner
        StopMoving();
        eyeGlow = 0.3f;
        FaceTowards(player.Feet, 200f);
        if (Time.time - ambushArrivedAt > ambushMaxWait || PathDist > ambushMaxDistance)
        {
            EndAmbush();
            return BTStatus.Failure;
        }
        return BTStatus.Running;
    }

    void EndAmbush()
    {
        if (hasAmbush) nextAmbushTime = Time.time + ambushCooldown;
        hasAmbush = false;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
    }

    protected override void OnHeard(NoiseEvent noise) => heardPlayer = true;

    // a stone scrape the first time it gets close behind you, so a jump from behind is never silent
    void CreepCue()
    {
        if (Seen || Time.time < nextCreepCue || Velocity.magnitude < 0.5f || PathDist > creepCueDistance) return;
        nextCreepCue = Time.time + creepCueCooldown;
        EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 1f, 0.45f);
        EnemySounds.PlayAt(EnemySounds.StoneCrack, transform.position, 0.35f, 0.5f);
    }

    // ---------------------------------------------------------------- retreat

    float FleeSpeed() => Desperate ? boltSpeed : fleeSpeed + fleeSpeedPerCrack * Cracks;
    float RetreatDistance() => retreatDistance + retreatPerCrack * Cracks;

    // somewhere away from you and ideally out of sight, so the cracks get a chance to heal
    void BuildFleeQuery()
    {
        fleeQuery = new EQSQuery("FleePos", EQSGen.Ring(() => transform.position, 6f, 20f, 3, 12))
            .Add(EQSTest.OnNavMesh(2f))
            .Add(EQSTest.LineOfSight(() => player.CameraPosition, 1.3f, false, IgnoreForSight, false), 1.5f)
            .Add(EQSTest.Custom("FarFromPlayer", i =>
            {
                Vector3 d = i.Point - player.Feet;
                d.y = 0f;
                return Mathf.Clamp01(d.magnitude / RetreatDistance());
            }), 2f)
            .Add(EQSTest.Custom("AwayFromPlayer", i =>
            {
                Vector3 go = i.Point - transform.position;
                go.y = 0f;
                if (go.sqrMagnitude < 0.01f) return 0.5f;
                return (Vector3.Dot(go.normalized, Flat(transform.position - player.Feet)) + 1f) * 0.5f;
            }), 1.5f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / 25f)), 0.5f);

        DebugQueries.Add(fleeQuery);
    }

    void TickFleeing(float dt)
    {
        eyeGlow = Desperate ? 1.2f + Mathf.Sin(Time.time * 30f) * 0.6f : 1f;

        if (PolicyRetreats)
        {
            SteerByPolicy(dt, FleeSpeed(), creepSpeed * 1.5f, Desperate);
            MoveFeedback(dt);
            return;
        }

        // far enough and can't see you: wait there for the cracks to heal
        if (DistToPlayer() >= RetreatDistance() * 0.9f && !PlayerVisible)
        {
            StopMoving();
        }
        else
        {
            if (Time.time >= nextFleeQuery || Arrived(1f))
            {
                nextFleeQuery = Time.time + 0.5f;
                EQSItem best = fleeQuery.Run();
                Board.Set(BB.FleePos, best != null
                    ? best.Point
                    : transform.position + Flat(transform.position - player.Feet) * 6f);
            }
            MoveTo(Board.Get(BB.FleePos, transform.position), FleeSpeed());
        }

        // backs away, never turns its back on you
        FaceTowards(player.Feet, 360f);
        MoveFeedback(dt);
    }

    // ---------------------------------------------------------------- dodging

    bool CanDodge => Brain != null && !IsDead
                     && !Brain.IsInState(launching) && !Brain.IsInState(recovering)
                     && !Brain.IsInState(dodging) && !Brain.IsInState(shattered);

    void Dodge(DodgeKind kind, Vector3 away)
    {
        dodgeKind = kind;
        dodgeAway = away;
        if (kind == DodgeKind.Zip)
        {
            swatted = false;
            swatUntil = Time.time + 0.6f;
        }
        Brain.Request(dodging);
    }

    void StartDodge()
    {
        activeTree?.Abort();
        activeTree = null;
        StopMoving();

        Vector3 dir = new Vector3(dodgeAway.x, 0f, dodgeAway.z);
        if (dir.sqrMagnitude < 0.01f) dir = transform.right * (UnityEngine.Random.value < 0.5f ? 1f : -1f);
        dir.Normalize();

        float dist = dodgeKind == DodgeKind.Flinch ? flinchDistance + flinchPerCrack * Cracks : dodgeDistance;
        dodgeFrom = transform.position;
        dodgeTo = PickDash(dir, dist) ?? transform.position;

        // if there was no room the zip carries on and you hit it face first instead
        zipDodging = dodgeKind == DodgeKind.Zip && (dodgeTo - dodgeFrom).sqrMagnitude > 0.25f;

        eyeGlow = 2f;
        AnimTrigger("Dodge");
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 1f, 0.65f);
        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.AirPuff(transform.position + Vector3.up * 0.3f, -dir, 0.5f);
        Emit(StillwalkerEvent.Dodged);
    }

    void TickDodge(float dt)
    {
        float t = Mathf.Clamp01(dodging.TimeInState / dodgeTime);
        Vector3 p = Vector3.Lerp(dodgeFrom, dodgeTo, 1f - (1f - t) * (1f - t));
        if (NavReady) nav.Warp(p);
        else transform.position = p;
        FaceTowards(player.Feet, 900f);
    }

    // tries the direction it wants first, then fans out
    static readonly float[] dashAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };

    Vector3? PickDash(Vector3 dir, float dist)
    {
        foreach (float a in dashAngles)
        {
            Vector3 target = transform.position + Quaternion.Euler(0f, a, 0f) * dir * dist;
            if (!NavReady) return target;
            if (!NavMesh.Raycast(transform.position, target, out NavMeshHit hit, NavMesh.AllAreas)) return target;
            if (hit.distance > dist * 0.5f) return hit.position;
        }
        return null;
    }

    bool ZipIncoming()
    {
        Grappling g = director != null ? director.Grapple : null;
        if (g == null || !g.IsZipping || !ReferenceEquals(g.ZipTarget, this) || !CanDodge) return false;
        return Vector3.Distance(player.Center, ChestPosition) < zipDodgeDistance;
    }

    // only once it's cracked, more likely the more cracked it is
    bool PogoIncoming()
    {
        if (!Hurt || !CanDodge || Time.time < nextPogoRoll) return false;
        if (player.IsGrounded || player.Velocity.y > -minFallSpeed) return false;

        Vector3 d = player.Feet - transform.position;
        float height = d.y;
        d.y = 0f;
        if (d.magnitude > 2.5f || height < 0.8f || height > 5f) return false;

        nextPogoRoll = Time.time + 0.6f;
        if (UnityEngine.Random.value > Mathf.Min(maxPogoDodgeChance, pogoDodgeChancePerCrack * Cracks)) return false;
        nextPogoRoll = Time.time + pogoDodgeCooldown;
        return true;
    }

    void Swat()
    {
        if (swatted) return;
        swatted = true;
        HitPlayer(zipSwatDamage);
        EnemySounds.PlayAt(crackSound != null ? crackSound : EnemySounds.StoneCrack, ChestPosition, 1f, 0.45f);
        Emit(StillwalkerEvent.Swatted);
    }

    // ---------------------------------------------------------------- zip target

    public bool ZipTargetValid => !IsDead && isActiveAndEnabled && !zipDodging;
    public Vector3 ZipPoint => ChestPosition;
    public float ZipArrivalRadius => zipArrivalRadius;

    public bool OnZipArrive(FirstPersonCharacterController controller, Rigidbody body)
    {
        if (Brain != null && Brain.IsInState(recovering))
        {
            // stuck after a launch: you bounce off and end up right over it, ready to pogo
            Vector3 v = body.linearVelocity;
            body.linearVelocity = new Vector3(v.x, 0f, v.z) * 0.15f + Vector3.up * zipBounceUp;
            controller.SuppressJumpHold();
            IgnorePlayerFor(0.3f);

            EnemySounds.PlayAt(EnemySounds.StoneStep, ChestPosition, 1f, 0.6f);
            JuiceFX fx = JuiceFX.Get();
            if (fx != null) fx.AirPuff(HeadPosition, Vector3.up, 0.7f);
            if (CameraShaker.Instance != null)
                CameraShaker.Instance.Impact(0.3f, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0f, 0f), 6f);
            return true;
        }

        // asleep or boxed in, so it couldn't dodge
        swatted = false;
        Swat();
        if (Brain != null && Brain.IsInState(dormant)) Brain.Request(hunting);
        return true;
    }

    // ---------------------------------------------------------------- launch

    Vector3 PredictedStrike()
    {
        float t = Mathf.Clamp(DistToPlayer() / launchSpeed, 0.25f, 0.9f);
        Vector3 p = player.Predict(t) - Vector3.up * player.FeetOffset;
        if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)
            && !IsOwnCollider(hit.collider) && hit.rigidbody == null)
            p = hit.point;
        return p;
    }

    void StartFlight()
    {
        flightFrom = transform.position;
        flightTo = LaunchTarget;

        Vector3 d = flightTo - flightFrom;
        if (d.magnitude > launchRange * 1.2f) flightTo = flightFrom + d.normalized * launchRange * 1.2f;

        flightTime = Mathf.Clamp(Vector3.Distance(flightFrom, flightTo) / launchSpeed, 0.25f, 0.9f);
        if (nav != null) nav.enabled = false;

        AnimTrigger("Lunge");
        eyeGlow = 2f;
        ShowMarker(flightTo, 1f);
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 1f, 0.5f);
    }

    void TickFlight(float dt)
    {
        float t = Mathf.Clamp01(flight.TimeInState / flightTime);
        Vector3 p = Vector3.Lerp(flightFrom, flightTo, t);
        p.y += Mathf.Sin(t * Mathf.PI) * launchArc;
        transform.position = p;
        transform.rotation = Quaternion.LookRotation(Flat(flightTo - flightFrom), Vector3.up);

        ShowMarker(flightTo, 1f - t * 0.5f);

        if (!hitThisLaunch && TouchingPlayer())
        {
            hitThisLaunch = true;
            HitPlayer(launchDamage);
        }
    }

    void Impact()
    {
        marker.enabled = false;
        if (IsDead) return;

        SnapToGround();

        if (!hitThisLaunch)
        {
            Vector3 d = player.Feet - transform.position;
            float vertical = Mathf.Abs(d.y);
            d.y = 0f;
            if (d.magnitude <= impactRadius && vertical < 1.2f)
            {
                hitThisLaunch = true;
                HitPlayer(launchDamage);
            }
        }

        Emit(hitThisLaunch ? StillwalkerEvent.LaunchHit : StillwalkerEvent.LaunchMissed);

        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.LandDust(transform.position, Vector3.up, 1f);
        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTraumaAtPoint(transform.position, 0.6f, 5f, 25f);
    }

    // Puts it back on the NavMesh after a launch or a teleport. Finds the mesh before turning
    // the agent on: switching it on (or warping) off the mesh fails and leaves it stuck for good.
    void SnapToGround()
    {
        if (nav == null) return;

        Vector3 onMesh;
        if (FindNavMesh(transform.position, out Vector3 found)) onMesh = found;
        else if (hasNavPos) onMesh = lastNavPos;
        else return;

        nav.enabled = false;
        transform.position = onMesh;
        nav.enabled = true;
        if (nav.isOnNavMesh) nav.Warp(onMesh);
    }

    static readonly float[] snapRadii = { 2f, 6f, 15f, 30f };

    static bool FindNavMesh(Vector3 near, out Vector3 onMesh)
    {
        foreach (float r in snapRadii)
        {
            if (NavMesh.SamplePosition(near, out NavMeshHit hit, r, NavMesh.AllAreas))
            {
                onMesh = hit.position;
                return true;
            }
        }
        onMesh = near;
        return false;
    }

    public bool OnNavMesh => NavReady;

    bool IsOwnCollider(Collider c)
    {
        foreach (Collider o in ownColliders) if (o == c) return true;
        return false;
    }

    void ShowMarker(Vector3 center, float intensity)
    {
        marker.enabled = true;
        float pulse = 1f + Mathf.Sin(Time.time * 18f) * 0.08f;
        float r = impactRadius * pulse;
        for (int i = 0; i <= 32; i++)
        {
            float a = i / 32f * Mathf.PI * 2f;
            marker.SetPosition(i, center + new Vector3(Mathf.Cos(a) * r, 0.05f, Mathf.Sin(a) * r));
        }
        Color c = markerColor;
        c.a *= Mathf.Clamp01(intensity);
        marker.startColor = c;
        marker.endColor = c;
    }

    // ---------------------------------------------------------------- per frame

    protected override void Update()
    {
        Subscribe();
        base.Update();
        if (IsDead || player == null || Brain == null) return;

        Exposed = EQSTest.Clear(player.CameraPosition, ChestPosition, IgnoreForSight);
        bool seen = Exposed
                    && player.LookAngleTo(ChestPosition) < seenAngle
                    && Vector3.Distance(player.CameraPosition, ChestPosition) < seenRange;
        Board.Set(BB.Seen, seen);
        if (Brain.IsInState(hunting))
        {
            UpdateProfile(Time.deltaTime);
            UpdatePathDistance();
            UpdateTension(Time.deltaTime);
            if (Brain.IsInState(stalking) && !TrainingRules) CreepCue();
        }

        // landing marker
        if (!Brain.IsInState(launching))
        {
            bool warn = Brain.IsInState(hunting) && LaunchReady && !player.IsGrounded
                        && DistToPlayer() <= launchRange
                        && player.PredictLanding(out Vector3 land, out _)
                        && Vector3.Distance(land, transform.position) <= launchRange;
            if (warn)
            {
                player.PredictLanding(out Vector3 spot, out _);
                ShowMarker(spot, 0.5f);
            }
            else marker.enabled = false;
        }

        if (Cracks > 0 && Time.time - lastCrackTime > crackHealDelay)
        {
            Cracks = 0;
            crackFX.Heal();
            EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 0.5f, 1.6f);
            Emit(StillwalkerEvent.Healed);
        }

        if (ZipIncoming())
        {
            Vector3 approach = player.Velocity;
            approach.y = 0f;
            if (approach.sqrMagnitude < 0.01f) approach = transform.position - player.Center;
            Dodge(DodgeKind.Zip, Vector3.Cross(Vector3.up, approach.normalized) * (UnityEngine.Random.value < 0.5f ? 1f : -1f));
        }
        else if (PogoIncoming())
        {
            Vector3 away = player.PredictLanding(out Vector3 land, out _) ? transform.position - land : Vector3.zero;
            Dodge(DodgeKind.Pogo, away);
        }

        // reaches out and swats you as the zip carries you past
        if (Time.time < swatUntil && !swatted && Vector3.Distance(player.Center, ChestPosition) < zipSwatRadius)
            Swat();

        if (NavReady)
        {
            lastNavPos = transform.position;
            hasNavPos = true;
        }

        UpdateEyes();
        if (crackShake > 0f) crackShake = Mathf.Max(0f, crackShake - Time.deltaTime * 4f);
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        if (IsDead || player == null || Brain == null) return;

        if (Brain.IsInState(moving) && Time.time >= nextContactTime && TouchingPlayer())
        {
            nextContactTime = Time.time + 1f;
            HitPlayer(contactDamage);
            Emit(StillwalkerEvent.ContactHit);
        }
    }

    void LateUpdate()
    {
        if (IsDead || player == null) return;

        // head always follows you
        if (head != null)
        {
            Vector3 look = player.CameraPosition - head.position;
            if (look.sqrMagnitude > 0.01f)
            {
                Quaternion want = Quaternion.LookRotation(look.normalized, Vector3.up);
                Quaternion baseRot = head.parent != null ? head.parent.rotation * headBaseLocal : headBaseLocal;
                Quaternion limited = Quaternion.RotateTowards(baseRot, want, 75f);
                head.rotation = anim != null && anim.enabled
                    ? Quaternion.Slerp(head.rotation, limited, 0.85f)
                    : Quaternion.Slerp(head.rotation, limited, 1f - Mathf.Exp(-10f * Time.deltaTime));
            }
        }

        if (visualRoot != null)
        {
            Vector3 shake = crackShake > 0f ? UnityEngine.Random.insideUnitSphere * crackShake * 4f : Vector3.zero;
            visualRoot.localRotation = visualBaseLocal * Quaternion.Euler(shake);
        }
    }

    void UpdateEyes()
    {
        float pulse = 1f + Mathf.Sin(Time.time * 9f) * 0.08f;
        Color c = eyeColor * Mathf.Clamp(eyeGlow * pulse, 0.05f, 2f);
        c.a = 1f;
        foreach (Material m in eyeMats) EnemyVisuals.SetColor(m, c);
    }

    // ---------------------------------------------------------------- hitting the player

    bool TouchingPlayer()
    {
        Vector3 p = player.Center;
        Vector3 me = transform.position;
        Vector3 flat = new Vector3(p.x - me.x, 0f, p.z - me.z);
        if (flat.magnitude > contactRadius + 0.45f) return false;

        float bottom = me.y;
        float top = me.y + eyeHeight + 0.4f;
        float pBottom = p.y - player.FeetOffset;
        float pTop = p.y + player.FeetOffset;

        return pTop > bottom && pBottom < top - 0.25f;
    }

    void HitPlayer(float amount)
    {
        bool real = director.Player == player;

        if (real && director.PlayerHealth != null)
            director.PlayerHealth.Damage(amount);

        if (real && director.PlayerBody != null)
        {
            Vector3 away = Flat(player.Center - transform.position);
            director.PlayerBody.linearVelocity = away * knockback + Vector3.up * knockUp;
            if (director.Controller != null) director.Controller.SuppressJumpHold();
        }

        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.ImpactBurst(ChestPosition, (player.Center - ChestPosition).normalized, 1f);
        if (real)
        {
            ImpactFrames.Hit(ChestPosition, 0.8f);
            if (CameraShaker.Instance != null) CameraShaker.Instance.AddTrauma(0.6f);
        }
    }

    // ---------------------------------------------------------------- cracks

    public override void TakeHit(EnemyHit hit)
    {
        if (IsDead) return;

        bool falling = !player.IsGrounded && player.Velocity.y < -minFallSpeed;
        bool counts = hit.kind == HitKind.Thrown || (hit.kind == HitKind.Melee && falling);
        if (!counts)
        {
            JuiceFX fx = JuiceFX.Get();
            if (fx != null) fx.Scuff(hit.point, -hit.direction, Vector3.up, 0.5f);
            EnemySounds.PlayAt(EnemySounds.Ricochet, hit.point, 0.5f, 0.6f);
            return;
        }

        Cracks++;
        lastCrackTime = Time.time;
        crackShake = 1f;
        EnemySounds.PlayAt(crackSound != null ? crackSound : EnemySounds.StoneCrack, hit.point, 1f, 1f + Cracks * 0.12f);
        JuiceFX juice = JuiceFX.Get();
        if (juice != null) juice.ImpactBurst(hit.point, -hit.direction, 0.5f + Cracks * 0.12f);
        if (Cracks < cracksToShatter) crackFX.AddCrack(hit.point, Cracks);
        Emit(StillwalkerEvent.Cracked);

        if (Cracks >= cracksToShatter)
        {
            Die(hit);
            return;
        }

        // jerks away from the hit, even while you're looking at it (not while stuck after a launch though)
        if (CanDodge)
        {
            Vector3 push = new Vector3(hit.direction.x, 0f, hit.direction.z);
            Vector3 away = transform.position - player.Feet;
            away.y = 0f;
            Dodge(DodgeKind.Flinch, push.sqrMagnitude > 0.04f ? push : away);
        }
    }

    protected override bool UseRagdollOnDeath => false;

    protected override void OnDeath(EnemyHit hit)
    {
        Emit(StillwalkerEvent.Shattered);
        Brain.Request(shattered);
        crackFX.Clear();
        marker.enabled = false;
        SpawnPieces(hit);

        // drop a stuck axe instead of leaving it floating where the body was
        ThrownAxe.DropAllStuckIn(transform);

        foreach (Renderer r in GetComponentsInChildren<Renderer>()) r.enabled = false;
        foreach (Collider c in ownColliders) if (c != null) c.enabled = false;
        if (nav != null) nav.enabled = false;
    }

    void SpawnPieces(EnemyHit hit)
    {
        Renderer bodyRenderer = null;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) < 0 && !(r is LineRenderer)) { bodyRenderer = r; break; }
        Material mat = bodyRenderer != null ? bodyRenderer.sharedMaterial : EnemyVisuals.Unlit(Color.gray);

        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        float height = eyeHeight + 0.4f;
        for (int i = 0; i < shatterPieces; i++)
        {
            Vector3 pos = transform.position + new Vector3(
                UnityEngine.Random.Range(-0.3f, 0.3f),
                UnityEngine.Random.Range(0.1f, height),
                UnityEngine.Random.Range(-0.3f, 0.3f));
            Vector3 size = Vector3.one * UnityEngine.Random.Range(0.15f, 0.35f);
            Vector3 outward = (pos - ChestPosition).normalized;
            Vector3 impulse = outward * UnityEngine.Random.Range(3f, 7f) + hit.direction * 3f + Vector3.up * 2f;

            GameObject go = new GameObject("StonePiece");
            go.AddComponent<Rigidbody>().mass = 2f;
            go.AddComponent<MeshRenderer>();
            WallShard shard = go.AddComponent<WallShard>();
            shard.Init(cube, mat, pos, UnityEngine.Random.rotation, size, impulse,
                UnityEngine.Random.insideUnitSphere * 8f, pieceLifetime, 1f, null);
        }

        if (CameraShaker.Instance != null) CameraShaker.Instance.AddTrauma(0.7f);
    }

    void Emit(StillwalkerEvent e)
    {
        Events?.Invoke(e);
        if (Policy != null) Policy.OnStillwalkerEvent(e);
    }

    // ---------------------------------------------------------------- reset

    public override void ResetAgent()
    {
        Cracks = 0;
        crackShake = 0f;
        lastLaunchTime = lastCrackTime = -99f;
        nextContactTime = 0f;
        nextPogoRoll = 0f;
        swatUntil = -99f;
        swatted = false;
        zipDodging = false;
        TrainingAxeThreat = 0f;
        cachedSensesTime = -1f;
        pathDist = 0f;
        nextPathTime = 0f;
        tension = 0f;
        wasSeen = false;
        heardPlayer = false;
        nextCreepCue = 0f;
        hasAmbush = false;
        ambushStartedAt = ambushArrivedAt = -1f;
        nextAmbushTime = nextAmbushSearch = 0f;
        policyStill = 0f;
        policyBenchedUntil = 0f;
        rulesSteering = true;
        activeTree = null;
        if (crackFX != null) crackFX.Clear();
        ResetProfile();
        if (marker != null) marker.enabled = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        foreach (Collider c in ownColliders) if (c != null) c.enabled = true;
        if (nav != null) nav.enabled = true;
        if (head != null) head.localRotation = headBaseLocal;
        base.ResetAgent();
        AnimSpeed(0f);
    }

    // ---------------------------------------------------------------- testing (F6 menu)

    // F6 menu
    public bool DebugIgnorePolicy { get; set; }

    public void DebugLaunch()
    {
        if (IsDead) return;
        lastLaunchTime = -99f;
        if (!Brain.IsInState(hunting)) Brain.ForceChange(hunting);
        Brain.Request(launching);
    }

    string WhyRules()
    {
        if (DebugIgnorePolicy) return "model ignored in F6";
        if (!PolicyReady) return "no model loaded";
        if (DistToPlayer() > policyMaxDistance) return "out of range";
        if (Time.time < policyBenchedUntil) return "model stalled or F6 override";
        if (!PlayerVisible) return "no line of sight";
        if (hasAmbush) return "ambushing";
        if (WantDistance() < policyHandOverDistance) return "creeping in";
        if (PathDist > policyMaxDistance * 1.4f) return "long way round";
        return "model";
    }

    public string DebugState
    {
        get
        {
            if (Brain == null) return "-";
            if (Brain.IsInState(dormant)) return "Dormant (asleep)";
            if (Brain.IsInState(shattered)) return "Shattered";
            if (Brain.IsInState(watched)) return InAmbush ? "In ambush, frozen (you're looking)" : "Frozen (you're looking)";
            if (Brain.IsInState(moving)) return "Stalking";
            if (Brain.IsInState(frozen)) return "Retreating, frozen";
            if (Brain.IsInState(fleeing)) return Desperate ? "Bolting" : "Retreating";
            if (Brain.IsInState(dodging)) return "Dodging";
            if (Brain.IsInState(launching)) return "Launching";
            if (Brain.IsInState(recovering)) return "Recovering (stuck)";
            return "Hunting";
        }
    }

    public bool DebugModelSteering => !Hurt && PolicyInControl && !rulesSteering;
    public string DebugWhyRules => WhyRules();
    public string DebugIntent => utility != null && utility.Current != null ? utility.Current.Name : "-";
    public float DebugTension01 => Mathf.Clamp01(tension / Mathf.Max(0.1f, closeInTime));
    public float DebugWantDistance => WantDistance();
    public string DebugAmbush => InAmbush ? $"waiting ({ambushMaxWait - (Time.time - ambushArrivedAt):0.0}s left)"
        : hasAmbush ? "heading to its spot"
        : Time.time < nextAmbushTime ? $"cooldown {nextAmbushTime - Time.time:0.0}s" : "looking (needs you moving 5+ m/s)";

    public IEnumerable<string> IntentNames
    {
        get { foreach (UtilityAction a in utility.Actions) if (a != uAmbush) yield return a.Name; }
    }

    void WakeForDebug()
    {
        if (Brain.IsInState(dormant)) Brain.ForceChange(hunting);
    }

    // the rules drive (not the model) for this long
    void BenchModel(float seconds) => policyBenchedUntil = Time.time + seconds;

    public void DebugWake() => WakeForDebug();

    public void DebugForceIntent(string name, float seconds = 6f)
    {
        WakeForDebug();
        foreach (UtilityAction a in utility.Actions)
        {
            if (a.Name != name) continue;
            BenchModel(seconds);
            utility.Force(a, seconds);
            if (Brain.IsInState(moving)) SwitchTree();
            return;
        }
    }

    // as if you'd ignored it for the full Close In Time
    public void DebugMaxTension()
    {
        WakeForDebug();
        tension = closeInTime;
    }

    // picks a hiding spot near a point, ignoring the speed and cooldown checks
    public bool DebugAmbushAt(Vector3 near)
    {
        WakeForDebug();
        if (!NavMesh.SamplePosition(near, out NavMeshHit onMesh, 4f, NavMesh.AllAreas)) return false;
        ambushCenter = onMesh.position;

        EQSItem best = ambushQuery.Run();
        if (best == null) return false;

        hasAmbush = true;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
        nextAmbushTime = 0f;
        Board.Set(BB.AmbushPos, best.Point);
        BenchModel(ambushMaxTravel + ambushMaxWait);
        return true;
    }

    public void DebugCreepCue()
    {
        nextCreepCue = 0f;
        EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 1f, 0.45f);
        EnemySounds.PlayAt(EnemySounds.StoneCrack, transform.position, 0.35f, 0.5f);
    }

    public void DebugCrack() => TakeHit(new EnemyHit
    {
        kind = HitKind.Thrown,
        point = ChestPosition,
        direction = Flat(transform.position - player.Feet),
        force = 1f
    });

    public void DebugShatter() => Die(new EnemyHit
    {
        kind = HitKind.Other,
        point = ChestPosition,
        direction = -transform.forward,
        force = 1f
    });

    public void PlaceForTraining(Vector3 position, int startCracks = 0)
    {
        ResetAgent();
        transform.position = position;
        SnapToGround();
        Brain.ForceChange(hunting);

        // start some episodes already hurt so it gets plenty of practice running
        startCracks = Mathf.Clamp(startCracks, 0, cracksToShatter - 1);
        for (int i = 1; i <= startCracks; i++)
            crackFX.AddCrack(ChestPosition + UnityEngine.Random.onUnitSphere * 0.3f, i);
        Cracks = startCracks;
        if (Hurt)
        {
            lastCrackTime = Time.time;
            Brain.ForceChange(retreating);
        }
    }

    public override string DebugText()
    {
        StringBuilder sb = new StringBuilder(base.DebugText());
        sb.Append($"\ncracks {Cracks}/{cracksToShatter}  seen {Seen}  launch {(LaunchReady ? "ready" : "cooling")}");
        sb.Append($"\ndistance {DistToPlayer():0.0}  path {PathDist:0.0}  wants {WantDistance():0.0}  chase {ChaseSpeed():0.0} m/s");
        sb.Append($"\nunseen build-up {tension:0.0}/{closeInTime:0}s{(InAmbush ? "  IN AMBUSH" : hasAmbush ? "  heading to ambush" : "")}");
        if (Hurt)
            sb.Append($"\nhurt: retreat to {RetreatDistance():0}m at {FleeSpeed():0} m/s{(Desperate ? "  BOLTING" : "")}" +
                      $"  heals in {Mathf.Max(0f, crackHealDelay - (Time.time - lastCrackTime)):0.0}s");
        if (PolicyReady)
        {
            bool steering = Hurt ? PolicyRetreats : PolicyInControl && !rulesSteering;
            string why = WhyRules();
            sb.Append(steering ? "\npolicy: ML-Agents" : $"\npolicy: ML-Agents ({why}, hand-written rules)");
        }
        sb.Append($"\nplayer: speed {profile.speed01:0.00}  air {profile.airborne01:0.00}  watching {profile.watching01:0.00}" +
                  $"  closing {profile.approach01:0.00}  aggro {profile.aggression01:0.00}");
        if (Brain != null && Brain.IsInState(moving))
        {
            sb.Append("\nintent: ").Append(utility.Current != null ? utility.Current.Name : "-");
            if (activeTree != null) sb.Append("\nbt: ").Append(activeTree.RunningPath);
            foreach (UtilityAction a in utility.Actions)
                sb.Append($"\n  {a.Name,-9} {a.Score:0.00}");
        }
        return sb.ToString();
    }
}
