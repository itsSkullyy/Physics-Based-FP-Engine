using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// GruntLeader: the F6 test menu hooks and the debug text for the F3 overlay.
public partial class GruntLeader
{
    // ---------------------------------------------------------------- testing (F6 menu)

    public SquadTactics Tactics => tactics;

    public IEnumerable<string> IntentNames
    {
        get { foreach (UtilityAction a in utility.Actions) { yield return a.Name; } }
    }

    void EnsureEngaged()
    {
        if (!Brain.IsInState(engage))
        {
            Brain.ForceChange(engage);
        }
    }

    public bool DebugThrowGrenade(Vector3 target)
    {
        if (IsDead)
        {
            return false;
        }
        nextGrenadeTime = 0f;
        grenadeBlockedUntil = -99f;
        EnsureEngaged();
        return RequestGrenade(target, false);
    }

    public void DebugCounter()
    {
        if (IsDead)
        {
            return;
        }
        EnsureEngaged();
        lastCounterTime = -99f;
        Brain.Request(countering);
    }

    public void DebugDive()
    {
        if (IsDead)
        {
            return;
        }
        EnsureEngaged();
        Vector3 d = ChestPosition - player.CameraPosition;
        d.y = 0f;
        ThreatDir = d.sqrMagnitude > 0.01f ? d.normalized : transform.forward;
        lastDiveTime = -99f;
        Brain.Request(diving);
    }

    public void DebugForceIntent(string name, float seconds = 5f)
    {
        if (IsDead)
        {
            return;
        }
        EnsureEngaged();
        foreach (UtilityAction a in utility.Actions)
        {
            if (a.Name != name)
            {
                continue;
            }
            if (a == uCover)
            {
                lastCoverTime = -99f;
            }
            if (a == uGrenade) { nextGrenadeTime = 0f; grenadeBlockedUntil = -99f; }
            utility.Force(a, seconds);
            return;
        }
    }

    public void DebugSetAlert(SquadAlert level, Vector3 at)
    {
        if (IsDead || Squad == null)
        {
            return;
        }

        float awareness = level == SquadAlert.Combat ? 1f : level == SquadAlert.Calm ? 0f : suspiciousAt + 0.05f;
        DebugSetAwareness(awareness);
        foreach (GruntFollower f in Squad.Alive(Squad.Followers))
        {
            f.DebugSetAwareness(awareness);
        }

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
}
