using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// F6 testing menu for the enemies. Game keeps running while it's open.
// Only added in the editor and dev builds.
public class AIDebugMenu : MonoBehaviour
{
    public Key toggleKey = Key.F6;

    bool open;
    PlayerInputRouter router;
    CursorLockMode restoreLock;
    bool restoreVisible;
    bool restoreInput;

    Rect window = new Rect(20f, 60f, 360f, 640f);
    Vector2 scroll;
    GruntLeader selected;
    string status = "";
    float statusUntil;

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && toggleKey != Key.None && kb[toggleKey].wasPressedThisFrame)
        {
            if (open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        if (selected != null && selected.IsDead)
        {
            selected = null;
        }
    }

    void Open()
    {
        if (router == null)
        {
            router = FindFirstObjectByType<PlayerInputRouter>();
        }

        open = true;
        restoreLock = Cursor.lockState;
        restoreVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (router != null)
        {
            restoreInput = router.inputEnabled;
            router.inputEnabled = false;
        }

        if (selected == null)
        {
            selected = LeaderNearestCrosshair();
        }
    }

    void Close()
    {
        open = false;
        Cursor.lockState = restoreLock;
        Cursor.visible = restoreVisible;
        if (router != null)
        {
            router.inputEnabled = restoreInput;
        }
    }

    void OnDisable()
    {
        if (open)
        {
            Close();
        }
    }

    // ---------------------------------------------------------------- drawing

    void OnGUI()
    {
        if (!open)
        {
            return;
        }
        window = GUILayout.Window(GetInstanceID(), window, DrawWindow, $"Enemy AI Testing ({toggleKey} to close)");
    }

    void DrawWindow(int id)
    {
        AIDirector d = AIDirector.Instance;
        if (d == null)
        {
            GUILayout.Label("No AIDirector yet (no enemies in the scene).");
            GUI.DragWindow();
            return;
        }

        scroll = GUILayout.BeginScrollView(scroll);

        DrawLeader(d);
        DrawStillwalkers(d);
        DrawSenses(d);
        DrawGeneral(d);

        GUILayout.EndScrollView();

        if (Time.unscaledTime < statusUntil)
        {
            GUILayout.Label(status);
        }
        GUI.DragWindow();
    }

    void DrawLeader(AIDirector d)
    {
        Header("Grunt Leader");

        List<GruntLeader> leaders = Leaders(d);
        if (leaders.Count == 0)
        {
            GUILayout.Label("No living leaders.");
            return;
        }
        if (selected == null || !leaders.Contains(selected))
        {
            selected = leaders[0];
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<", GUILayout.Width(30f)))
        {
            selected = leaders[(leaders.IndexOf(selected) + leaders.Count - 1) % leaders.Count];
        }
        GUILayout.Label($"{selected.name}  ({Distance(selected):0}m)  hits {selected.HitsTaken}/{selected.maxHits}");
        if (GUILayout.Button(">", GUILayout.Width(30f)))
        {
            selected = leaders[(leaders.IndexOf(selected) + 1) % leaders.Count];
        }
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Select the one nearest my crosshair"))
        {
            selected = LeaderNearestCrosshair() ?? selected;
        }

        if (selected.Squad != null)
        {
            GUILayout.Label($"Squad alert: {selected.Squad.Alert}  (set it below, 'here' = where you're looking)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Patrol")) { selected.DebugSetAlert(SquadAlert.Calm, d.Player.Feet); Report("Back to patrolling."); }
            if (GUILayout.Button("Suspicious here")) { selected.DebugSetAlert(SquadAlert.Suspicious, LookPointOr(d.Player.Feet)); Report("Something over there..."); }
            if (GUILayout.Button("Search here")) { selected.DebugSetAlert(SquadAlert.Searching, LookPointOr(d.Player.Feet)); Report("Searching."); }
            if (GUILayout.Button("Fight")) { selected.DebugSetAlert(SquadAlert.Combat, d.Player.Feet); Report("Contact."); }
            GUILayout.EndHorizontal();
        }

        GUILayout.Label("Grenades");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Throw at me"))
        {
            Report(selected.DebugThrowGrenade(d.Player.Feet) ? "Frag out." : "No clear arc to you from there.");
        }
        if (GUILayout.Button("Throw where I'm looking"))
        {
            if (LookPoint(out Vector3 p))
            {
                Report(selected.DebugThrowGrenade(p) ? "Frag out." : "No clear arc to that spot.");
            }
            else
            {
                Report("Not looking at anything.");
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Him");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Zip dodge")) { selected.DebugCounter(); Report("Sidestep."); }
        if (GUILayout.Button("Dive")) { selected.DebugDive(); Report("Dive."); }
        if (GUILayout.Button("Stagger")) { selected.DebugStagger(); Report("Staggered."); }
        if (GUILayout.Button("Kill")) { selected.DebugKill(); Report("Leader down. The squad breaks when they see it."); }
        GUILayout.EndHorizontal();

        GUILayout.Label("His intents (forced for 5s)");
        Grid(selected.IntentNames, n => { selected.DebugForceIntent(n); Report("Forcing " + n + "."); });

        if (selected.Tactics != null)
        {
            GUILayout.Label("Squad plays");
            Grid(selected.Tactics.PlayNames, n =>
                Report(selected.Tactics.ForcePlay(n) ? "Running " + n + "." : n + " couldn't start (no squad, or nowhere to cut off)."));

            GUILayout.Label("Formations (held 8s)");
            GUILayout.BeginHorizontal();
            foreach (FormationShape s in new[] { FormationShape.Wedge, FormationShape.Line, FormationShape.Column, FormationShape.Ring })
            {
                if (GUILayout.Button(s.ToString())) { selected.Tactics.PreviewFormation(s, 8f); Report(s + " formation."); }
            }
            GUILayout.EndHorizontal();
        }
    }

    void DrawStillwalkers(AIDirector d)
    {
        List<Stillwalker> walkers = new List<Stillwalker>();
        foreach (EnemyAgent e in d.Enemies)
        {
            if (e is Stillwalker s && !s.IsDead)
            {
                walkers.Add(s);
            }
        }
        if (walkers.Count == 0)
        {
            return;
        }

        Header("Stillwalker");
        Stillwalker nearest = walkers[0];
        foreach (Stillwalker s in walkers)
        {
            if (Distance(s) < Distance(nearest))
            {
                nearest = s;
            }
        }
        GUILayout.Label($"Nearest: {nearest.name} ({Distance(nearest):0}m, {nearest.PathDist:0}m walking)");
        GUILayout.Label($"State: {nearest.DebugState}   intent: {nearest.DebugIntent}");
        GUILayout.Label("Steering: " + (nearest.DebugModelSteering ? "trained model" : "rules (" + nearest.DebugWhyRules + ")"));
        GUILayout.Label($"Unseen build-up {nearest.DebugTension01 * 100f:0}%  wants to be {nearest.DebugWantDistance:0.0}m away");
        GUILayout.Label("Ambush: " + nearest.DebugAmbush);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Wake up")) { nearest.DebugWake(); Report("It's awake. It knows where you are now."); }
        if (GUILayout.Button("Launch at me")) { nearest.DebugLaunch(); Report("Launch!"); }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Max creep-in")) { nearest.DebugMaxTension(); Report("Full build-up: it creeps to 5m and pounces when you look away."); }
        if (GUILayout.Button("Ambush where I'm looking"))
        {
            Vector3 at = LookPointOr(d.Player.Feet + d.Player.CameraForward * 15f);
            Report(nearest.DebugAmbushAt(at)
                ? "It's heading for a hiding spot near there. Look away, then walk over."
                : "No hiding spot near there that's out of your sight. Try looking at a corner.");
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Creep sound")) { nearest.DebugCreepCue(); Report("Scrape."); }
        if (GUILayout.Button("Crack")) { nearest.DebugCrack(); Report($"Cracked ({nearest.Cracks}/{nearest.cracksToShatter})."); }
        if (GUILayout.Button("Shatter")) { nearest.DebugShatter(); Report("Shattered."); }
        GUILayout.EndHorizontal();

        GUILayout.Label("Its intents (forced for 6s, rules drive, look away to see them)");
        Grid(nearest.IntentNames, n => { nearest.DebugForceIntent(n); Report("Forcing " + n + "."); });

        bool ignore = GUILayout.Toggle(nearest.DebugIgnorePolicy, " Ignore trained model (hand-written stalking)");
        if (ignore != nearest.DebugIgnorePolicy)
        {
            foreach (Stillwalker s in walkers)
            {
                s.DebugIgnorePolicy = ignore;
            }
        }
    }

    void DrawSenses(AIDirector d)
    {
        Header("Senses");
        d.showAwareness = GUILayout.Toggle(d.showAwareness, " ? and ! over their heads");
        d.debugBlind = GUILayout.Toggle(d.debugBlind, " AI can't see me");
        d.debugDeaf = GUILayout.Toggle(d.debugDeaf, " AI can't hear me");
        if (GUILayout.Button("Make a loud noise here (40m)"))
        {
            bool deaf = d.debugDeaf;
            d.debugDeaf = false;
            d.MakeNoise(d.Player.Center, 40f, "debug");
            d.debugDeaf = deaf;
            Report("Bang.");
        }
        GUILayout.Label($"Bodies nobody's found yet: {d.UnfoundBodies}");
    }

    void DrawGeneral(AIDirector d)
    {
        Header("General");

        if (GUILayout.Button("Drop a live grenade in front of me (to bat)"))
        {
            Vector3 p = d.Player.CameraPosition + d.Player.CameraForward * 2.5f;
            GruntGrenade.Spawn(p, Vector3.up * 2f, null, new GruntGrenade.Settings
            {
                fuse = 4f,
                radius = 5f,
                maxDamage = 45f,
                minDamage = 9f,
                knockback = 12f,
                hurtsGrunts = true
            });
            Report("4 second fuse. Hit it.");
        }

        if (d.PlayerHealth != null)
        {
            d.PlayerHealth.invulnerable = GUILayout.Toggle(d.PlayerHealth.invulnerable, " God mode");
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Synth sounds {Synth.Volume:0.0}", GUILayout.Width(120f));
        Synth.Volume = GUILayout.HorizontalSlider(Synth.Volume, 0f, 1f);
        GUILayout.EndHorizontal();

        d.showDebug = GUILayout.Toggle(d.showDebug, " AI overlay (F3)");
        d.showBlackboards = GUILayout.Toggle(d.showBlackboards, " Blackboards (F4)");

        if (GUILayout.Button("Reset all enemies")) { d.ResetAll(); Report("Reset."); }
    }

    // ---------------------------------------------------------------- helpers

    static void Header(string text)
    {
        GUILayout.Space(6f);
        GUILayout.Label("<b>" + text + "</b>", new GUIStyle(GUI.skin.label) { richText = true });
    }

    static void Grid(IEnumerable<string> names, System.Action<string> onClick)
    {
        int i = 0;
        GUILayout.BeginHorizontal();
        foreach (string n in names)
        {
            if (i > 0 && i % 2 == 0)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
            }
            if (GUILayout.Button(n))
            {
                onClick(n);
            }
            i++;
        }
        GUILayout.EndHorizontal();
    }

    void Report(string text)
    {
        status = text;
        statusUntil = Time.unscaledTime + 3f;
    }

    static List<GruntLeader> Leaders(AIDirector d)
    {
        List<GruntLeader> list = new List<GruntLeader>();
        foreach (EnemyAgent e in d.Enemies)
        {
            if (e is GruntLeader l && !l.IsDead)
            {
                list.Add(l);
            }
        }
        return list;
    }

    static float Distance(EnemyAgent e)
    {
        AIDirector d = AIDirector.Instance;
        return d != null && d.Player != null ? Vector3.Distance(d.Player.Center, e.transform.position) : 0f;
    }

    static GruntLeader LeaderNearestCrosshair()
    {
        AIDirector d = AIDirector.Instance;
        if (d == null || d.Player == null)
        {
            return null;
        }

        GruntLeader best = null;
        float bestAngle = float.MaxValue;
        foreach (GruntLeader l in Leaders(d))
        {
            float a = d.Player.LookAngleTo(l.ChestPosition);
            if (a < bestAngle) { bestAngle = a; best = l; }
        }
        return best;
    }

    static Vector3 LookPointOr(Vector3 fallback) => LookPoint(out Vector3 p) ? p : fallback;

    static bool LookPoint(out Vector3 point)
    {
        point = default;
        AIDirector d = AIDirector.Instance;
        if (d == null || d.Player == null)
        {
            return false;
        }

        foreach (RaycastHit h in SortedHits(d.Player.CameraPosition, d.Player.CameraForward))
        {
            if (h.rigidbody != null && h.rigidbody == d.PlayerBody)
            {
                continue;
            }
            point = h.point;
            return true;
        }
        return false;
    }

    static RaycastHit[] SortedHits(Vector3 from, Vector3 dir)
    {
        RaycastHit[] hits = Physics.RaycastAll(from, dir, 120f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        return hits;
    }
}
