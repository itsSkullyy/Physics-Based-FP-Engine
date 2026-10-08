using UnityEngine;
using UnityEngine.AI;

// Fake player for Stillwalker training. Recordings teach it how players move around, but
// a recording never comes after it. This bot does: it runs it down on the NavMesh, jumps
// to land on its head (a pogo, the agent counts the hit), bounces off and goes again, and
// keeps looking at it most of the time. Each episode picks a style and a skill so the
// policy sees lots of different players. Drives a PlayerMotionTracker in Scripted mode.
public class StillwalkerHunterBot
{
    // Diver goes straight for it, Circler orbits and picks its moment, Kiter hangs back
    // and throws axes (the agent simulates the throws, Kiters throw more)
    public enum Style { Diver, Circler, Kiter }

    public Style CurrentStyle { get; private set; }
    public float Skill { get; private set; }

    const float Accel = 60f;
    const float FallMultiplier = 2.3f;

    Vector3 feet;
    Vector3 velocity;
    bool grounded;
    float feetOffset;
    float runSpeed;
    float jumpRange;
    Vector3 home;

    readonly NavMeshPath path = new NavMeshPath();
    float nextPath;
    int corner;

    float circleSign;
    float nextDive;
    float jumpCooldown;
    float lookAwayUntil;
    float nextLookAway;
    float watchShare;

    public void Reset(Vector3 startCenter, float feetOffset, float skill)
    {
        this.feetOffset = feetOffset;
        feet = startCenter - Vector3.up * feetOffset;
        if (NavMesh.SamplePosition(feet, out NavMeshHit hit, 4f, NavMesh.AllAreas)) feet = hit.position;
        home = feet;
        velocity = Vector3.zero;
        grounded = true;

        Skill = Mathf.Clamp01(skill);
        CurrentStyle = (Style)Random.Range(0, 3);
        runSpeed = Mathf.Lerp(6.5f, 12f, Skill) * Random.Range(0.85f, 1.15f);
        jumpRange = Mathf.Lerp(3.5f, 7f, Skill) * Random.Range(0.8f, 1.2f);
        watchShare = Mathf.Lerp(0.55f, 0.9f, Skill) * Random.Range(0.85f, 1.1f);
        circleSign = Random.value < 0.5f ? 1f : -1f;
        nextDive = Time.time + Random.Range(1f, 3f);
        nextLookAway = Time.time + Random.Range(1f, 4f);
        lookAwayUntil = 0f;
        jumpCooldown = 0f;
        nextPath = 0f;
        path.ClearCorners();
    }

    // a pogo landed: bounce up off its head like the real player does
    public void Bounce()
    {
        velocity.y = 9f;
        velocity.x *= 0.4f;
        velocity.z *= 0.4f;
        grounded = false;
    }

    public void Step(float dt, Stillwalker target, PlayerMotionTracker ghost)
    {
        jumpCooldown -= dt;
        Vector3 me = target.transform.position;

        if (grounded) GroundMove(dt, target, me);
        else AirMove(dt);

        // fell out of the arena somehow
        if (feet.y < home.y - 30f)
        {
            feet = home;
            velocity = Vector3.zero;
            grounded = true;
        }

        Vector3 center = feet + Vector3.up * feetOffset;
        ghost.Drive(center, velocity, grounded ? PlayerMotionTracker.Flags.Grounded : PlayerMotionTracker.Flags.None,
            LookDirection(center, target));
    }

    // ---------------------------------------------------------------- ground

    void GroundMove(float dt, Stillwalker target, Vector3 me)
    {
        Vector3 toMe = me - feet;
        toMe.y = 0f;
        float dist = toMe.magnitude;

        bool diving = CurrentStyle == Style.Diver || Time.time > nextDive;
        if (diving && jumpCooldown <= 0f && dist < jumpRange)
        {
            JumpAt(target);
            return;
        }

        Vector3 goal = Goal(me, toMe, dist, diving);
        Vector3 waypoint = NextWaypoint(goal);

        Vector3 want = waypoint - feet;
        want.y = 0f;
        want = want.sqrMagnitude > 0.04f ? want.normalized * runSpeed : Vector3.zero;

        Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
        flat = Vector3.MoveTowards(flat, want, Accel * dt);
        velocity = new Vector3(flat.x, 0f, flat.z);

        Vector3 next = feet + flat * dt;
        if (NavMesh.Raycast(feet, next, out NavMeshHit edge, NavMesh.AllAreas)) next = edge.position;
        if (NavMesh.SamplePosition(next, out NavMeshHit onMesh, 1.5f, NavMesh.AllAreas)) next = onMesh.position;
        feet = next;
    }

    Vector3 Goal(Vector3 me, Vector3 toMe, float dist, bool diving)
    {
        if (diving) return me;

        Vector3 fromMe = dist > 0.1f ? -toMe / dist : Vector3.forward;
        switch (CurrentStyle)
        {
            case Style.Circler:
                return me + Quaternion.Euler(0f, 40f * circleSign, 0f) * fromMe * 7f;
            case Style.Kiter:
                return me + fromMe * 14f;
            default:
                return me;
        }
    }

    Vector3 NextWaypoint(Vector3 goal)
    {
        if (Time.time >= nextPath)
        {
            nextPath = Time.time + 0.25f;
            corner = 1;
            if (!NavMesh.SamplePosition(goal, out NavMeshHit g, 3f, NavMesh.AllAreas)
                || !NavMesh.CalculatePath(feet, g.position, NavMesh.AllAreas, path))
                path.ClearCorners();
        }

        Vector3[] corners = path.corners;
        if (corners == null || corners.Length < 2) return goal;
        while (corner < corners.Length - 1 && (corners[corner] - feet).sqrMagnitude < 0.5f) corner++;
        return corners[Mathf.Min(corner, corners.Length - 1)];
    }

    // jump so the arc comes down on its head, leading it as well as this bot's skill allows
    void JumpAt(Stillwalker target)
    {
        float g = -Physics.gravity.y;
        float up = Mathf.Lerp(9f, 11f, Random.value);
        float headRise = target.HeadPosition.y + 0.6f - feet.y;

        float tUp = up / g;
        float apex = up * up / (2f * g);
        float drop = Mathf.Max(0.2f, apex - headRise);
        float time = tUp + Mathf.Sqrt(2f * drop / (g * FallMultiplier));

        Vector3 aim = target.transform.position + target.Velocity * time * Skill;
        aim += Random.insideUnitSphere * (1f - Skill) * 1.5f;
        Vector3 flat = aim - feet;
        flat.y = 0f;
        Vector3 horizontal = Vector3.ClampMagnitude(flat / time, 16f);

        velocity = horizontal + Vector3.up * up;
        grounded = false;
        jumpCooldown = Random.Range(0.6f, 1.6f) * Mathf.Lerp(1.4f, 0.7f, Skill);
        nextDive = Time.time + Random.Range(2f, 5f);
    }

    // ---------------------------------------------------------------- air

    void AirMove(float dt)
    {
        float g = Physics.gravity.y * (velocity.y < 0f ? FallMultiplier : 1f);
        velocity.y += g * dt;
        Vector3 next = feet + velocity * dt;

        if (velocity.y < 0f && GroundBelow(feet, next, out float groundY))
        {
            next.y = groundY;
            velocity.y = 0f;
            grounded = true;
            jumpCooldown = Mathf.Max(jumpCooldown, 0.3f);
            if (NavMesh.SamplePosition(next, out NavMeshHit onMesh, 2f, NavMesh.AllAreas)) next = onMesh.position;
        }
        feet = next;
    }

    static bool GroundBelow(Vector3 from, Vector3 to, out float groundY)
    {
        groundY = 0f;
        Vector3 start = from + Vector3.up * 0.3f;
        float len = (from.y - to.y) + 0.35f;
        RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, Mathf.Max(0.4f, len), ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        foreach (RaycastHit h in hits)
        {
            // falls through the Stillwalker itself, the agent handles that as a pogo
            if (h.collider.GetComponentInParent<EnemyAgent>() != null) continue;
            if (h.distance < best)
            {
                best = h.distance;
                groundY = h.point.y;
            }
        }
        return best < float.MaxValue;
    }

    // ---------------------------------------------------------------- looking

    // mostly watching it (so it freezes), sometimes glancing where it's going
    Vector3 LookDirection(Vector3 center, Stillwalker target)
    {
        if (Time.time > nextLookAway)
        {
            float away = Random.Range(0.4f, 2f) * (1f - watchShare) * 3f;
            lookAwayUntil = Time.time + away;
            nextLookAway = lookAwayUntil + Random.Range(1.5f, 5f) * watchShare;
        }

        Vector3 eye = center + Vector3.up * 0.6f;
        if (Time.time < lookAwayUntil)
        {
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            if (flat.sqrMagnitude > 1f) return flat.normalized;
            return Quaternion.Euler(0f, 120f, 0f) * (target.ChestPosition - eye).normalized;
        }
        return (target.ChestPosition - eye).normalized;
    }
}
