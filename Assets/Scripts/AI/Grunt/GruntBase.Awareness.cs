using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// GruntBase: the awareness meter (? and !), what it notices, and finding bodies.
public partial class GruntBase
{
    // ---------------------------------------------------------------- awareness

    protected override void AfterSenses(float dt)
    {
        if (InCombatState)
        {
            if (PlayerVisible)
            {
                Awareness = 1f;
                lastStimulus = Time.time;
            }
        }
        else if (PlayerVisible)
        {
            Awareness = Mathf.Min(1f, Awareness + dt / NoticeTime());
            lastStimulus = Time.time;
        }
        else if (Time.time - lastStimulus > 3f)
        {
            Awareness = Mathf.Max(0f, Awareness - awarenessDecay * dt);
        }

        Board.Set(BB.Awareness, Mathf.Round(Awareness * 20f) / 20f);
    }

    // seconds of seeing the player to fill the meter
    float NoticeTime()
    {
        float dist = Vector3.Distance(EyePosition, player.Center);
        float time = Mathf.Lerp(noticeTimeNear, noticeTimeFar, Mathf.InverseLerp(noticeNearRange, sightRange, dist));

        if (player.Speed > 9f)
        {
            time *= 0.7f;
        }
        if (!InMainCone(player.Center))
        {
            time *= 1.6f;
        }

        SquadAlert level = Alertness;
        if (level == SquadAlert.Suspicious)
        {
            time *= 0.6f;
        }
        else if (level == SquadAlert.Searching)
        {
            time *= 0.35f;
        }
        if (Squad != null && Squad.Alarmed)
        {
            time *= 0.75f;
        }

        return Mathf.Max(0.05f, time);
    }

    bool InMainCone(Vector3 point)
    {
        Vector3 to = point - EyePosition;
        to.y = 0f;
        return to.sqrMagnitude < 0.01f || Vector3.Angle(transform.forward, to) <= sightAngle * 0.5f;
    }

    protected void RaiseAwareness(float value)
    {
        Awareness = Mathf.Max(Awareness, Mathf.Clamp01(value));
        lastStimulus = Time.time;
    }

    protected void ClearAwareness(float to = 0f)
    {
        Awareness = Mathf.Min(Awareness, to);
    }

    // F6 menu
    public void DebugSetAwareness(float value)
    {
        Awareness = Mathf.Clamp01(value);
        lastStimulus = Time.time;
    }

    protected override void OnSawPlayer()
    {
        if (Squad == null || Squad.Broken)
        {
            return;
        }
        if (Aware || InCombatState)
        {
            Squad.ReportSighting(player.Center, player.Velocity);
        }
        else if (Awareness >= suspiciousAt)
        {
            ReportSuspicion(player.Center, false);
        }
    }

    protected override void OnHeard(NoiseEvent noise)
    {
        if (!InCombatState)
        {
            float d = Vector3.Distance(transform.position, noise.position);
            float loud = 1f - Mathf.Clamp01(d / Mathf.Max(0.1f, noise.radius * hearing));
            RaiseAwareness(Mathf.Min(0.9f, suspiciousAt + 0.15f + loud * 0.45f));
        }

        if (Squad == null || Squad.Broken)
        {
            return;
        }
        if (InCombatState || Squad.Alert >= SquadAlert.Searching)
        {
            Squad.ReportNoise(noise.position);
        }
        else
        {
            ReportSuspicion(noise.position, true, noise.kind == "axe");
        }
    }

    void ReportSuspicion(Vector3 at, bool heard, bool lure = false)
    {
        if (!Led)
        {
            return;
        }
        bool first = Squad.Alert == SquadAlert.Calm && Squad.TimeSinceSuspect > 4f;
        Squad.ReportSuspicion(at, this, heard, lure);
        if (!first)
        {
            return;
        }
        EnemySounds.PlayAt(EnemySounds.SuspiciousHum, HeadPosition, 1f, Random.Range(0.95f, 1.05f), 25f);
        OnFirstToNotice(heard, lure);
    }

    protected virtual void OnFirstToNotice(bool heard, bool lure) { }

    protected void MarkSpotted()
    {
        SpottedAt = Time.time;

        // only one sting at a time, not one per grunt
        if (Time.time - lastSting < 1.5f)
        {
            return;
        }
        lastSting = Time.time;
        EnemySounds.PlayAt(EnemySounds.AlertSting, HeadPosition, 1f, Random.Range(0.97f, 1.03f), 40f);
    }

    static float lastSting = -99f;

    // director calls this while a recalled axe he can see/hear flies back to the player
    public void SawAxeRecalled(Vector3 playerPos)
    {
        if (IsDead || (Squad != null && Squad.Broken))
        {
            return;
        }

        Vector2 off = Random.insideUnitCircle * axeTrailError;
        Vector3 guess = playerPos + new Vector3(off.x, 0f, off.y);
        bool fresh = Time.time - lastAxeTrail > 3f;
        lastAxeTrail = Time.time;

        if (InCombatState)
        {
            if (Squad != null)
            {
                Squad.ReportNoise(guess);
            }
            return;
        }

        RaiseAwareness(0.8f);
        Board.Set(BB.HeardPos, guess);
        Board.Set(BB.HeardAt, Time.time);
        if (Led)
        {
            Squad.ReportTrail(guess);
        }
        if (fresh)
        {
            Say(Random.value < 0.5f ? "THAT AXE! FOLLOW IT!" : "IT'S FLYING BACK TO HIM!", "axetrail");
        }
    }

    public bool CanSeePoint(Vector3 point, float range)
    {
        Vector3 eye = EyePosition;
        if ((point - eye).sqrMagnitude > range * range || !InMainCone(point))
        {
            return false;
        }
        return EQSTest.Clear(eye, point, IgnoreForSight);
    }

    // ---------------------------------------------------------------- bodies

    public bool CanSpotBody(Vector3 point, float range = -1f)
    {
        return CanSeePoint(point + Vector3.up * 0.3f, range < 0f ? bodySpotRange : range);
    }

    public bool WitnessedDeath(GruntBase victim)
    {
        if (Vector3.Distance(transform.position, victim.transform.position) < 5f)
        {
            return true;
        }
        return CanSpotBody(victim.BodyPosition, Mathf.Max(bodySpotRange, sightRange * 0.8f));
    }

    public void FoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (IsDead)
        {
            return;
        }
        bodyFoundTime = Time.time;
        bodyFoundPos = at;
        if (!InCombatState)
        {
            RaiseAwareness(0.85f);
        }
        OnFoundBody(victim, at, witnessed);
    }

    protected virtual void OnFoundBody(GruntBase victim, Vector3 at, bool witnessed)
    {
        if (Squad != null && !Squad.Broken)
        {
            if (victim == Squad.Leader)
            {
                Say(witnessed ? "THE CAPTAIN'S DOWN!" : "IT'S THE CAPTAIN! HE'S DEAD!", "capdead");
                Squad.Break();
                return;
            }
            Squad.ReportBody(at);
        }

        if (InCombatState)
        {
            Say("MAN DOWN!", "mandown");
        }
        else
        {
            Say(witnessed ? "MAN DOWN!" : "WE'VE GOT A BODY HERE!", "body");
        }
    }

    public void Say(string line, string key)
    {
        if (director != null)
        {
            director.Say(this, line, key);
        }
    }

    public virtual void AssignSquad(GruntSquad squad)
    {
        Squad = squad;
    }
}
