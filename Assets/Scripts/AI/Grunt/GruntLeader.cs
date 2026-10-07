using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Squad leader. 3 hits, better senses, runs the squad.
// HSM: Idle / Suspicious / Alert / Combat{Engage, Diving, Throwing, Countering} / Staggered / Dead
// Engage uses utility AI to pick an intent, each intent is a behaviour tree.
// The squad's plays come from SquadTactics (GOAP). Cover/flank/fire spots use EQS.
public class GruntLeader : GruntBase
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
    public float grenadeFuse = 2.4f;
    public float grenadeRadius = 5f;
    public float grenadeDamage = 45f;
    public bool grenadeHurtsGrunts = true;

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
            if (Squad != null) Squad.SetAlert(SquadAlert.Calm);
            if (calmLine != null) director.SayLater(this, calmLine, "calm", 0.8f);
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
            if (Squad != null) Squad.SetAlert(SquadAlert.Searching);
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
            if (Squad != null) Squad.SetAlert(SquadAlert.Combat);
            if (!freshContact) return;
            freshContact = false;
            MarkSpotted();
            warningShotDue = true;
            director.Say(this, Random.value < 0.5f ? "THERE HE IS!" : "CONTACT!", "spotted");
        };
        combat.OnTick = dt =>
        {
            if (Squad != null) Squad.SetAlert(SquadAlert.Combat);
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
        diving.OnExit = () => { lastDiveTime = Time.time; if (NavReady) nav.Warp(transform.position); };
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
            if (!thrown && throwing.TimeInState >= throwWindup) ThrowGrenade();
        };
        throwing.To(engage, () => throwing.TimeInState >= throwWindup + 0.35f);

        countering.OnEnter = StartCounter;
        countering.OnTick = TickCounter;
        countering.OnExit = () =>
        {
            zipDodging = false;
            lastCounterTime = Time.time;
            if (NavReady) nav.Warp(transform.position);
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
        if (Squad != null && Squad.TimeSinceSuspect < 60f) return Squad.SuspectPos;
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
            if (reporter != null && reporter != this) director.SayLater(this, "ON ME. STAY TIGHT.", "reply", 1.1f);
            else director.Say(this, "WHAT WAS THAT? ON ME, STAY TIGHT.", "holdup");
            return;
        }

        // send whoever noticed if he's a lot closer, otherwise go myself
        GruntFollower send = null;
        if (reporter is GruntFollower f && !f.IsDead
            && Vector3.Distance(f.transform.position, spot) + 6f < Vector3.Distance(transform.position, spot))
            send = f;
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
                director.SayLater(buddy, Squad.SuspectWasHeard ? "HEARD IT TOO." : "WHERE?", "buddy", 1.1f);
            if (!checkingMyself) director.SayLater(this, "YOU. GO TAKE A LOOK.", "send", 2.3f);
            else if (buddy != null) director.SayLater(this, "COVER ME. I'LL CHECK IT.", "send", 2.3f);
        }
    }

    void TickSuspicious(float dt)
    {
        suspiciousTree.Tick();
        if (Squad != null) Squad.SetAlert(SquadAlert.Suspicious);
        tactics.TickSuspicious(dt);

        // the guy he sent stopped reporting (only noticed after a few seconds)
        GruntFollower sent = tactics.Investigator;
        if (sent != null && Squad != null && !Squad.InvestigationDone)
        {
            if (sent.IsDead && investigatorDeadSince < 0f) investigatorDeadSince = Time.time;
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
            line = "FOLLOW THE AXE! HE'S THAT WAY!";
        else if (BodyJustFound() || investigatorMissing)
            line = investigatorMissing ? "SOMETHING'S WRONG. FIND HIM!" : "HE'S CLOSE. FIND HIM!";
        else if (lastTopState == suspicious)
            line = "THERE IT IS AGAIN! SPREAD OUT!";
        else
            line = Random.value < 0.5f ? "WHERE'D HE GO?" : "LOST HIM! SPREAD OUT!";
        if (!quietSearchStart) director.SayLater(this, line, "search", BodyJustFound() ? 1f : 0.2f);
        quietSearchStart = false;
    }

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
                    foreach (TacticalMap.Point p in near) items.Add(new EQSItem { Point = p.position });
                }
                else
                {
                    for (int r = 0; r < 3; r++)
                        for (int k = 0; k < 12; k++)
                        {
                            float a = k * Mathf.PI / 6f + r * 0.5f;
                            float rad = Mathf.Lerp(2f, coverSearchRadius, r / 2f);
                            items.Add(new EQSItem { Point = transform.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad });
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
        if (d < grenadeMinRange || d > grenadeMaxRange || AlliesNear(target, grenadeRadius)) return 0f;

        float score = 0.25f;
        if (Squad != null)
        {
            if (Squad.TimeSinceSeen > 0.6f && Squad.TimeSinceKnown < 6f) score = 1f;
            if (SquadPushing()) score = Mathf.Max(score, 0.8f);
            if (Time.time - Squad.LastFireTime < 1f) score = Mathf.Max(score, 0.65f);
        }
        if (PlayerVisible && player.StationaryTime > 1f && MapCoverAtPlayer() >= 0.5f) score = Mathf.Max(score, 0.9f);
        if (Board.Get(BB.InCover, false)) score = Mathf.Max(score, 0.85f);
        return score;
    }

    float MapCoverAtPlayer()
    {
        TacticalMap map = TacticalMap.Instance;
        return map != null && map.Ready ? map.CoverAt(player.Center, EyePosition) : 0f;
    }

    bool SquadPushing()
    {
        if (Squad == null) return false;
        foreach (GruntFollower f in Squad.Alive(Squad.Followers))
        {
            SquadOrder o = f.Board.Get(BB.SquadOrder, SquadOrder.Hold);
            if (o == SquadOrder.Advance || o == SquadOrder.Flank || o == SquadOrder.CutOff) return true;
        }
        return false;
    }

    // 1 if a charged throw is aimed at him or a thrown axe is about to hit
    float ThreatLevel()
    {
        BattleAxe axe = director.Axe;
        if (axe == null) return 0f;

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
        if (hadAxePos) lastAxePos = t.transform.position;
    }

    void OnIntentChanged(UtilityAction from, UtilityAction to)
    {
        Board.Set(BB.Intent, to != null ? to.Name : "-");

        if (to == uFlank) director.Say(this, "I'M GOING AROUND!", "flank");
        else if (to == uPush) director.Say(this, "HE'S UNARMED! PUSH!", "push");
        else if (to == uEvade) director.Say(this, "WHOA!", "evade");
        else if (to == uCover) director.Say(this, "TAKING COVER!", "cover");
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
                if (DistToPlayer() < 5f) StopMoving();
                else MoveTo(player.Feet, runSpeed);
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
                        return Arrived(1.2f) ? BTStatus.Success : BTStatus.Running;
                    }))),
                    Scan("LookAround", () => Random.Range(3f, 4.5f)),
                    BT.Do("Report", () =>
                    {
                        selfCheckDone = true;
                        if (Squad != null) Squad.InvestigationDone = true;

                        // found the axe
                        if (AxeNear(SuspectPoint(), 5f))
                        {
                            director.Say(this, "AN AXE... HE'S CLOSE. FIND HIM!", "axefound");
                            quietSearchStart = true;
                            if (Squad != null) Squad.ReportTrail(SuspectPoint());
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
                return Arrived(1.5f) ? BTStatus.Success : BTStatus.Running;
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
        if (!coverRunning) return;
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

        if (utility.Tick(dt)) SwitchTree();

        if (activeTree != null)
        {
            BTStatus s = activeTree.Tick();
            if (s != BTStatus.Running)
            {
                if (utility.Current == uCover) EndCover();
                // re-score straight away
                if (utility.Tick(0f, true)) SwitchTree();
            }
        }

        bool moving = Velocity.sqrMagnitude > 0.5f;
        TimeAtPos = moving ? 0f : TimeAtPos + dt;
        Board.Set(BB.AimedAt, AimedAtByPlayer());

        bool inCover = Board.Get(BB.InCover, false);
        aiming = PlayerVisible && !inCover && (laserOn || utility.Current == uHold || utility.Current == uPush || !moving);
        AnimBool("Aiming", aiming);

        if (aiming || inCover) FaceTowards(player.Center, turnSpeed);
        else FaceMovement(turnSpeed);

        if (PlayerVisible && player.IsWallRunning && !wasWallRunning)
            director.Say(this, "HE'S ON THE WALL!", "wall");
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
        if (NavReady) nav.Warp(p);
        else transform.position = p;
        FaceTowards(player.Center, turnSpeed);
    }

    // ---------------------------------------------------------------- zip counter

    bool ShouldCounterZip()
    {
        Grappling g = director.Grapple;
        if (g == null || !g.IsZipping || !ReferenceEquals(g.ZipTarget, this)) return false;
        if (Time.time - lastCounterTime < counterCooldown || Brain.IsInState(staggered)) return false;
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
        if (approach.sqrMagnitude < 0.01f) approach = transform.position - player.Center;
        Vector3 side = Vector3.Cross(Vector3.up, approach.normalized);

        diveFrom = transform.position;
        diveTo = PickSidestep(side) ?? PickSidestep(-side) ?? transform.position;
        AnimTrigger("Dive");
        director.Say(this, Random.value < 0.5f ? "NOT TODAY!" : "HE'S COMING!", "counter");

        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.AirPuff(transform.position + Vector3.up * 0.3f, -side, 0.5f);
    }

    Vector3? PickSidestep(Vector3 side)
    {
        Vector3 target = transform.position + side * 3f;
        if (!NavReady) return target;
        if (NavMesh.Raycast(transform.position, target, out NavMeshHit hit, NavMesh.AllAreas))
            return hit.distance > 1.5f ? hit.position : (Vector3?)null;
        return target;
    }

    void TickCounter(float dt)
    {
        float t = Mathf.Clamp01(countering.TimeInState / counterTime);
        Vector3 p = Vector3.Lerp(diveFrom, diveTo, 1f - (1f - t) * (1f - t));
        if (NavReady) nav.Warp(p);
        else transform.position = p;
        FaceTowards(player.Center, turnSpeed * 2f);
    }

    // ---------------------------------------------------------------- grenade

    // predicted spot if visible, otherwise last known
    Vector3 GrenadeAim()
    {
        if (PlayerVisible)
        {
            if (!player.IsGrounded && player.PredictLanding(out Vector3 landing, out float t, 2f)) return landing;
            return player.Predict(0.5f);
        }
        if (Squad != null) return Squad.KnownPos + Squad.KnownVel * 0.3f;
        return Board.Get(BB.LastSeenPos, player.Center);
    }

    bool AlliesNear(Vector3 point, float radius)
    {
        float r2 = radius * radius;
        foreach (EnemyAgent e in director.Enemies)
            if (e is GruntBase g && g != this && !g.IsDead && (g.transform.position - point).sqrMagnitude < r2) return true;
        return false;
    }

    // finds an arc over cover. careful = won't throw near his own men
    public bool RequestGrenade(Vector3 target, bool careful = true)
    {
        if (!GrenadeReady || Brain == null || !Brain.IsInState(engage)) return false;

        if (Physics.Raycast(target + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 12f, ~0, QueryTriggerInteraction.Ignore))
            target = floor.point;

        if (careful && AlliesNear(target, grenadeRadius))
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
            if (!ArcClear(origin, v, t, target)) continue;

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
                return false;
            prev = p;
        }
        return true;
    }

    void ThrowGrenade()
    {
        thrown = true;
        LastGrenadeTime = Time.time;
        nextGrenadeTime = Time.time + grenadeInterval * Random.Range(0.85f, 1.3f);
        GruntGrenade.Spawn(HandPosition, grenadeVelocity, this, new GruntGrenade.Settings
        {
            fuse = grenadeFuse,
            radius = grenadeRadius,
            maxDamage = grenadeDamage,
            minDamage = grenadeDamage * 0.2f,
            knockback = 12f,
            hurtsGrunts = grenadeHurtsGrunts
        });
        tactics.OnGrenadeThrown(grenadeTarget);
    }

    // ---------------------------------------------------------------- testing (F6 menu)

    public SquadTactics Tactics => tactics;

    public IEnumerable<string> IntentNames
    {
        get { foreach (UtilityAction a in utility.Actions) yield return a.Name; }
    }

    void EnsureEngaged()
    {
        if (!Brain.IsInState(engage)) Brain.ForceChange(engage);
    }

    public bool DebugThrowGrenade(Vector3 target)
    {
        if (IsDead) return false;
        nextGrenadeTime = 0f;
        grenadeBlockedUntil = -99f;
        EnsureEngaged();
        return RequestGrenade(target, false);
    }

    public void DebugCounter()
    {
        if (IsDead) return;
        EnsureEngaged();
        lastCounterTime = -99f;
        Brain.Request(countering);
    }

    public void DebugDive()
    {
        if (IsDead) return;
        EnsureEngaged();
        Vector3 d = ChestPosition - player.CameraPosition;
        d.y = 0f;
        ThreatDir = d.sqrMagnitude > 0.01f ? d.normalized : transform.forward;
        lastDiveTime = -99f;
        Brain.Request(diving);
    }

    public void DebugForceIntent(string name, float seconds = 5f)
    {
        if (IsDead) return;
        EnsureEngaged();
        foreach (UtilityAction a in utility.Actions)
        {
            if (a.Name != name) continue;
            if (a == uCover) lastCoverTime = -99f;
            if (a == uGrenade) { nextGrenadeTime = 0f; grenadeBlockedUntil = -99f; }
            utility.Force(a, seconds);
            return;
        }
    }

    public void DebugSetAlert(SquadAlert level, Vector3 at)
    {
        if (IsDead || Squad == null) return;

        float awareness = level == SquadAlert.Combat ? 1f : level == SquadAlert.Calm ? 0f : suspiciousAt + 0.05f;
        DebugSetAwareness(awareness);
        foreach (GruntFollower f in Squad.Alive(Squad.Followers)) f.DebugSetAwareness(awareness);

        switch (level)
        {
            case SquadAlert.Calm:
                Brain.ForceChange(idle);
                break;
            case SquadAlert.Suspicious:
                Squad.ReportSuspicion(at, this, true);
                Brain.ForceChange(suspicious);
                break;
            case SquadAlert.Searching:
                Squad.ReportBody(at);
                Brain.ForceChange(alert);
                break;
            case SquadAlert.Combat:
                Squad.ReportSighting(player.Center, player.Velocity);
                Brain.ForceChange(engage);
                break;
        }
    }

    public void DebugStagger() => RequestStagger(staggerTime);

    public void DebugKill() => Die(new EnemyHit
    {
        kind = HitKind.Other,
        point = ChestPosition,
        direction = -transform.forward,
        force = deathImpulse
    });

    // ---------------------------------------------------------------- events

    protected override void OnHurt(EnemyHit hit)
    {
        Board.Set(BB.HurtAt, Time.time);
        director.Say(this, "ARGH!", "hurt" + GetInstanceID());
        RequestStagger(hurtStaggerTime);
    }

    protected override void OnFoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (Squad != null) Squad.ReportBody(at);
        if (InCombatState) director.Say(this, "MAN DOWN!", "mandown");
        else director.Say(this, witnessed ? "MAN DOWN!" : "ONE OF OURS IS DOWN!", "body");
    }

    // squad breaks when someone finds the body, not here
    protected override void OnDeath(EnemyHit hit)
    {
        activeTree?.Abort();
        EndCover();
        zipDodging = false;
        tactics?.Stop();
        base.OnDeath(hit);
        if (Squad != null) Squad.MarkLeaderDied();
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
            sb.Append("\nsquad plan: ").Append(tactics.Describe());
        if (Brain != null && Brain.IsInState(suspicious))
            sb.Append(checkingMyself ? "\nchecking it myself" : "\nsent someone");
        if (Brain != null && Brain.IsInState(engage))
        {
            sb.Append("\nintent: ").Append(utility.Current != null ? utility.Current.Name : "-");
            AppendTree(sb, activeTree);
            foreach (UtilityAction a in utility.Actions)
                sb.Append($"\n  {a.Name,-13} {a.Score:0.00}");
        }
        sb.Append(GrenadeReady ? "\ngrenade ready" : $"\ngrenade in {nextGrenadeTime - Time.time:0.0}s");
        if (player != null) sb.Append($"\npredictability {player.Predictability01:0.00}");
        return sb.ToString();
    }
}
