using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Stillwalker: the F6 test menu hooks and the debug text for the F3 overlay.
public partial class Stillwalker
{
    // ---------------------------------------------------------------- testing (F6 menu)

    // F6 menu
    public bool DebugIgnorePolicy { get; set; }

    public void DebugLaunch()
    {
        if (IsDead)
        {
            return;
        }
        lastLaunchTime = -99f;
        if (!Brain.IsInState(hunting))
        {
            Brain.ForceChange(hunting);
        }
        Brain.Request(launching);
    }

    string WhyRules()
    {
        if (DebugIgnorePolicy)
        {
            return "model ignored in F6";
        }
        if (!PolicyReady)
        {
            return "no model loaded";
        }
        if (DistToPlayer() > policyMaxDistance)
        {
            return "out of range";
        }
        if (Time.time < policyBenchedUntil)
        {
            return "model stalled or F6 override";
        }
        if (!PlayerVisible)
        {
            return "no line of sight";
        }
        if (hasAmbush)
        {
            return "ambushing";
        }
        if (WantDistance() < policyHandOverDistance)
        {
            return "creeping in";
        }
        if (PathDist > policyMaxDistance * 1.4f)
        {
            return "long way round";
        }
        return "model";
    }

    public string DebugState
    {
        get
        {
            if (Brain == null)
            {
                return "-";
            }
            if (Brain.IsInState(dormant))
            {
                return "Dormant (asleep)";
            }
            if (Brain.IsInState(shattered))
            {
                return "Shattered";
            }
            if (Brain.IsInState(watched))
            {
                return InAmbush ? "In ambush, frozen (you're looking)" : "Frozen (you're looking)";
            }
            if (Brain.IsInState(moving))
            {
                return "Stalking";
            }
            if (Brain.IsInState(frozen))
            {
                return "Retreating, frozen";
            }
            if (Brain.IsInState(fleeing))
            {
                return Desperate ? "Bolting" : "Retreating";
            }
            if (Brain.IsInState(dodging))
            {
                return "Dodging";
            }
            if (Brain.IsInState(launching))
            {
                return "Launching";
            }
            if (Brain.IsInState(recovering))
            {
                return "Recovering (stuck)";
            }
            return "Hunting";
        }
    }

    public bool DebugModelSteering => !Hurt && PolicyInControl && !rulesSteering;
    public string DebugWhyRules => WhyRules();
    public string DebugIntent => utility != null && utility.Current != null ? utility.Current.Name : "-";
    public float DebugTension01 => Mathf.Clamp01(tension / Mathf.Max(0.1f, closeInTime));
    public float DebugWantDistance => WantDistance();
    public string DebugAmbush => InAmbush ? $"waiting ({ambushMaxWait - (Time.time - ambushArrivedAt):0.0}s left)"
        : hasAmbush ? "heading to its spot"
        : Time.time < nextAmbushTime ? $"cooldown {nextAmbushTime - Time.time:0.0}s" : "looking (needs you moving 5+ m/s)";

    public IEnumerable<string> IntentNames
    {
        get { foreach (UtilityAction a in utility.Actions) { if (a != uAmbush) { yield return a.Name; } } }
    }

    void WakeForDebug()
    {
        if (Brain.IsInState(dormant))
        {
            Brain.ForceChange(hunting);
        }
    }

    // the rules drive (not the model) for this long
    void BenchModel(float seconds) => policyBenchedUntil = Time.time + seconds;

    public void DebugWake() => WakeForDebug();

    public void DebugForceIntent(string name, float seconds = 6f)
    {
        WakeForDebug();
        foreach (UtilityAction a in utility.Actions)
        {
            if (a.Name != name)
            {
                continue;
            }
            BenchModel(seconds);
            utility.Force(a, seconds);
            if (Brain.IsInState(moving))
            {
                SwitchTree();
            }
            return;
        }
    }

    // as if you'd ignored it for the full Close In Time
    public void DebugMaxTension()
    {
        WakeForDebug();
        tension = closeInTime;
    }

    // picks a hiding spot near a point, ignoring the speed and cooldown checks
    public bool DebugAmbushAt(Vector3 near)
    {
        WakeForDebug();
        if (!NavMesh.SamplePosition(near, out NavMeshHit onMesh, 4f, NavMesh.AllAreas))
        {
            return false;
        }
        ambushCenter = onMesh.position;

        EQSItem best = ambushQuery.Run();
        if (best == null)
        {
            return false;
        }

        hasAmbush = true;
        ambushStartedAt = -1f;
        ambushArrivedAt = -1f;
        nextAmbushTime = 0f;
        Board.Set(BB.AmbushPos, best.Point);
        BenchModel(ambushMaxTravel + ambushMaxWait);
        return true;
    }

    public void DebugCreepCue()
    {
        nextCreepCue = 0f;
        EnemySounds.PlayAt(EnemySounds.StoneStep, transform.position, 1f, 0.45f);
        EnemySounds.PlayAt(EnemySounds.StoneCrack, transform.position, 0.35f, 0.5f);
    }

    public void DebugCrack() => TakeHit(new EnemyHit
    {
        kind = HitKind.Thrown,
        point = ChestPosition,
        direction = Flat(transform.position - player.Feet),
        force = 1f
    });

    public void DebugShatter() => Die(new EnemyHit
    {
        kind = HitKind.Other,
        point = ChestPosition,
        direction = -transform.forward,
        force = 1f
    });

    public void PlaceForTraining(Vector3 position, int startCracks = 0)
    {
        ResetAgent();
        transform.position = position;
        SnapToGround();
        Brain.ForceChange(hunting);

        // start some episodes already hurt so it gets plenty of practice running
        startCracks = Mathf.Clamp(startCracks, 0, cracksToShatter - 1);
        for (int i = 1; i <= startCracks; i++)
        {
            crackFX.AddCrack(ChestPosition + UnityEngine.Random.onUnitSphere * 0.3f, i);
        }
        Cracks = startCracks;
        if (Hurt)
        {
            lastCrackTime = Time.time;
            Brain.ForceChange(retreating);
        }
    }

    public override string DebugText()
    {
        StringBuilder sb = new StringBuilder(base.DebugText());
        sb.Append($"\ncracks {Cracks}/{cracksToShatter}  seen {Seen}  launch {(LaunchReady ? "ready" : "cooling")}");
        sb.Append($"\ndistance {DistToPlayer():0.0}  path {PathDist:0.0}  wants {WantDistance():0.0}  chase {ChaseSpeed():0.0} m/s");
        sb.Append($"\nunseen build-up {tension:0.0}/{closeInTime:0}s{(InAmbush ? "  IN AMBUSH" : hasAmbush ? "  heading to ambush" : "")}");
        if (Hurt)
        {
            sb.Append($"\nhurt: retreat to {RetreatDistance():0}m at {FleeSpeed():0} m/s{(Desperate ? "  BOLTING" : "")}" +
                      $"  heals in {Mathf.Max(0f, crackHealDelay - (Time.time - lastCrackTime)):0.0}s");
        }
        if (PolicyReady)
        {
            bool steering = Hurt ? PolicyRetreats : PolicyInControl && !rulesSteering;
            string why = WhyRules();
            sb.Append(steering ? "\npolicy: ML-Agents" : $"\npolicy: ML-Agents ({why}, hand-written rules)");
        }
        sb.Append($"\nplayer: speed {profile.speed01:0.00}  air {profile.airborne01:0.00}  watching {profile.watching01:0.00}" +
                  $"  closing {profile.approach01:0.00}  aggro {profile.aggression01:0.00}");
        if (Brain != null && Brain.IsInState(moving))
        {
            sb.Append("\nintent: ").Append(utility.Current != null ? utility.Current.Name : "-");
            if (activeTree != null)
            {
                sb.Append("\nbt: ").Append(activeTree.RunningPath);
            }
            foreach (UtilityAction a in utility.Actions)
            {
                sb.Append($"\n  {a.Name,-9} {a.Score:0.00}");
            }
        }
        return sb.ToString();
    }
}
