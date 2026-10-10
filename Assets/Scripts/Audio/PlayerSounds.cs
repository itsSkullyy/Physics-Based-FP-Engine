using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Plays the player sounds off the controller/grapple/axe/health state, like PlayerJuice.
// Adds itself to the player on scene load. Sliders scale whole groups.
[DefaultExecutionOrder(130)]
public class PlayerSounds : MonoBehaviour
{
    [Header("Refs (found automatically)")]
    public FirstPersonCharacterController controller;
    public Grappling grappling;
    public BattleAxe axe;
    public PlayerHealth health;

    [Header("Mix")]
    [Range(0f, 1f)] public float volume = 0.8f;
    [Tooltip("Steps, jumps, landings, slides, wall kicks, darts, vaults.")]
    [Range(0f, 1f)] public float movement = 0.8f;
    [Range(0f, 1f)] public float grapple = 0.8f;
    [Range(0f, 1f)] public float axeSounds = 1f;
    [Tooltip("Getting hurt, dying, respawning, the course start and finish.")]
    [Range(0f, 1f)] public float feedback = 0.8f;
    [Range(0f, 1f)] public float wind = 0.6f;
    [Tooltip("Slide scrape, grapple reel, charge hum, spinning axe.")]
    [Range(0f, 1f)] public float loops = 0.7f;

    [Header("Steps")]
    public float stepDistance = 2.6f;
    public float footstepMinSpeed = 3f;
    public float wallRunStepInterval = 0.16f;

    [Header("Landing")]
    public float landMinSpeed = 4f;
    public float landBigSpeed = 24f;

    [Header("Wind")]
    [Tooltip("Air rush starts at this speed and is loudest at Wind Full Speed.")]
    public float windMinSpeed = 11f;
    public float windFullSpeed = 40f;

    AudioSource windLoop, slideLoop, reelLoop, chargeLoop, spinLoop;
    ThrownAxe spinOwner;
    Vector3 lastAxePos;

    bool wasGrounded, wasSliding, wasWallRunning, wasDarting, wasSwinging, wasZipping;
    bool wasStuck, wasLoose, wasRecalling;
    int lastWallKicksLeft, lastVaultTier;
    float lastFallSpeed, stepAccum, wallStepTimer;
    Vector3 lastPos;

    CourseTimer course;
    readonly List<BreakableWall> walls = new List<BreakableWall>();
    readonly List<bool> wallsUp = new List<bool>();

    // ---------------------------------------------------------------- auto attach

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        AttachToPlayers();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AttachToPlayers();

    static void AttachToPlayers()
    {
        foreach (FirstPersonCharacterController c in FindObjectsByType<FirstPersonCharacterController>(FindObjectsSortMode.None))
        {
            if (c.GetComponentInChildren<PlayerSounds>() == null)
            {
                c.gameObject.AddComponent<PlayerSounds>();
            }
        }
    }

    // ---------------------------------------------------------------- setup

    void Awake()
    {
        controller = FindController();
        if (controller == null)
        {
            enabled = false;
            return;
        }

        FindReferences();
        MakeLoops();

        lastPos = controller.transform.position;
        lastWallKicksLeft = controller.AirWallKicksLeft;
        wasGrounded = controller.IsGrounded;
    }

    FirstPersonCharacterController FindController()
    {
        if (controller != null)
        {
            return controller;
        }
        FirstPersonCharacterController found = GetComponent<FirstPersonCharacterController>();
        return found != null ? found : GetComponentInChildren<FirstPersonCharacterController>();
    }

    void FindReferences()
    {
        if (grappling == null)
        {
            grappling = controller.GetComponent<Grappling>();
        }
        if (axe == null)
        {
            axe = controller.GetComponentInChildren<BattleAxe>();
        }
        if (axe == null)
        {
            axe = FindFirstObjectByType<BattleAxe>();
        }
        if (health == null)
        {
            health = controller.GetComponent<PlayerHealth>();
        }
    }

    // looping sources that fade in and out with the matching movement (wind, slide, reel, charge)
    void MakeLoops()
    {
        windLoop = Synth.MakeLoopSource(GameSounds.WindLoop, transform, false);
        slideLoop = Synth.MakeLoopSource(GameSounds.SlideLoop, transform, false);
        reelLoop = Synth.MakeLoopSource(GameSounds.ReelLoop, transform, false);
        chargeLoop = Synth.MakeLoopSource(GameSounds.ChargeLoop, transform, false);
    }

    void Start()
    {
        if (axe != null)
        {
            axe.AxeSwingStarted += OnSwing;
            axe.AxeHit += OnAxeHit;
            axe.AxeThrown += OnThrown;
            axe.AxeChargeFull += OnChargeFull;
            axe.AxeReturned += OnCaught;
        }

        if (health != null)
        {
            health.Damaged += OnDamaged;
            health.Died += OnDied;
            health.Respawned += OnRespawned;
        }

        foreach (BreakableWall w in FindObjectsByType<BreakableWall>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            walls.Add(w);
            wallsUp.Add(w.gameObject.activeSelf);
        }
    }

    void OnDestroy()
    {
        if (axe != null)
        {
            axe.AxeSwingStarted -= OnSwing;
            axe.AxeHit -= OnAxeHit;
            axe.AxeThrown -= OnThrown;
            axe.AxeChargeFull -= OnChargeFull;
            axe.AxeReturned -= OnCaught;
        }

        if (health != null)
        {
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
            health.Respawned -= OnRespawned;
        }

        if (course != null)
        {
            course.RunStarted -= OnRunStarted;
            course.RunFinished -= OnRunFinished;
        }
    }

    // ---------------------------------------------------------------- per frame

    void Update()
    {
        float dt = Time.deltaTime;
        bool paused = Time.timeScale <= 0f;

        HookCourse();

        if (!paused)
        {
            TrackGround(dt);
            TrackWallMoves(dt);
            TrackAirMoves();
            TrackGrapple();
            TrackThrownAxe();
            TrackWalls();
        }

        DriveLoops(paused);
        lastPos = controller.transform.position;
    }

    void One(AudioClip clip, float group, float amount = 1f, float pitch = 1f) =>
        Synth.Play(clip, volume * group * amount, pitch);

    void At(AudioClip clip, Vector3 position, float group, float amount = 1f, float pitch = 1f, float maxDistance = 50f) =>
        Synth.PlayAt(clip, position, volume * group * amount, pitch, maxDistance);

    static float Jitter(float amount = 0.06f) => Random.Range(1f - amount, 1f + amount);

    void TrackGround(float dt)
    {
        bool grounded = controller.IsGrounded;
        float vy = controller.Velocity.y;
        if (vy < 0f)
        {
            lastFallSpeed = Mathf.Max(lastFallSpeed, -vy);
        }

        // small hop = step, big drop = heavier thud
        if (grounded && !wasGrounded)
        {
            if (lastFallSpeed > landMinSpeed)
            {
                float s = Mathf.InverseLerp(landMinSpeed, landBigSpeed, lastFallSpeed);
                One(GameSounds.Land, movement, Mathf.Lerp(0.3f, 1f, s), Mathf.Lerp(1.05f, 0.8f, s) * Jitter(0.04f));
            }
            else
            {
                One(GameSounds.Footstep, movement, 1f, Jitter());
            }
            stepAccum = 0f;
        }

        if (!grounded && wasGrounded && vy > 2.5f && !controller.IsSwinging && !controller.IsZipping)
        {
            One(GameSounds.Jump, movement, 1f, Jitter(0.05f));
        }

        if (grounded)
        {
            lastFallSpeed = 0f;
        }
        wasGrounded = grounded;

        if (grounded && !controller.IsSliding && controller.CurrentSpeed >= footstepMinSpeed)
        {
            stepAccum += Vector3.Distance(controller.transform.position, lastPos);
            if (stepAccum >= stepDistance)
            {
                stepAccum = 0f;
                float t = Mathf.InverseLerp(footstepMinSpeed, controller.maxSpeed, controller.CurrentSpeed);
                One(GameSounds.Footstep, movement, Mathf.Lerp(0.6f, 1f, t), Jitter(0.08f));
            }
        }
        else if (!grounded)
        {
            stepAccum = 0f;
        }

        bool sliding = controller.IsSliding && grounded;
        if (sliding && !wasSliding)
        {
            One(GameSounds.SlideStart, movement, 1f, Jitter(0.04f));
        }
        wasSliding = sliding;
    }

    void TrackWallMoves(float dt)
    {
        bool running = controller.IsWallRunning;
        if (running && !wasWallRunning)
        {
            One(GameSounds.Footstep, movement, 1f, 1.1f);
        }
        wasWallRunning = running;

        if (running)
        {
            wallStepTimer -= dt;
            if (wallStepTimer <= 0f)
            {
                wallStepTimer = wallRunStepInterval;
                One(GameSounds.Footstep, movement, 0.7f, 1.12f * Jitter(0.06f));
            }
        }
        else
        {
            wallStepTimer = 0f;
        }

        int left = controller.AirWallKicksLeft;
        if (left < lastWallKicksLeft)
        {
            One(GameSounds.WallKick, movement, 1f, Jitter(0.04f));
        }
        lastWallKicksLeft = left;
    }

    void TrackAirMoves()
    {
        bool darting = controller.IsDarting;
        if (darting && !wasDarting)
        {
            float chain = Mathf.Max(0, controller.DartChain - 1);
            One(GameSounds.Dart, movement, 1f, 1f + chain * 0.05f);
        }
        wasDarting = darting;

        int tier = controller.VaultTier;
        if (tier > 0 && lastVaultTier == 0)
        {
            One(GameSounds.Vault, movement, 1f, Jitter());
        }
        lastVaultTier = tier;
    }

    void TrackGrapple()
    {
        if (grappling == null)
        {
            return;
        }

        bool swinging = grappling.IsSwinging;
        bool zipping = grappling.IsZipping;

        // click, then a short reel for swings (zips use the reel loop)
        if ((swinging && !wasSwinging) || (zipping && !wasZipping))
        {
            One(GameSounds.GrappleClick, grapple, 1f, Jitter(0.04f));
            if (swinging)
            {
                One(GameSounds.GrappleReel, grapple, 1f, Jitter(0.04f));
            }
        }
        if (!swinging && !zipping && (wasSwinging || wasZipping))
        {
            One(GameSounds.GrappleRelease, grapple);
        }

        wasSwinging = swinging;
        wasZipping = zipping;
    }

    void TrackThrownAxe()
    {
        ThrownAxe t = axe != null ? axe.ActiveAxe : null;

        if (t != spinOwner)
        {
            if (spinLoop != null)
            {
                Destroy(spinLoop.gameObject);
            }
            spinLoop = t != null ? Synth.MakeLoopSource(GameSounds.AxeSpinLoop, t.transform, true, 35f) : null;
            spinOwner = t;
            if (t != null)
            {
                lastAxePos = t.transform.position;
            }
            wasStuck = wasLoose = wasRecalling = false;
        }
        if (t == null)
        {
            return;
        }

        bool stuck = t.IsStuck;
        bool loose = t.IsLoose;
        bool recalling = t.IsRecalling;

        if (stuck && !wasStuck)
        {
            At(EnemyAgent.HitFleshRecently ? GameSounds.AxeHitFlesh : GameSounds.AxeStick, t.HeadPosition, axeSounds, 1f, Jitter(0.04f), 60f);
        }
        if (loose && !wasLoose)
        {
            At(GameSounds.AxeClatter, t.transform.position, axeSounds, 1f, Jitter(), 40f);
        }
        if (recalling && !wasRecalling)
        {
            At(GameSounds.AxeRecall, t.transform.position, axeSounds, 1f, 1f, 45f);
        }

        wasStuck = stuck;
        wasLoose = loose;
        wasRecalling = recalling;

        float speed = (t.transform.position - lastAxePos).magnitude / Mathf.Max(0.0001f, Time.deltaTime);
        lastAxePos = t.transform.position;

        bool flying = !stuck && !loose;
        float amount = flying ? Mathf.Clamp01(speed / 20f) : 0f;
        Synth.Drive(spinLoop, amount * loops * volume, recalling ? 1.2f : Mathf.Lerp(0.85f, 1.1f, speed / 45f), 20f);
    }

    // walls deactivate when they shatter
    void TrackWalls()
    {
        for (int i = 0; i < walls.Count; i++)
        {
            BreakableWall w = walls[i];
            if (w == null)
            {
                continue;
            }
            bool up = w.gameObject.activeSelf;
            if (!up && wallsUp[i])
            {
                Collider c = w.GetComponent<Collider>();
                Vector3 p = c != null ? c.bounds.center : w.transform.position;
                At(GameSounds.WallShatter, p, axeSounds, 1f, Jitter(0.05f), 60f);
            }
            wallsUp[i] = up;
        }
    }

    void DriveLoops(bool paused)
    {
        if (paused)
        {
            Synth.Drive(windLoop, 0f, 1f, 30f);
            Synth.Drive(slideLoop, 0f, 1f, 30f);
            Synth.Drive(reelLoop, 0f, 1f, 30f);
            Synth.Drive(chargeLoop, 0f, 1f, 30f);
            if (spinLoop != null)
            {
                Synth.Drive(spinLoop, 0f, 1f, 30f);
            }
            return;
        }

        float speed = controller.Velocity.magnitude;
        float w = Mathf.InverseLerp(windMinSpeed, windFullSpeed, speed);
        Synth.Drive(windLoop, w * w * wind * volume, Mathf.Lerp(0.85f, 1.3f, w), 6f);

        bool sliding = controller.IsSliding && controller.IsGrounded;
        float s = sliding ? Mathf.InverseLerp(3f, controller.maxSpeed * 1.4f, controller.CurrentSpeed) : 0f;
        Synth.Drive(slideLoop, s * loops * volume, Mathf.Lerp(0.85f, 1.15f, s), 14f);

        bool zipping = grappling != null && grappling.IsZipping;
        float z = Mathf.InverseLerp(5f, 45f, speed);
        Synth.Drive(reelLoop, zipping ? (0.5f + z * 0.5f) * loops * grapple * volume : 0f, Mathf.Lerp(0.85f, 1.35f, z), 12f);

        float c = axe != null ? axe.ChargeAmount : 0f;
        Synth.Drive(chargeLoop, c > 0f ? (0.3f + c * 0.7f) * loops * axeSounds * volume : 0f, Mathf.Lerp(0.85f, 1.3f, c), 16f);
    }

    // ---------------------------------------------------------------- events

    void OnSwing() => One(GameSounds.AxeSwing, axeSounds, 1f, Jitter(0.08f));

    void OnAxeHit(Vector3 point, Vector3 normal, bool bounced)
    {
        bool flesh = EnemyAgent.LastFleshHitFrame == Time.frameCount;
        One(flesh ? GameSounds.AxeHitFlesh : GameSounds.AxeHitWall, axeSounds, 1f, Jitter(0.05f));
        if (bounced)
        {
            One(GameSounds.AxeBounce, axeSounds, 1f, Jitter(0.04f));
        }
    }

    void OnThrown() => One(GameSounds.AxeThrow, axeSounds, 1f, Jitter(0.04f));

    void OnChargeFull() => One(GameSounds.ChargeFull, axeSounds);

    void OnCaught() => One(GameSounds.AxeCatch, axeSounds, 1f, Jitter(0.04f));

    void OnDamaged(float amount) => One(GameSounds.Hurt, feedback, Mathf.Clamp(0.6f + amount / 60f, 0.6f, 1f), Jitter(0.06f));

    void OnDied() => One(GameSounds.Death, feedback);

    void OnRespawned() => One(GameSounds.Respawn, feedback);

    // timer might not exist yet
    void HookCourse()
    {
        if (course != null)
        {
            return;
        }
        course = CourseTimer.Instance;
        if (course == null)
        {
            return;
        }
        course.RunStarted += OnRunStarted;
        course.RunFinished += OnRunFinished;
    }

    void OnRunStarted() => One(GameSounds.RunStart, feedback);

    void OnRunFinished(float time, bool newBest) => One(newBest ? GameSounds.NewBest : GameSounds.RunFinish, feedback);
}
