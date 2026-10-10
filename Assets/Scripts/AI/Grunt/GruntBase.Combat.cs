using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// GruntBase: behaviour tree pieces shared by leaders and followers, shooting, running from
// grenades, snap-firing at a zip, and what happens when the player zips into one.
public partial class GruntBase
{
    // ---------------------------------------------------------------- shared tree pieces

    protected BTNode QueryInto(string name, EQSQuery query, string key, bool claim = true)
    {
        return BT.Action(name,
            () =>
            {
                if (waitingForQuery)
                {
                    return BTStatus.Running;
                }
                if (pendingResult == null)
                {
                    return BTStatus.Failure;
                }
                Board.Set(key, pendingResult.Point);
                if (claim)
                {
                    director.Claim(this, pendingResult.Point);
                }
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
                    if (ticket != queryTicket)
                    {
                        return;
                    }
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
            return MoveStatus(0.8f);
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
                    return MoveStatus(0.8f);
                }))),
                Scan("LookAround", () => Random.Range(patrolWait * 0.7f, patrolWait * 1.4f)),
                BT.Do("Next", () => patrolIndex++)),
            Scan("Guard", () => float.PositiveInfinity, () => guardForward, 160f)));
    }

    protected List<Vector3> PatrolRoute
    {
        get
        {
            if (route == null)
            {
                BuildPatrolRoute();
            }
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
            {
                if (t != null)
                {
                    route.Add(t.position);
                }
            }
            return;
        }

        if (autoPatrolRadius <= 0f || autoPatrolStops < 2)
        {
            route = new List<Vector3>();
            return;
        }

        if (!NavReady)
        {
            return;
        }

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
                if (!NavMesh.SamplePosition(p, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                {
                    continue;
                }
                if (!NavMesh.CalculatePath(home, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }

                bool crowded = false;
                foreach (Vector3 r in route)
                {
                    if ((r - hit.position).sqrMagnitude < 25f)
                    {
                        crowded = true;
                    }
                }
                if (crowded)
                {
                    continue;
                }

                route.Add(hit.position);
                break;
            }
        }
        if (route.Count < 2)
        {
            route.Clear();
        }
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
        if (IsFacing(transform.position + scanDir, 6f))
        {
            scanHoldLeft -= Time.deltaTime;
        }
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
        if (TokenKind == null || HasToken)
        {
            return true;
        }
        HasToken = director.TryTakeToken(TokenKind, this, TokenLimit);
        return HasToken;
    }

    protected void ReleaseShootToken()
    {
        firing.Remove(this);
        if (!HasToken || director == null || TokenKind == null)
        {
            return;
        }
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
        if (!PlayerVisible)
        {
            return BTStatus.Failure;
        }

        telegraphElapsed += Time.deltaTime;

        // stops tracking right before firing so you can dodge
        if (telegraphElapsed < telegraphTime - aimLockTime)
        {
            AimPoint = Vector3.Lerp(AimPoint, AimTarget(), 1f - Mathf.Exp(-14f * Time.deltaTime));
        }

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
        if (shotTimer > 0f)
        {
            return BTStatus.Running;
        }

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
        if (Squad != null)
        {
            Squad.MarkFired();
        }
        EnemySounds.PlayAt(shotSound != null ? shotSound : EnemySounds.Gunshot, origin, 1f, Random.Range(0.92f, 1.08f));
        AnimTrigger("Fire");

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null)
        {
            fx.AirPuff(origin + dir * 0.2f, dir, 0.15f);
        }
    }

    public static int FiringNear(Vector3 position, float radius, GruntBase except)
    {
        int n = 0;
        float r2 = radius * radius;
        foreach (GruntBase g in firing)
        {
            if (g != null && g != except && !g.IsDead && (g.transform.position - position).sqrMagnitude <= r2)
            {
                n++;
            }
        }
        return n;
    }

    protected void SetLaser(bool on)
    {
        laserOn = on;
        if (laser != null)
        {
            laser.enabled = on;
        }
    }

    Vector3 LaserEnd()
    {
        Vector3 o = muzzle.position;
        Vector3 d = AimPoint - o;
        float len = d.magnitude;
        if (len < 0.01f)
        {
            return AimPoint;
        }
        if (Physics.Raycast(o, d / len, out RaycastHit hit, len + 30f, ~0, QueryTriggerInteraction.Ignore))
        {
            return hit.point;
        }
        return o + d / len * (len + 30f);
    }

    // ---------------------------------------------------------------- grenade panic

    void TickGrenadePanic()
    {
        GruntGrenade g = NearestThreat();
        if (g == null || Brain.IsInState(StaggerState))
        {
            grenadeNoticeDelay = -1f;
            if (fleeingFrom != null && fleeingFrom != g)
            {
                ClearMoveOverride();
            }
            fleeingFrom = null;
            return;
        }

        if (g == fleeingFrom)
        {
            return;
        }

        if (grenadeNoticeDelay < 0f)
        {
            grenadeNoticeDelay = Random.Range(grenadeNoticeTime.x, grenadeNoticeTime.y);
        }
        if (g.Age < grenadeNoticeDelay)
        {
            return;
        }

        Vector3? spot = EscapeFrom(g);
        if (spot == null)
        {
            return;
        }

        fleeingFrom = g;
        OverrideMove(spot.Value, runSpeed * 1.2f, g.FuseLeft + 0.2f);
        director.Say(this, "GRENADE!", "grenade");
    }

    GruntGrenade NearestThreat()
    {
        GruntGrenade best = null;
        float bestDist = float.MaxValue;
        foreach (GruntGrenade g in GruntGrenade.Live)
        {
            // a batted one is your payoff, they don't get to dodge it
            if (g == null || !g.HurtsGrunts || g.Batted)
            {
                continue;
            }
            Vector3 d = g.transform.position - transform.position;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < g.Radius * grenadeFearRadius && dist < bestDist)
            {
                bestDist = dist;
                best = g;
            }
        }
        return best;
    }

    Vector3? EscapeFrom(GruntGrenade g)
    {
        Vector3 away = transform.position - g.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f)
        {
            away = -transform.forward;
        }
        away.Normalize();

        float want = g.Radius * 1.6f;
        foreach (float angle in new[] { 0f, 40f, -40f, 80f, -80f })
        {
            Vector3 p = g.transform.position + Quaternion.Euler(0f, angle, 0f) * away * want;
            if (NavMesh.SamplePosition(p, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            {
                return hit.position;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- zip lock-on

    static void TrackZip(Grappling g)
    {
        if (zipFrame == Time.frameCount)
        {
            return;
        }
        zipFrame = Time.frameCount;

        GruntBase t = g != null && g.IsZipping ? g.ZipTarget as GruntBase : null;
        if (t != null && t != zipTargetNow)
        {
            zipSerial++;
            zipHelpersJoined = 0;
        }
        zipTargetNow = t;
    }

    // a zip into a Grunt is easy to see coming and dead straight, so they get very good at shooting it
    void TickZipLock()
    {
        TrackZip(director != null ? director.Grapple : null);
        GruntBase target = zipTargetNow;

        if (target == null || target.IsDead)
        {
            EndZipLock();
            return;
        }

        if (lockSerial != zipSerial)
        {
            lockSerial = zipSerial;
            lockShotsLeft = 0;
            if (!JoinZipLock(target))
            {
                return;
            }

            lockShotsLeft = target == this ? zipLockShots : zipLockHelperShots;
            lockNextShot = Time.time + Random.Range(zipLockReaction.x, zipLockReaction.y);
            Awareness = 1f;
            SpottedAt = Time.time;
            lastStimulus = Time.time;

            if (!laserOn)
            {
                SetLaser(true);
                lockLaser = true;
            }
            EnemySounds.PlayAt(chargeSound != null ? chargeSound : EnemySounds.LaserCharge, muzzle.position, 1f, 1.3f);
            if (target == this)
            {
                director.Say(this, Random.value < 0.5f ? "I SEE YOU!" : "COME ON THEN!", "ziplock");
            }
        }

        if (lockShotsLeft <= 0)
        {
            return;
        }
        if (Brain.IsInState(StaggerState) || zipDodging)
        {
            EndZipLock();
            return;
        }

        aiming = true;
        AimPoint = ZipLead();
        FaceTowards(player.Center, turnSpeed * 2f);
        if (lockLaser)
        {
            laser.startWidth = 0.05f;
            laser.endWidth = 0.025f;
            laser.startColor = laser.endColor = new Color(1f, 0.1f, 0.05f, 1f);
        }

        if (Time.time < lockNextShot)
        {
            return;
        }
        Fire((AimPoint - muzzle.position).normalized, zipLockSpread);
        lockShotsLeft--;
        lockNextShot = Time.time + zipLockInterval;
        if (lockShotsLeft <= 0)
        {
            EndZipLock();
        }
    }

    bool JoinZipLock(GruntBase target)
    {
        if (!zipLockOn || !PlayerVisible || muzzle == null)
        {
            return false;
        }
        if (Brain.IsInState(StaggerState))
        {
            return false;
        }
        if (target == this)
        {
            return true;
        }

        bool mate = Squad != null && Squad == target.Squad;
        if (!mate && Vector3.Distance(transform.position, target.transform.position) > zipLockRange)
        {
            return false;
        }
        if (zipHelpersJoined >= target.zipLockHelpers)
        {
            return false;
        }
        zipHelpersJoined++;
        return true;
    }

    void EndZipLock()
    {
        lockShotsLeft = 0;
        if (!lockLaser)
        {
            return;
        }
        lockLaser = false;
        SetLaser(false);
    }

    // first order lead is enough, the zip is a straight line at a steady speed
    Vector3 ZipLead()
    {
        Vector3 from = muzzle.position;
        float t = Vector3.Distance(from, player.Center) / Mathf.Max(1f, bulletSpeed);
        return player.Center + player.Velocity * t;
    }

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
            if (!IsDead)
            {
                RequestStagger(staggerTime);
            }
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
        if (fx != null)
        {
            fx.AirPuff(HeadPosition, Vector3.up, 0.7f);
        }
        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.Impact(0.3f, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0f, 0f), 6f);
        }

        RequestStagger(staggerTime);
        return true;
    }
}
