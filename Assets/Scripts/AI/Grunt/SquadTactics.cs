using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Squad brain for a leader, using GOAP. Facts come from what the squad knows plus the
// TacticalMap, not from the player directly.
// Goals: Find, Protect, Trap, Flush, Pressure
// Actions: SearchSweep, BoundingAdvance, SuppressLine, FireteamFlank, Pincer,
//          FlushGrenade, PushCover, CutOff, Rally
// Also handles formations when idle and who checks things out when suspicious.
public class SquadTactics
{
    enum F { Known, Sees, InRange, Pinned, Exposed, Flanked, EscapeCovered, Covered, GrenadeReady, Strong, Guarded }

    static readonly string[] FactNames =
    {
        "Known", "Sees", "InRange", "Pinned", "Exposed", "Flanked", "EscapeCovered",
        "Covered", "GrenadeReady", "Strong", "Guarded"
    };

    // seconds unseen mid-fight before they start searching
    const float HideTime = 7f;

    readonly GruntLeader leader;
    readonly GoapAgent goap;

    float escapeCoveredAt = -99f;
    float guardedAt = -99f;
    Vector3 exitPoint;
    float mobility;
    float formationTimer;

    struct Searched { public Vector3 pos; public float at; }
    readonly List<Searched> searched = new List<Searched>();
    readonly List<TacticalMap.Point> scratch = new List<TacticalMap.Point>();

    public FormationShape Shape { get; private set; } = FormationShape.Wedge;
    // null if the leader went himself
    public GruntFollower Investigator { get; private set; }
    public GruntFollower Overwatch { get; private set; }
    public string CurrentPlay => goap.CurrentAction != null ? goap.CurrentAction.Name : "-";

    GruntSquad Squad => leader.Squad;
    PlayerMotionTracker Player => leader.Target;
    TacticalMap Map => TacticalMap.Instance != null && TacticalMap.Instance.Ready ? TacticalMap.Instance : null;

    public SquadTactics(GruntLeader leader)
    {
        this.leader = leader;
        goap = new GoapAgent(Sense, FactNames) { GoalCheckInterval = 0.5f };
        BuildGoals();
        BuildActions();
        goap.Replanned += (goal, plan) =>
        {
            if (goal != null && Squad != null)
            {
                Squad.Board.Set(BB.Tactic, goap.Describe());
            }
        };
    }

    // ---------------------------------------------------------------- ticking

    public void Tick(float dt)
    {
        if (Squad == null || Player == null)
        {
            return;
        }

        // how much the player's been flying about lately
        float now = Player.IsSwinging || Player.IsWallRunning || Player.IsZipping || Player.Speed > 11f ? 1f : 0f;
        mobility = Mathf.MoveTowards(mobility, now, dt * (now > mobility ? 1.5f : 0.4f));

        if (Previewing)
        {
            return;
        }
        goap.Tick(dt);
    }

    // column while walking, wedge when stopped
    public void TickIdle(float dt)
    {
        if (Squad == null || Previewing)
        {
            return;
        }

        formationTimer -= dt;
        if (formationTimer > 0f)
        {
            return;
        }
        formationTimer = 0.5f;

        Vector3 vel = leader.Velocity;
        bool walking = new Vector3(vel.x, 0f, vel.z).sqrMagnitude > 0.5f;
        Shape = walking ? FormationShape.Column : FormationShape.Wedge;
        Vector3 forward = walking ? vel : leader.transform.forward;

        List<GruntFollower> all = AliveAll();
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 slot = Formations.World(Shape, i, all.Count, leader.transform.position, forward, 2.5f);
            Assign(all[i], SquadOrder.Formation, OnNavMesh(slot), 1.5f);
        }
        Squad.SetOrder(SquadOrder.Formation);
    }

    public void Stop() => goap.Stop();

    // ---------------------------------------------------------------- suspicious

    // send = who checks it (null = leader). groupUp = everyone follows the leader
    public void BeginInvestigation(Vector3 spot, GruntFollower send, GruntBase reporter, bool groupUp)
    {
        if (Squad == null)
        {
            return;
        }
        goap.Stop();
        previewUntil = 0f;
        Investigator = send;
        grouped = groupUp;
        groupTimer = 0f;

        if (groupUp)
        {
            Overwatch = null;
            FollowInWedge(spot);
            return;
        }

        List<GruntFollower> watchers = AliveAll();
        if (send != null)
        {
            watchers.Remove(send);
        }

        // one watches the spot, the rest face outwards
        Overwatch = reporter is GruntFollower r && watchers.Contains(r) ? r : (watchers.Count > 0 ? Nearest(watchers, spot) : null);

        Vector3 toSpot = Flat(spot - leader.transform.position);
        int others = watchers.Count - (Overwatch != null ? 1 : 0);
        int k = 0;
        foreach (GruntFollower f in watchers)
        {
            Vector3 dir;
            if (f == Overwatch)
            {
                dir = Flat(spot - f.transform.position);
            }
            else
            {
                k++;
                dir = Quaternion.Euler(0f, 360f * k / (others + 1), 0f) * toSpot;
            }
            f.Board.Set(BB.WatchDir, dir);
            Assign(f, SquadOrder.Watch, OnNavMesh(f.transform.position), 0f);
        }

        if (send != null)
        {
            Assign(send, SquadOrder.Investigate, OnNavMesh(spot), 0f);
        }
        Squad.SetOrder(SquadOrder.Watch);
    }

    public void TickSuspicious(float dt)
    {
        if (Squad == null)
        {
            return;
        }

        if (grouped)
        {
            groupTimer -= dt;
            if (groupTimer > 0f)
            {
                return;
            }
            groupTimer = 0.5f;
            FollowInWedge(Squad.SuspectPos);
            return;
        }

        if (Investigator == null || Investigator.IsDead || Squad.InvestigationDone)
        {
            return;
        }
        if (Squad.TimeSinceSuspect < 1f)
        {
            Assign(Investigator, SquadOrder.Investigate, OnNavMesh(Squad.SuspectPos), 3f);
        }
    }

    bool grouped;
    float groupTimer;

    void FollowInWedge(Vector3 spot)
    {
        List<GruntFollower> all = AliveAll();
        Vector3 forward = Flat(spot - leader.transform.position);
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 slot = Formations.World(FormationShape.Wedge, i, all.Count, leader.transform.position, forward, 2.5f);
            Assign(all[i], SquadOrder.Formation, OnNavMesh(slot), 2f);
        }
        Squad.SetOrder(SquadOrder.Formation);
        Shape = FormationShape.Wedge;
    }

    // push in behind a grenade thrown at a hiding player
    public void OnGrenadeThrown(Vector3 target)
    {
        if (Squad == null || Previewing || !PlayerHiding() || Squad.AliveFollowers < 2)
        {
            return;
        }
        string current = CurrentPlay;
        if (current == "FlushGrenade" || current == "PushCover")
        {
            return;
        }

        GoapAction push = goap.Actions.Find(a => a.Name == "PushCover");
        if (push != null)
        {
            goap.Force(push);
        }
    }

    // ---------------------------------------------------------------- testing

    float previewUntil;

    public IEnumerable<string> PlayNames
    {
        get { foreach (GoapAction a in goap.Actions) { yield return a.Name; } }
    }

    // F6 menu
    public bool ForcePlay(string name)
    {
        if (Squad == null || Player == null)
        {
            return false;
        }
        GoapAction action = goap.Actions.Find(a => a.Name == name);
        if (action == null)
        {
            return false;
        }

        Squad.ReportSighting(Player.Center, Player.Velocity);
        if (name == "CutOff" && !FindExit())
        {
            return false;
        }
        previewUntil = 0f;
        goap.Force(action);
        return true;
    }

    // F6 menu
    public void PreviewFormation(FormationShape shape, float seconds)
    {
        if (Squad == null || Player == null)
        {
            return;
        }
        goap.Stop();
        previewUntil = Time.time + seconds;
        Shape = shape;

        List<GruntFollower> all = AliveAll();
        Vector3 forward = Flat(Player.Center - leader.transform.position);
        List<Vector3> used = new List<Vector3>();
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 slot = Formations.World(shape, i, all.Count, leader.transform.position, forward, 3f);
            Vector3 spot = Snap(slot, 2.5f, Player.Center, false, used);
            used.Add(spot);
            Assign(all[i], SquadOrder.Formation, spot, 0.5f);
        }
        leader.Say("FORMATION: " + shape.ToString().ToUpper(), "formation");
    }

    bool Previewing => Time.time < previewUntil;

    public string Describe() => goap.Describe() + "\nworld: " + goap.DescribeWorld() + "\nformation: " + Shape;

    // ---------------------------------------------------------------- world state

    WorldState Sense()
    {
        WorldState w = default;
        if (Squad == null)
        {
            return w;
        }

        bool known = Squad.TimeSinceKnown < 15f || Squad.Alert == SquadAlert.Searching;
        bool sees = Squad.TimeSinceSeen < 1f;
        Vector3 threat = Squad.KnownPos;

        w = w.With((int)F.Known, known);
        w = w.With((int)F.Sees, sees);
        w = w.With((int)F.InRange, AverageDistance(threat) <= leader.idealRange + 6f);
        w = w.With((int)F.Pinned, sees && Time.time - Squad.LastFireTime < 1.5f);
        w = w.With((int)F.Flanked, FlankedNow(threat));
        w = w.With((int)F.Exposed, sees && (!Player.IsGrounded || CoverAtPlayer(threat) < 0.5f || FlankedNow(threat)));
        w = w.With((int)F.EscapeCovered, Time.time - escapeCoveredAt < 6f);
        w = w.With((int)F.Covered, PlayerHiding() || PlayerCamping(sees, threat));
        w = w.With((int)F.GrenadeReady, leader.GrenadeReady && !leader.GrenadeBlocked);
        w = w.With((int)F.Strong, Squad.AliveFollowers >= 2);
        w = w.With((int)F.Guarded, Time.time - guardedAt < 5f);
        return w;
    }

    bool PlayerHiding() =>
        Squad != null && Squad.Alert == SquadAlert.Combat && Squad.TimeSinceSeen >= 1f && Squad.TimeSinceSeen < HideTime;

    bool PlayerCamping(bool sees, Vector3 threat) =>
        sees && Player.StationaryTime > 1.5f && Player.OnGround && CoverAtPlayer(threat) >= 0.5f;

    float CoverAtPlayer(Vector3 threat)
    {
        TacticalMap map = Map;
        return map != null ? map.CoverAt(threat, leader.EyePosition) : 0f;
    }

    // someone sees him from 60+ degrees off the leader's line
    bool FlankedNow(Vector3 threat)
    {
        Vector3 leaderAxis = Flat(leader.transform.position - threat);
        foreach (GruntFollower f in AliveAll())
        {
            if (!f.PlayerVisible)
            {
                continue;
            }
            if (Vector3.Angle(leaderAxis, Flat(f.transform.position - threat)) > 60f)
            {
                return true;
            }
        }
        return false;
    }

    float AverageDistance(Vector3 threat)
    {
        float total = 0f;
        int n = 0;
        foreach (GruntFollower f in AliveAll())
        {
            total += Vector3.Distance(f.transform.position, threat);
            n++;
        }
        return n > 0 ? total / n : Vector3.Distance(leader.transform.position, threat);
    }

    // ---------------------------------------------------------------- goals

    void BuildGoals()
    {
        goap.Goals.Add(new GoapGoal("Find")
        {
            Priority = () => Squad != null && (Squad.Alert == SquadAlert.Searching
                             || Squad.TimeSinceKnown < 15f && Squad.TimeSinceSeen >= HideTime) ? 2f : 0f
        }.Wants((int)F.Sees));

        goap.Goals.Add(new GoapGoal("Protect")
        {
            Priority = () => Squad != null && leader.HitsTaken >= leader.maxHits - 1 && Squad.AliveFollowers >= 2
                             && Squad.TimeSinceSeen < 3f ? 1.8f : 0f
        }.Wants((int)F.Guarded));

        goap.Goals.Add(new GoapGoal("Trap")
        {
            Priority = () => Squad != null && Squad.TimeSinceSeen < 1f && mobility > 0.5f && Squad.AliveFollowers >= 2 ? 1.3f : 0f
        }.Wants((int)F.EscapeCovered).Wants((int)F.Pinned));

        goap.Goals.Add(new GoapGoal("Flush")
        {
            Priority = () => Squad != null && (PlayerHiding() || PlayerCamping(Squad.TimeSinceSeen < 1f, Squad.KnownPos)) ? 1.6f : 0f
        }.Wants((int)F.Exposed));

        goap.Goals.Add(new GoapGoal("Pressure")
        {
            Priority = () => Squad != null && Squad.TimeSinceKnown < 15f ? 1f : 0f
        }.Wants((int)F.Pinned).Wants((int)F.Exposed));
    }

    // ---------------------------------------------------------------- actions

    void BuildActions()
    {
        GoapAction search = new GoapAction("SearchSweep")
            .Requires((int)F.Known).Requires((int)F.Sees, false)
            .Causes((int)F.Sees);
        search.CreateBehaviour = () =>
        {
            float knownAt = 0f;
            return BT.Sequence("SearchSweep",
                BT.Do("SendSearchers", () =>
                {
                    AssignSearch();
                    knownAt = Squad.Board.Get(BB.KnownAt, -999f);
                }),
                WaitFor("LookingForHim", () => Squad.TimeSinceSeen < 1f || Squad.Board.Get(BB.KnownAt, -999f) > knownAt + 1f, 8f));
        };
        goap.Actions.Add(search);

        GoapAction bound = new GoapAction("BoundingAdvance")
            .Requires((int)F.Known).Requires((int)F.InRange, false)
            .Causes((int)F.InRange);
        bound.Cost = () => 2f;
        bound.CreateBehaviour = MakeBounding;
        goap.Actions.Add(bound);

        GoapAction suppress = new GoapAction("SuppressLine")
            .Requires((int)F.Sees).Requires((int)F.InRange)
            .Causes((int)F.Pinned);
        suppress.CreateBehaviour = () => BT.Sequence("SuppressLine",
            BT.Do("FormLine", FormLine),
            WaitFor("Firing", () => Time.time - Squad.LastFireTime < 0.5f, 3f));
        goap.Actions.Add(suppress);

        GoapAction flank = new GoapAction("FireteamFlank")
            .Requires((int)F.Pinned).Requires((int)F.Strong)
            .Causes((int)F.Flanked).Causes((int)F.Exposed);
        flank.Cost = () => CoverAtPlayer(Squad.KnownPos) > 0.5f ? 1.5f : 2.5f;
        flank.CreateBehaviour = () => BT.Sequence("FireteamFlank",
            BT.Do("SendBravo", () => SendFlank(SmallerTeam())),
            WaitFor("GettingRound", () => FlankedNow(Squad.KnownPos) || TeamArrived(SmallerTeam()), 5f));
        goap.Actions.Add(flank);

        GoapAction pincer = new GoapAction("Pincer")
            .Requires((int)F.Sees).Requires((int)F.InRange).Requires((int)F.Strong)
            .Causes((int)F.Flanked).Causes((int)F.Exposed).Causes((int)F.Pinned);
        pincer.Cost = () => OpenGroundAt(Squad.KnownPos) ? 2f : 3.5f;
        pincer.IsPossible = () => Squad.Alpha.Count > 0 && Squad.Bravo.Count > 0;
        pincer.CreateBehaviour = () => BT.Sequence("Pincer",
            BT.Do("SwingBothSides", SendPincer),
            WaitFor("Closing", () => TeamArrived(Fireteam.Alpha) && TeamArrived(Fireteam.Bravo), 6f));
        goap.Actions.Add(pincer);

        GoapAction grenade = new GoapAction("FlushGrenade")
            .Requires((int)F.Covered).Requires((int)F.GrenadeReady)
            .Causes((int)F.Exposed).Causes((int)F.Covered, false);
        grenade.CreateBehaviour = () =>
        {
            float started = 0f;
            bool accepted = false;
            return BT.Action("Grenade",
                () =>
                {
                    if (!accepted)
                    {
                        return BTStatus.Failure;
                    }
                    if (leader.LastGrenadeTime >= started)
                    {
                        return BTStatus.Success;
                    }
                    return Time.time - started > 2.5f ? BTStatus.Failure : BTStatus.Running;
                },
                () =>
                {
                    started = Time.time;
                    accepted = leader.RequestGrenade(Squad.KnownPos + Squad.KnownVel * 0.3f);
                    if (accepted && Squad.AliveFollowers > 0)
                    {
                        SendAssault(BiggerTeam(), false);
                    }
                });
        };
        goap.Actions.Add(grenade);

        GoapAction push = new GoapAction("PushCover")
            .Requires((int)F.Known).Requires((int)F.Covered).Requires((int)F.Strong)
            .Causes((int)F.Exposed).Causes((int)F.Sees);
        push.Cost = () => 2.5f;
        push.CreateBehaviour = () => BT.Sequence("PushCover",
            BT.Do("MoveIn", () => SendAssault(BiggerTeam(), true)),
            WaitFor("ClosingIn", () => Squad.TimeSinceSeen < 0.5f || TeamArrived(BiggerTeam()), 6f));
        goap.Actions.Add(push);

        GoapAction cutOff = new GoapAction("CutOff")
            .Requires((int)F.Sees).Requires((int)F.Strong)
            .Causes((int)F.EscapeCovered);
        cutOff.Cost = () => 2f;
        cutOff.IsPossible = FindExit;
        cutOff.CreateBehaviour = () => BT.Sequence("CutOff",
            BT.Do("CoverExit", () => SendCutOff(SmallerTeam())),
            WaitFor("InPosition", () => TeamArrived(SmallerTeam()), 5f),
            BT.Do("Covered", () => escapeCoveredAt = Time.time));
        goap.Actions.Add(cutOff);

        GoapAction rally = new GoapAction("Rally")
            .Requires((int)F.Strong)
            .Causes((int)F.Guarded);
        rally.CreateBehaviour = () => BT.Sequence("Rally",
            BT.Do("Ring", FormRing),
            WaitFor("Guarding", () => TeamArrived(Fireteam.Alpha) && TeamArrived(Fireteam.Bravo), 3f),
            BT.Do("Guarded", () => guardedAt = Time.time));
        goap.Actions.Add(rally);
    }

    static BTNode WaitFor(string name, System.Func<bool> condition, float timeout)
    {
        float elapsed = 0f;
        return BT.Action(name,
            () =>
            {
                elapsed += Time.deltaTime;
                return condition() || elapsed >= timeout ? BTStatus.Success : BTStatus.Running;
            },
            () => elapsed = 0f);
    }

    // ---------------------------------------------------------------- plays

    BTNode MakeBounding()
    {
        Fireteam moving = Fireteam.Alpha;
        float phase = 0f;

        return BT.Action("Bounding",
            () =>
            {
                if (AverageDistance(Squad.KnownPos) <= leader.idealRange + 6f)
                {
                    return BTStatus.Success;
                }
                phase += Time.deltaTime;
                if (TeamArrived(moving) || phase > 3.5f)
                {
                    moving = moving == Fireteam.Alpha ? Fireteam.Bravo : Fireteam.Alpha;
                    phase = 0f;
                    BoundStep(moving);
                }
                return BTStatus.Running;
            },
            () =>
            {
                moving = Fireteam.Alpha;
                phase = 0f;
                BoundStep(moving);
            });
    }

    void BoundStep(Fireteam moving)
    {
        Vector3 threat = Squad.KnownPos;
        Shape = FormationShape.Wedge;

        List<GruntFollower> movers = AliveTeam(moving);
        if (movers.Count == 0)
        {
            return;
        }
        Vector3 centroid = Centroid(movers);
        Vector3 toThreat = Flat(threat - centroid);
        float dist = Vector3.Distance(centroid, threat);
        Vector3 anchor = centroid + toThreat * Mathf.Min(8f, Mathf.Max(0f, dist - leader.minRange));

        List<Vector3> used = new List<Vector3>();
        for (int i = 0; i < movers.Count; i++)
        {
            Vector3 slot = Formations.World(FormationShape.Wedge, i, movers.Count, anchor, toThreat, 3f);
            Vector3 spot = Snap(slot, 3.5f, threat, false, used);
            used.Add(spot);
            Assign(movers[i], SquadOrder.Advance, spot);
        }

        foreach (GruntFollower f in AliveTeam(moving == Fireteam.Alpha ? Fireteam.Bravo : Fireteam.Alpha))
        {
            Assign(f, SquadOrder.Suppress);
        }

        leader.Say(moving == Fireteam.Alpha ? "ALPHA, MOVE UP!" : "BRAVO, MOVE UP!", "bound");
    }

    void FormLine()
    {
        Vector3 threat = Squad.KnownPos;
        Shape = FormationShape.Line;

        List<GruntFollower> all = AliveAll();
        Vector3 centroid = Centroid(all);
        Vector3 forward = Flat(threat - centroid);
        List<Vector3> used = new List<Vector3>();
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 slot = Formations.World(FormationShape.Line, i, all.Count, centroid, forward, 3f);
            Vector3 spot = Snap(slot, 3.5f, threat, true, used);
            used.Add(spot);
            Assign(all[i], SquadOrder.Formation, spot);
        }
        Squad.SetOrder(SquadOrder.Suppress);
        leader.Say("LIGHT HIM UP!", "order");
    }

    void SendFlank(Fireteam team)
    {
        Vector3 threat = Squad.KnownPos;
        Vector3 axis = Flat(leader.transform.position - threat);

        // pick the side with more spots that can see him
        float radius = leader.idealRange * 0.8f;
        List<Vector3> left = FlankSpots(team, threat, Quaternion.Euler(0f, -80f, 0f) * axis, radius, out int leftScore);
        List<Vector3> right = FlankSpots(team, threat, Quaternion.Euler(0f, 80f, 0f) * axis, radius, out int rightScore);
        List<Vector3> spots = leftScore >= rightScore ? left : right;

        List<GruntFollower> movers = AliveTeam(team);
        for (int i = 0; i < movers.Count && i < spots.Count; i++)
        {
            Assign(movers[i], SquadOrder.Flank, spots[i]);
        }

        foreach (GruntFollower f in AliveTeam(team == Fireteam.Alpha ? Fireteam.Bravo : Fireteam.Alpha))
        {
            Assign(f, SquadOrder.Suppress);
        }

        leader.Say(team == Fireteam.Alpha ? "ALPHA, FLANK LEFT!" : "BRAVO, GO AROUND!", "flankteam");
    }

    void SendAssault(Fireteam team, bool bark)
    {
        Vector3 threat = Squad.KnownPos;
        Vector3 axis = Flat(leader.transform.position - threat);

        List<Vector3> left = FlankSpots(team, threat, Quaternion.Euler(0f, -50f, 0f) * axis, 8f, out int leftScore);
        List<Vector3> right = FlankSpots(team, threat, Quaternion.Euler(0f, 50f, 0f) * axis, 8f, out int rightScore);
        List<Vector3> spots = leftScore >= rightScore ? left : right;

        List<GruntFollower> movers = AliveTeam(team);
        for (int i = 0; i < movers.Count && i < spots.Count; i++)
        {
            Assign(movers[i], SquadOrder.Advance, spots[i]);
        }

        foreach (GruntFollower f in AliveTeam(team == Fireteam.Alpha ? Fireteam.Bravo : Fireteam.Alpha))
        {
            Assign(f, SquadOrder.Suppress);
        }

        leader.Say(bark ? "MOVE IN! FLUSH HIM OUT!" : "FRAG OUT! MOVE UP!", "assault");
    }

    void SendPincer()
    {
        Vector3 threat = Squad.KnownPos;
        Vector3 axis = Flat(leader.transform.position - threat);

        float radius = leader.idealRange * 0.8f;
        List<Vector3> a = FlankSpots(Fireteam.Alpha, threat, Quaternion.Euler(0f, -85f, 0f) * axis, radius, out _);
        List<Vector3> b = FlankSpots(Fireteam.Bravo, threat, Quaternion.Euler(0f, 85f, 0f) * axis, radius, out _);

        List<GruntFollower> alpha = AliveTeam(Fireteam.Alpha);
        List<GruntFollower> bravo = AliveTeam(Fireteam.Bravo);
        for (int i = 0; i < alpha.Count && i < a.Count; i++)
        {
            Assign(alpha[i], SquadOrder.Flank, a[i]);
        }
        for (int i = 0; i < bravo.Count && i < b.Count; i++)
        {
            Assign(bravo[i], SquadOrder.Flank, b[i]);
        }

        leader.Say("PINCER! BOTH SIDES!", "pincer");
    }

    List<Vector3> FlankSpots(Fireteam team, Vector3 threat, Vector3 side, float radius, out int withSight)
    {
        List<Vector3> spots = new List<Vector3>();
        withSight = 0;
        int n = AliveTeam(team).Count;
        Vector3 across = Vector3.Cross(Vector3.up, side);

        for (int i = 0; i < n; i++)
        {
            Vector3 slot = threat + side.normalized * radius + across * (i - (n - 1) * 0.5f) * 3f;
            Vector3 spot = Snap(slot, 5f, threat, true, spots);
            if (EQSTest.Clear(spot + Vector3.up * 1.5f, threat, IgnoreDynamic))
            {
                withSight++;
            }
            spots.Add(spot);
        }
        return spots;
    }

    void SendCutOff(Fireteam team)
    {
        Vector3 threat = Squad.KnownPos;
        TacticalMap map = Map;
        List<GruntFollower> movers = AliveTeam(team);
        List<Vector3> used = new List<Vector3>();

        foreach (GruntFollower f in movers)
        {
            Vector3 best = exitPoint;
            float bestScore = float.MinValue;

            if (map != null)
            {
                scratch.Clear();
                map.Near(exitPoint, 14f, scratch);
                foreach (TacticalMap.Point p in scratch)
                {
                    if (TooClose(p.position, used, 3f))
                    {
                        continue;
                    }
                    Vector3 eye = p.position + Vector3.up * 1.5f;
                    if (!EQSTest.Clear(eye, exitPoint, IgnoreDynamic))
                    {
                        continue;
                    }

                    float score = map.CoverFrom(p, threat) + (EQSTest.Clear(eye, threat, IgnoreDynamic) ? 0.5f : 0f)
                                  - Vector3.Distance(p.position, f.transform.position) / 40f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p.position;
                    }
                }
            }

            best = OnNavMesh(best);
            used.Add(best);
            Assign(f, SquadOrder.CutOff, best);
        }

        foreach (GruntFollower f in AliveTeam(team == Fireteam.Alpha ? Fireteam.Bravo : Fireteam.Alpha))
        {
            Assign(f, SquadOrder.Suppress);
        }

        leader.Say("CUT HIM OFF!", "cutoff");
    }

    // grapple point or wall run he's heading for
    bool FindExit()
    {
        TacticalMap map = Map;
        if (map == null || Squad == null)
        {
            return false;
        }

        Vector3 from = Squad.KnownPos;
        Vector3 heading = Flat(Squad.KnownVel);
        if (heading.sqrMagnitude < 0.01f)
        {
            heading = Flat(from - leader.transform.position);
        }

        float bestScore = 0.2f;
        bool found = false;

        foreach (Vector3 g in map.GrapplePoints)
        {
            float d = Vector3.Distance(g, from);
            if (d > 40f || d < 4f)
            {
                continue;
            }
            float score = Vector3.Dot(heading, Flat(g - from)) - d / 40f + (g.y - from.y > 2f ? 0.3f : 0f);
            if (score > bestScore) { bestScore = score; exitPoint = g; found = true; }
        }

        foreach (Collider w in map.WallRunWalls)
        {
            if (w == null)
            {
                continue;
            }
            Vector3 p = w.ClosestPoint(from);
            float d = Vector3.Distance(p, from);
            if (d > 15f || d < 1f)
            {
                continue;
            }
            float score = Vector3.Dot(heading, Flat(p - from)) - d / 30f + 0.1f;
            if (score > bestScore) { bestScore = score; exitPoint = p; found = true; }
        }

        return found;
    }

    // hiding spots nobody can currently see
    void AssignSearch()
    {
        Vector3 known = Squad.KnownPos;
        float elapsed = Mathf.Min(Squad.TimeSinceKnown, 6f);
        Vector3 predicted = known + Squad.KnownVel * Mathf.Min(elapsed, 3f) * 0.5f;
        float radius = Mathf.Clamp(4f + elapsed * 6f, 6f, 30f);

        searched.RemoveAll(s => Time.time - s.at > 20f);

        List<(Vector3 pos, float score)> options = new List<(Vector3, float)>();
        TacticalMap map = Map;
        if (map != null)
        {
            scratch.Clear();
            map.Near(predicted, radius, scratch);

            // cap it, the LOS checks get expensive
            if (scratch.Count > 200)
            {
                scratch.Sort((a, b) => (a.position - predicted).sqrMagnitude.CompareTo((b.position - predicted).sqrMagnitude));
                scratch.RemoveRange(200, scratch.Count - 200);
            }

            foreach (TacticalMap.Point p in scratch)
            {
                if (WasSearched(p.position) || SeenBySquad(p.position))
                {
                    continue;
                }
                float score = (1f - Vector3.Distance(p.position, predicted) / radius) * 1.5f + (1f - p.exposure) * 0.5f;
                options.Add((p.position, score));
            }
        }
        options.Sort((a, b) => b.score.CompareTo(a.score));

        List<Vector3> picks = new List<Vector3>();
        foreach (var o in options)
        {
            if (TooClose(o.pos, picks, 4f))
            {
                continue;
            }
            picks.Add(o.pos);
            if (picks.Count > Squad.AliveFollowers)
            {
                break;
            }
        }
        if (picks.Count == 0)
        {
            picks.Add(OnNavMesh(predicted));
        }

        leader.Board.Set(BB.SearchPos, picks[0]);
        Remember(picks[0]);

        List<GruntFollower> waiting = AliveAll();
        for (int i = 1; i < picks.Count && waiting.Count > 0; i++)
        {
            GruntFollower nearest = Nearest(waiting, picks[i]);
            waiting.Remove(nearest);
            Assign(nearest, SquadOrder.Search, picks[i]);
            Remember(picks[i]);
        }
        foreach (GruntFollower f in waiting)
        {
            Assign(f, SquadOrder.Search, OnNavMesh(predicted));
        }

        Squad.SetOrder(SquadOrder.Search);
        string[] lines = { "CHECK THE CORNERS.", "KEEP LOOKING.", "HE'S HERE SOMEWHERE." };
        leader.Say(lines[Random.Range(0, lines.Length)], "sweep");
    }

    void FormRing()
    {
        Vector3 threat = Squad.KnownPos;
        Shape = FormationShape.Ring;
        List<GruntFollower> all = AliveAll();
        List<Vector3> used = new List<Vector3>();
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 slot = Formations.World(FormationShape.Ring, i, all.Count, leader.transform.position, Flat(threat - leader.transform.position), 3f);
            Vector3 spot = Snap(slot, 2.5f, threat, false, used);
            used.Add(spot);
            Assign(all[i], SquadOrder.Formation, spot);
        }
        leader.Say("PROTECT ME!", "rally");
    }

    // ---------------------------------------------------------------- helpers

    void Assign(GruntFollower f, SquadOrder order)
    {
        f.Board.Set(BB.SquadOrder, order);
    }

    // only update the spot if it moved enough
    void Assign(GruntFollower f, SquadOrder order, Vector3 spot, float threshold = 2.5f)
    {
        f.Board.Set(BB.SquadOrder, order);
        if (!f.Board.TryGet(BB.AssignedSpot, out Vector3 current) || Vector3.Distance(current, spot) > threshold)
        {
            f.Board.Set(BB.AssignedSpot, spot);
        }
    }

    bool TeamArrived(Fireteam team)
    {
        foreach (GruntFollower f in AliveTeam(team))
        {
            if (f.Board.TryGet(BB.AssignedSpot, out Vector3 spot) && Vector3.Distance(f.transform.position, spot) > 1.5f)
            {
                return false;
            }
        }
        return true;
    }

    Fireteam BiggerTeam() =>
        AliveTeam(Fireteam.Alpha).Count >= AliveTeam(Fireteam.Bravo).Count ? Fireteam.Alpha : Fireteam.Bravo;

    Fireteam SmallerTeam()
    {
        int a = AliveTeam(Fireteam.Alpha).Count, b = AliveTeam(Fireteam.Bravo).Count;
        if (a == 0)
        {
            return Fireteam.Bravo;
        }
        if (b == 0)
        {
            return Fireteam.Alpha;
        }
        return b <= a ? Fireteam.Bravo : Fireteam.Alpha;
    }

    bool OpenGroundAt(Vector3 pos)
    {
        TacticalMap map = Map;
        TacticalMap.Point p = map != null ? map.Nearest(pos, 3f) : null;
        return p != null && p.exposure > 0.6f;
    }

    Vector3 Snap(Vector3 slot, float radius, Vector3 threat, bool needSight, List<Vector3> used)
    {
        TacticalMap map = Map;
        if (map == null)
        {
            return OnNavMesh(slot);
        }
        return map.SnapToCover(slot, radius, threat, needSight, p => TooClose(p, used, 2f));
    }

    bool SeenBySquad(Vector3 point)
    {
        Vector3 target = point + Vector3.up;
        if (EQSTest.Clear(leader.EyePosition, target, IgnoreDynamic))
        {
            return true;
        }
        foreach (GruntFollower f in AliveAll())
        {
            if (EQSTest.Clear(f.EyePosition, target, IgnoreDynamic))
            {
                return true;
            }
        }
        return false;
    }

    bool WasSearched(Vector3 p)
    {
        foreach (Searched s in searched)
        {
            if ((s.pos - p).sqrMagnitude < 36f)
            {
                return true;
            }
        }
        return false;
    }

    void Remember(Vector3 p) => searched.Add(new Searched { pos = p, at = Time.time });

    static bool IgnoreDynamic(Collider c) => c == null || c.attachedRigidbody != null;

    static bool TooClose(Vector3 p, List<Vector3> others, float min)
    {
        foreach (Vector3 o in others)
        {
            if ((o - p).sqrMagnitude < min * min)
            {
                return true;
            }
        }
        return false;
    }

    static Vector3 OnNavMesh(Vector3 p) =>
        UnityEngine.AI.NavMesh.SamplePosition(p, out UnityEngine.AI.NavMeshHit hit, 4f, UnityEngine.AI.NavMesh.AllAreas) ? hit.position : p;

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
    }

    List<GruntFollower> AliveAll()
    {
        List<GruntFollower> list = new List<GruntFollower>();
        if (Squad != null)
        {
            list.AddRange(Squad.Alive(Squad.Followers));
        }
        return list;
    }

    List<GruntFollower> AliveTeam(Fireteam t)
    {
        List<GruntFollower> list = new List<GruntFollower>();
        if (Squad != null)
        {
            list.AddRange(Squad.Alive(Squad.Team(t)));
        }
        return list;
    }

    static Vector3 Centroid(List<GruntFollower> list)
    {
        if (list.Count == 0)
        {
            return Vector3.zero;
        }
        Vector3 sum = Vector3.zero;
        foreach (GruntFollower f in list)
        {
            sum += f.transform.position;
        }
        return sum / list.Count;
    }

    static GruntFollower Nearest(List<GruntFollower> list, Vector3 p)
    {
        GruntFollower best = null;
        float d = float.MaxValue;
        foreach (GruntFollower f in list)
        {
            float dd = (f.transform.position - p).sqrMagnitude;
            if (dd < d) { d = dd; best = f; }
        }
        return best;
    }
}
