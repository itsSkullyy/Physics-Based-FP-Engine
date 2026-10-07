using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Shared Grunt stuff (leader + follower): rifle, awareness, bodies, patrol, looking
// around, zip impacts and stagger.
// Zip in standing = bounce off him. Zip in crouched = slide through and hit him.
[RequireComponent(typeof(NavMeshAgent))]
public abstract class GruntBase : EnemyAgent, IZipTarget
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

    // ---------------------------------------------------------------- awareness

    protected override void AfterSenses(float dt)
    {
        if (InCombatState)
        {
            if (PlayerVisible)
            {
                Awareness = 1f;
                lastStimulus = Time.time;
            }
        }
        else if (PlayerVisible)
        {
            Awareness = Mathf.Min(1f, Awareness + dt / NoticeTime());
            lastStimulus = Time.time;
        }
        else if (Time.time - lastStimulus > 3f)
        {
            Awareness = Mathf.Max(0f, Awareness - awarenessDecay * dt);
        }

        Board.Set(BB.Awareness, Mathf.Round(Awareness * 20f) / 20f);
    }

    // seconds of seeing the player to fill the meter
    float NoticeTime()
    {
        float dist = Vector3.Distance(EyePosition, player.Center);
        float time = Mathf.Lerp(noticeTimeNear, noticeTimeFar, Mathf.InverseLerp(noticeNearRange, sightRange, dist));

        if (player.Speed > 9f) time *= 0.7f;
        if (!InMainCone(player.Center)) time *= 1.6f;

        SquadAlert level = Alertness;
        if (level == SquadAlert.Suspicious) time *= 0.6f;
        else if (level == SquadAlert.Searching) time *= 0.35f;
        if (Squad != null && Squad.Alarmed) time *= 0.75f;

        return Mathf.Max(0.05f, time);
    }

    bool InMainCone(Vector3 point)
    {
        Vector3 to = point - EyePosition;
        to.y = 0f;
        return to.sqrMagnitude < 0.01f || Vector3.Angle(transform.forward, to) <= sightAngle * 0.5f;
    }

    protected void RaiseAwareness(float value)
    {
        Awareness = Mathf.Max(Awareness, Mathf.Clamp01(value));
        lastStimulus = Time.time;
    }

    protected void ClearAwareness(float to = 0f)
    {
        Awareness = Mathf.Min(Awareness, to);
    }

    // F6 menu
    public void DebugSetAwareness(float value)
    {
        Awareness = Mathf.Clamp01(value);
        lastStimulus = Time.time;
    }

    protected override void OnSawPlayer()
    {
        if (Squad == null || Squad.Broken) return;
        if (Aware || InCombatState) Squad.ReportSighting(player.Center, player.Velocity);
        else if (Awareness >= suspiciousAt) ReportSuspicion(player.Center, false);
    }

    protected override void OnHeard(NoiseEvent noise)
    {
        if (!InCombatState)
        {
            float d = Vector3.Distance(transform.position, noise.position);
            float loud = 1f - Mathf.Clamp01(d / Mathf.Max(0.1f, noise.radius * hearing));
            RaiseAwareness(Mathf.Min(0.9f, suspiciousAt + 0.15f + loud * 0.45f));
        }

        if (Squad == null || Squad.Broken) return;
        if (InCombatState || Squad.Alert >= SquadAlert.Searching) Squad.ReportNoise(noise.position);
        else ReportSuspicion(noise.position, true, noise.kind == "axe");
    }

    void ReportSuspicion(Vector3 at, bool heard, bool lure = false)
    {
        if (!Led) return;
        bool first = Squad.Alert == SquadAlert.Calm && Squad.TimeSinceSuspect > 4f;
        Squad.ReportSuspicion(at, this, heard, lure);
        if (!first) return;
        EnemySounds.PlayAt(EnemySounds.SuspiciousHum, HeadPosition, 1f, Random.Range(0.95f, 1.05f), 25f);
        OnFirstToNotice(heard, lure);
    }

    protected virtual void OnFirstToNotice(bool heard, bool lure) { }

    protected void MarkSpotted()
    {
        SpottedAt = Time.time;

        // only one sting at a time, not one per grunt
        if (Time.time - lastSting < 1.5f) return;
        lastSting = Time.time;
        EnemySounds.PlayAt(EnemySounds.AlertSting, HeadPosition, 1f, Random.Range(0.97f, 1.03f), 40f);
    }

    static float lastSting = -99f;

    // director calls this while a recalled axe he can see/hear flies back to the player
    public void SawAxeRecalled(Vector3 playerPos)
    {
        if (IsDead || (Squad != null && Squad.Broken)) return;

        Vector2 off = Random.insideUnitCircle * axeTrailError;
        Vector3 guess = playerPos + new Vector3(off.x, 0f, off.y);
        bool fresh = Time.time - lastAxeTrail > 3f;
        lastAxeTrail = Time.time;

        if (InCombatState)
        {
            if (Squad != null) Squad.ReportNoise(guess);
            return;
        }

        RaiseAwareness(0.8f);
        Board.Set(BB.HeardPos, guess);
        Board.Set(BB.HeardAt, Time.time);
        if (Led) Squad.ReportTrail(guess);
        if (fresh) Say(Random.value < 0.5f ? "THAT AXE! FOLLOW IT!" : "IT'S FLYING BACK TO HIM!", "axetrail");
    }

    public bool CanSeePoint(Vector3 point, float range)
    {
        Vector3 eye = EyePosition;
        if ((point - eye).sqrMagnitude > range * range || !InMainCone(point)) return false;
        return EQSTest.Clear(eye, point, IgnoreForSight);
    }

    // ---------------------------------------------------------------- bodies

    public bool CanSpotBody(Vector3 point, float range = -1f)
    {
        return CanSeePoint(point + Vector3.up * 0.3f, range < 0f ? bodySpotRange : range);
    }

    public bool WitnessedDeath(GruntBase victim)
    {
        if (Vector3.Distance(transform.position, victim.transform.position) < 5f) return true;
        return CanSpotBody(victim.BodyPosition, Mathf.Max(bodySpotRange, sightRange * 0.8f));
    }

    public void FoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (IsDead) return;
        bodyFoundTime = Time.time;
        bodyFoundPos = at;
        if (!InCombatState) RaiseAwareness(0.85f);
        OnFoundBody(victim, at, witnessed);
    }

    protected virtual void OnFoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (Squad != null && !Squad.Broken)
        {
            if (victim == Squad.Leader)
            {
                Say(witnessed ? "THE CAPTAIN'S DOWN!" : "IT'S THE CAPTAIN! HE'S DEAD!", "capdead");
                Squad.Break();
                return;
            }
            Squad.ReportBody(at);
        }

        if (InCombatState) Say("MAN DOWN!", "mandown");
        else Say(witnessed ? "MAN DOWN!" : "WE'VE GOT A BODY HERE!", "body");
    }

    public void Say(string line, string key)
    {
        if (director != null) director.Say(this, line, key);
    }

    public virtual void AssignSquad(GruntSquad squad)
    {
        Squad = squad;
    }

    // ---------------------------------------------------------------- setup

    protected override void Awake()
    {
        base.Awake();

        if (muzzle == null)
        {
            Transform m = transform.Find("Muzzle");
            if (m == null)
            {
                m = new GameObject("Muzzle").transform;
                m.SetParent(transform, false);
                m.localPosition = new Vector3(0.25f, 1.35f, 0.65f);
            }
            muzzle = m;
        }

        if (visualRoot == null)
        {
            visualRoot = transform.Find("Model");
            if (visualRoot == null) visualRoot = transform.Find("Placeholder");
        }
        if (visualRoot != null) visualBaseRot = visualRoot.localRotation;
        guardForward = transform.forward;

        laser = EnemyVisuals.MakeLine(transform, "Laser", new Color(1f, 0.15f, 0.1f, 0.85f), 0.05f);

        if (nav != null)
        {
            nav.speed = walkSpeed;
            nav.angularSpeed = 0f;
            nav.acceleration = 30f;
            nav.stoppingDistance = 0.3f;
        }
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
        if (g == null || root == null) return;

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
                if ((mask & (1 << layer)) == 0) continue;
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
        if (IsDead) return;
        staggerDuration = seconds;
        Brain.Request(StaggerState);
    }

    // ---------------------------------------------------------------- shared tree pieces

    protected BTNode QueryInto(string name, EQSQuery query, string key, bool claim = true)
    {
        return BT.Action(name,
            () =>
            {
                if (waitingForQuery) return BTStatus.Running;
                if (pendingResult == null) return BTStatus.Failure;
                Board.Set(key, pendingResult.Point);
                if (claim) director.Claim(this, pendingResult.Point);
                return BTStatus.Success;
            },
            () =>
            {
                // ticket so an old query's answer can't land in a different tree
                int ticket = ++queryTicket;
                waitingForQuery = true;
                pendingResult = null;
                EQSService.Get().Enqueue(query, best =>
                {
                    if (ticket != queryTicket) return;
                    waitingForQuery = false;
                    pendingResult = best;
                });
            });
    }

    protected BTNode MoveToKey(string name, string key, System.Func<float> speed, float timeout)
    {
        return BT.TimeLimit(timeout, BT.Action(name, () =>
        {
            Vector3 target = Board.Get(key, transform.position);
            MoveTo(target, speed());
            return Arrived(0.8f) ? BTStatus.Success : BTStatus.Running;
        },
        () => aiming = false));
    }

    // token > laser > burst > recover
    protected BTNode MakeShootSequence()
    {
        return BT.Sequence("Shoot",
            BT.Action("Token", () => TakeShootToken() ? BTStatus.Success : BTStatus.Failure),
            BT.Action("Telegraph", TickTelegraph, StartTelegraph, () => { SetLaser(false); firing.Remove(this); }),
            BT.Action("Burst", TickBurst, StartBurst, () => { ReleaseShootToken(); firing.Remove(this); }),
            BT.Wait("Recover", () => recoverTime * Random.Range(0.8f, 1.3f)));
    }

    protected BTNode FaceTarget()
    {
        return BT.Action("Face", () => IsFacing(player.Center, 25f) ? BTStatus.Success : BTStatus.Running,
            () => { StopMoving(); aiming = true; });
    }

    // patrol loop, or stand guard if there isn't one
    protected BehaviourTree MakeIdleTree()
    {
        return new BehaviourTree(BT.Selector("Idle",
            BT.Sequence("Patrol",
                BT.Condition("HasRoute", () => PatrolRoute.Count > 0),
                BT.Always(BT.TimeLimit(30f, BT.Action("WalkToStop", () =>
                {
                    MoveTo(PatrolRoute[patrolIndex % PatrolRoute.Count], walkSpeed * patrolPace);
                    return Arrived(0.8f) ? BTStatus.Success : BTStatus.Running;
                }))),
                Scan("LookAround", () => Random.Range(patrolWait * 0.7f, patrolWait * 1.4f)),
                BT.Do("Next", () => patrolIndex++)),
            Scan("Guard", () => float.PositiveInfinity, () => guardForward, 160f)));
    }

    protected List<Vector3> PatrolRoute
    {
        get
        {
            if (route == null) BuildPatrolRoute();
            return route ?? NoRoute;
        }
    }

    static readonly List<Vector3> NoRoute = new List<Vector3>();

    void BuildPatrolRoute()
    {
        if (patrolPoints != null && patrolPoints.Length > 0)
        {
            route = new List<Vector3>();
            foreach (Transform t in patrolPoints)
                if (t != null) route.Add(t.position);
            return;
        }

        if (autoPatrolRadius <= 0f || autoPatrolStops < 2)
        {
            route = new List<Vector3>();
            return;
        }

        if (!NavReady) return;

        // points around a circle in order so it's a loop, each one reachable
        route = new List<Vector3>();
        Vector3 home = SpawnPosition;
        NavMeshPath path = new NavMeshPath();
        float start = Random.Range(0f, 360f);
        for (int i = 0; i < autoPatrolStops; i++)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = start + (i + Random.Range(-0.3f, 0.3f)) * 360f / autoPatrolStops;
                Vector3 p = home + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * autoPatrolRadius * Random.Range(0.5f, 1f);
                if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 4f, NavMesh.AllAreas)) continue;
                if (!NavMesh.CalculatePath(home, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;

                bool crowded = false;
                foreach (Vector3 r in route)
                    if ((r - hit.position).sqrMagnitude < 25f) crowded = true;
                if (crowded) continue;

                route.Add(hit.position);
                break;
            }
        }
        if (route.Count < 2) route.Clear();
    }

    protected BTNode Scan(string name, System.Func<float> seconds, System.Func<Vector3> center = null, float arc = 360f)
    {
        float until = 0f;
        Vector3 fixedCenter = Vector3.forward;
        return BT.Action(name,
            () =>
            {
                StopMoving();
                TickScan(center != null ? center() : fixedCenter, arc);
                return Time.time >= until ? BTStatus.Success : BTStatus.Running;
            },
            () =>
            {
                until = Time.time + seconds();
                fixedCenter = transform.forward;
                scanHoldLeft = 0f;
                scanDir = Vector3.zero;
            });
    }

    // pick a direction (open + not just looked at), turn, hold, repeat
    protected void TickScan(Vector3 center, float arc)
    {
        if (scanDir == Vector3.zero || scanHoldLeft <= 0f)
        {
            scanDir = PickScanDir(center, arc);
            scanHoldLeft = Random.Range(scanHold.x, scanHold.y);
        }

        FaceTowards(transform.position + scanDir, scanTurnSpeed);
        if (IsFacing(transform.position + scanDir, 6f)) scanHoldLeft -= Time.deltaTime;
    }

    Vector3 PickScanDir(Vector3 center, float arc)
    {
        center.y = 0f;
        center = center.sqrMagnitude > 0.01f ? center.normalized : transform.forward;

        Vector3 best = center;
        float bestScore = float.MinValue;
        for (int i = 0; i < 7; i++)
        {
            Vector3 d = Quaternion.Euler(0f, Random.Range(-arc * 0.5f, arc * 0.5f), 0f) * center;
            float open = Physics.Raycast(EyePosition, d, out RaycastHit hit, 20f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance / 20f : 1f;
            float turn = Vector3.Angle(transform.forward, d) / 180f;
            float score = open + Mathf.Min(turn, 0.5f) * 0.8f + Random.value * 0.3f;
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }

    protected float CautiousSpeed => walkSpeed * 1.1f;

    // ---------------------------------------------------------------- shooting

    protected bool TakeShootToken()
    {
        if (TokenKind == null || HasToken) return true;
        HasToken = director.TryTakeToken(TokenKind, this, TokenLimit);
        return HasToken;
    }

    protected void ReleaseShootToken()
    {
        firing.Remove(this);
        if (!HasToken || director == null || TokenKind == null) return;
        director.ReleaseToken(TokenKind, this);
        HasToken = false;
    }

    void StartTelegraph()
    {
        telegraphElapsed = 0f;
        AimPoint = AimTarget();
        SetLaser(true);
        firing.Add(this);
        EnemySounds.PlayAt(chargeSound != null ? chargeSound : EnemySounds.LaserCharge, muzzle.position, 1f);
    }

    BTStatus TickTelegraph()
    {
        if (!PlayerVisible) return BTStatus.Failure;

        telegraphElapsed += Time.deltaTime;

        // stops tracking right before firing so you can dodge
        if (telegraphElapsed < telegraphTime - aimLockTime)
            AimPoint = Vector3.Lerp(AimPoint, AimTarget(), 1f - Mathf.Exp(-14f * Time.deltaTime));

        float t = Mathf.Clamp01(telegraphElapsed / telegraphTime);
        laser.startWidth = Mathf.Lerp(0.07f, 0.015f, t);
        laser.endWidth = laser.startWidth * 0.5f;
        Color c = new Color(1f, 0.15f, 0.1f, Mathf.Lerp(0.35f, 1f, t));
        laser.startColor = c;
        laser.endColor = c;

        return telegraphElapsed >= telegraphTime ? BTStatus.Success : BTStatus.Running;
    }

    void StartBurst()
    {
        shotsFired = 0;
        shotTimer = 0f;
        SetLaser(false);
        firing.Add(this);
    }

    BTStatus TickBurst()
    {
        shotTimer -= Time.deltaTime;
        if (shotTimer > 0f) return BTStatus.Running;

        Vector3 target = shotsFired == 0 ? AimPoint : AimTarget();
        if (warningShotDue)
        {
            // warning shot
            Vector3 side = Vector3.Cross(Vector3.up, (target - muzzle.position).normalized);
            target += side * 2.2f * (Random.value < 0.5f ? -1f : 1f) + Vector3.up * 0.6f;
        }

        Fire((target - muzzle.position).normalized, ShotSpread());
        shotsFired++;
        shotTimer = burstInterval;

        if (shotsFired >= burstCount)
        {
            warningShotDue = false;
            OnBurstFinished();
            return BTStatus.Success;
        }
        return BTStatus.Running;
    }

    protected virtual void OnBurstFinished() { }

    protected void Fire(Vector3 dir, float spread)
    {
        Vector3 origin = muzzle.position;
        dir = Quaternion.AngleAxis(Random.Range(0f, spread), Vector3.Cross(dir, Random.onUnitSphere).normalized) * dir;

        GruntBullet.Spawn(origin, dir, bulletSpeed, damage, this, deflectRange, deflectAngle);
        if (Squad != null) Squad.MarkFired();
        EnemySounds.PlayAt(shotSound != null ? shotSound : EnemySounds.Gunshot, origin, 1f, Random.Range(0.92f, 1.08f));
        AnimTrigger("Fire");

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null) fx.AirPuff(origin + dir * 0.2f, dir, 0.15f);
    }

    public static int FiringNear(Vector3 position, float radius, GruntBase except)
    {
        int n = 0;
        float r2 = radius * radius;
        foreach (GruntBase g in firing)
            if (g != null && g != except && !g.IsDead && (g.transform.position - position).sqrMagnitude <= r2) n++;
        return n;
    }

    protected void SetLaser(bool on)
    {
        laserOn = on;
        if (laser != null) laser.enabled = on;
    }

    Vector3 LaserEnd()
    {
        Vector3 o = muzzle.position;
        Vector3 d = AimPoint - o;
        float len = d.magnitude;
        if (len < 0.01f) return AimPoint;
        if (Physics.Raycast(o, d / len, out RaycastHit hit, len + 30f, ~0, QueryTriggerInteraction.Ignore))
            return hit.point;
        return o + d / len * (len + 30f);
    }

    // ---------------------------------------------------------------- per frame

    protected override void Update()
    {
        base.Update();
        if (IsDead || player == null) return;

        if (laserOn && laser != null)
        {
            laser.SetPosition(0, muzzle.position);
            laser.SetPosition(1, LaserEnd());
        }

        if (gunPivot != null && aiming)
        {
            Quaternion look = Quaternion.LookRotation((laserOn ? AimPoint : player.Center) - gunPivot.position);
            gunPivot.rotation = Quaternion.Slerp(gunPivot.rotation, look, 1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        AnimFloat("Speed", Velocity.magnitude);
        Footsteps();

        // fake stagger for the placeholder
        if (visualRoot != null)
        {
            Quaternion tilt = visualBaseRot * Quaternion.Euler(-65f * staggerTilt, 0f, 0f) * VisualWobble();
            visualRoot.localRotation = Quaternion.Slerp(visualRoot.localRotation, tilt, 1f - Mathf.Exp(-12f * Time.deltaTime));
        }
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
        if (stepAccum < stepLength) return;
        stepAccum = 0f;

        Camera cam = Camera.main;
        if (cam == null || (cam.transform.position - transform.position).sqrMagnitude > 30f * 30f) return;

        float loud = Mathf.InverseLerp(1f, runSpeed, speed);
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.GruntStep, transform.position,
            Mathf.Lerp(0.5f, 1f, loud), Random.Range(0.9f, 1.1f), 26f);
    }

    protected virtual Quaternion VisualWobble() => Quaternion.identity;

    // ---------------------------------------------------------------- zip impact

    public bool ZipTargetValid => !IsDead && isActiveAndEnabled && !zipDodging;
    public Vector3 ZipPoint => ChestPosition;
    public float ZipArrivalRadius => zipArrivalRadius;

    public bool OnZipArrive(FirstPersonCharacterController controller, Rigidbody body)
    {
        Vector3 v = body.linearVelocity;
        bool crouched = controller.IsSliding || controller.IsAirSliding || controller.CrouchAmount > 0.5f;

        if (crouched)
        {
            // crouched: slide through and keep speed
            EnemySounds.PlayAt(EnemySounds.Slice, ChestPosition, 1f, Random.Range(0.95f, 1.05f), 30f);
            IgnorePlayerFor(passThroughTime);
            TakeHit(new EnemyHit
            {
                kind = HitKind.ZipSlide,
                point = ChestPosition,
                direction = v.sqrMagnitude > 0.01f ? v.normalized : transform.forward,
                force = deathImpulse * 1.6f
            });
            if (!IsDead) RequestStagger(staggerTime);
            body.linearVelocity = v;
            return true;
        }

        // standing: bounce off
        Vector3 flat = new Vector3(v.x, 0f, v.z) * bounceKeepHorizontal;
        body.linearVelocity = flat + Vector3.up * bounceUpSpeed;
        controller.SuppressJumpHold();
        IgnorePlayerFor(0.3f);

        EnemySounds.PlayAt(EnemySounds.Boing, ChestPosition, 1f, Random.Range(0.95f, 1.05f), 30f);
        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.AirPuff(HeadPosition, Vector3.up, 0.7f);
        if (CameraShaker.Instance != null)
            CameraShaker.Instance.Impact(0.3f, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0f, 0f), 6f);

        RequestStagger(staggerTime);
        return true;
    }

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
        if (laser != null) laser.enabled = false;
        if (visualRoot != null) visualRoot.localRotation = visualBaseRot;
        base.ResetAgent();
        if (director != null) director.MarkSquadsDirty();
    }

    protected override void OnDestroy()
    {
        firing.Remove(this);
        base.OnDestroy();
    }

    protected string SquadLine()
    {
        if (Squad == null) return $"squad: none\nawareness {Awareness:0.00}";
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
        if (tree != null) sb.Append("\nbt: ").Append(tree.RunningPath);
    }
}
