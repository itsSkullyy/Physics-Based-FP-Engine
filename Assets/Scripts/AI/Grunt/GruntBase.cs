using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Shared Grunt stuff (leader + follower): rifle, awareness, bodies, patrol, looking
// around, zip impacts and stagger.
// Zip in standing = bounce off him. Zip in crouched = slide through and hit him.
// This file has the settings, setup, shared states and the per-frame update. Awareness and
// bodies are in GruntBase.Awareness.cs, shooting and zip/grenade reactions in GruntBase.Combat.cs.
[RequireComponent(typeof(NavMeshAgent))]
public abstract partial class GruntBase : EnemyAgent, IZipTarget
{
    [Header("Refs (auto-created if empty)")]
    public Transform muzzle;
    [Tooltip("Optional. Turned to face the aim point (the placeholder gun). Leave empty when the gun is parented to a rigged hand.")]
    public Transform gunPivot;
    [Tooltip("Model or placeholder root. Tilted back for the stagger when there's no Stagger animation.")]
    public Transform visualRoot;

    [Header("Movement")]
    public float walkSpeed = 3.2f;
    public float runSpeed = 6.5f;
    public float turnSpeed = 540f;

    [Header("Engagement")]
    [Tooltip("How long he has to see you before he reacts.")]
    public float reactionTime = 0.35f;
    public float loseInterestTime = 4f;

    [Header("Gun")]
    public float bulletSpeed = 38f;
    public float damage = 20f;
    public int burstCount = 3;
    public float burstInterval = 0.12f;
    public float telegraphTime = 0.5f;
    [Tooltip("The laser stops tracking for this long before the burst, so it can be dodged.")]
    public float aimLockTime = 0.15f;
    public float recoverTime = 0.7f;
    [Tooltip("An axe swing bats bullets back inside this range and cone.")]
    public float deflectRange = 3.4f;
    public float deflectAngle = 45f;

    [Header("Zip Impact")]
    public float zipArrivalRadius = 1.1f;
    public float bounceUpSpeed = 11f;
    [Range(0f, 1f)] public float bounceKeepHorizontal = 0.3f;
    public float staggerTime = 1.1f;
    public float passThroughTime = 0.6f;

    [Header("Zip Lock-On")]
    [Tooltip("Grappling a Grunt makes him and his nearby squadmates snap-fire at you while you zip in.")]
    public bool zipLockOn = true;
    [Tooltip("Squadmates within this range of the zipped Grunt join in, if they can see you.")]
    public float zipLockRange = 35f;
    [Tooltip("Max Grunts helping the zipped one.")]
    public int zipLockHelpers = 2;
    [Tooltip("Delay before the first shot (min, max). This is your warning.")]
    public Vector2 zipLockReaction = new Vector2(0.15f, 0.3f);
    public int zipLockShots = 3;
    public int zipLockHelperShots = 2;
    public float zipLockInterval = 0.1f;
    [Tooltip("Shot spread in degrees while locked on. Normal shots are several times wider.")]
    public float zipLockSpread = 1.2f;

    [Header("Grenade Panic")]
    [Tooltip("Runs from live grenades that land within this many blast radii of him.")]
    public float grenadeFearRadius = 1.3f;
    [Tooltip("Seconds before he notices a grenade near him (min, max). Slow ones still get caught.")]
    public Vector2 grenadeNoticeTime = new Vector2(0.2f, 0.6f);

    [Header("Awareness")]
    [Tooltip("Seconds of looking right at you before he's sure it's you: up close, and at the edge of his sight range.")]
    public float noticeTimeNear = 0.3f;
    public float noticeTimeFar = 2.5f;
    public float noticeNearRange = 7f;
    [Tooltip("Half-noticing you past this point (or hearing you) makes him suspicious.")]
    [Range(0f, 1f)] public float suspiciousAt = 0.3f;
    [Tooltip("How fast awareness drains once he's seen and heard nothing for a few seconds.")]
    public float awarenessDecay = 0.1f;
    [Tooltip("Dead Grunts he can see within this range get noticed.")]
    public float bodySpotRange = 28f;
    [Tooltip("How far off his guess is when he follows your recalled axe back to you.")]
    public float axeTrailError = 4f;

    [Header("Patrol")]
    [Tooltip("Walks between these in order. Leave empty to make up a loop around where he starts.")]
    public Transform[] patrolPoints;
    public float patrolWait = 2f;
    [Tooltip("Size of the made-up loop when there are no patrol points. 0 = stand guard instead.")]
    public float autoPatrolRadius = 16f;
    public int autoPatrolStops = 4;
    [Tooltip("Patrol walking speed as a fraction of Walk Speed.")]
    [Range(0.3f, 1f)] public float patrolPace = 0.7f;

    [Header("Looking Around")]
    public float scanTurnSpeed = 140f;
    [Tooltip("How long he holds each direction he looks in (min, max).")]
    public Vector2 scanHold = new Vector2(0.6f, 1.4f);

    [Header("Audio (optional, synthesised if empty)")]
    public AudioClip shotSound;
    public AudioClip chargeSound;
    public AudioClip stepSound;
    [Tooltip("Metres between footsteps.")]
    public float stepLength = 1.6f;

    public GruntSquad Squad { get; protected set; }

    // 0 = nothing, 1 = knows it's you
    public float Awareness { get; private set; }
    public bool Aware => Awareness >= 1f;
    public float SpottedAt { get; private set; } = -99f;
    public abstract bool InCombatState { get; }
    public virtual SquadAlert Alertness => Led ? Squad.Alert : (InCombatState ? SquadAlert.Combat : SquadAlert.Calm);
    // in a squad with a living leader
    public bool Led => Squad != null && !Squad.Broken && !Squad.LeaderDown;
    public Vector3 BodyPosition => ragdoll != null ? ragdoll.BodyCenter : transform.position;
    public float TimeSinceBody => Time.time - bodyFoundTime;
    public Vector3 LastBodyPos => bodyFoundPos;

    // grunts currently shooting, followers aim worse near more of these
    static readonly HashSet<GruntBase> firing = new HashSet<GruntBase>();

    protected LineRenderer laser;
    protected bool laserOn;
    protected bool aiming;
    protected bool freshContact = true;
    protected bool warningShotDue;
    protected float staggerTilt;
    protected Quaternion visualBaseRot;

    float telegraphElapsed;
    int shotsFired;
    float shotTimer;
    float staggerDuration;
    int patrolIndex;
    List<Vector3> route;
    Vector3 guardForward;
    Vector3 scanDir;
    float scanHoldLeft;
    float lastStimulus = -99f;
    float stepAccum;
    float lastAxeTrail = -99f;
    float bodyFoundTime = -99f;
    Vector3 bodyFoundPos;

    // zip lock-on, shared so a zip is only answered once per grunt
    static int zipFrame = -1;
    static int zipSerial;
    static GruntBase zipTargetNow;
    static int zipHelpersJoined;
    int lockSerial = -1;
    int lockShotsLeft;
    float lockNextShot;
    bool lockLaser;

    GruntGrenade fleeingFrom;
    float grenadeNoticeDelay = -1f;

    EQSItem pendingResult;
    bool waitingForQuery;
    int queryTicket;

    public override Vector3 ChestPosition => transform.position + Vector3.up * 1.25f;

    // on the blackboard so the trees and F4 see them
    protected Vector3 AimPoint
    {
        get => Board.Get(BB.AimPoint, ChestPosition + transform.forward * 10f);
        set => Board.Set(BB.AimPoint, value);
    }

    protected bool HasToken
    {
        get => Board.Get(BB.HasToken, false);
        set => Board.Set(BB.HasToken, value);
    }

    protected float TimeAtPos
    {
        get => Board.Get(BB.TimeAtPos, 0f);
        set => Board.Set(BB.TimeAtPos, value);
    }

    // ---------------------------------------------------------------- hooks

    protected abstract HState StaggerState { get; }
    protected abstract HState DeadState { get; }

    protected abstract Vector3 AimTarget();

    protected abstract float ShotSpread();

    // null = no token needed
    protected virtual string TokenKind => Squad != null ? Squad.ShootTokens : "shoot-loners";
    protected virtual int TokenLimit => 3;

    protected bool zipDodging;

    // ---------------------------------------------------------------- setup

    protected override void Awake()
    {
        base.Awake();

        FindOrMakeMuzzle();
        FindVisualRoot();
        guardForward = transform.forward;
        laser = EnemyVisuals.MakeLine(transform, "Laser", new Color(1f, 0.15f, 0.1f, 0.85f), 0.05f);
        SetupNavAgent();
    }

    // where bullets and the laser come from, made at a rough gun height if the model has none
    void FindOrMakeMuzzle()
    {
        if (muzzle != null)
        {
            return;
        }

        Transform m = transform.Find("Muzzle");
        if (m == null)
        {
            m = new GameObject("Muzzle").transform;
            m.SetParent(transform, false);
            m.localPosition = new Vector3(0.25f, 1.35f, 0.65f);
        }
        muzzle = m;
    }

    void FindVisualRoot()
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
            visualBaseRot = visualRoot.localRotation;
        }
    }

    // turning is done by hand (FaceTowards), so the agent's own rotation is off
    void SetupNavAgent()
    {
        if (nav == null)
        {
            return;
        }
        nav.speed = walkSpeed;
        nav.angularSpeed = 0f;
        nav.acceleration = 30f;
        nav.stoppingDistance = 0.3f;
    }

    protected override void Start()
    {
        base.Start();
        MatchGrappleTarget(director != null ? director.Grapple : null, gameObject);
        director.MarkSquadsDirty();
    }

    // put the grunt on the grapple's layer/tag so the zip can target him
    public static void MatchGrappleTarget(Grappling g, GameObject root)
    {
        if (g == null || root == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(g.grappleTag) && !root.CompareTag(g.grappleTag))
        {
            try { root.tag = g.grappleTag; }
            catch (UnityException) { Debug.LogWarning($"Couldn't give {root.name} the grapple tag '{g.grappleTag}'.", root); }
        }

        int mask = g.grappleMask.value;
        if (mask != 0 && (mask & (1 << root.layer)) == 0)
        {
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0)
                {
                    continue;
                }
                root.layer = layer;
                break;
            }
        }
    }

    // ---------------------------------------------------------------- shared states

    protected void SetupStagger(HState staggered, HState backTo)
    {
        staggered.OnEnter = () =>
        {
            ClearMoveOverride();
            StopMoving();
            ReleaseShootToken();
            SetLaser(false);
            AnimTrigger("Stagger");
        };
        staggered.OnTick = dt => staggerTilt = staggered.TimeInState < staggerDuration * 0.7f ? 1f : 0f;
        staggered.OnExit = () => staggerTilt = 0f;
        staggered.To(backTo, () => staggered.TimeInState > staggerDuration);
    }

    protected void RequestStagger(float seconds)
    {
        if (IsDead)
        {
            return;
        }
        staggerDuration = seconds;
        Brain.Request(StaggerState);
    }

    // ---------------------------------------------------------------- per frame

    protected override void Update()
    {
        base.Update();
        if (IsDead || player == null)
        {
            return;
        }

        UpdateLaser();
        UpdateGunAim(Time.deltaTime);
        TickZipLock();
        TickGrenadePanic();
        AnimFloat("Speed", Velocity.magnitude);
        Footsteps();
        UpdateStaggerTilt(Time.deltaTime);
    }

    void UpdateLaser()
    {
        if (!laserOn || laser == null)
        {
            return;
        }
        laser.SetPosition(0, muzzle.position);
        laser.SetPosition(1, LaserEnd());
    }

    void UpdateGunAim(float dt)
    {
        if (gunPivot == null || !aiming)
        {
            return;
        }
        Quaternion look = Quaternion.LookRotation((laserOn ? AimPoint : player.Center) - gunPivot.position);
        gunPivot.rotation = Quaternion.Slerp(gunPivot.rotation, look, 1f - Mathf.Exp(-18f * dt));
    }

    // fake stagger for the placeholder
    void UpdateStaggerTilt(float dt)
    {
        if (visualRoot == null)
        {
            return;
        }
        Quaternion tilt = visualBaseRot * Quaternion.Euler(-65f * staggerTilt, 0f, 0f) * VisualWobble();
        visualRoot.localRotation = Quaternion.Slerp(visualRoot.localRotation, tilt, 1f - Mathf.Exp(-12f * dt));
    }

    void Footsteps()
    {
        float speed = Velocity.magnitude;
        if (speed < 0.5f)
        {
            stepAccum = 0f;
            return;
        }

        stepAccum += speed * Time.deltaTime;
        if (stepAccum < stepLength)
        {
            return;
        }
        stepAccum = 0f;

        Camera cam = Camera.main;
        if (cam == null || (cam.transform.position - transform.position).sqrMagnitude > 30f * 30f)
        {
            return;
        }

        float loud = Mathf.InverseLerp(1f, runSpeed, speed);
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.GruntStep, transform.position,
            Mathf.Lerp(0.5f, 1f, loud), Random.Range(0.9f, 1.1f), 26f);
    }

    protected virtual Quaternion VisualWobble() => Quaternion.identity;

    // ---------------------------------------------------------------- events

    protected override void OnDeath(EnemyHit hit)
    {
        ReleaseShootToken();
        SetLaser(false);
        director.Unclaim(this);
        Brain.Request(DeadState);
    }

    public override void ResetAgent()
    {
        laserOn = false;
        lockLaser = false;
        lockShotsLeft = 0;
        fleeingFrom = null;
        grenadeNoticeDelay = -1f;
        freshContact = true;
        warningShotDue = false;
        staggerTilt = 0f;
        patrolIndex = 0;
        Awareness = 0f;
        SpottedAt = -99f;
        lastStimulus = -99f;
        bodyFoundTime = -99f;
        scanDir = Vector3.zero;
        firing.Remove(this);
        if (laser != null)
        {
            laser.enabled = false;
        }
        if (visualRoot != null)
        {
            visualRoot.localRotation = visualBaseRot;
        }
        base.ResetAgent();
        if (director != null)
        {
            director.MarkSquadsDirty();
        }
    }

    protected override void OnDestroy()
    {
        firing.Remove(this);
        base.OnDestroy();
    }

    protected string SquadLine()
    {
        if (Squad == null)
        {
            return $"squad: none\nawareness {Awareness:0.00}";
        }
        return $"squad: {Squad.Leader.name}  {Squad.Alert}  order {Squad.Order}{(Squad.Broken ? "  BROKEN" : "")}\nawareness {Awareness:0.00}";
    }

    protected float DistToPlayer() => Vector3.Distance(transform.position, player.Feet);

    protected bool AimedAtByPlayer()
    {
        Vector3 to = ChestPosition - player.CameraPosition;
        return Vector3.Dot(player.CameraForward, to.normalized) > 0.97f && to.magnitude < 35f;
    }

    protected void AppendTree(StringBuilder sb, BehaviourTree tree)
    {
        if (tree != null)
        {
            sb.Append("\nbt: ").Append(tree.RunningPath);
        }
    }
}
