using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// GruntLeader: fighting. EQS queries for firing spots, flanks, cover and dives, the utility
// intents and the behaviour tree for each one, aiming, diving, countering a zip, and grenades.
public partial class GruntLeader
{
    // ---------------------------------------------------------------- EQS

    void BuildQueries()
    {
        DebugQueries.Clear();

        firePosQuery = new EQSQuery("FirePos", EQSGen.Ring(() => player.Feet, minRange, maxRange, 3, 12))
            .Add(EQSTest.OnNavMesh(2f))
            .Add(EQSTest.LineOfSight(() => player.Center, eyeHeight, true, IgnoreForSight))
            .Add(EQSTest.Distance(() => player.Feet, minRange * 0.8f, maxRange * 1.1f, Curves.Bell(Mathf.InverseLerp(minRange * 0.8f, maxRange * 1.1f, idealRange), 0.5f)), 1.5f)
            .Add(EQSTest.Custom("CoverFacingHim", i => MapCover(i.Point)), 1f)
            .Add(EQSTest.Custom("Spread", i => 1f - director.Crowding(i.Point, this, 6f)), 1.2f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / 20f)), 1f)
            .Add(EQSTest.Custom("HighGround", i => 0.5f + 0.5f * Mathf.Clamp01((i.Point.y - player.Feet.y) / 4f)), 0.7f);

        // behind or beside where the player is looking
        flankQuery = new EQSQuery("FlankPos", EQSGen.Ring(() => player.Feet, 8f, 18f, 2, 14))
            .Add(EQSTest.OnNavMesh(2f))
            .Add(EQSTest.LineOfSight(() => player.Center, eyeHeight, true, IgnoreForSight))
            .Add(EQSTest.Dot(() => player.Feet, () => player.CameraForward, Curves.FromFunc(t => 1f - t * t)), 2f)
            .Add(EQSTest.Custom("CoverFacingHim", i => MapCover(i.Point)), 0.5f)
            .Add(EQSTest.Custom("Spread", i => 1f - director.Crowding(i.Point, this, 6f)), 1f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / 25f)), 0.7f);

        diveQuery = new EQSQuery("DivePos", EQSGen.Ring(() => transform.position, diveDistance * 0.6f, diveDistance, 2, 8))
            .Add(EQSTest.OnNavMesh(1f))
            .Add(EQSTest.Custom("ClearPath", i => EQSTest.Clear(transform.position + Vector3.up * 0.5f, i.Point + Vector3.up * 0.5f, IgnoreForSight) ? 1f : -1f, true))
            .Add(EQSTest.Custom("Perpendicular", i =>
            {
                Vector3 d = i.Point - transform.position;
                d.y = 0f;
                return 1f - Mathf.Abs(Vector3.Dot(d.normalized, ThreatDir));
            }), 2f);

        // tactical map points covered from the player, ideally peekable
        coverQuery = new EQSQuery("CoverPos", EQSGen.From(items =>
            {
                TacticalMap map = TacticalMap.Instance;
                if (map != null && map.Ready)
                {
                    var near = new List<TacticalMap.Point>();
                    map.Near(transform.position, coverSearchRadius, near);
                    foreach (TacticalMap.Point p in near)
                    {
                        items.Add(new EQSItem { Point = p.position });
                    }
                }
                else
                {
                    for (int r = 0; r < 3; r++)
                    {
                        for (int k = 0; k < 12; k++)
                        {
                            float a = k * Mathf.PI / 6f + r * 0.5f;
                            float rad = Mathf.Lerp(2f, coverSearchRadius, r / 2f);
                            items.Add(new EQSItem { Point = transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad });
                        }
                    }
                }
            }))
            .Add(EQSTest.OnNavMesh(1.5f))
            .Add(EQSTest.LineOfSight(() => player.Center, 1.0f, false, IgnoreForSight))
            .Add(EQSTest.Distance(() => player.Feet, 6f, 60f, Curves.Linear()), 0.5f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / coverSearchRadius)), 2f)
            .Add(EQSTest.Custom("CoverFacingHim", i => MapCover(i.Point)), 1.5f)
            .Add(EQSTest.Custom("Peekable", i => EQSTest.Clear(i.Point + Vector3.up * eyeHeight, player.Center, IgnoreForSight) ? 1f : 0.3f), 0.5f)
            .Add(EQSTest.Custom("Spread", i => 1f - director.Crowding(i.Point, this, 4f)), 0.8f);

        DebugQueries.Add(firePosQuery);
        DebugQueries.Add(flankQuery);
        DebugQueries.Add(diveQuery);
        DebugQueries.Add(coverQuery);
    }

    float MapCover(Vector3 point)
    {
        TacticalMap map = TacticalMap.Instance;
        return map != null && map.Ready ? map.CoverAt(point, player.Center) : 0.5f;
    }

    // ---------------------------------------------------------------- utility

    void BuildUtility()
    {
        utility = new UtilityBrain { Interval = 0.25f, Stickiness = 1.25f };

        uHold = utility.Add(new UtilityAction("HoldAndShoot"))
            .When(() => PlayerVisible)
            .Consider("Range", () => DistToPlayer() / (maxRange * 1.4f), Curves.Bell(idealRange / (maxRange * 1.4f), 0.55f));

        uReposition = utility.Add(new UtilityAction("Reposition", 0.85f))
            .Consider("Reason", () => Mathf.Max(
                    Mathf.Clamp01(TimeAtPos / 6f),
                    PlayerVisible ? 0f : 1f,
                    DistToPlayer() < minRange * 0.7f ? 0.9f : 0f),
                Curves.Rising(0.45f, 9f));

        uFlank = utility.Add(new UtilityAction("Flank", 0.9f))
            .When(() => TimeSinceSeen < 2f && Squad != null && Squad.AliveFollowers > 0)
            .Consider("PlayerCamping", () => Mathf.Clamp01(player.StationaryTime / 3f), Curves.Rising(0.4f, 8f))
            .Consider("SquadShooting", () => Squad != null ? director.TokensInUse(Squad.ShootTokens) / 2f : 0f, Curves.Linear());

        uPush = utility.Add(new UtilityAction("Push", 1.3f))
            .When(() => Board.Get(BB.PlayerDisarmed, false))
            .Consider("Distance", () => DistToPlayer() / 30f, Curves.Falling(0.8f, 6f));

        uEvade = utility.Add(new UtilityAction("Evade", 2f))
            .When(() => Time.time - lastDiveTime > diveCooldown)
            .Consider("Threat", ThreatLevel, Curves.Rising(0.5f, 20f));

        uCover = utility.Add(new UtilityAction("TakeCover", 1.15f))
            .When(() => coverRunning || (Time.time - lastCoverTime > coverCooldown && TimeSinceSeen < 3f))
            .Consider("Danger", Danger, Curves.Rising(0.45f, 10f));

        uGrenade = utility.Add(new UtilityAction("Grenade", 1.5f))
            .When(() => GrenadeReady && !GrenadeBlocked && TimeSinceSquadSaw() < 7f)
            .Consider("Opportunity", GrenadeOpportunity, Curves.Linear());

        utility.Changed += OnIntentChanged;
    }

    // aimed at, hurt, low hp, or just fired
    float Danger()
    {
        float hurt = 1f - Mathf.Clamp01((Time.time - Board.Get(BB.HurtAt, -99f)) / 4f);
        float lowHealth = maxHits > 1 ? (float)HitsTaken / maxHits * 0.7f : 0f;
        float aimedAt = AimedAtByPlayer() ? 0.8f : 0f;
        float reloading = Time.time - lastBurstTime < 1f ? 0.5f : 0f;
        float inCover = Board.Get(BB.InCover, false) ? 0.6f : 0f;
        return Mathf.Max(hurt, lowHealth, aimedAt, reloading, inCover);
    }

    // best when the player is hiding, also good when he's in cover or the squad is
    // pushing/shooting
    float GrenadeOpportunity()
    {
        Vector3 target = GrenadeAim();
        float d = Vector3.Distance(transform.position, target);
        if (d < grenadeMinRange || d > grenadeMaxRange || AlliesNear(target, grenadeRadius))
        {
            return 0f;
        }

        float score = 0.25f;
        if (Squad != null)
        {
            if (Squad.TimeSinceSeen > 0.6f && Squad.TimeSinceKnown < 6f)
            {
                score = 1f;
            }
            if (SquadPushing())
            {
                score = Mathf.Max(score, 0.8f);
            }
            if (Time.time - Squad.LastFireTime < 1f)
            {
                score = Mathf.Max(score, 0.65f);
            }
        }
        if (PlayerVisible && player.StationaryTime > 1f && MapCoverAtPlayer() >= 0.5f)
        {
            score = Mathf.Max(score, 0.9f);
        }
        if (Board.Get(BB.InCover, false))
        {
            score = Mathf.Max(score, 0.85f);
        }
        return score;
    }

    float MapCoverAtPlayer()
    {
        TacticalMap map = TacticalMap.Instance;
        return map != null && map.Ready ? map.CoverAt(player.Center, EyePosition) : 0f;
    }

    bool SquadPushing()
    {
        if (Squad == null)
        {
            return false;
        }
        foreach (GruntFollower f in Squad.Alive(Squad.Followers))
        {
            SquadOrder o = f.Board.Get(BB.SquadOrder, SquadOrder.Hold);
            if (o == SquadOrder.Advance || o == SquadOrder.Flank || o == SquadOrder.CutOff)
            {
                return true;
            }
        }
        return false;
    }

    // 1 if a charged throw is aimed at him or a thrown axe is about to hit
    float ThreatLevel()
    {
        BattleAxe axe = director.Axe;
        if (axe == null)
        {
            return 0f;
        }

        if (axe.IsCharging && axe.ChargeAmount > 0.35f && AimedAtByPlayer())
        {
            Vector3 d = ChestPosition - player.CameraPosition;
            d.y = 0f;
            ThreatDir = d.normalized;
            return 1f;
        }

        ThrownAxe thrownAxe = axe.ActiveAxe;
        if (thrownAxe != null && !thrownAxe.IsStuck && !thrownAxe.IsLoose && !thrownAxe.IsRecalling && hadAxePos)
        {
            Vector3 p = thrownAxe.transform.position;
            Vector3 v = (p - lastAxePos) / Mathf.Max(0.001f, Time.deltaTime);
            if (v.sqrMagnitude > 4f)
            {
                Vector3 toMe = ChestPosition - p;
                float tClosest = Vector3.Dot(toMe, v) / v.sqrMagnitude;
                if (tClosest > 0f && tClosest < 0.6f && (toMe - v * tClosest).magnitude < 1.6f)
                {
                    ThreatDir = new Vector3(v.x, 0f, v.z).normalized;
                    return 1f;
                }
            }
        }

        return 0f;
    }

    void TrackAxe()
    {
        ThrownAxe t = director.Axe != null ? director.Axe.ActiveAxe : null;
        hadAxePos = t != null;
        if (hadAxePos)
        {
            lastAxePos = t.transform.position;
        }
    }

    void OnIntentChanged(UtilityAction from, UtilityAction to)
    {
        Board.Set(BB.Intent, to != null ? to.Name : "-");

        if (to == uFlank)
        {
            director.Say(this, "I'M GOING AROUND!", "flank");
        }
        else if (to == uPush)
        {
            director.Say(this, "HE'S UNARMED! PUSH!", "push");
        }
        else if (to == uEvade)
        {
            director.Say(this, "WHOA!", "evade");
        }
        else if (to == uCover)
        {
            director.Say(this, "TAKING COVER!", "cover");
        }
    }

    // ---------------------------------------------------------------- trees

    void BuildTrees()
    {
        intentTrees.Clear();

        intentTrees[uHold] = new BehaviourTree(BT.Selector("HoldAndShoot",
            BT.Sequence("Engage",
                BT.Check(Board, BB.PlayerVisible, true),
                FaceTarget(),
                MakeShootSequence()),
            BT.Sequence("WaitTurn",
                BT.Do("Hold", StopMoving),
                BT.Wait("Hold", 0.4f))));

        intentTrees[uReposition] = new BehaviourTree(BT.Sequence("Reposition",
            QueryInto("FindFirePos", firePosQuery, BB.FirePos),
            MoveToKey("RunToFirePos", BB.FirePos, () => runSpeed, 6f),
            BT.Do("Settle", () => TimeAtPos = 0f)));

        intentTrees[uFlank] = new BehaviourTree(BT.Sequence("Flank",
            QueryInto("FindFlank", flankQuery, BB.FlankPos),
            MoveToKey("RunToFlank", BB.FlankPos, () => runSpeed * 1.1f, 7f),
            BT.Do("Settle", () => TimeAtPos = 0f),
            MakeShootSequence()));

        intentTrees[uPush] = new BehaviourTree(BT.Parallel("Push", BTParallel.Policy.RequireOne,
            BT.Action("Charge", () =>
            {
                if (DistToPlayer() < 5f)
                {
                    StopMoving();
                }
                else
                {
                    MoveTo(player.Feet, runSpeed);
                }
                return BTStatus.Running;
            }),
            BT.Repeat(-1, BT.Selector("ShootWhileRunning",
                BT.Sequence("Shoot", BT.Check(Board, BB.PlayerVisible, true), MakeShootSequence()),
                BT.Wait("NoShot", 0.2f)))));

        // diving is an HSM state
        intentTrees[uEvade] = new BehaviourTree(BT.Action("Dive", () =>
        {
            Brain.Request(diving);
            return BTStatus.Running;
        }));

        intentTrees[uCover] = new BehaviourTree(BT.Sequence("TakeCover",
            BT.Do("Begin", () => coverRunning = true),
            QueryInto("FindCover", coverQuery, BB.CoverPos),
            MoveToKey("RunToCover", BB.CoverPos, () => runSpeed * 1.1f, 6f),
            BT.Do("InCover", () => Board.Set(BB.InCover, true)),
            BT.Wait("StayDown", () => Random.Range(coverHoldTime.x, coverHoldTime.y)),
            BT.Do("Done", EndCover)));

        intentTrees[uGrenade] = new BehaviourTree(BT.Action("Throw", () =>
            GrenadeReady && !GrenadeBlocked && RequestGrenade(GrenadeAim()) ? BTStatus.Running : BTStatus.Failure));

        idleTree = MakeIdleTree();

        suspiciousTree = new BehaviourTree(BT.Sequence("Suspicious",
            BT.Action("Freeze", () =>
            {
                StopMoving();
                FaceTowards(SuspectPoint(), turnSpeed * 0.5f);
                return suspicious.TimeInState > 0.9f ? BTStatus.Success : BTStatus.Running;
            }),
            BT.Selector("Check",
                BT.Sequence("CheckItMyself",
                    BT.Condition("MyJob", () => checkingMyself),
                    BT.Always(BT.TimeLimit(15f, BT.Action("WalkOver", () =>
                    {
                        MoveTo(ShortOf(SuspectPoint(), 1.5f), CautiousSpeed);
                        FaceMovement(turnSpeed * 0.5f);
                        return MoveStatus(1.2f);
                    }))),
                    Scan("LookAround", () => Random.Range(3f, 4.5f)),
                    BT.Do("Report", () =>
                    {
                        selfCheckDone = true;
                        if (Squad != null)
                        {
                            Squad.InvestigationDone = true;
                        }

                        // found the axe
                        if (AxeNear(SuspectPoint(), 5f))
                        {
                            director.Say(this, "AN AXE... HE'S CLOSE. FIND HIM!", "axefound");
                            quietSearchStart = true;
                            if (Squad != null)
                            {
                                Squad.ReportTrail(SuspectPoint());
                            }
                            return;
                        }
                        director.Say(this, "NOTHING HERE.", "nothing");
                        calmLine = "ALRIGHT, BACK TO IT.";
                    })),
                Scan("Overwatch", () => float.PositiveInfinity, () => SuspectPoint() - transform.position, 70f))));

        alertTree = new BehaviourTree(BT.Repeat(-1, BT.Sequence("Investigate",
            BT.Always(BT.TimeLimit(12f, BT.Action("GoToSearchSpot", () =>
            {
                Vector3 target = Board.Get(BB.SearchPos, Squad != null ? Squad.KnownPos : Board.Get(BB.LastSeenPos, transform.position));
                MoveTo(target, CautiousSpeed);
                return MoveStatus(1.5f);
            }))),
            Scan("LookAround", () => Random.Range(2f, 3.5f)))));
    }

    bool AxeNear(Vector3 point, float radius)
    {
        ThrownAxe t = director.Axe != null ? director.Axe.ActiveAxe : null;
        return t != null && (t.IsStuck || t.IsLoose) && Vector3.Distance(t.transform.position, point) < radius;
    }

    Vector3 ShortOf(Vector3 point, float distance)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        return d.magnitude <= distance ? transform.position : point - d.normalized * distance;
    }

    void EndCover()
    {
        if (!coverRunning)
        {
            return;
        }
        coverRunning = false;
        lastCoverTime = Time.time;
        Board.Set(BB.InCover, false);
        TimeAtPos = 0f;
    }

    void SwitchTree()
    {
        activeTree?.Abort();
        EndCover();
        ReleaseShootToken();
        SetLaser(false);
        activeTree = utility.Current != null && intentTrees.TryGetValue(utility.Current, out BehaviourTree t) ? t : null;
    }

    void TickEngage(float dt)
    {
        TrackAxe();

        if (utility.Tick(dt))
        {
            SwitchTree();
        }

        if (activeTree != null)
        {
            BTStatus s = activeTree.Tick();
            if (s != BTStatus.Running)
            {
                if (utility.Current == uCover)
                {
                    EndCover();
                }
                // re-score straight away
                if (utility.Tick(0f, true))
                {
                    SwitchTree();
                }
            }
        }

        bool moving = Velocity.sqrMagnitude > 0.5f;
        TimeAtPos = moving ? 0f : TimeAtPos + dt;
        Board.Set(BB.AimedAt, AimedAtByPlayer());

        bool inCover = Board.Get(BB.InCover, false);
        aiming = PlayerVisible && !inCover && (laserOn || utility.Current == uHold || utility.Current == uPush || !moving);
        AnimBool("Aiming", aiming);

        if (aiming || inCover)
        {
            FaceTowards(player.Center, turnSpeed);
        }
        else
        {
            FaceMovement(turnSpeed);
        }

        if (PlayerVisible && player.IsWallRunning && !wasWallRunning)
        {
            director.Say(this, "HE'S ON THE WALL!", "wall");
        }
        wasWallRunning = player.IsWallRunning;
    }

    // ---------------------------------------------------------------- aim

    // lead the target, predicted twice since the lead changes the distance
    protected override Vector3 AimTarget()
    {
        Vector3 origin = muzzle.position;
        float t = Vector3.Distance(origin, player.Center) / bulletSpeed;
        Vector3 p = player.Predict(t);
        t = Vector3.Distance(origin, p) / bulletSpeed;
        return player.Predict(t);
    }

    protected override float ShotSpread() => Mathf.Lerp(maxSpread, minSpread, player.Predictability01);

    protected override void OnBurstFinished() => lastBurstTime = Time.time;

    // ---------------------------------------------------------------- dive

    void StartDive()
    {
        activeTree?.Abort();
        EndCover();
        ReleaseShootToken();
        SetLaser(false);
        StopMoving();

        diveFrom = transform.position;
        EQSItem best = diveQuery.Run();
        diveTo = best != null ? best.Point : transform.position + Vector3.Cross(Vector3.up, ThreatDir) * diveDistance;
        AnimTrigger("Dive");
    }

    void TickDive(float dt)
    {
        float t = Mathf.Clamp01(diving.TimeInState / diveTime);
        float eased = 1f - (1f - t) * (1f - t);
        Vector3 p = Vector3.Lerp(diveFrom, diveTo, eased);
        if (NavReady)
        {
            nav.Warp(p);
        }
        else
        {
            transform.position = p;
        }
        FaceTowards(player.Center, turnSpeed);
    }

    // ---------------------------------------------------------------- zip counter

    bool ShouldCounterZip()
    {
        Grappling g = director.Grapple;
        if (g == null || !g.IsZipping || !ReferenceEquals(g.ZipTarget, this))
        {
            return false;
        }
        if (Time.time - lastCounterTime < counterCooldown || Brain.IsInState(staggered))
        {
            return false;
        }
        return Vector3.Distance(player.Center, ChestPosition) < counterDistance;
    }

    void StartCounter()
    {
        zipDodging = true;
        activeTree?.Abort();
        EndCover();
        ReleaseShootToken();
        SetLaser(false);
        StopMoving();

        Vector3 approach = player.Velocity;
        approach.y = 0f;
        if (approach.sqrMagnitude < 0.01f)
        {
            approach = transform.position - player.Center;
        }
        Vector3 side = Vector3.Cross(Vector3.up, approach.normalized);

        diveFrom = transform.position;
        diveTo = PickSidestep(side) ?? PickSidestep(-side) ?? transform.position;
        AnimTrigger("Dive");
        director.Say(this, Random.value < 0.5f ? "NOT TODAY!" : "HE'S COMING!", "counter");

        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.AirPuff(transform.position + Vector3.up * 0.3f, -side, 0.5f);
        }
    }

    Vector3? PickSidestep(Vector3 side)
    {
        Vector3 target = transform.position + side * 3f;
        if (!NavReady)
        {
            return target;
        }
        if (NavMesh.Raycast(transform.position, target, out NavMeshHit hit, NavMesh.AllAreas))
        {
            return hit.distance > 1.5f ? hit.position : (Vector3?)null;
        }
        return target;
    }

    void TickCounter(float dt)
    {
        float t = Mathf.Clamp01(countering.TimeInState / counterTime);
        Vector3 p = Vector3.Lerp(diveFrom, diveTo, 1f - (1f - t) * (1f - t));
        if (NavReady)
        {
            nav.Warp(p);
        }
        else
        {
            transform.position = p;
        }
        FaceTowards(player.Center, turnSpeed * 2f);
    }

    // ---------------------------------------------------------------- grenade

    // predicted spot if visible, otherwise last known
    Vector3 GrenadeAim()
    {
        if (PlayerVisible)
        {
            if (!player.IsGrounded && player.PredictLanding(out Vector3 landing, out float t, 2f))
            {
                return landing;
            }
            return player.Predict(0.5f);
        }
        if (Squad != null)
        {
            return Squad.KnownPos + Squad.KnownVel * 0.3f;
        }
        return Board.Get(BB.LastSeenPos, player.Center);
    }

    bool AlliesNear(Vector3 point, float radius)
    {
        float r2 = radius * radius;
        foreach (EnemyAgent e in director.Enemies)
        {
            if (e is GruntBase g && g != this && !g.IsDead && (g.transform.position - point).sqrMagnitude < r2)
            {
                return true;
            }
        }
        return false;
    }

    // finds an arc over cover. careful = won't throw near his own men
    public bool RequestGrenade(Vector3 target, bool careful = true)
    {
        if (!GrenadeReady || Brain == null || !Brain.IsInState(engage))
        {
            return false;
        }

        if (Physics.Raycast(target + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 12f, ~0, QueryTriggerInteraction.Ignore))
        {
            target = floor.point;
        }

        if (careful && AlliesNear(target, grenadeRadius))
        {
            grenadeBlockedUntil = Time.time + 1f;
            return false;
        }

        // never at his own feet, and not while you're rushing him (you'd be on top of him when it lands)
        if (FlatDistance(target, transform.position) < grenadeRadius * 1.3f || PlayerRushing())
        {
            grenadeBlockedUntil = Time.time + 1f;
            return false;
        }

        Vector3 origin = HandPosition;
        float baseTime = Mathf.Clamp(Vector3.Distance(origin, target) / 14f, 0.7f, 1.6f);
        foreach (float mult in new[] { 1f, 1.35f, 1.75f, 2.2f })
        {
            float t = baseTime * mult;
            Vector3 v = (target - origin) / t - 0.5f * Physics.gravity * t;
            if (!ArcClear(origin, v, t, target))
            {
                continue;
            }

            grenadeTarget = target;
            grenadeVelocity = v;
            Brain.Request(throwing);
            return true;
        }

        grenadeBlockedUntil = Time.time + 1.5f;
        return false;
    }

    Vector3 HandPosition => muzzle != null ? muzzle.position + Vector3.up * 0.25f : ChestPosition + Vector3.up * 0.4f;

    static bool ArcClear(Vector3 origin, Vector3 v, float time, Vector3 target)
    {
        const int steps = 12;
        Vector3 prev = origin;
        for (int i = 1; i <= steps; i++)
        {
            float t = time * i / steps;
            Vector3 p = origin + v * t + 0.5f * Physics.gravity * t * t;
            Vector3 d = p - prev;
            if (Physics.Raycast(prev, d.normalized, out RaycastHit hit, d.magnitude, ~0, QueryTriggerInteraction.Ignore)
                && hit.rigidbody == null && Vector3.Distance(hit.point, target) > 1.5f)
            {
                return false;
            }
            prev = p;
        }
        return true;
    }

    float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = b.y = 0f;
        return Vector3.Distance(a, b);
    }

    bool PlayerRushing()
    {
        Vector3 to = transform.position - player.Center;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist > grenadeMinRange * 2f)
        {
            return false;
        }
        float closing = Vector3.Dot(player.Velocity, to / Mathf.Max(0.01f, dist));
        return closing > grenadeRushSpeed;
    }

    void ThrowGrenade()
    {
        thrown = true;

        // you closed in during the windup, keep the pin in
        if (FlatDistance(player.Center, transform.position) < grenadeRadius * 1.2f || PlayerRushing())
        {
            nextGrenadeTime = Time.time + 1.5f;
            director.Say(this, "TOO CLOSE!", "frag-abort");
            return;
        }

        LastGrenadeTime = Time.time;
        nextGrenadeTime = Time.time + grenadeInterval * Random.Range(0.85f, 1.3f);
        GruntGrenade.Spawn(HandPosition, grenadeVelocity, this, new GruntGrenade.Settings
        {
            fuse = grenadeFuseTime,
            radius = grenadeRadius,
            maxDamage = grenadeDamage,
            minDamage = grenadeDamage * 0.2f,
            knockback = 12f,
            hurtsGrunts = grenadeHurtsGrunts
        });
        tactics.OnGrenadeThrown(grenadeTarget);
    }
}
