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
// This file has the settings, setup, the state machine and the per-frame update. The rest is in
// Stillwalker.Stalking.cs (moving and ambushing), Stillwalker.Combat.cs (retreat, dodging,
// launch, cracks) and Stillwalker.Debug.cs (F6 menu and overlay text).
[RequireComponent(typeof(NavMeshAgent))]
public partial class Stillwalker : EnemyAgent, IZipTarget
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
    float flightArc;
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

        FindVisuals();
        SetupEyes();
        SetupCracks();
        ResetProfile();
        SetupMarker();
        SetupNavAgent();
    }

    // Model or placeholder, and the head bone, so the look-at and stone jitter have something to move
    void FindVisuals()
    {
        if (visualRoot == null)
        {
            visualRoot = transform.Find("Model");
            if (visualRoot == null)
            {
                visualRoot = transform.Find("Placeholder");
            }
        }
        if (visualRoot != null)
        {
            visualBaseLocal = visualRoot.localRotation;
        }

        if (head == null)
        {
            if (anim != null && anim.isHuman)
            {
                head = anim.GetBoneTransform(HumanBodyBones.Head);
            }
            if (head == null && visualRoot != null)
            {
                head = visualRoot.Find("Head");
            }
        }
        if (head != null)
        {
            headBaseLocal = head.localRotation;
        }
    }

    // any child renderer with "Eye" in its name gets its own glowing unlit material
    void SetupEyes()
    {
        if (eyes == null || eyes.Length == 0)
        {
            var found = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add(r);
                }
            }
            eyes = found.ToArray();
        }

        eyeMats = new Material[eyes.Length];
        for (int i = 0; i < eyes.Length; i++)
        {
            eyeMats[i] = EnemyVisuals.Unlit(eyeColor);
            eyes[i].sharedMaterial = eyeMats[i];
        }
    }

    void SetupCracks()
    {
        crackFX = GetComponent<StillwalkerCracks>();
        if (crackFX == null)
        {
            crackFX = gameObject.AddComponent<StillwalkerCracks>();
        }
        crackFX.Setup(transform, visualRoot, cracksToShatter);
    }

    void SetupMarker()
    {
        marker = EnemyVisuals.MakeLine(null, "StillwalkerMarker", markerColor, 0.08f);
        marker.positionCount = 33;
        marker.loop = false;
        navPath = new NavMeshPath();
    }

    // snappy starts and stops, it turns itself so the agent shouldn't
    void SetupNavAgent()
    {
        if (nav == null)
        {
            return;
        }
        nav.acceleration = 60f;
        nav.angularSpeed = 0f;
        nav.stoppingDistance = 0.5f;
        nav.autoBraking = true;
    }

    protected override void Start()
    {
        base.Start();
        Subscribe();
        GruntBase.MatchGrappleTarget(director != null ? director.Grapple : null, gameObject);
    }

    protected override void OnDestroy()
    {
        if (subscribed != null)
        {
            subscribed.Landed -= OnPlayerLanded;
        }
        if (marker != null)
        {
            Destroy(marker.gameObject);
        }
        base.OnDestroy();
    }

    void Subscribe()
    {
        if (player == subscribed)
        {
            return;
        }
        if (subscribed != null)
        {
            subscribed.Landed -= OnPlayerLanded;
        }
        subscribed = player;
        if (subscribed != null)
        {
            subscribed.Landed += OnPlayerLanded;
        }
    }

    void OnPlayerLanded()
    {
        Board.Set(BB.LandedAt, Time.time);
        if (Brain == null || !Brain.IsInState(hunting))
        {
            return;
        }

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
        if (!LaunchReady || player.IsDead || !player.OnGround || !PlayerVisible)
        {
            return false;
        }

        float dist = DistToPlayer();
        if (!TrainingRules)
        {
            // you walked round the corner it was waiting behind
            if (InAmbush && dist <= ambushLaunchRange)
            {
                return true;
            }
            // crept right up behind you
            if (!Seen && dist <= pounceRange && Brain.IsInState(moving))
            {
                return true;
            }
        }

        if (dist > launchRange)
        {
            return false;
        }

        bool justLanded = Time.time - Board.Get(BB.LandedAt, -99f) < 0.25f;
        return justLanded || player.GroundedTime > groundedTooLong;
    }

    // through a portal if that's shorter, so it keeps its stalking distance when you're
    // on the other side of one instead of thinking you're a whole scene away
    float DistToPlayer() => Portal.TravelDistance(transform.position, player.Feet);

    bool PolicyReady => Policy != null && Policy.PolicyActive && !DebugIgnorePolicy;
    bool PolicyRetreats => PolicyReady && DistToPlayer() <= policyMaxRetreatDistance;
    bool TrainingRules => Policy != null && Policy.Training;

    // The policy was trained on open ground at close range. Behind walls, on a long detour,
    // while ambushing or when it has stalled, the rules drive instead.
    bool PolicyInControl
    {
        get
        {
            if (!PolicyReady || DistToPlayer() > policyMaxDistance)
            {
                return false;
            }
            if (TrainingRules)
            {
                return true;
            }
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
        if (Time.time < nextPathTime)
        {
            return;
        }
        nextPathTime = Time.time + 0.25f;

        float straight = DistToPlayer();
        pathDist = straight;
        if (!NavReady || navPath == null)
        {
            return;
        }
        if (!NavMesh.SamplePosition(player.Feet, out NavMeshHit hit, 4f, NavMesh.AllAreas))
        {
            return;
        }
        if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, navPath))
        {
            return;
        }
        if (navPath.status == NavMeshPathStatus.PathInvalid)
        {
            return;
        }

        Vector3[] corners = navPath.corners;
        float length = Portal.PathLength(corners);
        if (corners.Length > 0)
        {
            length += Vector3.Distance(corners[corners.Length - 1], hit.position);
        }
        pathDist = Mathf.Max(straight, length);
        Board.Set(BB.PathDistance, Mathf.Round(pathDist));
    }

    // how far away it wants to be: the stalking band, shrinking the longer you ignore it
    float WantDistance()
    {
        float band = (stalkMin + stalkMax) * 0.5f;
        if (TrainingRules)
        {
            return band;
        }
        return Mathf.Lerp(band, closeInMin, Mathf.Clamp01(tension / Mathf.Max(0.1f, closeInTime)));
    }

    float Gap => PathDist - WantDistance();

    bool FacingAway => player.LookAngleTo(ChestPosition) > lookAwayAngle;

    float CloseInSpeed() => FacingAway || !Exposed ? sneakSpeed : creepSpeed;

    void UpdateTension(float dt)
    {
        if (Seen)
        {
            if (!wasSeen)
            {
                tension *= spottedTensionKeep;
            }
        }
        else
        {
            tension = Mathf.Min(closeInTime, tension + dt);
        }
        wasSeen = Seen;
    }

    // ---------------------------------------------------------------- per frame

    protected override void Update()
    {
        Subscribe();
        base.Update();
        if (IsDead || player == null || Brain == null)
        {
            return;
        }

        UpdateSeen();
        if (Brain.IsInState(hunting))
        {
            UpdateHunting(Time.deltaTime);
        }
        UpdateLandingWarning();
        UpdateCrackHealing();
        CheckIncomingAttacks();
        CheckZipSwat();
        RememberNavPosition();
        UpdateEyes();
        UpdateCrackShake(Time.deltaTime);
    }

    // seen = in the open, near the middle of your view and in range
    void UpdateSeen()
    {
        Exposed = EQSTest.Clear(player.CameraPosition, ChestPosition, IgnoreForSight);
        bool seen = Exposed
                    && player.LookAngleTo(ChestPosition) < seenAngle
                    && Vector3.Distance(player.CameraPosition, ChestPosition) < seenRange;
        Board.Set(BB.Seen, seen);
    }

    void UpdateHunting(float dt)
    {
        UpdateProfile(dt);
        UpdatePathDistance();
        UpdateTension(dt);
        if (Brain.IsInState(stalking) && !TrainingRules)
        {
            CreepCue();
        }
    }

    // ring on the ground where you'll land, while it could launch at you there
    void UpdateLandingWarning()
    {
        if (Brain.IsInState(launching))
        {
            return;
        }

        bool warn = Brain.IsInState(hunting) && LaunchReady && !player.IsGrounded
                    && DistToPlayer() <= launchRange
                    && player.PredictLanding(out Vector3 land, out _)
                    && Vector3.Distance(land, transform.position) <= launchRange;
        if (warn)
        {
            player.PredictLanding(out Vector3 spot, out _);
            ShowMarker(spot, 0.5f);
        }
        else
        {
            marker.enabled = false;
        }
    }

    void UpdateCrackHealing()
    {
        if (Cracks == 0 || Time.time - lastCrackTime <= crackHealDelay)
        {
            return;
        }

        Cracks = 0;
        crackFX.Heal();
        EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 0.5f, 1.6f);
        Emit(StillwalkerEvent.Healed);
    }

    // sidesteps a zip coming at it, or a pogo coming down on it
    void CheckIncomingAttacks()
    {
        if (ZipIncoming())
        {
            Vector3 approach = player.Velocity;
            approach.y = 0f;
            if (approach.sqrMagnitude < 0.01f)
            {
                approach = transform.position - player.Center;
            }
            float side = UnityEngine.Random.value < 0.5f ? 1f : -1f;
            Dodge(DodgeKind.Zip, Vector3.Cross(Vector3.up, approach.normalized) * side);
        }
        else if (PogoIncoming())
        {
            Vector3 away = player.PredictLanding(out Vector3 land, out _) ? transform.position - land : Vector3.zero;
            Dodge(DodgeKind.Pogo, away);
        }
    }

    // reaches out and swats you as the zip carries you past
    void CheckZipSwat()
    {
        if (Time.time < swatUntil && !swatted && Vector3.Distance(player.Center, ChestPosition) < zipSwatRadius)
        {
            Swat();
        }
    }

    // last good spot on the mesh, for getting back onto it after a launch
    void RememberNavPosition()
    {
        if (!NavReady)
        {
            return;
        }
        lastNavPos = transform.position;
        hasNavPos = true;
    }

    void UpdateCrackShake(float dt)
    {
        if (crackShake > 0f)
        {
            crackShake = Mathf.Max(0f, crackShake - dt * 4f);
        }
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        if (IsDead || player == null || Brain == null)
        {
            return;
        }

        if (Brain.IsInState(moving) && Time.time >= nextContactTime && TouchingPlayer())
        {
            nextContactTime = Time.time + 1f;
            HitPlayer(contactDamage);
            Emit(StillwalkerEvent.ContactHit);
        }
    }

    void LateUpdate()
    {
        if (IsDead || player == null)
        {
            return;
        }

        TrackPlayerWithHead();
        ApplyCrackShake();
    }

    // head always follows you, within a limit, after the animator has posed it
    void TrackPlayerWithHead()
    {
        if (head == null)
        {
            return;
        }

        Vector3 look = player.CameraPosition - head.position;
        if (look.sqrMagnitude <= 0.01f)
        {
            return;
        }

        Quaternion want = Quaternion.LookRotation(look.normalized, Vector3.up);
        Quaternion baseRot = head.parent != null ? head.parent.rotation * headBaseLocal : headBaseLocal;
        Quaternion limited = Quaternion.RotateTowards(baseRot, want, 75f);
        head.rotation = anim != null && anim.enabled
            ? Quaternion.Slerp(head.rotation, limited, 0.85f)
            : Quaternion.Slerp(head.rotation, limited, 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    void ApplyCrackShake()
    {
        if (visualRoot == null)
        {
            return;
        }
        Vector3 shake = crackShake > 0f ? UnityEngine.Random.insideUnitSphere * crackShake * 4f : Vector3.zero;
        visualRoot.localRotation = visualBaseLocal * Quaternion.Euler(shake);
    }

    void UpdateEyes()
    {
        float pulse = 1f + Mathf.Sin(Time.time * 9f) * 0.08f;
        Color c = eyeColor * Mathf.Clamp(eyeGlow * pulse, 0.05f, 2f);
        c.a = 1f;
        foreach (Material m in eyeMats)
        {
            EnemyVisuals.SetColor(m, c);
        }
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
        if (crackFX != null)
        {
            crackFX.Clear();
        }
        ResetProfile();
        if (marker != null)
        {
            marker.enabled = false;
        }
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            r.enabled = true;
        }
        foreach (Collider c in ownColliders)
        {
            if (c != null)
            {
                c.enabled = true;
            }
        }
        if (nav != null)
        {
            nav.enabled = true;
        }
        if (head != null)
        {
            head.localRotation = headBaseLocal;
        }
        base.ResetAgent();
        AnimSpeed(0f);
    }
}
