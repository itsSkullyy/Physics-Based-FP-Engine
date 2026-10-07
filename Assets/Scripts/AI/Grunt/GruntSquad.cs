using System.Collections.Generic;
using UnityEngine;

public enum SquadOrder
{
    Hold,
    Advance,
    Suppress,
    Regroup,
    Search,
    Flank,
    CutOff,
    Formation,
    Investigate,
    Watch
}

public enum Fireteam { Alpha, Bravo }

public enum SquadAlert
{
    Calm,
    Suspicious,
    Searching,
    Combat
}

// A leader and his followers (two fireteams). Made by AIDirector. The squad board sits
// between the followers' boards and the global one and holds orders plus what the
// squad knows (sightings, noises, suspicion, bodies, alert level).
public class GruntSquad
{
    static int nextId;

    public readonly int Id;
    public GruntLeader Leader { get; }
    public readonly List<GruntFollower> Followers = new List<GruntFollower>();
    public readonly List<GruntFollower> Alpha = new List<GruntFollower>();
    public readonly List<GruntFollower> Bravo = new List<GruntFollower>();
    public readonly Blackboard Board;

    public float LastFireTime { get; private set; } = -99f;
    public float LastSeenTime { get; private set; } = -99f;
    public float TimeSinceSeen => Time.time - LastSeenTime;

    public bool Broken => Board.Get(BB.SquadBroken, false);
    public SquadOrder Order => Board.Get(BB.SquadOrder, SquadOrder.Hold);
    public string ShootTokens => "shoot" + Id;

    public SquadAlert Alert => Board.Get(BB.Alert, SquadAlert.Calm);
    public bool LeaderDown => Leader == null || Leader.IsDead;
    public float LeaderDiedAt { get; private set; } = -99f;

    // noise or glimpse worth checking out
    public Vector3 SuspectPos { get; private set; }
    public GruntBase SuspectReporter { get; private set; }
    public bool SuspectWasHeard { get; private set; }
    // thrown axe landing, whole squad goes
    public bool SuspectLure { get; private set; }
    public float SuspectTime { get; private set; } = -99f;
    public float TimeSinceSuspect => Time.time - SuspectTime;
    public int SuspectEvents { get; private set; }
    public bool InvestigationDone { get; set; }

    public float BodyFoundTime { get; private set; } = -99f;
    public Vector3 BodyPos { get; private set; }

    // someone followed a recalled axe
    public float TrailTime { get; private set; } = -99f;

    // jumpy for a while after a fight or a body
    public float AlarmedUntil { get; private set; } = -99f;
    public bool Alarmed => Time.time < AlarmedUntil;

    public Vector3 KnownPos => Board.Get(BB.KnownPos, Leader != null ? Leader.transform.position : Vector3.zero);
    public Vector3 KnownVel => Board.Get(BB.KnownVel, Vector3.zero);
    public float TimeSinceKnown => Time.time - Board.Get(BB.KnownAt, -999f);

    public GruntSquad(GruntLeader leader, Blackboard global)
    {
        Id = ++nextId;
        Leader = leader;
        Board = new Blackboard(global);
        Board.Set(BB.LeaderAlive, true);
        Board.Set(BB.SquadBroken, false);
        Board.Set(BB.LeaderEngaged, false);
        Board.Set(BB.SquadOrder, SquadOrder.Hold);
    }

    public void Add(GruntFollower f)
    {
        if (Followers.Contains(f)) return;
        Followers.Add(f);
        f.AssignSquad(this);
    }

    // split left/right of the leader
    public void SplitFireteams()
    {
        Alpha.Clear();
        Bravo.Clear();
        if (Leader == null) return;

        Transform lt = Leader.transform;
        List<GruntFollower> sorted = new List<GruntFollower>(Followers);
        sorted.Sort((a, b) =>
            Vector3.Dot(a.transform.position - lt.position, lt.right)
                .CompareTo(Vector3.Dot(b.transform.position - lt.position, lt.right)));

        for (int i = 0; i < sorted.Count; i++)
        {
            bool alpha = i < (sorted.Count + 1) / 2;
            (alpha ? Alpha : Bravo).Add(sorted[i]);
            sorted[i].Fireteam = alpha ? Fireteam.Alpha : Fireteam.Bravo;
        }
    }

    public List<GruntFollower> Team(Fireteam t) => t == Fireteam.Alpha ? Alpha : Bravo;

    public void SetOrder(SquadOrder order) => Board.Set(BB.SquadOrder, order);

    public void SetAlert(SquadAlert alert)
    {
        Board.Set(BB.Alert, alert);
        Board.Set(BB.LeaderEngaged, alert == SquadAlert.Combat);
        if (alert == SquadAlert.Combat) Alarm(60f);
    }

    public void Alarm(float seconds) => AlarmedUntil = Mathf.Max(AlarmedUntil, Time.time + seconds);

    public void ReportSuspicion(Vector3 position, GruntBase who, bool heard, bool lure = false)
    {
        if (Time.time - SuspectTime > 2f)
        {
            SuspectEvents++;
            SuspectLure = false;
        }
        SuspectPos = position;
        SuspectReporter = who;
        SuspectWasHeard = heard;
        SuspectLure |= lure;
        SuspectTime = Time.time;
    }

    public void ReportTrail(Vector3 position)
    {
        TrailTime = Time.time;
        Alarm(60f);
        if (TimeSinceSeen > 2f)
        {
            Board.Set(BB.KnownPos, position);
            Board.Set(BB.KnownVel, Vector3.zero);
            Board.Set(BB.KnownAt, Time.time);
        }
    }

    public void ReportBody(Vector3 position)
    {
        BodyFoundTime = Time.time;
        BodyPos = position;
        Alarm(120f);
        if (TimeSinceSeen > 2f)
        {
            Board.Set(BB.KnownPos, position);
            Board.Set(BB.KnownVel, Vector3.zero);
            Board.Set(BB.KnownAt, Time.time);
        }
    }

    public void MarkLeaderDied() => LeaderDiedAt = Time.time;

    public void ReportSighting(Vector3 position, Vector3 velocity)
    {
        LastSeenTime = Time.time;
        Board.Set(BB.KnownPos, position);
        Board.Set(BB.KnownVel, velocity);
        Board.Set(BB.KnownAt, Time.time);
    }

    // ignored if someone saw him recently
    public void ReportNoise(Vector3 position)
    {
        if (TimeSinceKnown < 1.5f) return;
        Board.Set(BB.KnownPos, position);
        Board.Set(BB.KnownVel, Vector3.zero);
        Board.Set(BB.KnownAt, Time.time);
    }

    public void MarkFired() => LastFireTime = Time.time;

    public void Break()
    {
        if (Broken) return;
        Board.Set(BB.LeaderAlive, false);
        Board.Set(BB.SquadBroken, true);
        Board.Set(BB.LeaderEngaged, false);
    }

    public int AliveFollowers
    {
        get
        {
            int n = 0;
            foreach (GruntFollower f in Followers)
                if (f != null && !f.IsDead) n++;
            return n;
        }
    }

    public IEnumerable<GruntFollower> Alive(List<GruntFollower> list)
    {
        foreach (GruntFollower f in list)
            if (f != null && !f.IsDead) yield return f;
    }
}
