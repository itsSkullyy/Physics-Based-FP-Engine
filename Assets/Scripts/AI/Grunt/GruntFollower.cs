using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Basic Grunt. 1 hit, laggy aim that gets worse in groups. Follows the squad orders on
// the blackboard. Loners patrol and search by themselves. Panics forever once he
// knows the leader is dead.
public class GruntFollower : GruntBase
{
    [Header("Aim")]
    [Tooltip("Base spread in degrees, before the group penalty.")]
    public float spread = 2.5f;
    [Tooltip("Seconds his aim trails behind you.")]
    public float aimLag = 0.3f;

    [Header("Group Accuracy")]
    [Tooltip("Each other Grunt firing within groupRadius widens his spread by this much (0.35 = +35%).")]
    public float spreadPerAlly = 0.35f;
    public float maxGroupMultiplier = 2.5f;
    public float groupRadius = 15f;
    [Tooltip("How many leaderless followers can be shooting at once. Squads use their leader's Squad Shooters.")]
    public int maxShooters = 2;

    [Header("Between Bursts")]
    [Tooltip("Shuffles to a nearby spot (with a clear shot, ideally some cover) this often while suppressing.")]
    public float sidestepCooldown = 3f;
    public float sidestepRadius = 4f;

    [Header("On His Own")]
    [Tooltip("How long he keeps looking for you on his own after losing you.")]
    public float loneSearchTime = 25f;
    [Tooltip("Seconds of his captain going quiet before someone goes to check on him.")]
    public float captainSilenceTime = 4f;

    [Header("Panic")]
    public float panicSpeed = 8f;
    public Vector2 sprayInterval = new Vector2(0.25f, 0.9f);
    [Range(0f, 1f)] public float sprayAtPlayerChance = 0.3f;

    HState idle, alert, combat, engage, staggered, panicked, dead;
    protected override HState StaggerState => staggered;
    protected override HState DeadState => dead;
    protected override int TokenLimit => Squad != null && Squad.Leader != null ? Squad.Leader.squadShooters : maxShooters;
    public override bool InCombatState => Brain != null && (Brain.IsInState(combat) || Brain.IsInState(staggered));
    public override SquadAlert Alertness
    {
        get
        {
            if (Led) return Squad.Alert;
            if (InCombatState) return SquadAlert.Combat;
            return Brain != null && Brain.IsInState(alert) ? SquadAlert.Searching : SquadAlert.Calm;
        }
    }

    public Fireteam Fireteam { get; set; }

    BehaviourTree idleTree, alertTree, engageTree, panicTree;
    EQSQuery fleeQuery;
    Vector3 laggedAim;
    Vector3 sidestepTo;
    Vector3 loneSpot;
    float lastClueTime = -99f;
    float loneAlertUntil = -99f;

    bool Broken => Squad != null && Squad.Broken;
    bool SquadFighting => Led && Squad.Alert == SquadAlert.Combat;
    SquadOrder Order => Board.Get(BB.SquadOrder, SquadOrder.Suppress);

    // leader died and nobody's found him yet
    bool CaptainMissing => Squad != null && !Squad.Broken && Squad.LeaderDown
                           && Time.time - Squad.LeaderDiedAt > captainSilenceTime;

    bool OrderIs(params SquadOrder[] orders)
    {
        SquadOrder o = Order;
        foreach (SquadOrder x in orders) if (o == x) return true;
        return false;
    }

    public override void AssignSquad(GruntSquad squad)
    {
        base.AssignSquad(squad);
        Board.Parent = squad != null ? squad.Board : director.Global;
    }

    // ---------------------------------------------------------------- brain

    protected override HStateMachine BuildBrain()
    {
        BuildQueries();
        BuildTrees();

        HState root = new HState("Follower");
        idle = root.Add(new HState("Idle"), initial: true);
        alert = root.Add(new HState("Alert"));
        combat = root.Add(new HState("Combat"));
        engage = combat.Add(new HState("Engage"), initial: true);
        staggered = root.Add(new HState("Staggered"));
        panicked = root.Add(new HState("Panicked"));
        dead = root.Add(new HState("Dead"));

        root.To(panicked, () => !IsDead && Broken);

        idle.OnEnter = () =>
        {
            AnimBool("Alert", false);
            idleTree.Abort();
            ClearAwareness(suspiciousAt * 0.5f);
        };
        idle.OnTick = dt => { idleTree.Tick(); FaceMovement(turnSpeed * 0.5f); };
        idle.OnExit = () => { freshContact = true; StopMoving(); };
        idle.To(combat, () => Aware || SquadFighting);
        idle.To(alert, WantsAlert);

        alert.OnEnter = () =>
        {
            AnimBool("Alert", true);
            alertTree.Abort();
            if (!Led) KeepLoneAlert(loneSearchTime * 0.5f);
        };
        alert.OnTick = dt =>
        {
            alertTree.Tick();
            FaceMovement(turnSpeed * 0.6f);
        };
        alert.OnExit = () => { alertTree.Abort(); StopMoving(); freshContact = true; };
        alert.To(combat, () => Aware || SquadFighting);
        alert.To(idle, () => !WantsAlert() && alert.TimeInState > 1f);

        combat.OnEnter = () =>
        {
            AnimBool("Alert", true);
            laggedAim = player.Center;
            if (!freshContact) return;
            freshContact = false;
            MarkSpotted();
            if (Aware && Led && Squad.Alert != SquadAlert.Combat) Say("OVER HERE!", "overhere");
        };
        combat.OnExit = () =>
        {
            ReleaseShootToken();
            SetLaser(false);
            director.Unclaim(this);
            if (!Led) KeepLoneAlert(loneSearchTime);
        };
        combat.To(alert, () => !PlayerVisible && TimeSinceSeen > 1.5f
                              && (Led ? Squad.Alert != SquadAlert.Combat : TimeSinceSeen > loseInterestTime && TimeSinceHeard > loseInterestTime));

        engage.OnEnter = () => engageTree.Abort();
        engage.OnTick = TickEngage;
        engage.OnExit = () =>
        {
            engageTree.Abort();
            aiming = false;
            AnimBool("Aiming", false);
        };

        SetupStagger(staggered, combat);

        panicked.OnEnter = StartPanic;
        panicked.OnTick = dt =>
        {
            panicTree.Tick();
            FaceMovement(turnSpeed * 2f);
        };

        dead.OnEnter = () =>
        {
            engageTree.Abort();
            alertTree.Abort();
            panicTree.Abort();
            AnimBool("Panic", false);
        };

        return new HStateMachine(root);
    }

    bool WantsAlert()
    {
        if (Led) return Squad.Alert == SquadAlert.Suspicious || Squad.Alert == SquadAlert.Searching;
        return CaptainMissing || Awareness >= suspiciousAt || Time.time < loneAlertUntil;
    }

    void KeepLoneAlert(float seconds) => loneAlertUntil = Mathf.Max(loneAlertUntil, Time.time + seconds);

    bool IAmChecker
    {
        get
        {
            if (!CaptainMissing) return false;
            Vector3 body = Squad.Leader.BodyPosition;
            GruntFollower best = null;
            float bestD = float.MaxValue;
            foreach (GruntFollower f in Squad.Alive(Squad.Followers))
            {
                float d = (f.transform.position - body).sqrMagnitude;
                if (d < bestD) { bestD = d; best = f; }
            }
            return best == this;
        }
    }

    void BuildQueries()
    {
        DebugQueries.Clear();

        fleeQuery = new EQSQuery("FleePos", EQSGen.Ring(() => transform.position, 4f, 12f, 2, 10))
            .Add(EQSTest.OnNavMesh(1.5f))
            .Add(EQSTest.Custom("AwayFromPlayer", i => Mathf.Clamp01(Vector3.Distance(i.Point, player.Feet) / 20f)), 1.5f)
            .Add(EQSTest.Custom("Chaos", i => Random.value), 1.2f);

        DebugQueries.Add(fleeQuery);
    }

    void BuildTrees()
    {
        // formation if in a squad, otherwise patrol
        idleTree = new BehaviourTree(BT.Selector("Idle",
            BT.Sequence("KeepFormation",
                BT.Condition("HasPlace", () => Led && OrderIs(SquadOrder.Formation) && Board.Has(BB.AssignedSpot)),
                BT.Action("WalkToPlace", () =>
                {
                    Vector3 spot = Board.Get(BB.AssignedSpot, transform.position);
                    float d = Vector3.Distance(transform.position, spot);
                    if (d > 0.8f) MoveTo(spot, d > 6f ? runSpeed : walkSpeed);
                    else StopMoving();
                    return BTStatus.Running;
                })),
            BT.Sequence("StandBy",
                BT.Condition("InSquad", () => Squad != null),
                Scan("LookAround", () => 3f)),
            MakeIdleTree().Root));

        alertTree = new BehaviourTree(BT.Observe("Orders", Board, new[] { BB.SquadOrder, BB.AssignedSpot, BB.WatchDir },
            BT.Selector("Alert",
                // go check on the leader
                BT.Sequence("CheckOnCaptain",
                    BT.Condition("MyJob", () => IAmChecker),
                    BT.Do("Call", () => Say("CAPTAIN? YOU THERE?", "captain")),
                    BT.Always(BT.TimeLimit(20f, BT.Action("WalkOver", () =>
                    {
                        if (Squad == null || Squad.Leader == null) return BTStatus.Failure;
                        MoveTo(Squad.Leader.BodyPosition, CautiousSpeed);
                        return Arrived(2f) ? BTStatus.Success : BTStatus.Running;
                    }))),
                    Scan("LookAround", () => 3f)),
                BT.Sequence("Investigate",
                    BT.Condition("Ordered", () => Led && OrderIs(SquadOrder.Investigate) && Board.Has(BB.AssignedSpot)),
                    BT.Always(CarefulMoveToKey("WalkOver", BB.AssignedSpot, 15f)),
                    Scan("LookAround", () => Random.Range(3f, 4.5f)),
                    BT.Do("Report", () =>
                    {
                        if (!Led || Squad.InvestigationDone) return;
                        Squad.InvestigationDone = true;
                        Say("NOTHING HERE, CAPTAIN.", "nothing");
                        director.SayLater(Squad.Leader, "COPY. STAY SHARP.", "copy", 1.1f);
                    }),
                    Scan("Wait", () => float.PositiveInfinity)),
                BT.Sequence("Watch",
                    BT.Condition("Ordered", () => Led && OrderIs(SquadOrder.Watch)),
                    Scan("Watch", () => float.PositiveInfinity, () => Board.Get(BB.WatchDir, transform.forward), 90f)),
                BT.Sequence("Search",
                    BT.Condition("Ordered", () => Led && OrderIs(SquadOrder.Search, SquadOrder.Regroup) && Board.Has(BB.AssignedSpot)),
                    BT.Always(CarefulMoveToKey("CheckSpot", BB.AssignedSpot, 12f)),
                    Scan("LookAround", () => Random.Range(2f, 3.5f)),
                    Scan("Wait", () => float.PositiveInfinity)),
                BT.Sequence("GoToSpot",
                    BT.Condition("Ordered", () => Led && Board.Has(BB.AssignedSpot)),
                    BT.Always(CarefulMoveToKey("Walk", BB.AssignedSpot, 10f)),
                    Scan("Wait", () => float.PositiveInfinity)),
                BT.Sequence("LoneSearch",
                    BT.Condition("Alone", () => !Led),
                    BT.Always(BT.TimeLimit(12f, BT.Action("GoLook",
                        () =>
                        {
                            MoveTo(loneSpot, CautiousSpeed);
                            return Arrived(1.5f) ? BTStatus.Success : BTStatus.Running;
                        },
                        () => loneSpot = LoneSearchSpot()))),
                    Scan("LookAround", () => Random.Range(2.5f, 4f))),
                Scan("LookAround", () => float.PositiveInfinity))));

        // restarts when the order or spot changes
        engageTree = new BehaviourTree(BT.Observe("Orders", Board, new[] { BB.SquadOrder, BB.AssignedSpot },
            BT.Selector("Follow",
                BT.Sequence("MoveThenShoot",
                    BT.Condition("Ordered", () => OrderIs(SquadOrder.Advance, SquadOrder.Flank, SquadOrder.CutOff, SquadOrder.Formation)),
                    BT.Condition("HasSpot", () => Board.Has(BB.AssignedSpot)),
                    MoveToKey("RunToSpot", BB.AssignedSpot, () => runSpeed, 7f),
                    ShootIfVisible()),
                BT.Sequence("MoveThenLook",
                    BT.Condition("Ordered", () => OrderIs(SquadOrder.Search, SquadOrder.Regroup)),
                    BT.Condition("HasSpot", () => Board.Has(BB.AssignedSpot)),
                    MoveToKey("CheckSpot", BB.AssignedSpot, () => runSpeed, 7f),
                    Scan("LookAround", () => 1.5f)),
                BT.Sequence("LoneInvestigate",
                    BT.Condition("Alone", () => !Led && !PlayerVisible && Mathf.Min(TimeSinceSeen, TimeSinceHeard) < 12f),
                    BT.TimeLimit(8f, BT.Action("GoLook", () =>
                    {
                        Vector3 target = TimeSinceHeard < TimeSinceSeen
                            ? Board.Get(BB.HeardPos, transform.position)
                            : Board.Get(BB.LastSeenPos, transform.position);
                        MoveTo(target, runSpeed * 0.8f);
                        return Arrived(1.5f) ? BTStatus.Success : BTStatus.Running;
                    })),
                    Scan("LookAround", () => 2f)),
                BT.Sequence("Suppress",
                    BT.Check(Board, BB.PlayerVisible, true),
                    FaceTarget(),
                    MakeShootSequence(),
                    BT.Always(BT.Cooldown(sidestepCooldown, Sidestep()))),
                BT.Sequence("Hold",
                    BT.Do("Stop", StopMoving),
                    BT.Wait("Watch", 0.4f)))));

        panicTree = new BehaviourTree(BT.Parallel("Panic", BTParallel.Policy.RequireAll,
            BT.Repeat(-1, BT.Always(BT.Sequence("Flee",
                QueryInto("PickSpot", fleeQuery, BB.FleePos, false),
                MoveToKey("Run", BB.FleePos, () => panicSpeed, 3f)))),
            BT.Repeat(-1, BT.Sequence("Spray",
                BT.Wait("Gap", () => Random.Range(sprayInterval.x, sprayInterval.y)),
                BT.Do("Shoot", SprayShot)))));
    }

    BTNode CarefulMoveToKey(string name, string key, float timeout)
    {
        return BT.TimeLimit(timeout, BT.Action(name, () =>
        {
            Vector3 spot = Board.Get(key, transform.position);
            float d = Vector3.Distance(transform.position, spot);
            MoveTo(spot, d > 6f ? walkSpeed * 1.4f : CautiousSpeed);
            return Arrived(1f) ? BTStatus.Success : BTStatus.Running;
        }));
    }

    // newest clue, then random spots around it
    Vector3 LoneSearchSpot()
    {
        float seenAt = Board.Get(BB.LastSeenTime, -999f);
        float heardAt = Board.Get(BB.HeardAt, -999f);
        float bodyAt = Time.time - TimeSinceBody;

        Vector3 clue = transform.position;
        float clueTime = -999f;
        if (seenAt > clueTime) { clueTime = seenAt; clue = Board.Get(BB.LastSeenPos, clue); }
        if (heardAt > clueTime) { clueTime = heardAt; clue = Board.Get(BB.HeardPos, clue); }
        if (bodyAt > clueTime) { clueTime = bodyAt; clue = LastBodyPos; }

        if (clueTime != lastClueTime)
        {
            lastClueTime = clueTime;
            return clue;
        }

        Vector2 r = Random.insideUnitCircle * 9f;
        Vector3 p = clue + new Vector3(r.x, 0f, r.y);
        return NavMesh.SamplePosition(p, out NavMeshHit hit, 4f, NavMesh.AllAreas) ? hit.position : clue;
    }

    // small move between bursts to somewhere with a shot and some cover
    BTNode Sidestep()
    {
        return BT.TimeLimit(1.5f, BT.Action("Sidestep",
            () =>
            {
                MoveTo(sidestepTo, walkSpeed * 1.4f);
                return Arrived(0.6f) ? BTStatus.Success : BTStatus.Running;
            },
            () =>
            {
                sidestepTo = transform.position;
                TacticalMap map = TacticalMap.Instance;
                if (map == null || !map.Ready || player == null) return;

                var near = new System.Collections.Generic.List<TacticalMap.Point>();
                map.Near(transform.position, sidestepRadius, near);
                float best = float.MinValue;
                foreach (TacticalMap.Point p in near)
                {
                    float d = Vector3.Distance(p.position, transform.position);
                    if (d < 1.5f || director.Crowding(p.position, this, 2f) > 0.5f) continue;
                    if (!EQSTest.Clear(p.position + Vector3.up * eyeHeight, player.Center, IgnoreForSight)) continue;
                    float score = map.CoverFrom(p, player.Center) + Random.value * 0.3f;
                    if (score > best)
                    {
                        best = score;
                        sidestepTo = p.position;
                    }
                }
            }));
    }

    BTNode ShootIfVisible()
    {
        return BT.Selector("ShootIfVisible",
            BT.Sequence("Shoot", BT.Check(Board, BB.PlayerVisible, true), FaceTarget(), MakeShootSequence()),
            BT.Wait("NoTarget", 0.3f));
    }

    void TickEngage(float dt)
    {
        engageTree.Tick();

        bool moving = Velocity.sqrMagnitude > 0.5f;
        TimeAtPos = moving ? 0f : TimeAtPos + dt;

        aiming = PlayerVisible && (laserOn || !moving);
        AnimBool("Aiming", aiming);

        if (aiming) FaceTowards(player.Center, turnSpeed);
        else FaceMovement(turnSpeed);
    }

    // ---------------------------------------------------------------- senses

    protected override void AfterSenses(float dt)
    {
        base.AfterSenses(dt);
        if (!Led && !InCombatState && Awareness >= suspiciousAt) KeepLoneAlert(15f);
    }

    protected override void OnHeard(NoiseEvent noise)
    {
        base.OnHeard(noise);
        if (!Led) KeepLoneAlert(20f);
    }

    protected override void OnFirstToNotice(bool heard, bool lure)
    {
        if (lure) Say("WHAT WAS THAT? OVER THERE!", "notice");
        else Say(heard ? "CAPTAIN! I HEARD SOMETHING!" : "CAPTAIN! SAW SOMETHING MOVE!", "notice");
    }

    protected override void OnFoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        base.OnFoundBody(victim, at, witnessed);
        if (!Led) KeepLoneAlert(45f);
    }

    // ---------------------------------------------------------------- aim

    protected override Vector3 AimTarget() => laggedAim;

    protected override float ShotSpread() => spread * GroupMultiplier();

    float GroupMultiplier()
    {
        int others = FiringNear(transform.position, groupRadius, this);
        return Mathf.Min(maxGroupMultiplier, 1f + spreadPerAlly * others);
    }

    protected override void Update()
    {
        base.Update();
        if (IsDead || player == null) return;

        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, aimLag));
        laggedAim = Vector3.Lerp(laggedAim, player.Center, k);
    }

    // ---------------------------------------------------------------- panic

    void StartPanic()
    {
        engageTree.Abort();
        idleTree.Abort();
        alertTree.Abort();
        StopMoving();
        ReleaseShootToken();
        SetLaser(false);
        aiming = false;
        AnimBool("Aiming", false);
        AnimBool("Panic", true);

        string[] lines = { "AAAAAH!", "BOSS IS DOWN!", "RUN!", "WE'RE DONE FOR!" };
        director.Say(this, lines[Random.Range(0, lines.Length)], "panic" + GetInstanceID());
    }

    void SprayShot()
    {
        if (IsDead) return;

        int shots = Random.Range(1, 4);
        for (int i = 0; i < shots; i++)
        {
            Vector3 dir;
            if (Random.value < sprayAtPlayerChance)
            {
                dir = (player.Center - muzzle.position).normalized;
                Fire(dir, 18f);
            }
            else
            {
                float yaw = Random.Range(0f, 360f);
                float pitch = Random.Range(-35f, 5f);
                dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
                Fire(dir, 0f);
            }
        }
    }

    protected override Quaternion VisualWobble()
    {
        if (Brain == null || !Brain.IsInState(panicked)) return Quaternion.identity;
        float t = Time.time;
        return Quaternion.Euler(Mathf.Sin(t * 23f) * 12f, Mathf.Sin(t * 17f) * 20f, Mathf.Cos(t * 29f) * 14f);
    }

    public override void ResetAgent()
    {
        loneAlertUntil = -99f;
        lastClueTime = -99f;
        base.ResetAgent();
    }

    // ---------------------------------------------------------------- debug

    public override string DebugText()
    {
        StringBuilder sb = new StringBuilder(base.DebugText());
        sb.Append('\n').Append(SquadLine());
        if (Squad != null) sb.Append($"\n{Fireteam}  my order: {Order}");
        if (CaptainMissing) sb.Append("\ncaptain's gone quiet");
        if (Brain != null && Brain.IsInState(alert)) AppendTree(sb, alertTree);
        if (Brain != null && Brain.IsInState(engage)) AppendTree(sb, engageTree);
        if (Brain != null && Brain.IsInState(panicked)) AppendTree(sb, panicTree);
        sb.Append($"\nspread x{GroupMultiplier():0.00}");
        return sb.ToString();
    }
}
