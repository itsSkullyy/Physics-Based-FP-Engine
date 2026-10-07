using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// A sound the player made. Radius is before each enemy's hearing multiplier.
public struct NoiseEvent
{
    public Vector3 position;
    public float radius;
    public string kind;
    public float time;
}

// Shared enemy stuff: global blackboard, noises, squads, shoot tokens, claimed spots,
// barks, bodies, the thrown axe, resets and the F3/F4 overlay.
// Spawns itself the first time an enemy needs it.
[DefaultExecutionOrder(-50)]
public class AIDirector : MonoBehaviour
{
    public static AIDirector Instance { get; private set; }

    [Header("Barks")]
    public bool showBarks = true;
    public float barkDuration = 1.6f;
    [Tooltip("Same line can't be shouted again by anyone for this long.")]
    public float barkCooldown = 3f;
    public int barkFontSize = 18;

    [Header("Awareness Markers")]
    [Tooltip("? over a Grunt's head while he's noticing you or looking for you, ! when he's spotted you.")]
    public bool showAwareness = true;

    [Header("Thrown Axe")]
    [Tooltip("How far the thunk of a thrown axe sticking into something carries. Loud on purpose: it's a lure.")]
    public float axeLandNoise = 38f;
    [Tooltip("Same, for an axe that bounces off and clatters to the floor.")]
    public float axeClatterNoise = 28f;
    [Tooltip("Grunts this close to a recalled axe hear it rip free and fly. Further out they have to see it.")]
    public float recallHearRange = 16f;

    [Header("Bodies")]
    [Tooltip("How often living Grunts check for bodies they haven't found yet.")]
    public float bodyCheckInterval = 0.25f;

    [Header("Debug Overlay")]
    public Key debugKey = Key.F3;
    public Key blackboardKey = Key.F4;
    public bool showDebug = false;
    public bool showBlackboards = false;
    public float debugRange = 45f;

    [Header("Testing (toggled from the F6 menu)")]
    [Tooltip("Enemies can't see the player at all.")]
    public bool debugBlind;
    [Tooltip("The player makes no noise.")]
    public bool debugDeaf;

    public readonly Blackboard Global = new Blackboard();

    public PlayerMotionTracker Player { get; private set; }
    public PlayerHealth PlayerHealth { get; private set; }
    public FirstPersonCharacterController Controller { get; private set; }
    public Rigidbody PlayerBody { get; private set; }
    public BattleAxe Axe { get; private set; }
    public Grappling Grapple { get; private set; }

    public IReadOnlyList<EnemyAgent> Enemies => enemies;

    readonly List<EnemyAgent> enemies = new List<EnemyAgent>();
    readonly List<GruntSquad> squads = new List<GruntSquad>();
    bool squadsDirty;
    readonly Dictionary<string, List<EnemyAgent>> tokens = new Dictionary<string, List<EnemyAgent>>();
    readonly Dictionary<EnemyAgent, Vector3> claims = new Dictionary<EnemyAgent, Vector3>();
    readonly Dictionary<string, float> barkReadyAt = new Dictionary<string, float>();

    struct Bark
    {
        public EnemyAgent who;
        public string text;
        public float until;
    }
    readonly List<Bark> barks = new List<Bark>();

    struct PendingBark
    {
        public EnemyAgent who;
        public string text;
        public string key;
        public float at;
    }
    readonly List<PendingBark> pendingBarks = new List<PendingBark>();

    // Dead Grunt that nobody has found yet (or has).
    public class Body
    {
        public GruntBase who;
        public bool found;
        public float diedAt;
        public Vector3 Position => who != null ? who.BodyPosition : Vector3.zero;
    }
    readonly List<Body> bodies = new List<Body>();
    public IReadOnlyList<Body> Bodies => bodies;
    float bodyTimer;

    GUIStyle barkStyle;
    GUIStyle debugStyle;
    Texture2D whiteTex;

    public static AIDirector Get()
    {
        if (Instance != null) return Instance;

        Instance = FindFirstObjectByType<AIDirector>();
        if (Instance != null) return Instance;

        GameObject go = new GameObject("AIDirector");
        Instance = go.AddComponent<AIDirector>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        FindPlayer();

        // F6 menu, editor/dev builds only
        if ((Application.isEditor || Debug.isDebugBuild) && GetComponent<AIDebugMenu>() == null)
            gameObject.AddComponent<AIDebugMenu>();
    }

    void OnDestroy()
    {
        if (PlayerHealth != null) PlayerHealth.Respawned -= ResetAll;
        if (Axe != null) Axe.AxeHit -= OnAxeHit;
        if (Instance == this) Instance = null;
    }

    void FindPlayer()
    {
        if (Controller != null) return;

        Controller = FindFirstObjectByType<FirstPersonCharacterController>();
        if (Controller == null) return;

        PlayerBody = Controller.GetComponent<Rigidbody>();
        PlayerHealth = Controller.GetComponent<PlayerHealth>();
        Grapple = Controller.GetComponent<Grappling>();
        Axe = FindFirstObjectByType<BattleAxe>();

        Player = Controller.GetComponent<PlayerMotionTracker>();
        if (Player == null) Player = Controller.gameObject.AddComponent<PlayerMotionTracker>();

        if (PlayerHealth != null) PlayerHealth.Respawned += ResetAll;
        if (Axe != null) Axe.AxeHit += OnAxeHit;
    }

    void OnAxeHit(Vector3 point, Vector3 normal, bool bounced)
    {
        bool flesh = EnemyAgent.LastFleshHitFrame == Time.frameCount;
        MakeNoise(point, flesh ? 5f : 18f, flesh ? "thud" : "axe");
    }

    // ---------------------------------------------------------------- thrown axe

    ThrownAxe trackedAxe;
    bool axeWasStuck, axeWasLoose, axeWasRecalling;
    float trailTimer;

    // Landing makes a loud noise (lure). Recalling it lets anyone who sees/hears it
    // follow it back to the player.
    void TrackThrownAxe()
    {
        ThrownAxe t = Axe != null ? Axe.ActiveAxe : null;
        if (t != trackedAxe)
        {
            trackedAxe = t;
            axeWasStuck = axeWasLoose = axeWasRecalling = false;
        }
        if (t == null) return;

        bool stuck = t.IsStuck;
        bool loose = t.IsLoose;
        bool recalling = t.IsRecalling;

        if ((stuck && !axeWasStuck) || (loose && !axeWasLoose))
        {
            if (EnemyAgent.HitFleshRecently) MakeNoise(t.HeadPosition, 6f, "thud");
            else MakeNoise(t.HeadPosition, stuck ? axeLandNoise : axeClatterNoise, "axe");
        }

        if (recalling)
        {
            trailTimer -= Time.deltaTime;
            if (!axeWasRecalling || trailTimer <= 0f)
            {
                trailTimer = 0.2f;
                FollowRecalledAxe(t.transform.position, !axeWasRecalling);
            }
        }

        axeWasStuck = stuck;
        axeWasLoose = loose;
        axeWasRecalling = recalling;
    }

    void FollowRecalledAxe(Vector3 axePos, bool rippedOut)
    {
        if (Player == null || debugDeaf && debugBlind) return;

        foreach (EnemyAgent e in enemies)
        {
            if (!(e is GruntBase g) || g.IsDead) continue;
            bool heard = !debugDeaf && rippedOut && Vector3.Distance(g.transform.position, axePos) < recallHearRange * g.hearing;
            bool seen = !debugBlind && g.CanSeePoint(axePos, g.sightRange);
            if (heard || seen) g.SawAxeRecalled(Player.Feet);
        }
    }

    // ---------------------------------------------------------------- noise

    readonly List<NoiseEvent> recentNoises = new List<NoiseEvent>();

    public void MakeNoise(Vector3 position, float radius, string kind)
    {
        if (debugDeaf) return;
        NoiseEvent n = new NoiseEvent { position = position, radius = radius, kind = kind, time = Time.time };
        recentNoises.Add(n);

        foreach (EnemyAgent e in enemies)
            if (e != null && !e.IsDead) e.OnHeardNoise(n);
    }

    // Training scenes use a playback ghost instead of the player
    public void UseTracker(PlayerMotionTracker tracker)
    {
        Player = tracker;
    }

    // ---------------------------------------------------------------- registry

    public void Register(EnemyAgent e)
    {
        if (!enemies.Contains(e)) enemies.Add(e);
    }

    public void Unregister(EnemyAgent e)
    {
        enemies.Remove(e);
        ReleaseAllTokens(e);
        claims.Remove(e);
    }

    public void ResetAll()
    {
        Global.Clear();
        tokens.Clear();
        claims.Clear();
        barks.Clear();
        pendingBarks.Clear();
        bodies.Clear();
        trackedAxe = null;

        foreach (EnemyAgent e in enemies.ToArray())
            if (e != null) e.ResetAgent();

        squadsDirty = true;
    }

    // ---------------------------------------------------------------- grunt squads

    public IReadOnlyList<GruntSquad> Squads => squads;

    // Rebuilt next Update so every Grunt has had Start first
    public void MarkSquadsDirty() => squadsDirty = true;

    void FormSquads()
    {
        squadsDirty = false;
        squads.Clear();

        foreach (EnemyAgent e in enemies)
        {
            if (e is GruntLeader leader && !leader.IsDead)
            {
                GruntSquad s = new GruntSquad(leader, Global);
                squads.Add(s);
                leader.AssignSquad(s);
            }
        }

        // closest pairs first, leaders fill up then overflow goes to the next one
        var pairs = new List<(float dist, GruntSquad squad, GruntFollower follower)>();
        var followers = new List<GruntFollower>();
        foreach (EnemyAgent e in enemies)
        {
            if (!(e is GruntFollower f) || f.IsDead) continue;
            followers.Add(f);
            foreach (GruntSquad s in squads)
            {
                float d = Vector3.Distance(f.transform.position, s.Leader.transform.position);
                if (d <= s.Leader.squadRadius) pairs.Add((d, s, f));
            }
        }
        pairs.Sort((a, b) => a.dist.CompareTo(b.dist));

        var placed = new HashSet<GruntFollower>();
        foreach (var p in pairs)
        {
            if (placed.Contains(p.follower) || p.squad.Followers.Count >= p.squad.Leader.maxFollowers) continue;
            p.squad.Add(p.follower);
            placed.Add(p.follower);
        }

        // leftovers are loners
        foreach (GruntFollower f in followers)
            if (!placed.Contains(f)) f.AssignSquad(null);

        foreach (GruntSquad s in squads) s.SplitFireteams();
    }

    public void NotifyDeath(EnemyAgent e)
    {
        ReleaseAllTokens(e);
        claims.Remove(e);

        foreach (EnemyAgent other in enemies)
        {
            if (other == e || other.IsDead) continue;
            if ((other.transform.position - e.transform.position).sqrMagnitude < 25f * 25f)
                other.OnAllyDied(e);
        }

        if (!(e is GruntBase g)) return;

        // witnesses know straight away, otherwise it waits to be found
        Body body = new Body { who = g, diedAt = Time.time };
        bodies.Add(body);
        foreach (EnemyAgent other in enemies.ToArray())
            if (other is GruntBase witness && witness != g && !witness.IsDead && witness.WitnessedDeath(g))
                Discover(body, witness, true);
    }

    void CheckBodies()
    {
        bodyTimer -= Time.deltaTime;
        if (bodyTimer > 0f) return;
        bodyTimer = bodyCheckInterval;

        foreach (Body b in bodies)
        {
            if (b.found || b.who == null) continue;
            foreach (EnemyAgent e in enemies)
            {
                if (!(e is GruntBase g) || g.IsDead || g == b.who || !g.CanSpotBody(b.Position)) continue;
                Discover(b, g, false);
                break;
            }
        }
    }

    void Discover(Body b, GruntBase finder, bool witnessed)
    {
        b.found = true;
        finder.FoundBody(b.who, b.Position, witnessed);
    }

    public int UnfoundBodies
    {
        get
        {
            int n = 0;
            foreach (Body b in bodies) if (!b.found && b.who != null) n++;
            return n;
        }
    }

    // ---------------------------------------------------------------- tokens

    public bool TryTakeToken(string kind, EnemyAgent who, int max)
    {
        if (!tokens.TryGetValue(kind, out List<EnemyAgent> holders))
        {
            holders = new List<EnemyAgent>();
            tokens[kind] = holders;
        }

        holders.RemoveAll(h => h == null || h.IsDead);

        if (holders.Contains(who)) return true;
        if (holders.Count >= max) return false;

        holders.Add(who);
        return true;
    }

    public void ReleaseToken(string kind, EnemyAgent who)
    {
        if (tokens.TryGetValue(kind, out List<EnemyAgent> holders))
            holders.Remove(who);
    }

    public int TokensInUse(string kind) =>
        tokens.TryGetValue(kind, out List<EnemyAgent> holders) ? holders.Count : 0;

    // each squad has its own "shoot" pool
    public int TokensWithPrefix(string prefix)
    {
        int n = 0;
        foreach (var kv in tokens)
            if (kv.Key.StartsWith(prefix)) n += kv.Value.Count;
        return n;
    }

    void ReleaseAllTokens(EnemyAgent who)
    {
        foreach (var kv in tokens) kv.Value.Remove(who);
    }

    // ---------------------------------------------------------------- claims

    public void Claim(EnemyAgent who, Vector3 point) => claims[who] = point;
    public void Unclaim(EnemyAgent who) => claims.Remove(who);

    // 1 = on top of someone else's spot, 0 = radius or further
    public float Crowding(Vector3 point, EnemyAgent asker, float radius)
    {
        float worst = 0f;
        foreach (var kv in claims)
        {
            if (kv.Key == asker || kv.Key == null) continue;
            float d = Vector3.Distance(point, kv.Value);
            worst = Mathf.Max(worst, 1f - Mathf.Clamp01(d / radius));
        }

        foreach (EnemyAgent e in enemies)
        {
            if (e == asker || e == null || e.IsDead) continue;
            float d = Vector3.Distance(point, e.transform.position);
            worst = Mathf.Max(worst, (1f - Mathf.Clamp01(d / radius)) * 0.7f);
        }
        return worst;
    }

    // ---------------------------------------------------------------- barks

    // key is for the cooldown so the whole squad doesn't shout the same thing
    public void Say(EnemyAgent who, string line, string key = null)
    {
        if (!showBarks || who == null) return;

        key = key ?? line;
        if (barkReadyAt.TryGetValue(key, out float ready) && Time.time < ready) return;
        barkReadyAt[key] = Time.time + barkCooldown;

        barks.RemoveAll(b => b.who == who);
        barks.Add(new Bark { who = who, text = line, until = Time.time + barkDuration });

        if (who is GruntBase && !who.IsDead)
            EnemySounds.PlayAt(EnemySounds.RadioBlip, who.HeadPosition, 1f, Random.Range(0.95f, 1.05f), 30f);
    }

    // for replies
    public void SayLater(EnemyAgent who, string line, string key, float delay)
    {
        if (!showBarks || who == null) return;
        pendingBarks.Add(new PendingBark { who = who, text = line, key = key, at = Time.time + delay });
    }

    // ---------------------------------------------------------------- update

    void Update()
    {
        if (Controller == null) FindPlayer();
        if (squadsDirty) FormSquads();

        if (Axe != null) Global.Set(BB.PlayerDisarmed, Axe.IsThrown);
        Global.Set(BB.ActiveShooters, TokensWithPrefix("shoot"));

        Keyboard kb = Keyboard.current;
        if (kb != null && debugKey != Key.None && kb[debugKey].wasPressedThisFrame)
            showDebug = !showDebug;
        if (kb != null && blackboardKey != Key.None && kb[blackboardKey].wasPressedThisFrame)
        {
            showBlackboards = !showBlackboards;
            if (showBlackboards) showDebug = true;
        }

        for (int i = pendingBarks.Count - 1; i >= 0; i--)
        {
            PendingBark p = pendingBarks[i];
            if (Time.time < p.at) continue;
            pendingBarks.RemoveAt(i);
            if (p.who != null && !p.who.IsDead) Say(p.who, p.text, p.key);
        }
        barks.RemoveAll(b => b.who == null || Time.time > b.until);
        CheckBodies();
        TrackThrownAxe();
        recentNoises.RemoveAll(n => Time.time - n.time > 1.5f);
    }

    // ---------------------------------------------------------------- drawing

    // Marker, bark and debug text are stacked per enemy. Nearest enemy goes first and
    // anything overlapping gets pushed up so text doesn't overlap.
    readonly List<Rect> placedStacks = new List<Rect>();
    readonly List<EnemyAgent> drawOrder = new List<EnemyAgent>();
    EnemyAgent debugFocus;

    void OnGUI()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        EnsureStyles();

        debugFocus = showDebug ? FindDebugFocus(cam) : null;
        if (showDebug) DrawDebugWorld(cam);
        DrawOverheads(cam);
        if (showDebug) DrawDebugHeader();
    }

    void DrawOverheads(Camera cam)
    {
        Vector3 camPos = cam.transform.position;
        drawOrder.Clear();
        foreach (EnemyAgent e in enemies)
            if (e != null) drawOrder.Add(e);
        drawOrder.Sort((a, b) => (a.transform.position - camPos).sqrMagnitude.CompareTo((b.transform.position - camPos).sqrMagnitude));

        placedStacks.Clear();
        foreach (EnemyAgent e in drawOrder) DrawStack(cam, e);
    }

    void DrawStack(Camera cam, EnemyAgent e)
    {
        float dist = Vector3.Distance(cam.transform.position, e.transform.position);

        string bark = BarkFor(e, out float barkAge, out float barkLeft);
        if (!showBarks) bark = null;
        string mark = null;
        Color markColor = Color.white;
        float markSize = 0f;
        bool hasMark = showAwareness && MarkerFor(e, dist, out mark, out markColor, out markSize);
        string debug = showDebug ? DebugTextFor(e, dist) : null;
        if (bark == null && !hasMark && debug == null) return;

        if (!ToScreen(cam, e.HeadPosition + Vector3.up * 0.3f, out Vector2 anchor)) return;

        float scale = Mathf.Lerp(1f, 0.6f, Mathf.InverseLerp(8f, 60f, dist));
        const float gap = 2f;

        GUIContent barkContent = null, markContent = null, debugContent = null;
        Vector2 barkSize = Vector2.zero, markBox = Vector2.zero, debugSize = Vector2.zero;
        int barkFont = 0, markFont = 0, debugFont = 0;

        if (hasMark)
        {
            markFont = Mathf.Max(10, Mathf.RoundToInt(markSize * scale));
            barkStyle.fontSize = markFont;
            markContent = new GUIContent(mark);
            markBox = barkStyle.CalcSize(markContent);
            markBox.y *= 0.85f;
        }
        if (bark != null)
        {
            float pop = barkAge < 0.08f ? Mathf.Lerp(1.3f, 1f, barkAge / 0.08f) : 1f;
            barkFont = Mathf.Max(10, Mathf.RoundToInt(barkFontSize * scale * pop));
            barkStyle.fontSize = barkFont;
            barkContent = new GUIContent(bark);
            float w = Mathf.Min(barkStyle.CalcSize(barkContent).x + 6f, 260f * scale + 40f);
            barkSize = new Vector2(w, barkStyle.CalcHeight(barkContent, w));
        }
        if (debug != null)
        {
            debugFont = e == debugFocus ? 12 : 11;
            debugStyle.fontSize = debugFont;
            debugContent = new GUIContent(debug);
            debugSize = debugStyle.CalcSize(debugContent) + new Vector2(8f, 4f);
        }

        float width = Mathf.Max(markBox.x, barkSize.x, debugSize.x);
        float height = markBox.y + barkSize.y + debugSize.y + gap * 2f;
        Rect stack = new Rect(anchor.x - width * 0.5f, anchor.y - height, width, height);
        stack.x = Mathf.Clamp(stack.x, 4f, Mathf.Max(4f, Screen.width - width - 4f));

        for (int pass = 0; pass < 16; pass++)
        {
            bool moved = false;
            foreach (Rect r in placedStacks)
            {
                if (!r.Overlaps(stack)) continue;
                stack.y = r.yMin - stack.height - gap;
                moved = true;
            }
            if (!moved) break;
        }
        if (stack.yMax < 0f) return;
        placedStacks.Add(stack);

        // line back down if it got pushed up
        if (anchor.y - stack.yMax > 12f)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(new Rect(anchor.x - 0.5f, stack.yMax, 1f, anchor.y - stack.yMax), whiteTex);
            GUI.color = Color.white;
        }

        // bottom up: marker, bark, debug
        float cx = stack.center.x;
        float y = stack.yMax;

        if (markContent != null)
        {
            barkStyle.fontSize = markFont;
            y -= markBox.y;
            ShadowLabel(new Rect(cx - markBox.x * 0.5f, y - markBox.y * 0.1f, markBox.x, markBox.y / 0.85f), markContent, barkStyle, markColor);
        }
        if (barkContent != null)
        {
            barkStyle.fontSize = barkFont;
            barkStyle.wordWrap = true;
            y -= barkSize.y + gap;
            Color c = new Color(1f, 0.95f, 0.75f, Mathf.Clamp01(barkLeft / 0.25f));
            ShadowLabel(new Rect(cx - barkSize.x * 0.5f, y, barkSize.x, barkSize.y), barkContent, barkStyle, c);
            barkStyle.wordWrap = false;
        }
        if (debugContent != null)
        {
            debugStyle.fontSize = debugFont;
            y -= debugSize.y + gap;
            Rect r = new Rect(cx - debugSize.x * 0.5f, y, debugSize.x, debugSize.y);
            GUI.color = new Color(0f, 0f, 0f, e == debugFocus ? 0.7f : 0.45f);
            GUI.DrawTexture(r, whiteTex);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 4f, r.y + 2f, r.width, r.height), debugContent, debugStyle);
        }
    }

    string BarkFor(EnemyAgent e, out float age, out float left)
    {
        foreach (Bark b in barks)
        {
            if (b.who != e) continue;
            left = b.until - Time.time;
            age = barkDuration - left;
            return b.text;
        }
        age = left = 0f;
        return null;
    }

    // ? while noticing/searching, ! when spotted
    static bool MarkerFor(EnemyAgent e, float dist, out string text, out Color color, out float size)
    {
        text = null;
        color = Color.white;
        size = 0f;
        if (!(e is GruntBase g) || g.IsDead || (g.Squad != null && g.Squad.Broken) || dist > 70f) return false;

        float spotted = Time.time - g.SpottedAt;
        if (spotted < 1.2f)
        {
            text = "!";
            size = 34f * (spotted < 0.1f ? Mathf.Lerp(1.6f, 1f, spotted / 0.1f) : 1f);
            color = new Color(1f, 0.2f, 0.15f, Mathf.Clamp01((1.2f - spotted) * 3f));
            return true;
        }

        if (g.InCombatState || (g.Awareness <= 0.05f && g.Alertness < SquadAlert.Suspicious)) return false;

        float fill = g.Alertness >= SquadAlert.Suspicious ? Mathf.Max(0.6f, g.Awareness) : g.Awareness;
        text = "?";
        size = Mathf.Lerp(16f, 30f, fill);
        color = Color.Lerp(new Color(1f, 1f, 1f, 0.5f), new Color(1f, 0.85f, 0.15f, 1f), fill);
        return true;
    }

    // full text for the focused enemy, one line for the rest
    string DebugTextFor(EnemyAgent e, float dist)
    {
        if (e == debugFocus)
        {
            string text = e.DebugText();
            if (showBlackboards && e.Board != null)
            {
                text += "\n-- blackboard --\n" + e.Board.Describe();
                Blackboard parent = e.Board.Parent;
                if (parent != null && parent != Global)
                    text += "\n-- squad --\n" + parent.Describe();
            }
            return text;
        }

        if (e.IsDead || dist > debugRange) return null;
        string state = e.Brain != null && e.Brain.Leaf != null ? e.Brain.Leaf.Name : "";
        return e.name + "  " + state;
    }

    // closest to the crosshair, otherwise nearest
    EnemyAgent FindDebugFocus(Camera cam)
    {
        Vector2 middle = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float maxPixels = Screen.height * 0.25f;
        EnemyAgent best = null, nearest = null;
        float bestPixels = float.MaxValue, nearestDist = float.MaxValue;

        foreach (EnemyAgent e in enemies)
        {
            if (e == null || e.IsDead) continue;
            float d = Vector3.Distance(cam.transform.position, e.transform.position);
            if (d > debugRange) continue;
            if (d < nearestDist) { nearestDist = d; nearest = e; }

            if (!ToScreen(cam, e.ChestPosition, out Vector2 sp)) continue;
            float px = Vector2.Distance(sp, middle);
            if (px < maxPixels && px < bestPixels) { bestPixels = px; best = e; }
        }
        return best != null ? best : nearest;
    }

    void DrawDebugHeader()
    {
        string text = $"AI DEBUG ({debugKey}, {blackboardKey} for blackboards)   enemies: {enemies.Count}  squads: {squads.Count}  shooters: {TokensWithPrefix("shoot")}"
                      + $"\ndetail: {(debugFocus != null ? debugFocus.name : "-")}   (aim at an enemy to see everything about it)";
        debugStyle.fontSize = 12;
        GUIContent c = new GUIContent(text);
        Vector2 size = debugStyle.CalcSize(c);
        Rect r = new Rect(8f, 8f, size.x + 8f, size.y + 4f);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(r, whiteTex);
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + 4f, r.y + 2f, r.width, r.height), c, debugStyle);
    }

    void DrawDebugWorld(Camera cam)
    {
        debugStyle.fontSize = 11;
        foreach (NoiseEvent n in recentNoises)
        {
            if (!ToScreen(cam, n.position + Vector3.up * 0.5f, out Vector2 np)) continue;
            GUI.color = new Color(1f, 0.85f, 0.2f, 1f - (Time.time - n.time) / 1.5f);
            GUI.Label(new Rect(np.x - 40f, np.y - 10f, 80f, 20f), "(( " + n.kind + " ))", debugStyle);
        }
        GUI.color = Color.white;

        if (debugFocus == null) return;

        // green = covered from the player
        TacticalMap map = TacticalMap.Instance;
        if (map != null && map.Ready && Player != null && debugFocus is GruntBase)
        {
            List<TacticalMap.Point> near = new List<TacticalMap.Point>();
            map.Near(debugFocus.transform.position, 12f, near);
            foreach (TacticalMap.Point p in near)
            {
                if (!ToScreen(cam, p.position + Vector3.up * 0.1f, out Vector2 tp)) continue;
                GUI.color = Color.Lerp(new Color(1f, 0.3f, 0.3f, 0.6f), new Color(0.3f, 1f, 0.4f, 0.8f),
                    map.CoverFrom(p, Player.Center));
                GUI.DrawTexture(new Rect(tp.x - 2f, tp.y - 2f, 4f, 4f), whiteTex);
            }
        }

        foreach (EQSQuery q in debugFocus.DebugQueries)
        {
            if (q == null || Time.time - q.LastRunTime > 3f) continue;
            foreach (EQSItem item in q.LastItems)
            {
                if (!ToScreen(cam, item.Point, out Vector2 p)) continue;
                Color c = item.Valid ? Color.Lerp(Color.red, Color.green, item.Score) : new Color(0.4f, 0.4f, 0.4f, 0.5f);
                float s = item == q.LastBest ? 10f : (item.Valid ? 6f : 4f);
                GUI.color = c;
                GUI.DrawTexture(new Rect(p.x - s * 0.5f, p.y - s * 0.5f, s, s), whiteTex);
            }
        }
        GUI.color = Color.white;
    }

    static bool ToScreen(Camera cam, Vector3 world, out Vector2 gui)
    {
        Vector3 sp = cam.WorldToScreenPoint(world);
        gui = new Vector2(sp.x, Screen.height - sp.y);
        return sp.z > 0f;
    }

    static void ShadowLabel(Rect r, GUIContent content, GUIStyle style, Color color)
    {
        style.normal.textColor = new Color(0f, 0f, 0f, 0.85f * color.a);
        GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), content, style);
        style.normal.textColor = color;
        GUI.Label(r, content, style);
    }

    void EnsureStyles()
    {
        if (barkStyle == null)
        {
            barkStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = barkFontSize
            };
            barkStyle.normal.textColor = new Color(1f, 0.95f, 0.75f, 1f);
        }

        if (debugStyle == null)
        {
            debugStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };
            debugStyle.normal.textColor = Color.white;
        }

        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
