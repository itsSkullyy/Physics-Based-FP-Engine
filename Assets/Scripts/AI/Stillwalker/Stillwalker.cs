using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Statue when looked at, follows you when not, launches at you when you land in range.
// Only pogo hits and thrown axes crack it, 4 cracks to kill, they heal after 3s.
// HSM: Dormant / Hunting{Stalking{Moving, Watched}, Launching{Windup, Flight}, Recovering} / Shattered
// Stalking uses utility (Creep / CatchUp / HoldBack) or the ML policy if there is one.
[RequireComponent(typeof(NavMeshAgent))]
public class Stillwalker : EnemyAgent
{
    [Header("Wake")]
    public float wakeRadius = 25f;

    [Header("Watched")]
    [Tooltip("Within this many degrees of the centre of your screen counts as looking at it.")]
    public float seenAngle = 35f;
    public float seenRange = 70f;

    [Header("Stalking")]
    public float creepSpeed = 2.2f;
    [Tooltip("Distance band it tries to stay in.")]
    public float stalkMin = 9f;
    public float stalkMax = 16f;

    [Header("Chase")]
    [Tooltip("Speed when you're just past its range band.")]
    public float catchUpSpeed = 10f;
    [Tooltip("Extra speed for every metre you are beyond the band.")]
    public float speedPerMetre = 0.6f;
    public float maxChaseSpeed = 26f;
    [Tooltip("The ML policy only steers within this distance (what it was trained at). Beyond it the chase rule runs.")]
    public float policyMaxDistance = 22f;

    [Header("Launch")]
    public float launchRange = 20f;
    public float launchWindup = 0.2f;
    public float launchSpeed = 24f;
    public float launchArc = 2.5f;
    public float launchCooldown = 2.5f;
    [Tooltip("Standing on the ground in range this long triggers a launch even without a fresh landing.")]
    public float groundedTooLong = 1.2f;
    public float launchDamage = 55f;
    public float impactRadius = 1.6f;
    public float knockback = 11f;
    public float knockUp = 6f;
    [Tooltip("Stuck in place after a launch. A good moment to hit it.")]
    public float recoverTime = 1f;

    [Header("Contact")]
    public float contactDamage = 25f;
    public float contactRadius = 0.55f;

    [Header("Cracks")]
    public int cracksToShatter = 4;
    [Tooltip("Cracks heal if this long passes without a new one.")]
    public float crackHealDelay = 3f;
    [Tooltip("Melee only cracks it if you're falling at least this fast.")]
    public float minFallSpeed = 1.5f;
    public int shatterPieces = 16;
    public float pieceLifetime = 5f;

    [Header("Landing Marker")]
    public Color markerColor = new Color(1f, 0.15f, 0.1f, 0.9f);

    [Header("Visuals (auto-found)")]
    public Transform visualRoot;
    public Transform head;
    public Renderer[] eyes;
    public Color eyeColor = new Color(1f, 0.15f, 0.1f, 1f);

    [Header("Audio (optional, synthesised if empty)")]
    public AudioClip stepSound;
    public AudioClip crackSound;
    public AudioClip launchSound;

    public IStillwalkerPolicy Policy { get; set; }
    public event Action<StillwalkerEvent> Events;

    public int Cracks { get; private set; }
    public bool Seen => Board.Get(BB.Seen, false);
    public bool LaunchReady => Time.time - lastLaunchTime > launchCooldown;

    HState dormant, hunting, stalking, moving, watched, launching, windup, flight, recovering, shattered;

    UtilityBrain utility;
    UtilityAction uCreep, uCatchUp, uHoldBack;
    readonly Dictionary<UtilityAction, BehaviourTree> intentTrees = new Dictionary<UtilityAction, BehaviourTree>();
    BehaviourTree activeTree;

    float lastLaunchTime = -99f;
    float lastCrackTime = -99f;
    float nextContactTime;
    float stepSoundTimer;
    Vector3 policyDir;
    bool hitThisLaunch;
    Vector3 flightFrom, flightTo;
    float flightTime;
    PlayerMotionTracker subscribed;

    Quaternion headBaseLocal;
    Quaternion visualBaseLocal;
    Material[] eyeMats;
    float eyeGlow;
    float crackShake;
    LineRenderer marker;

    public override Vector3 HeadPosition => head != null ? head.position : transform.position + Vector3.up * 2f;
    public override Vector3 ChestPosition => transform.position + Vector3.up * 1.3f;

    Vector3 LaunchTarget
    {
        get => Board.Get(BB.LaunchTarget, transform.position);
        set => Board.Set(BB.LaunchTarget, value);
    }

    // ---------------------------------------------------------------- setup

    protected override void Awake()
    {
        base.Awake();
        sightAngle = 360f;

        if (visualRoot == null)
        {
            visualRoot = transform.Find("Model");
            if (visualRoot == null) visualRoot = transform.Find("Placeholder");
        }
        if (visualRoot != null) visualBaseLocal = visualRoot.localRotation;

        if (head == null)
        {
            if (anim != null && anim.isHuman) head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (head == null && visualRoot != null) head = visualRoot.Find("Head");
        }
        if (head != null) headBaseLocal = head.localRotation;

        if (eyes == null || eyes.Length == 0)
        {
            var found = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0) found.Add(r);
            eyes = found.ToArray();
        }
        eyeMats = new Material[eyes.Length];
        for (int i = 0; i < eyes.Length; i++)
        {
            eyeMats[i] = EnemyVisuals.Unlit(eyeColor);
            eyes[i].sharedMaterial = eyeMats[i];
        }

        marker = EnemyVisuals.MakeLine(null, "StillwalkerMarker", markerColor, 0.08f);
        marker.positionCount = 33;
        marker.loop = false;

        if (nav != null)
        {
            nav.acceleration = 60f;
            nav.angularSpeed = 0f;
            nav.stoppingDistance = 0.5f;
            nav.autoBraking = true;
        }
    }

    protected override void Start()
    {
        base.Start();
        Subscribe();
    }

    protected override void OnDestroy()
    {
        if (subscribed != null) subscribed.Landed -= OnPlayerLanded;
        if (marker != null) Destroy(marker.gameObject);
        base.OnDestroy();
    }

    void Subscribe()
    {
        if (player == subscribed) return;
        if (subscribed != null) subscribed.Landed -= OnPlayerLanded;
        subscribed = player;
        if (subscribed != null) subscribed.Landed += OnPlayerLanded;
    }

    void OnPlayerLanded()
    {
        Board.Set(BB.LandedAt, Time.time);
        if (Brain == null || !Brain.IsInState(hunting)) return;

        bool inRange = DistToPlayer() <= launchRange;
        Emit(inRange ? StillwalkerEvent.PlayerLandedInRange : StillwalkerEvent.PlayerLandedOutOfRange);
    }

    // ---------------------------------------------------------------- brain

    protected override HStateMachine BuildBrain()
    {
        DebugQueries.Clear();
        BuildUtility();
        BuildTrees();

        HState root = new HState("Stillwalker");
        dormant = root.Add(new HState("Dormant"), initial: true);
        hunting = root.Add(new HState("Hunting"));
        stalking = hunting.Add(new HState("Stalking"), initial: true);
        moving = stalking.Add(new HState("Moving"), initial: true);
        watched = stalking.Add(new HState("Watched"));
        launching = hunting.Add(new HState("Launching"));
        windup = launching.Add(new HState("Windup"), initial: true);
        flight = launching.Add(new HState("Flight"));
        recovering = hunting.Add(new HState("Recovering"));
        shattered = root.Add(new HState("Shattered"));

        dormant.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.15f; };
        dormant.To(hunting, () => DistToPlayer() < wakeRadius && (PlayerVisible || DistToPlayer() < wakeRadius * 0.5f));

        stalking.To(launching, ShouldLaunch);

        moving.OnEnter = () => { utility.Clear(); utility.Tick(0f, true); SwitchTree(); AnimSpeed(1f); eyeGlow = 0.8f; };
        moving.OnTick = TickMoving;
        moving.OnExit = () => { activeTree?.Abort(); activeTree = null; StopMoving(); };
        moving.To(watched, () => Seen);

        watched.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.5f; };
        watched.To(moving, () => !Seen);

        launching.OnExit = () =>
        {
            lastLaunchTime = Time.time;
            marker.enabled = false;
        };

        windup.OnEnter = () =>
        {
            StopMoving();
            AnimSpeed(1f);
            AnimTrigger("LungeWindup");
            eyeGlow = 2f;
            hitThisLaunch = false;
            EnemySounds.PlayAt(launchSound != null ? launchSound : EnemySounds.StoneCrack, transform.position, 1f, 0.55f);
        };
        windup.OnTick = dt =>
        {
            LaunchTarget = PredictedStrike();
            FaceTowards(LaunchTarget, 900f);
            ShowMarker(LaunchTarget, 1f);
        };
        windup.To(flight, () => windup.TimeInState >= launchWindup);

        flight.OnEnter = StartFlight;
        flight.OnTick = TickFlight;
        flight.To(recovering, () => flight.TimeInState >= flightTime);
        flight.OnExit = Impact;

        // stuck after landing, best time to pogo it
        recovering.OnEnter = () => { StopMoving(); AnimSpeed(0f); eyeGlow = 0.25f; };
        recovering.To(stalking, () => recovering.TimeInState >= recoverTime);

        shattered.OnEnter = () => { StopMoving(); AnimSpeed(0f); marker.enabled = false; };

        return new HStateMachine(root);
    }

    bool ShouldLaunch()
    {
        if (!LaunchReady || player.IsDead || !player.OnGround) return false;
        if (DistToPlayer() > launchRange || !PlayerVisible) return false;

        bool justLanded = Time.time - Board.Get(BB.LandedAt, -99f) < 0.25f;
        return justLanded || player.GroundedTime > groundedTooLong;
    }

    float DistToPlayer()
    {
        Vector3 d = player.Feet - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    bool PolicyInControl => Policy != null && Policy.PolicyActive && !DebugIgnorePolicy && DistToPlayer() <= policyMaxDistance;

    float ChaseSpeed()
    {
        float beyond = Mathf.Max(0f, DistToPlayer() - stalkMax);
        return Mathf.Min(maxChaseSpeed, catchUpSpeed + beyond * speedPerMetre);
    }

    // ---------------------------------------------------------------- utility

    void BuildUtility()
    {
        utility = new UtilityBrain { Interval = 0.3f, Stickiness = 1.15f };

        uCreep = utility.Add(new UtilityAction("Creep"))
            .Consider("InBand", () => Mathf.InverseLerp(0f, stalkMax * 2f, DistToPlayer()),
                Curves.Bell(Mathf.InverseLerp(0f, stalkMax * 2f, (stalkMin + stalkMax) * 0.5f), 0.35f));

        uCatchUp = utility.Add(new UtilityAction("CatchUp"))
            .Consider("TooFar", () => Mathf.InverseLerp(stalkMin, stalkMax * 2.5f, DistToPlayer()), Curves.Rising(0.3f, 10f));

        uHoldBack = utility.Add(new UtilityAction("HoldBack", 1.2f))
            .Consider("TooClose", () => DistToPlayer() / stalkMin, Curves.Falling(0.5f, 12f));
    }

    void BuildTrees()
    {
        intentTrees.Clear();

        intentTrees[uCreep] = new BehaviourTree(BT.Action("Creep", () => Stalk(StalkPoint(), creepSpeed)));
        intentTrees[uCatchUp] = new BehaviourTree(BT.Action("CatchUp", () => Stalk(StalkPoint(), ChaseSpeed())));
        intentTrees[uHoldBack] = new BehaviourTree(BT.Action("BackOff", () => Stalk(StalkPoint(), creepSpeed * 1.8f)));
    }

    Vector3 StalkPoint()
    {
        Vector3 away = transform.position - player.Feet;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -player.CameraForward;
        float want = (stalkMin + stalkMax) * 0.5f;
        Vector3 p = player.Feet + away.normalized * want;
        Board.Set(BB.StalkTarget, p);
        return p;
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
        // only use the policy within the distances it was trained at
        if (PolicyInControl)
        {
            Policy.Decide(Senses(), out Vector3 dir, out bool fast);
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
            {
                // smooth between decisions so it doesn't jitter
                policyDir = policyDir.sqrMagnitude > 0.001f
                    ? Vector3.Slerp(policyDir, dir.normalized, 1f - Mathf.Exp(-10f * dt))
                    : dir.normalized;
                MoveTo(transform.position + policyDir * 3f, fast ? ChaseSpeed() : creepSpeed);
            }
            else
            {
                policyDir = Vector3.zero;
                StopMoving();
            }
            FaceTowards(player.Feet, 360f);
        }
        else
        {
            if (utility.Tick(dt) || activeTree == null) SwitchTree();
            if (activeTree != null && activeTree.Tick() != BTStatus.Running)
                if (utility.Tick(0f, true)) SwitchTree();
        }

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

    public StillwalkerSenses Senses()
    {
        Vector3 predicted;
        if (!player.IsGrounded && player.PredictLanding(out Vector3 land, out _)) predicted = land;
        else predicted = player.Predict(0.35f) - Vector3.up * player.FeetOffset;

        return new StillwalkerSenses
        {
            toPlayerLocal = transform.InverseTransformDirection(player.Feet - transform.position),
            toPredictedLocal = transform.InverseTransformDirection(predicted - transform.position),
            playerHeightAbove = player.Feet.y - transform.position.y,
            playerVerticalSpeed = player.Velocity.y,
            playerOnGround = player.OnGround,
            playerGroundedTime = player.GroundedTime,
            playerAirTime = player.AirTime,
            cracks = Cracks,
            launchReady = LaunchReady,
            seen = Seen,
            distance = DistToPlayer()
        };
    }

    // ---------------------------------------------------------------- launch

    Vector3 PredictedStrike()
    {
        float t = Mathf.Clamp(DistToPlayer() / launchSpeed, 0.25f, 0.9f);
        Vector3 p = player.Predict(t) - Vector3.up * player.FeetOffset;
        if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)
            && !IsOwnCollider(hit.collider) && hit.rigidbody == null)
            p = hit.point;
        return p;
    }

    void StartFlight()
    {
        flightFrom = transform.position;
        flightTo = LaunchTarget;

        Vector3 d = flightTo - flightFrom;
        if (d.magnitude > launchRange * 1.2f) flightTo = flightFrom + d.normalized * launchRange * 1.2f;

        flightTime = Mathf.Clamp(Vector3.Distance(flightFrom, flightTo) / launchSpeed, 0.25f, 0.9f);
        if (nav != null) nav.enabled = false;

        AnimTrigger("Lunge");
        eyeGlow = 2f;
        ShowMarker(flightTo, 1f);
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 1f, 0.5f);
    }

    void TickFlight(float dt)
    {
        float t = Mathf.Clamp01(flight.TimeInState / flightTime);
        Vector3 p = Vector3.Lerp(flightFrom, flightTo, t);
        p.y += Mathf.Sin(t * Mathf.PI) * launchArc;
        transform.position = p;
        transform.rotation = Quaternion.LookRotation(Flat(flightTo - flightFrom), Vector3.up);

        ShowMarker(flightTo, 1f - t * 0.5f);

        if (!hitThisLaunch && TouchingPlayer())
        {
            hitThisLaunch = true;
            HitPlayer(launchDamage);
        }
    }

    void Impact()
    {
        marker.enabled = false;
        if (IsDead) return;

        SnapToGround();

        if (!hitThisLaunch)
        {
            Vector3 d = player.Feet - transform.position;
            float vertical = Mathf.Abs(d.y);
            d.y = 0f;
            if (d.magnitude <= impactRadius && vertical < 1.2f)
            {
                hitThisLaunch = true;
                HitPlayer(launchDamage);
            }
        }

        Emit(hitThisLaunch ? StillwalkerEvent.LaunchHit : StillwalkerEvent.LaunchMissed);

        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.LandDust(transform.position, Vector3.up, 1f);
        if (CameraShaker.Instance != null)
            CameraShaker.Instance.AddTraumaAtPoint(transform.position, 0.6f, 5f, 25f);
    }

    void SnapToGround()
    {
        if (nav == null) return;
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            transform.position = hit.position;
        nav.enabled = true;
        if (nav.isOnNavMesh) nav.Warp(transform.position);
    }

    bool IsOwnCollider(Collider c)
    {
        foreach (Collider o in ownColliders) if (o == c) return true;
        return false;
    }

    void ShowMarker(Vector3 center, float intensity)
    {
        marker.enabled = true;
        float pulse = 1f + Mathf.Sin(Time.time * 18f) * 0.08f;
        float r = impactRadius * pulse;
        for (int i = 0; i <= 32; i++)
        {
            float a = i / 32f * Mathf.PI * 2f;
            marker.SetPosition(i, center + new Vector3(Mathf.Cos(a) * r, 0.05f, Mathf.Sin(a) * r));
        }
        Color c = markerColor;
        c.a *= Mathf.Clamp01(intensity);
        marker.startColor = c;
        marker.endColor = c;
    }

    // ---------------------------------------------------------------- per frame

    protected override void Update()
    {
        Subscribe();
        base.Update();
        if (IsDead || player == null || Brain == null) return;

        bool seen = player.LookAngleTo(ChestPosition) < seenAngle
                    && Vector3.Distance(player.CameraPosition, ChestPosition) < seenRange
                    && EQSTest.Clear(player.CameraPosition, ChestPosition, IgnoreForSight);
        Board.Set(BB.Seen, seen);

        // landing marker
        if (!Brain.IsInState(launching))
        {
            bool warn = Brain.IsInState(hunting) && LaunchReady && !player.IsGrounded
                        && DistToPlayer() <= launchRange
                        && player.PredictLanding(out Vector3 land, out _)
                        && Vector3.Distance(land, transform.position) <= launchRange;
            if (warn)
            {
                player.PredictLanding(out Vector3 spot, out _);
                ShowMarker(spot, 0.5f);
            }
            else marker.enabled = false;
        }

        if (Cracks > 0 && Time.time - lastCrackTime > crackHealDelay)
        {
            Cracks = 0;
            EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 0.5f, 1.6f);
        }

        UpdateEyes();
        if (crackShake > 0f) crackShake = Mathf.Max(0f, crackShake - Time.deltaTime * 4f);
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        if (IsDead || player == null || Brain == null) return;

        if (Brain.IsInState(moving) && Time.time >= nextContactTime && TouchingPlayer())
        {
            nextContactTime = Time.time + 1f;
            HitPlayer(contactDamage);
            Emit(StillwalkerEvent.ContactHit);
        }
    }

    void LateUpdate()
    {
        if (IsDead || player == null) return;

        // head always follows you
        if (head != null)
        {
            Vector3 look = player.CameraPosition - head.position;
            if (look.sqrMagnitude > 0.01f)
            {
                Quaternion want = Quaternion.LookRotation(look.normalized, Vector3.up);
                Quaternion baseRot = head.parent != null ? head.parent.rotation * headBaseLocal : headBaseLocal;
                Quaternion limited = Quaternion.RotateTowards(baseRot, want, 75f);
                head.rotation = anim != null && anim.enabled
                    ? Quaternion.Slerp(head.rotation, limited, 0.85f)
                    : Quaternion.Slerp(head.rotation, limited, 1f - Mathf.Exp(-10f * Time.deltaTime));
            }
        }

        if (visualRoot != null)
        {
            Vector3 shake = crackShake > 0f ? UnityEngine.Random.insideUnitSphere * crackShake * 4f : Vector3.zero;
            visualRoot.localRotation = visualBaseLocal * Quaternion.Euler(shake);
        }
    }

    void UpdateEyes()
    {
        float pulse = 1f + Mathf.Sin(Time.time * 9f) * 0.08f;
        Color c = eyeColor * Mathf.Clamp(eyeGlow * pulse, 0.05f, 2f);
        c.a = 1f;
        foreach (Material m in eyeMats) EnemyVisuals.SetColor(m, c);
    }

    // ---------------------------------------------------------------- hitting the player

    bool TouchingPlayer()
    {
        Vector3 p = player.Center;
        Vector3 me = transform.position;
        Vector3 flat = new Vector3(p.x - me.x, 0f, p.z - me.z);
        if (flat.magnitude > contactRadius + 0.45f) return false;

        float bottom = me.y;
        float top = me.y + eyeHeight + 0.4f;
        float pBottom = p.y - player.FeetOffset;
        float pTop = p.y + player.FeetOffset;

        return pTop > bottom && pBottom < top - 0.25f;
    }

    void HitPlayer(float amount)
    {
        bool real = director.Player == player;

        if (real && director.PlayerHealth != null)
            director.PlayerHealth.Damage(amount);

        if (real && director.PlayerBody != null)
        {
            Vector3 away = Flat(player.Center - transform.position);
            director.PlayerBody.linearVelocity = away * knockback + Vector3.up * knockUp;
            if (director.Controller != null) director.Controller.SuppressJumpHold();
        }

        JuiceFX fx = JuiceFX.Get();
        if (fx != null) fx.ImpactBurst(ChestPosition, (player.Center - ChestPosition).normalized, 1f);
        if (real)
        {
            ImpactFrames.Hit(ChestPosition, 0.8f);
            if (CameraShaker.Instance != null) CameraShaker.Instance.AddTrauma(0.6f);
        }
    }

    // ---------------------------------------------------------------- cracks

    public override void TakeHit(EnemyHit hit)
    {
        if (IsDead) return;

        bool falling = !player.IsGrounded && player.Velocity.y < -minFallSpeed;
        bool counts = hit.kind == HitKind.Thrown || (hit.kind == HitKind.Melee && falling);
        if (!counts)
        {
            JuiceFX fx = JuiceFX.Get();
            if (fx != null) fx.Scuff(hit.point, -hit.direction, Vector3.up, 0.5f);
            EnemySounds.PlayAt(EnemySounds.Ricochet, hit.point, 0.5f, 0.6f);
            return;
        }

        Cracks++;
        lastCrackTime = Time.time;
        crackShake = 1f;
        EnemySounds.PlayAt(crackSound != null ? crackSound : EnemySounds.StoneCrack, hit.point, 1f, 1f + Cracks * 0.12f);
        JuiceFX juice = JuiceFX.Get();
        if (juice != null) juice.ImpactBurst(hit.point, -hit.direction, 0.5f + Cracks * 0.12f);
        Emit(StillwalkerEvent.Cracked);

        if (Cracks >= cracksToShatter) Die(hit);
    }

    protected override bool UseRagdollOnDeath => false;

    protected override void OnDeath(EnemyHit hit)
    {
        Emit(StillwalkerEvent.Shattered);
        Brain.Request(shattered);
        marker.enabled = false;
        SpawnPieces(hit);

        // drop a stuck axe instead of it vanishing
        foreach (ThrownAxe axe in GetComponentsInChildren<ThrownAxe>(true))
        {
            axe.transform.SetParent(null, true);
            axe.DropFromSurface();
        }

        foreach (Renderer r in GetComponentsInChildren<Renderer>()) r.enabled = false;
        foreach (Collider c in ownColliders) if (c != null) c.enabled = false;
        if (nav != null) nav.enabled = false;
    }

    void SpawnPieces(EnemyHit hit)
    {
        Renderer bodyRenderer = null;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) < 0 && !(r is LineRenderer)) { bodyRenderer = r; break; }
        Material mat = bodyRenderer != null ? bodyRenderer.sharedMaterial : EnemyVisuals.Unlit(Color.gray);

        GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh cube = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        float height = eyeHeight + 0.4f;
        for (int i = 0; i < shatterPieces; i++)
        {
            Vector3 pos = transform.position + new Vector3(
                UnityEngine.Random.Range(-0.3f, 0.3f),
                UnityEngine.Random.Range(0.1f, height),
                UnityEngine.Random.Range(-0.3f, 0.3f));
            Vector3 size = Vector3.one * UnityEngine.Random.Range(0.15f, 0.35f);
            Vector3 outward = (pos - ChestPosition).normalized;
            Vector3 impulse = outward * UnityEngine.Random.Range(3f, 7f) + hit.direction * 3f + Vector3.up * 2f;

            GameObject go = new GameObject("StonePiece");
            go.AddComponent<Rigidbody>().mass = 2f;
            go.AddComponent<MeshRenderer>();
            WallShard shard = go.AddComponent<WallShard>();
            shard.Init(cube, mat, pos, UnityEngine.Random.rotation, size, impulse,
                UnityEngine.Random.insideUnitSphere * 8f, pieceLifetime, 1f, null);
        }

        if (CameraShaker.Instance != null) CameraShaker.Instance.AddTrauma(0.7f);
    }

    void Emit(StillwalkerEvent e)
    {
        Events?.Invoke(e);
        if (Policy != null) Policy.OnStillwalkerEvent(e);
    }

    // ---------------------------------------------------------------- reset

    public override void ResetAgent()
    {
        Cracks = 0;
        crackShake = 0f;
        lastLaunchTime = lastCrackTime = -99f;
        nextContactTime = 0f;
        activeTree = null;
        if (marker != null) marker.enabled = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        foreach (Collider c in ownColliders) if (c != null) c.enabled = true;
        if (nav != null) nav.enabled = true;
        if (head != null) head.localRotation = headBaseLocal;
        base.ResetAgent();
        AnimSpeed(0f);
    }

    // ---------------------------------------------------------------- testing (F6 menu)

    // F6 menu
    public bool DebugIgnorePolicy { get; set; }

    public void DebugLaunch()
    {
        if (IsDead) return;
        lastLaunchTime = -99f;
        if (!Brain.IsInState(hunting)) Brain.ForceChange(hunting);
        Brain.Request(launching);
    }

    public void DebugShatter() => Die(new EnemyHit
    {
        kind = HitKind.Other,
        point = ChestPosition,
        direction = -transform.forward,
        force = 1f
    });

    public void PlaceForTraining(Vector3 position)
    {
        ResetAgent();
        transform.position = position;
        SnapToGround();
        Brain.ForceChange(hunting);
    }

    public override string DebugText()
    {
        StringBuilder sb = new StringBuilder(base.DebugText());
        sb.Append($"\ncracks {Cracks}/{cracksToShatter}  seen {Seen}  launch {(LaunchReady ? "ready" : "cooling")}");
        sb.Append($"\ndistance {DistToPlayer():0.0}  band {stalkMin:0}-{stalkMax:0}  chase {ChaseSpeed():0.0} m/s");
        if (Policy != null && Policy.PolicyActive && !DebugIgnorePolicy)
            sb.Append(PolicyInControl ? "\npolicy: ML-Agents" : "\npolicy: ML-Agents (out of range, chase rule)");
        if (Brain != null && Brain.IsInState(moving))
        {
            sb.Append("\nintent: ").Append(utility.Current != null ? utility.Current.Name : "-");
            if (activeTree != null) sb.Append("\nbt: ").Append(activeTree.RunningPath);
            foreach (UtilityAction a in utility.Actions)
                sb.Append($"\n  {a.Name,-9} {a.Score:0.00}");
        }
        return sb.ToString();
    }
}
