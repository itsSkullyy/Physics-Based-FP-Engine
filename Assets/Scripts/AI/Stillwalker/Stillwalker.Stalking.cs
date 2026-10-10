using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Stillwalker: where it moves while hunting. Utility intents (CloseIn, CatchUp, Ambush,
// Lurk, HoldBack) and their behaviour trees, the running player profile the ML policy
// reads, and the ambush spot it runs ahead to.
public partial class Stillwalker
{
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
        if (away.sqrMagnitude < 0.01f)
        {
            away = -player.CameraForward;
        }
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
            if (utility.Tick(dt) || activeTree == null)
            {
                SwitchTree();
            }
            if (activeTree != null && activeTree.Tick() != BTStatus.Running)
            {
                if (utility.Tick(0f, true))
                {
                    SwitchTree();
                }
            }
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
        if (cachedSensesTime == Time.time)
        {
            return cachedSenses;
        }
        cachedSensesTime = Time.time;

        Vector3 predicted;
        if (!player.IsGrounded && player.PredictLanding(out Vector3 land, out _))
        {
            predicted = land;
        }
        else
        {
            predicted = player.Predict(0.35f) - Vector3.up * player.FeetOffset;
        }

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
            {
                room = hit.distance;
            }

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
            if (axe == null || !axe.IsFlying)
            {
                continue;
            }

            Vector3 v = axe.Velocity;
            float speed = v.magnitude;
            if (speed < 1f)
            {
                continue;
            }

            Vector3 to = ChestPosition - axe.HeadPosition;
            float along = Vector3.Dot(to, v / speed);
            if (along <= 0f || along > 35f)
            {
                continue;
            }
            if ((to - v / speed * along).magnitude > 2.5f)
            {
                continue;
            }

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
        if (dt <= 0f)
        {
            return;
        }
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
        if (player.IsGrounded || player.Velocity.y > 0f)
        {
            return false;
        }
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
        if (TrainingRules || hasAmbush || Hurt || Time.time < nextAmbushTime || Time.time < nextAmbushSearch)
        {
            return;
        }
        nextAmbushSearch = Time.time + 0.6f;

        if (player.Speed < ambushMinPlayerSpeed || !player.IsGrounded)
        {
            return;
        }
        float dist = PathDist;
        if (dist < ambushLaunchRange || dist > ambushMaxDistance)
        {
            return;
        }

        Vector3 flat = player.Velocity;
        flat.y = 0f;
        ambushCenter = player.Feet + flat.normalized * Mathf.Clamp(flat.magnitude * ambushLookahead, 6f, 25f);
        if (!NavMesh.SamplePosition(ambushCenter, out NavMeshHit onMesh, 3f, NavMesh.AllAreas))
        {
            return;
        }
        ambushCenter = onMesh.position;

        EQSItem best = ambushQuery.Run();
        if (best == null)
        {
            return;
        }

        hasAmbush = true;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
        Board.Set(BB.AmbushPos, best.Point);
    }

    void StartAmbush()
    {
        if (ambushStartedAt < 0f)
        {
            ambushStartedAt = Time.time;
        }
    }

    BTStatus TickAmbush()
    {
        if (!hasAmbush)
        {
            return BTStatus.Failure;
        }
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
        if (hasAmbush)
        {
            nextAmbushTime = Time.time + ambushCooldown;
        }
        hasAmbush = false;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
    }

    protected override void OnHeard(NoiseEvent noise) => heardPlayer = true;

    // a stone scrape the first time it gets close behind you, so a jump from behind is never silent
    void CreepCue()
    {
        if (Seen || Time.time < nextCreepCue || Velocity.magnitude < 0.5f || PathDist > creepCueDistance)
        {
            return;
        }
        nextCreepCue = Time.time + creepCueCooldown;
        EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 1f, 0.45f);
        EnemySounds.PlayAt(EnemySounds.StoneCrack, transform.position, 0.35f, 0.5f);
    }
}
