using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Squad leader. 3 hits, better senses, runs the squad.
// HSM: Idle / Suspicious / Alert / Combat{Engage, Diving, Throwing, Countering} / Staggered / Dead
// Engage uses utility AI to pick an intent, each intent is a behaviour tree.
// The squad's plays come from SquadTactics (GOAP). Cover/flank/fire spots use EQS.
// This file has the settings, setup, the state machine and the calm/suspicious/search states.
// Combat is in GruntLeader.Combat.cs and the F6 menu hooks are in GruntLeader.Debug.cs.
public partial class GruntLeader : GruntBase
{
    [Header("Squad")]
    [Tooltip("Followers within this distance can join his squad when the level starts.")]
    public float squadRadius = 25f;
    [Tooltip("Most followers he'll take. Extras go to the next nearest leader.")]
    public int maxFollowers = 6;
    [Tooltip("How many of his followers can shoot at once.")]
    public int squadShooters = 3;
    [Tooltip("How long a non-fatal hit staggers him.")]
    public float hurtStaggerTime = 0.35f;

    [Header("Perception")]
    public float visionAngle = 220f;
    public float visionRange = 60f;
    [Tooltip("Wider band that only notices fast movement.")]
    public float peripheralVision = 300f;
    public float hearingRange = 1.5f;

    [Header("Ranges")]
    public float minRange = 9f;
    public float idealRange = 16f;
    public float maxRange = 26f;

    [Header("Searching")]
    [Tooltip("How long they keep checking out something suspicious before calling it nothing.")]
    public float suspiciousDuration = 20f;
    [Tooltip("How long a full search lasts with nothing new turning up.")]
    public float searchDuration = 25f;
    [Tooltip("Seconds without anyone seeing you before a fight turns into a search.")]
    public float combatMemory = 8f;

    [Header("Aim")]
    [Tooltip("Spread in degrees when you're perfectly predictable / completely unpredictable.")]
    public float minSpread = 0.4f;
    public float maxSpread = 7f;

    [Header("Evade")]
    public float diveDistance = 3.5f;
    public float diveTime = 0.45f;
    public float diveCooldown = 3f;

    [Header("Zip Counter")]
    [Tooltip("When you zip at him and get this close, he sidesteps and your zip loses its lock.")]
    public float counterDistance = 7f;
    public float counterCooldown = 6f;
    public float counterTime = 0.25f;

    [Header("Cover")]
    public float coverSearchRadius = 12f;
    public float coverCooldown = 4f;
    public Vector2 coverHoldTime = new Vector2(1.2f, 2.2f);

    [Header("Grenade")]
    [Tooltip("Average seconds between grenades (randomised a bit each time).")]
    public float grenadeInterval = 6f;
    public float grenadeMinRange = 7f;
    public float grenadeMaxRange = 30f;
    public float throwWindup = 0.45f;
    [Tooltip("Long enough to run up and bat it back with the axe.")]
    public float grenadeFuseTime = 4f;
    public float grenadeRadius = 5f;
    public float grenadeDamage = 45f;
    public bool grenadeHurtsGrunts = true;
    [Tooltip("Won't throw while you're closing in on him faster than this.")]
    public float grenadeRushSpeed = 9f;

    public float LastGrenadeTime { get; private set; } = -99f;
    public bool GrenadeReady => !IsDead && Time.time >= nextGrenadeTime;
    // couldn't find an arc recently
    public bool GrenadeBlocked => Time.time < grenadeBlockedUntil;

    HState idle, suspicious, alert, combat, engage, diving, throwing, countering, staggered, dead;
    protected override HState StaggerState => staggered;
    protected override HState DeadState => dead;
    protected override string TokenKind => null;
    public override bool InCombatState => Brain != null && (Brain.IsInState(combat) || Brain.IsInState(staggered));

    SquadTactics tactics;
    UtilityBrain utility;
    UtilityAction uHold, uReposition, uFlank, uPush, uEvade, uCover, uGrenade;
    readonly Dictionary<UtilityAction, BehaviourTree> intentTrees = new Dictionary<UtilityAction, BehaviourTree>();
    BehaviourTree activeTree, idleTree, suspiciousTree, alertTree;

    EQSQuery firePosQuery, flankQuery, diveQuery, coverQuery;

    float lastDiveTime = -99f;
    float lastCoverTime = -99f;
    float lastBurstTime = -99f;
    float lastCounterTime = -99f;
    float nextGrenadeTime;
    float grenadeBlockedUntil = -99f;
    bool coverRunning;
    bool thrown;
    Vector3 grenadeTarget, grenadeVelocity;
    Vector3 diveFrom, diveTo;
    Vector3 lastAxePos;
    bool hadAxePos;
    bool wasWallRunning;

    HState lastTopState;
    string calmLine;
    bool checkingMyself;
    bool CheckDone => Squad != null ? Squad.InvestigationDone : selfCheckDone;
    bool selfCheckDone;
    bool quietSearchStart;
    int suspectEventsAtStart;
    float investigatorDeadSince = -1f;
    bool investigatorMissing;

    Vector3 ThreatDir
    {
        get => Board.Get(BB.ThreatDir, -transform.forward);
        set => Board.Set(BB.ThreatDir, value);
    }

    // only runs when the component is added
    void Reset()
    {
        maxHits = 3;
    }

    protected override void Awake()
    {
        base.Awake();
        sightAngle = visionAngle;
        sightRange = visionRange;
        peripheralAngle = peripheralVision;
        hearing = hearingRange;
    }

    protected override void Start()
    {
        base.Start();
        TacticalMap.Get();
    }

    // ---------------------------------------------------------------- brain

    protected override HStateMachine BuildBrain()
    {
        tactics = new SquadTactics(this);
        BuildQueries();
        BuildUtility();
        BuildTrees();

        HState root = new HState("Leader");
        idle = root.Add(new HState("Idle"), initial: true);
        suspicious = root.Add(new HState("Suspicious"));
        alert = root.Add(new HState("Alert"));
        combat = root.Add(new HState("Combat"));
        engage = combat.Add(new HState("Engage"), initial: true);
        diving = combat.Add(new HState("Diving"));
        throwing = combat.Add(new HState("Throwing"));
        countering = combat.Add(new HState("Countering"));
        staggered = root.Add(new HState("Staggered"));
        dead = root.Add(new HState("Dead"));

        // Idle: patrol
        idle.OnEnter = () =>
        {
            AnimBool("Alert", false);
            idleTree.Abort();
            ClearAwareness(suspiciousAt * 0.5f);
            if (Squad != null)
            {
                Squad.SetAlert(SquadAlert.Calm);
            }
            if (calmLine != null)
            {
                director.SayLater(this, calmLine, "calm", 0.8f);
            }
            calmLine = null;
        };
        idle.OnTick = dt =>
        {
            idleTree.Tick();
            FaceMovement(turnSpeed * 0.5f);
            tactics.TickIdle(dt);
        };
        idle.OnExit = () => { freshContact = true; lastTopState = idle; StopMoving(); };
        idle.To(combat, () => Aware || SquadSeesPlayer(0.5f));
        idle.To(alert, ClueJustFound);
        idle.To(suspicious, () => Awareness >= suspiciousAt || (Squad != null && Squad.TimeSinceSuspect < 0.5f));

        // Suspicious: someone checks it out, the rest watch
        suspicious.OnEnter = StartSuspicious;
        suspicious.OnTick = TickSuspicious;
        suspicious.OnExit = () =>
        {
            suspiciousTree.Abort();
            StopMoving();
            freshContact = true;
            lastTopState = suspicious;
        };
        suspicious.To(combat, () => Aware || SquadSeesPlayer(0.5f));
        suspicious.To(alert, () => ClueJustFound() || investigatorMissing || Escalated());
        suspicious.To(idle, () =>
            CheckDone && (Squad == null || Squad.TimeSinceSuspect > 2f)
            || suspicious.TimeInState > suspiciousDuration);

        // Alert: searching
        alert.OnEnter = StartSearch;
        alert.OnTick = dt =>
        {
            alertTree.Tick();
            FaceMovement(turnSpeed * 0.6f);
            if (Squad != null)
            {
                Squad.SetAlert(SquadAlert.Searching);
            }
            tactics.Tick(dt);
        };
        alert.OnExit = () =>
        {
            StopMoving();
            freshContact = true;
            lastTopState = alert;
            tactics.Stop();
            calmLine = Random.value < 0.5f ? "HE'S GONE. BACK TO YOUR POSTS." : "LOST HIM. EYES OPEN.";
        };
        alert.To(combat, () => Aware || SquadSeesPlayer(0.3f));
        alert.To(idle, () => alert.TimeInState > searchDuration && TimeSinceSquadSaw() > searchDuration
                             && (Squad == null || Squad.TimeSinceKnown > 10f));

        // no warning shot/shout when coming back from a stagger
        combat.OnEnter = () =>
        {
            AnimBool("Alert", true);
            if (Squad != null)
            {
                Squad.SetAlert(SquadAlert.Combat);
            }
            if (!freshContact)
            {
                return;
            }
            freshContact = false;
            MarkSpotted();
            warningShotDue = true;
            director.Say(this, Random.value < 0.5f ? "THERE HE IS!" : "CONTACT!", "spotted");
        };
        combat.OnTick = dt =>
        {
            if (Squad != null)
            {
                Squad.SetAlert(SquadAlert.Combat);
            }
            tactics.Tick(dt);
        };
        combat.OnExit = () =>
        {
            ReleaseShootToken();
            SetLaser(false);
            director.Unclaim(this);
        };
        // on the parent so it interrupts everything in combat
        combat.To(countering, ShouldCounterZip);
        combat.To(alert, () => TimeSinceSquadSaw() > combatMemory);

        engage.OnEnter = () => { utility.Clear(); utility.Tick(0f, true); SwitchTree(); };
        engage.OnTick = TickEngage;
        engage.OnExit = () =>
        {
            activeTree?.Abort();
            activeTree = null;
            EndCover();
            ReleaseShootToken();
            SetLaser(false);
            aiming = false;
            AnimBool("Aiming", false);
        };

        diving.OnEnter = StartDive;
        diving.OnTick = TickDive;
        diving.OnExit = () => { lastDiveTime = Time.time; if (NavReady) { nav.Warp(transform.position); } };
        diving.To(engage, () => diving.TimeInState >= diveTime);

        throwing.OnEnter = () =>
        {
            StopMoving();
            thrown = false;
            AnimTrigger("Throw");
            director.Say(this, "FRAG OUT!", "frag");
        };
        throwing.OnTick = dt =>
        {
            FaceTowards(grenadeTarget, 720f);
            if (!thrown && throwing.TimeInState >= throwWindup)
            {
                ThrowGrenade();
            }
        };
        throwing.To(engage, () => throwing.TimeInState >= throwWindup + 0.35f);

        countering.OnEnter = StartCounter;
        countering.OnTick = TickCounter;
        countering.OnExit = () =>
        {
            zipDodging = false;
            lastCounterTime = Time.time;
            if (NavReady)
            {
                nav.Warp(transform.position);
            }
        };
        countering.To(engage, () => countering.TimeInState >= counterTime);

        SetupStagger(staggered, combat);

        dead.OnEnter = () =>
        {
            activeTree?.Abort();
            tactics.Stop();
            ReleaseShootToken();
            SetLaser(false);
        };

        return new HStateMachine(root);
    }

    bool SquadSeesPlayer(float within) => Squad != null && Squad.TimeSinceSeen < within;

    float TimeSinceSquadSaw() => Mathf.Min(TimeSinceSeen, Squad != null ? Squad.TimeSinceSeen : 999f);

    bool BodyJustFound() => Squad != null && Time.time - Squad.BodyFoundTime < 1f;

    bool TrailJustFound() => Squad != null && Time.time - Squad.TrailTime < 1f;

    bool ClueJustFound() => BodyJustFound() || TrailJustFound();

    // something new turned up while already checking
    bool Escalated() =>
        Squad != null && Squad.SuspectEvents > suspectEventsAtStart && suspicious.TimeInState > 3f && Squad.TimeSinceSuspect < 0.5f;

    // ---------------------------------------------------------------- suspicious

    Vector3 SuspectPoint()
    {
        if (Squad != null && Squad.TimeSinceSuspect < 60f)
        {
            return Squad.SuspectPos;
        }
        return TimeSinceHeard < TimeSinceSeen ? Board.Get(BB.HeardPos, transform.position) : Board.Get(BB.LastSeenPos, transform.position);
    }

    void StartSuspicious()
    {
        AnimBool("Alert", true);
        StopMoving();
        suspiciousTree.Abort();
        investigatorMissing = false;
        investigatorDeadSince = -1f;
        selfCheckDone = false;

        if (Squad == null)
        {
            checkingMyself = true;
            director.Say(this, "HUH?", "huh");
            return;
        }

        Squad.SetAlert(SquadAlert.Suspicious);
        Squad.InvestigationDone = false;
        suspectEventsAtStart = Squad.SuspectEvents;

        Vector3 spot = SuspectPoint();
        GruntBase reporter = Squad.SuspectReporter;

        // axe noise: whole squad goes together
        if (Squad.SuspectLure)
        {
            checkingMyself = true;
            tactics.BeginInvestigation(spot, null, reporter, true);
            if (reporter != null && reporter != this)
            {
                director.SayLater(this, "ON ME. STAY TIGHT.", "reply", 1.1f);
            }
            else
            {
                director.Say(this, "WHAT WAS THAT? ON ME, STAY TIGHT.", "holdup");
            }
            return;
        }

        // send whoever noticed if he's a lot closer, otherwise go myself
        GruntFollower send = null;
        if (reporter is GruntFollower f && !f.IsDead
            && Vector3.Distance(f.transform.position, spot) + 6f < Vector3.Distance(transform.position, spot))
        {
            send = f;
        }
        checkingMyself = send == null;

        tactics.BeginInvestigation(spot, send, reporter, false);

        if (reporter != null && reporter != this)
        {
            director.SayLater(this, checkingMyself ? "HOLD HERE. I'LL TAKE A LOOK." : "GO CHECK IT OUT. CAREFUL.", "reply", 1.1f);
        }
        else
        {
            director.Say(this, Squad.SuspectWasHeard ? "HOLD UP. DID YOU HEAR THAT?" : "HOLD UP. SOMETHING MOVED.", "holdup");
            GruntFollower buddy = tactics.Overwatch;
            if (buddy != null)
            {
                director.SayLater(buddy, Squad.SuspectWasHeard ? "HEARD IT TOO." : "WHERE?", "buddy", 1.1f);
            }
            if (!checkingMyself)
            {
                director.SayLater(this, "YOU. GO TAKE A LOOK.", "send", 2.3f);
            }
            else if (buddy != null)
            {
                director.SayLater(this, "COVER ME. I'LL CHECK IT.", "send", 2.3f);
            }
        }
    }

    void TickSuspicious(float dt)
    {
        suspiciousTree.Tick();
        if (Squad != null)
        {
            Squad.SetAlert(SquadAlert.Suspicious);
        }
        tactics.TickSuspicious(dt);

        // the guy he sent stopped reporting (only noticed after a few seconds)
        GruntFollower sent = tactics.Investigator;
        if (sent != null && Squad != null && !Squad.InvestigationDone)
        {
            if (sent.IsDead && investigatorDeadSince < 0f)
            {
                investigatorDeadSince = Time.time;
            }
            bool silent = investigatorDeadSince >= 0f && Time.time - investigatorDeadSince > 5f;
            if ((silent || suspicious.TimeInState > 25f) && !investigatorMissing)
            {
                investigatorMissing = true;
                director.Say(this, "HEY! REPORT IN!", "reportin");
                Squad.ReportBody(sent.BodyPosition);
            }
        }
    }

    // ---------------------------------------------------------------- searching

    void StartSearch()
    {
        AnimBool("Alert", true);
        alertTree.Abort();
        if (Squad != null)
        {
            Squad.SetAlert(SquadAlert.Searching);
            Squad.Alarm(searchDuration + 60f);
        }

        string line;
        if (TrailJustFound() && !BodyJustFound())
        {
            line = "FOLLOW THE AXE! HE'S THAT WAY!";
        }
        else if (BodyJustFound() || investigatorMissing)
        {
            line = investigatorMissing ? "SOMETHING'S WRONG. FIND HIM!" : "HE'S CLOSE. FIND HIM!";
        }
        else if (lastTopState == suspicious)
        {
            line = "THERE IT IS AGAIN! SPREAD OUT!";
        }
        else
        {
            line = Random.value < 0.5f ? "WHERE'D HE GO?" : "LOST HIM! SPREAD OUT!";
        }
        if (!quietSearchStart)
        {
            director.SayLater(this, line, "search", BodyJustFound() ? 1f : 0.2f);
        }
        quietSearchStart = false;
    }

    // ---------------------------------------------------------------- events

    protected override void OnHurt(EnemyHit hit)
    {
        Board.Set(BB.HurtAt, Time.time);
        director.Say(this, "ARGH!", "hurt" + GetInstanceID());
        RequestStagger(hurtStaggerTime);
    }

    protected override void OnFoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (Squad != null)
        {
            Squad.ReportBody(at);
        }
        if (InCombatState)
        {
            director.Say(this, "MAN DOWN!", "mandown");
        }
        else
        {
            director.Say(this, witnessed ? "MAN DOWN!" : "ONE OF OURS IS DOWN!", "body");
        }
    }

    // squad breaks when someone finds the body, not here
    protected override void OnDeath(EnemyHit hit)
    {
        activeTree?.Abort();
        EndCover();
        zipDodging = false;
        tactics?.Stop();
        base.OnDeath(hit);
        if (Squad != null)
        {
            Squad.MarkLeaderDied();
        }
    }

    public override void ResetAgent()
    {
        activeTree = null;
        coverRunning = false;
        zipDodging = false;
        calmLine = null;
        lastTopState = null;
        lastCoverTime = lastDiveTime = lastBurstTime = lastCounterTime = LastGrenadeTime = grenadeBlockedUntil = -99f;
        nextGrenadeTime = 0f;
        base.ResetAgent();
    }

    public override string DebugText()
    {
        StringBuilder sb = new StringBuilder(base.DebugText());
        sb.Append($"\nhits {HitsTaken}/{maxHits}  ").Append(SquadLine());
        if (tactics != null && Brain != null && !Brain.IsInState(idle) && !IsDead)
        {
            sb.Append("\nsquad plan: ").Append(tactics.Describe());
        }
        if (Brain != null && Brain.IsInState(suspicious))
        {
            sb.Append(checkingMyself ? "\nchecking it myself" : "\nsent someone");
        }
        if (Brain != null && Brain.IsInState(engage))
        {
            sb.Append("\nintent: ").Append(utility.Current != null ? utility.Current.Name : "-");
            AppendTree(sb, activeTree);
            foreach (UtilityAction a in utility.Actions)
            {
                sb.Append($"\n  {a.Name,-13} {a.Score:0.00}");
            }
        }
        sb.Append(GrenadeReady ? "\ngrenade ready" : $"\ngrenade in {nextGrenadeTime - Time.time:0.0}s");
        if (player != null)
        {
            sb.Append($"\npredictability {player.Predictability01:0.00}");
        }
        return sb.ToString();
    }
}
