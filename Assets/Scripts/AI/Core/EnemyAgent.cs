using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public enum HitKind { Melee, Thrown, Deflect, ZipSlide, Other }

public struct EnemyHit
{
    public HitKind kind;
    public Vector3 point;
    public Vector3 direction;
    public float force;
}

// Base enemy: health/death/ragdoll, axe hits, sight and hearing, NavMesh movement,
// animator helpers and reset. Subclasses build their state machine in BuildBrain().
[RequireComponent(typeof(EnemyRagdoll))]
public abstract class EnemyAgent : MonoBehaviour
{
    [Header("Health")]
    public int maxHits = 1;

    [Header("Senses")]
    public float eyeHeight = 1.6f;
    public float sightRange = 45f;
    [Tooltip("Full cone angle in degrees. 360 = sees all around.")]
    public float sightAngle = 150f;
    public float senseInterval = 0.1f;
    [Tooltip("Wider cone that only notices the player moving fast. 0 = off.")]
    public float peripheralAngle = 0f;
    public float peripheralMinSpeed = 7f;
    [Tooltip("Multiplies the radius of every noise the player makes.")]
    public float hearing = 1f;

    [Header("Death Juice")]
    public float deathShake = 0.3f;
    [Tooltip("Short slow-down when you kill something while airborne (Karlson's slow-mo moments).")]
    public float airKillHitstop = 0.12f;
    [Range(0.05f, 1f)] public float airKillTimeScale = 0.25f;
    public float deathImpulse = 7f;

    [Header("Target")]
    [Tooltip("Leave empty to chase the real player. Training arenas point this at a playback ghost.")]
    public PlayerMotionTracker targetOverride;

    [Header("Stuck Detection")]
    [Tooltip("Seconds of no progress along a path before it counts as stuck.")]
    public float stuckTime = 1.5f;
    [Tooltip("Has to move at least this far to count as making progress.")]
    public float stuckProgress = 0.3f;

    [Header("Debug")]
    public bool logStateChanges = false;

    public Blackboard Board { get; private set; }
    public HStateMachine Brain { get; private set; }
    public bool IsDead { get; private set; }
    public int HitsTaken { get; private set; }
    public readonly List<EQSQuery> DebugQueries = new List<EQSQuery>();

    // so the director can tell an axe hitting a body from hitting a wall
    public static int LastFleshHitFrame { get; private set; } = -1;
    public static bool HitFleshRecently => Time.frameCount - LastFleshHitFrame <= 2;

    public Vector3 EyePosition => transform.position + Vector3.up * eyeHeight;
    public PlayerMotionTracker Target => player;
    public virtual Vector3 HeadPosition => transform.position + Vector3.up * (eyeHeight + 0.2f);
    public virtual Vector3 ChestPosition => transform.position + Vector3.up * (eyeHeight * 0.75f);
    public bool PlayerVisible { get; private set; }
    public float TimeSinceSeen => Time.time - Board.Get(BB.LastSeenTime, -999f);
    public float VisibleFor { get; private set; }

    protected AIDirector director;
    protected PlayerMotionTracker player;
    protected NavMeshAgent nav;
    protected Animator anim;
    protected EnemyRagdoll ragdoll;
    protected Collider[] ownColliders;

    Vector3 spawnPos;
    Quaternion spawnRot;
    float senseTimer;

    readonly HashSet<int> animParams = new HashSet<int>();

    Vector3 fallbackTarget;
    float fallbackSpeed;
    bool fallbackMoving;
    Vector3 fallbackVelocity;
    static bool warnedNoNavMesh;

    protected virtual void Awake()
    {
        Board = new Blackboard();
        nav = GetComponent<NavMeshAgent>();
        ragdoll = GetComponent<EnemyRagdoll>();
        CacheAnimator();

        spawnPos = transform.position;
        spawnRot = transform.rotation;

        if (nav != null)
        {
            nav.updateRotation = false;
        }
        ownColliders = GetComponentsInChildren<Collider>(true);
    }

    protected virtual void Start()
    {
        director = AIDirector.Get();
        Board.Parent = director.Global;
        player = targetOverride != null ? targetOverride : director.Player;
        director.Register(this);
        Rebuild();
    }

    protected virtual void OnDestroy()
    {
        if (director != null)
        {
            director.Unregister(this);
        }
    }

    void Rebuild()
    {
        Brain = BuildBrain();
        if (logStateChanges)
        {
            Brain.Changed += (from, to) => Debug.Log($"[{name}] {from?.Name} -> {Brain.ActivePath}", this);
        }
        Brain.Start();
    }

    protected abstract HStateMachine BuildBrain();

    public void CacheAnimator()
    {
        anim = GetComponentInChildren<Animator>();
        animParams.Clear();
        if (anim == null || anim.runtimeAnimatorController == null)
        {
            return;
        }
        foreach (AnimatorControllerParameter p in anim.parameters)
        {
            animParams.Add(p.nameHash);
        }
    }

    protected virtual void Update()
    {
        if (player == null && director != null)
        {
            player = targetOverride != null ? targetOverride : director.Player;
        }
        if (player == null || Brain == null)
        {
            return;
        }

        float dt = Time.deltaTime;
        if (!IsDead)
        {
            UpdateSenses(dt);
            AfterSenses(dt);
        }
        Brain.Tick(dt);

        if (!IsDead)
        {
            UpdateFallbackMovement(dt);
            UpdateStuckCheck(dt);
        }
    }

    protected virtual void FixedUpdate()
    {
        if (Brain != null)
        {
            Brain.FixedTick(Time.fixedDeltaTime);
        }
    }

    // ---------------------------------------------------------------- senses

    void UpdateSenses(float dt)
    {
        senseTimer -= dt;
        if (senseTimer <= 0f)
        {
            senseTimer = senseInterval;
            PlayerVisible = !player.IsDead && !director.debugBlind && CanSee(player.Center);
            Board.Set(BB.PlayerVisible, PlayerVisible);

            if (PlayerVisible)
            {
                Board.Set(BB.LastSeenPos, player.Center);
                Board.Set(BB.LastSeenTime, Time.time);
                director.Global.Set(BB.SquadLastKnown, player.Center);
                director.Global.Set(BB.SquadLastKnownAt, Time.time);
                OnSawPlayer();
            }
        }

        VisibleFor = PlayerVisible ? VisibleFor + dt : 0f;
    }

    protected virtual void OnSawPlayer() { }

    // after senses, before the brain ticks
    protected virtual void AfterSenses(float dt) { }

    public virtual void OnHeardNoise(NoiseEvent noise)
    {
        if (IsDead)
        {
            return;
        }
        if (Vector3.Distance(transform.position, noise.position) > noise.radius * hearing)
        {
            return;
        }

        Board.Set(BB.HeardPos, noise.position);
        Board.Set(BB.HeardAt, Time.time);
        OnHeard(noise);
    }

    protected virtual void OnHeard(NoiseEvent noise) { }

    public float TimeSinceHeard => Time.time - Board.Get(BB.HeardAt, -999f);

    public bool CanSee(Vector3 point)
    {
        Vector3 eye = EyePosition;
        Vector3 to = point - eye;
        float dist = to.magnitude;
        if (dist > sightRange)
        {
            return false;
        }

        if (sightAngle < 359f)
        {
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            float angle = flat.sqrMagnitude > 0.01f ? Vector3.Angle(transform.forward, flat) : 0f;
            if (angle > sightAngle * 0.5f)
            {
                // peripheral vision only notices fast movement
                bool peripheral = peripheralAngle > sightAngle && angle <= peripheralAngle * 0.5f
                                  && player != null && player.Speed >= peripheralMinSpeed;
                if (!peripheral)
                {
                    return false;
                }
            }
        }

        return EQSTest.Clear(eye, point, IgnoreForSight);
    }

    protected bool IgnoreForSight(Collider c)
    {
        if (c == null)
        {
            return true;
        }
        if (director != null && director.PlayerBody != null && c.attachedRigidbody == director.PlayerBody)
        {
            return true;
        }
        if (c.GetComponentInParent<EnemyAgent>() != null)
        {
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- damage

    // Sent by BattleAxe via SendMessageUpwards.
    void OnAxeHit(Vector3 point)
    {
        LastFleshHitFrame = Time.frameCount;
        Vector3 dir = point - (director != null && director.Player != null ? director.Player.CameraPosition : point - transform.forward);
        TakeHit(new EnemyHit { kind = HitKind.Melee, point = point, direction = dir.normalized, force = deathImpulse });
    }

    // Sent by ThrownAxe via SendMessageUpwards.
    void OnThrownAxeStuck(Vector3 point)
    {
        LastFleshHitFrame = Time.frameCount;
        Vector3 dir = point - (director != null && director.Player != null ? director.Player.CameraPosition : point - transform.forward);
        TakeHit(new EnemyHit { kind = HitKind.Thrown, point = point, direction = dir.normalized, force = deathImpulse * 1.3f });
    }

    public virtual void TakeHit(EnemyHit hit)
    {
        if (IsDead)
        {
            return;
        }
        if (!AcceptsHit(hit))
        {
            return;
        }

        HitsTaken++;
        if (HitsTaken >= maxHits)
        {
            Die(hit);
        }
        else
        {
            OnHurt(hit);
        }
    }

    protected virtual bool AcceptsHit(EnemyHit hit) => true;
    protected virtual bool UseRagdollOnDeath => true;
    protected virtual void OnHurt(EnemyHit hit) { }
    protected virtual void OnDeath(EnemyHit hit) { }
    public virtual void OnAllyDied(EnemyAgent ally) { }

    public void Die(EnemyHit hit)
    {
        if (IsDead)
        {
            return;
        }
        IsDead = true;
        overrideMoveUntil = -1f;
        StopMoving();

        OnDeath(hit);

        Vector3 dir = hit.direction.sqrMagnitude > 0.001f ? hit.direction.normalized : -transform.forward;
        Vector3 impulse = dir * hit.force + Vector3.up * hit.force * 0.35f;
        if (UseRagdollOnDeath)
        {
            ragdoll.Activate(impulse, hit.point);
            EnemySounds.PlayAt(EnemySounds.BodyFall, transform.position, 1f, Random.Range(0.9f, 1.1f), 30f);
        }

        KillJuice(hit.point, dir);
        if (director != null)
        {
            director.NotifyDeath(this);
        }
    }

    protected void KillJuice(Vector3 point, Vector3 dir)
    {
        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(point, -dir, 1f);
            if (player != null && !player.IsGrounded && airKillHitstop > 0f)
            {
                fx.Hitstop(airKillHitstop, airKillTimeScale);
            }
        }

        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTrauma(deathShake);
        }
        ImpactFrames.Hit(point, 0.6f);
    }

    public virtual void ResetAgent()
    {
        StopAllCoroutines();
        IsDead = false;
        HitsTaken = 0;
        VisibleFor = 0f;
        PlayerVisible = false;
        Board.Clear();
        fallbackMoving = false;
        overrideMoveUntil = -1f;
        ClearStuck();

        ragdoll.Restore(spawnPos, spawnRot);
        SetPlayerCollision(true);

        Rebuild();
    }

    // ---------------------------------------------------------------- collisions

    public void IgnorePlayerFor(float seconds)
    {
        SetPlayerCollision(false);
        StartCoroutine(RestoreCollisionAfter(seconds));
    }

    IEnumerator RestoreCollisionAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        SetPlayerCollision(true);
    }

    void SetPlayerCollision(bool collide)
    {
        if (director == null || director.PlayerBody == null)
        {
            return;
        }
        Collider[] playerCols = director.PlayerBody.GetComponentsInChildren<Collider>();
        foreach (Collider pc in playerCols)
        {
            foreach (Collider oc in ownColliders)
            {
                if (pc != null && oc != null)
                {
                    Physics.IgnoreCollision(pc, oc, !collide);
                }
            }
        }
    }

    // ---------------------------------------------------------------- movement

    protected Vector3 SpawnPosition => spawnPos;

    protected bool NavReady => nav != null && nav.enabled && nav.isOnNavMesh;

    public Vector3 Velocity => NavReady ? nav.velocity : fallbackVelocity;

    // while set, normal MoveTo/StopMoving calls are ignored (running from a grenade)
    float overrideMoveUntil = -1f;
    protected bool MoveOverridden => Time.time < overrideMoveUntil;

    protected void OverrideMove(Vector3 point, float speed, float seconds)
    {
        overrideMoveUntil = -1f;
        MoveTo(point, speed);
        overrideMoveUntil = Time.time + seconds;
    }

    protected void ClearMoveOverride() => overrideMoveUntil = -1f;

    public void MoveTo(Vector3 point, float speed)
    {
        if (MoveOverridden)
        {
            return;
        }
        if (NavReady)
        {
            nav.isStopped = false;
            nav.speed = speed;
            if (!nav.hasPath || (nav.destination - point).sqrMagnitude > 0.25f)
            {
                // a new destination gets a fresh chance, the old one might have been the problem
                if ((moveTarget - point).sqrMagnitude > 4f)
                {
                    ClearStuck();
                }
                moveTarget = point;
                nav.SetDestination(point);
            }
            return;
        }

        if (!warnedNoNavMesh && nav != null)
        {
            warnedNoNavMesh = true;
            Debug.LogWarning("Enemy isn't on a NavMesh, moving in straight lines instead. Bake a NavMeshSurface.", this);
        }

        fallbackTarget = point;
        fallbackSpeed = speed;
        fallbackMoving = true;
    }

    public void StopMoving()
    {
        if (MoveOverridden)
        {
            return;
        }
        ClearStuck();
        fallbackMoving = false;
        fallbackVelocity = Vector3.zero;
        if (NavReady)
        {
            nav.isStopped = true;
            nav.ResetPath();
            nav.velocity = Vector3.zero;
        }
    }

    public bool Arrived(float tolerance = 0.6f)
    {
        if (NavReady)
        {
            if (nav.pathPending || nav.remainingDistance > Mathf.Max(tolerance, nav.stoppingDistance))
            {
                return false;
            }
            // the end of a partial path isn't the target, it's as close as the mesh gets
            return nav.pathStatus == NavMeshPathStatus.PathComplete || FlatDistance(moveTarget) <= tolerance * 2f;
        }

        Vector3 d = fallbackTarget - transform.position;
        d.y = 0f;
        return !fallbackMoving || d.magnitude <= tolerance;
    }

    // ---------------------------------------------------------------- stuck detection

    // True when the current move can't finish: no progress for stuckTime even after a
    // recovery attempt, or the path ends short of an unreachable target.
    public bool MoveFailed => IsStuck || PathBlocked;
    public bool IsStuck { get; private set; }
    public int StuckRecoveries { get; private set; }

    bool PathBlocked =>
        NavReady && !nav.pathPending && nav.hasPath
        && nav.pathStatus != NavMeshPathStatus.PathComplete
        && nav.remainingDistance <= nav.stoppingDistance + 0.5f
        && FlatDistance(moveTarget) > 1.5f;

    Vector3 moveTarget;
    Vector3 progressPos;
    float noProgressTime;
    bool recoveryTried;

    // Behaviour tree helper for any "walk there" leaf: finishes, keeps going, or fails so
    // the tree (and utility AI above it) can pick something else instead of waiting forever.
    protected BTStatus MoveStatus(float tolerance)
    {
        if (Arrived(tolerance))
        {
            return BTStatus.Success;
        }
        return MoveFailed ? BTStatus.Failure : BTStatus.Running;
    }

    void UpdateStuckCheck(float dt)
    {
        bool tryingToMove = NavReady && !nav.isStopped && nav.hasPath && !nav.pathPending
                            && nav.remainingDistance > nav.stoppingDistance + 0.2f;
        if (!tryingToMove || MoveOverridden)
        {
            noProgressTime = 0f;
            progressPos = transform.position;
            return;
        }

        if (FlatDistance(progressPos) >= stuckProgress)
        {
            progressPos = transform.position;
            noProgressTime = 0f;
            IsStuck = false;
            recoveryTried = false;
            return;
        }

        noProgressTime += dt;
        if (noProgressTime < stuckTime)
        {
            return;
        }
        noProgressTime = 0f;

        if (!recoveryTried)
        {
            // first try: snap back onto the mesh and ask for a fresh path, which fixes
            // most jams on ledges, corners and other agents
            recoveryTried = true;
            StuckRecoveries++;
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                nav.Warp(hit.position);
            }
            nav.SetDestination(moveTarget);
            if (logStateChanges)
            {
                Debug.Log($"[{name}] stuck, re-pathing to {moveTarget}", this);
            }
            return;
        }

        // still stuck: give up on this move so whatever asked for it can choose again
        IsStuck = true;
        nav.ResetPath();
        if (logStateChanges)
        {
            Debug.Log($"[{name}] stuck again, giving up on the move", this);
        }
    }

    void ClearStuck()
    {
        IsStuck = false;
        recoveryTried = false;
        noProgressTime = 0f;
        progressPos = transform.position;
    }

    float FlatDistance(Vector3 point)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    void UpdateFallbackMovement(float dt)
    {
        if (NavReady || !fallbackMoving)
        {
            fallbackVelocity = Vector3.zero;
            return;
        }

        Vector3 d = fallbackTarget - transform.position;
        d.y = 0f;
        float dist = d.magnitude;
        if (dist < 0.3f)
        {
            fallbackMoving = false;
            fallbackVelocity = Vector3.zero;
            return;
        }

        Vector3 step = d / dist * Mathf.Min(dist, fallbackSpeed * dt);
        transform.position += step;
        fallbackVelocity = step / Mathf.Max(0.0001f, dt);
    }

    public void FaceTowards(Vector3 point, float degreesPerSecond)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        if (d.sqrMagnitude < 0.001f)
        {
            return;
        }
        Quaternion target = Quaternion.LookRotation(d.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, degreesPerSecond * Time.deltaTime);
    }

    public void FaceMovement(float degreesPerSecond)
    {
        Vector3 v = Velocity;
        v.y = 0f;
        if (v.sqrMagnitude > 0.2f)
        {
            FaceTowards(transform.position + v, degreesPerSecond);
        }
    }

    public bool IsFacing(Vector3 point, float maxAngle)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        return d.sqrMagnitude < 0.001f || Vector3.Angle(transform.forward, d) <= maxAngle;
    }

    // ---------------------------------------------------------------- animator

    protected void AnimFloat(string param, float value, float damp = 0.1f)
    {
        int h = Animator.StringToHash(param);
        if (anim != null && anim.isActiveAndEnabled && animParams.Contains(h))
        {
            anim.SetFloat(h, value, damp, Time.deltaTime);
        }
    }

    protected void AnimBool(string param, bool value)
    {
        int h = Animator.StringToHash(param);
        if (anim != null && anim.isActiveAndEnabled && animParams.Contains(h))
        {
            anim.SetBool(h, value);
        }
    }

    protected void AnimTrigger(string param)
    {
        int h = Animator.StringToHash(param);
        if (anim != null && anim.isActiveAndEnabled && animParams.Contains(h))
        {
            anim.SetTrigger(h);
        }
    }

    protected void AnimSpeed(float speed)
    {
        if (anim != null)
        {
            anim.speed = speed;
        }
    }

    // ---------------------------------------------------------------- debug

    public virtual string DebugText()
    {
        string stuck = IsStuck ? " STUCK" : StuckRecoveries > 0 ? $" (unstuck x{StuckRecoveries})" : "";
        return name + (IsDead ? " (dead)" : "") + stuck + "\n" + (Brain != null ? Brain.ActivePath : "");
    }

    protected virtual void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(EyePosition, 0.12f);

        foreach (EQSQuery q in DebugQueries)
        {
            if (q == null)
            {
                continue;
            }
            foreach (EQSItem item in q.LastItems)
            {
                Gizmos.color = item.Valid ? Color.Lerp(Color.red, Color.green, item.Score) : Color.gray;
                Gizmos.DrawSphere(item.Point, item == q.LastBest ? 0.25f : 0.1f);
            }
        }
    }
}
