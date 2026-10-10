using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

// Stillwalker: fighting. Retreating while cracked, dodging and flinching, being a zip
// target, the launch (windup, flight, impact), hitting the player, and taking cracks.
public partial class Stillwalker
{
    // ---------------------------------------------------------------- retreat

    float FleeSpeed() => Desperate ? boltSpeed : fleeSpeed + fleeSpeedPerCrack * Cracks;
    float RetreatDistance() => retreatDistance + retreatPerCrack * Cracks;

    // somewhere away from you and ideally out of sight, so the cracks get a chance to heal
    void BuildFleeQuery()
    {
        fleeQuery = new EQSQuery("FleePos", EQSGen.Ring(() => transform.position, 6f, 20f, 3, 12))
            .Add(EQSTest.OnNavMesh(2f))
            .Add(EQSTest.LineOfSight(() => player.CameraPosition, 1.3f, false, IgnoreForSight, false), 1.5f)
            .Add(EQSTest.Custom("FarFromPlayer", i =>
            {
                Vector3 d = i.Point - player.Feet;
                d.y = 0f;
                return Mathf.Clamp01(d.magnitude / RetreatDistance());
            }), 2f)
            .Add(EQSTest.Custom("AwayFromPlayer", i =>
            {
                Vector3 go = i.Point - transform.position;
                go.y = 0f;
                if (go.sqrMagnitude < 0.01f)
                {
                    return 0.5f;
                }
                return (Vector3.Dot(go.normalized, Flat(transform.position - player.Feet)) + 1f) * 0.5f;
            }), 1.5f)
            .Add(EQSTest.Custom("CloseToMe", i => 1f - Mathf.Clamp01(Vector3.Distance(i.Point, transform.position) / 25f)), 0.5f);

        DebugQueries.Add(fleeQuery);
    }

    void TickFleeing(float dt)
    {
        eyeGlow = Desperate ? 1.2f + Mathf.Sin(Time.time * 30f) * 0.6f : 1f;

        if (PolicyRetreats)
        {
            SteerByPolicy(dt, FleeSpeed(), creepSpeed * 1.5f, Desperate);
            MoveFeedback(dt);
            return;
        }

        // far enough and can't see you: wait there for the cracks to heal
        if (DistToPlayer() >= RetreatDistance() * 0.9f && !PlayerVisible)
        {
            StopMoving();
        }
        else
        {
            if (Time.time >= nextFleeQuery || Arrived(1f))
            {
                nextFleeQuery = Time.time + 0.5f;
                EQSItem best = fleeQuery.Run();
                Board.Set(BB.FleePos, best != null
                    ? best.Point
                    : transform.position + Flat(transform.position - player.Feet) * 6f);
            }
            MoveTo(Board.Get(BB.FleePos, transform.position), FleeSpeed());
        }

        // backs away, never turns its back on you
        FaceTowards(player.Feet, 360f);
        MoveFeedback(dt);
    }

    // ---------------------------------------------------------------- dodging

    bool CanDodge => Brain != null && !IsDead
                     && !Brain.IsInState(launching) && !Brain.IsInState(recovering)
                     && !Brain.IsInState(dodging) && !Brain.IsInState(shattered);

    void Dodge(DodgeKind kind, Vector3 away)
    {
        dodgeKind = kind;
        dodgeAway = away;
        if (kind == DodgeKind.Zip)
        {
            swatted = false;
            swatUntil = Time.time + 0.6f;
        }
        Brain.Request(dodging);
    }

    void StartDodge()
    {
        activeTree?.Abort();
        activeTree = null;
        StopMoving();

        Vector3 dir = new Vector3(dodgeAway.x, 0f, dodgeAway.z);
        if (dir.sqrMagnitude < 0.01f)
        {
            dir = transform.right * (UnityEngine.Random.value < 0.5f ? 1f : -1f);
        }
        dir.Normalize();

        float dist = dodgeKind == DodgeKind.Flinch ? flinchDistance + flinchPerCrack * Cracks : dodgeDistance;
        dodgeFrom = transform.position;
        dodgeTo = PickDash(dir, dist) ?? transform.position;

        // if there was no room the zip carries on and you hit it face first instead
        zipDodging = dodgeKind == DodgeKind.Zip && (dodgeTo - dodgeFrom).sqrMagnitude > 0.25f;

        eyeGlow = 2f;
        AnimTrigger("Dodge");
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 1f, 0.65f);
        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.AirPuff(transform.position + Vector3.up * 0.3f, -dir, 0.5f);
        }
        Emit(StillwalkerEvent.Dodged);
    }

    void TickDodge(float dt)
    {
        float t = Mathf.Clamp01(dodging.TimeInState / dodgeTime);
        Vector3 p = Vector3.Lerp(dodgeFrom, dodgeTo, 1f - (1f - t) * (1f - t));
        if (NavReady)
        {
            nav.Warp(p);
        }
        else
        {
            transform.position = p;
        }
        FaceTowards(player.Feet, 900f);
    }

    // tries the direction it wants first, then fans out
    static readonly float[] dashAngles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };

    Vector3? PickDash(Vector3 dir, float dist)
    {
        foreach (float a in dashAngles)
        {
            Vector3 target = transform.position + Quaternion.Euler(0f, a, 0f) * dir * dist;
            if (!NavReady)
            {
                return target;
            }
            if (!NavMesh.Raycast(transform.position, target, out NavMeshHit hit, NavMesh.AllAreas))
            {
                return target;
            }
            if (hit.distance > dist * 0.5f)
            {
                return hit.position;
            }
        }
        return null;
    }

    bool ZipIncoming()
    {
        Grappling g = director != null ? director.Grapple : null;
        if (g == null || !g.IsZipping || !ReferenceEquals(g.ZipTarget, this) || !CanDodge)
        {
            return false;
        }
        return Vector3.Distance(player.Center, ChestPosition) < zipDodgeDistance;
    }

    // only once it's cracked, more likely the more cracked it is
    bool PogoIncoming()
    {
        if (!Hurt || !CanDodge || Time.time < nextPogoRoll)
        {
            return false;
        }
        if (player.IsGrounded || player.Velocity.y > -minFallSpeed)
        {
            return false;
        }

        Vector3 d = player.Feet - transform.position;
        float height = d.y;
        d.y = 0f;
        if (d.magnitude > 2.5f || height < 0.8f || height > 5f)
        {
            return false;
        }

        nextPogoRoll = Time.time + 0.6f;
        if (UnityEngine.Random.value > Mathf.Min(maxPogoDodgeChance, pogoDodgeChancePerCrack * Cracks))
        {
            return false;
        }
        nextPogoRoll = Time.time + pogoDodgeCooldown;
        return true;
    }

    void Swat()
    {
        if (swatted)
        {
            return;
        }
        swatted = true;
        HitPlayer(zipSwatDamage);
        EnemySounds.PlayAt(crackSound != null ? crackSound : EnemySounds.StoneCrack, ChestPosition, 1f, 0.45f);
        Emit(StillwalkerEvent.Swatted);
    }

    // ---------------------------------------------------------------- zip target

    public bool ZipTargetValid => !IsDead && isActiveAndEnabled && !zipDodging;
    public Vector3 ZipPoint => ChestPosition;
    public float ZipArrivalRadius => zipArrivalRadius;

    public bool OnZipArrive(FirstPersonCharacterController controller, Rigidbody body)
    {
        if (Brain != null && Brain.IsInState(recovering))
        {
            // stuck after a launch: you bounce off and end up right over it, ready to pogo
            Vector3 v = body.linearVelocity;
            body.linearVelocity = new Vector3(v.x, 0f, v.z) * 0.15f + Vector3.up * zipBounceUp;
            controller.SuppressJumpHold();
            IgnorePlayerFor(0.3f);

            EnemySounds.PlayAt(EnemySounds.StoneStep, ChestPosition, 1f, 0.6f);
            JuiceFX fx = JuiceFX.Get();
            if (fx != null)
            {
                fx.AirPuff(HeadPosition, Vector3.up, 0.7f);
            }
            if (CameraShaker.Instance != null)
            {
                CameraShaker.Instance.Impact(0.3f, new Vector3(0f, -0.05f, 0f), new Vector3(6f, 0f, 0f), 6f);
            }
            return true;
        }

        // asleep or boxed in, so it couldn't dodge
        swatted = false;
        Swat();
        if (Brain != null && Brain.IsInState(dormant))
        {
            Brain.Request(hunting);
        }
        return true;
    }

    // ---------------------------------------------------------------- launch

    Vector3 PredictedStrike()
    {
        float t = Mathf.Clamp(DistToPlayer() / launchSpeed, 0.25f, 0.9f);
        Vector3 p = player.Predict(t) - Vector3.up * player.FeetOffset;
        if (Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore)
            && !IsOwnCollider(hit.collider) && hit.rigidbody == null)
        {
            p = hit.point;
        }
        return p;
    }

    void StartFlight()
    {
        flightFrom = transform.position;
        flightTo = LaunchTarget;

        Vector3 d = flightTo - flightFrom;
        if (d.magnitude > launchRange * 1.2f)
        {
            flightTo = flightFrom + d.normalized * launchRange * 1.2f;
        }

        ClampFlightToGeometry();
        flightTime = Mathf.Clamp(Vector3.Distance(flightFrom, flightTo) / launchSpeed, 0.25f, 0.9f);
        if (nav != null)
        {
            nav.enabled = false;
        }

        AnimTrigger("Lunge");
        eyeGlow = 2f;
        ShowMarker(flightTo, 1f);
        EnemySounds.PlayAt(stepSound != null ? stepSound : EnemySounds.StoneStep, transform.position, 1f, 0.5f);
    }

    void TickFlight(float dt)
    {
        float t = Mathf.Clamp01(flight.TimeInState / flightTime);
        Vector3 p = Vector3.Lerp(flightFrom, flightTo, t);
        p.y += Mathf.Sin(t * Mathf.PI) * flightArc;
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
        if (IsDead)
        {
            return;
        }

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
        if (fx != null)
        {
            fx.LandDust(transform.position, Vector3.up, 1f);
        }
        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTraumaAtPoint(transform.position, 0.6f, 5f, 25f);
        }
    }

    // Puts it back on the NavMesh after a launch or a teleport. Finds the mesh before turning
    // the agent on: switching it on (or warping) off the mesh fails and leaves it stuck for good.
    void SnapToGround()
    {
        if (nav == null)
        {
            return;
        }

        Vector3 onMesh;
        if (FindNavMesh(transform.position, out Vector3 found))
        {
            onMesh = found;
        }
        else if (hasNavPos)
        {
            onMesh = lastNavPos;
        }
        else
        {
            return;
        }

        nav.enabled = false;
        transform.position = onMesh;
        nav.enabled = true;
        if (nav.isOnNavMesh)
        {
            nav.Warp(onMesh);
        }
    }

    static readonly float[] snapRadii = { 2f, 6f, 15f, 30f };

    static bool FindNavMesh(Vector3 near, out Vector3 onMesh)
    {
        foreach (float r in snapRadii)
        {
            if (NavMesh.SamplePosition(near, out NavMeshHit hit, r, NavMesh.AllAreas))
            {
                onMesh = hit.position;
                return true;
            }
        }
        onMesh = near;
        return false;
    }

    public bool OnNavMesh => NavReady;

    // The flight is a scripted arc, not physics, so without this it would pass straight
    // through a wall if you went round a corner during the windup. Sweeps the body along
    // the path, stops short of the first wall, and flattens the arc under a low ceiling.
    const float FlightBodyRadius = 0.4f;
    static readonly RaycastHit[] flightHits = new RaycastHit[16];

    void ClampFlightToGeometry()
    {
        flightArc = launchArc;
        Vector3 lift = Vector3.up * (eyeHeight * 0.6f);
        Vector3 start = flightFrom + lift;
        Vector3 path = flightTo + lift - start;
        float length = path.magnitude;
        if (length < 0.01f)
        {
            return;
        }

        if (FirstFlightBlocker(start, path / length, length, out RaycastHit wall))
        {
            Vector3 stop = flightFrom + path / length * Mathf.Max(0f, wall.distance - FlightBodyRadius);
            if (Physics.Raycast(stop + Vector3.up * 1.5f, Vector3.down, out RaycastHit floor, 6f, ~0, QueryTriggerInteraction.Ignore)
                && !IsOwnCollider(floor.collider))
            {
                stop.y = floor.point.y;
            }
            flightTo = stop;
        }

        // low ceiling over the middle of the jump: keep the arc under it
        Vector3 mid = Vector3.Lerp(flightFrom, flightTo, 0.5f) + lift;
        if (FirstFlightBlocker(mid, Vector3.up, launchArc + 0.5f, out RaycastHit ceiling))
        {
            flightArc = Mathf.Max(0f, ceiling.distance - 0.5f);
        }
    }

    bool FirstFlightBlocker(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        float bestDistance = float.MaxValue;
        int count = Physics.SphereCastNonAlloc(origin, FlightBodyRadius, dir, flightHits, distance, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = flightHits[i];
            // the player, shards, props and other enemies don't stop a launch
            if (h.collider == null || IsOwnCollider(h.collider) || h.collider.attachedRigidbody != null)
            {
                continue;
            }
            if (h.collider.GetComponentInParent<EnemyAgent>() != null)
            {
                continue;
            }
            // starting overlaps report distance 0 with no point, skip them
            if (h.distance <= 0f)
            {
                continue;
            }
            if (h.distance < bestDistance)
            {
                bestDistance = h.distance;
                best = h;
            }
        }
        return bestDistance < float.MaxValue;
    }

    bool IsOwnCollider(Collider c)
    {
        foreach (Collider o in ownColliders)
        {
            if (o == c)
            {
                return true;
            }
        }
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

    // ---------------------------------------------------------------- hitting the player

    bool TouchingPlayer()
    {
        Vector3 p = player.Center;
        Vector3 me = transform.position;
        Vector3 flat = new Vector3(p.x - me.x, 0f, p.z - me.z);
        if (flat.magnitude > contactRadius + 0.45f)
        {
            return false;
        }

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
        {
            director.PlayerHealth.Damage(amount);
        }

        if (real && director.PlayerBody != null)
        {
            Vector3 away = Flat(player.Center - transform.position);
            director.PlayerBody.linearVelocity = away * knockback + Vector3.up * knockUp;
            if (director.Controller != null)
            {
                director.Controller.SuppressJumpHold();
            }
        }

        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(ChestPosition, (player.Center - ChestPosition).normalized, 1f);
        }
        if (real)
        {
            ImpactFrames.Hit(ChestPosition, 0.8f);
            if (CameraShaker.Instance != null)
            {
                CameraShaker.Instance.AddTrauma(0.6f);
            }
        }
    }

    // ---------------------------------------------------------------- cracks

    public override void TakeHit(EnemyHit hit)
    {
        if (IsDead)
        {
            return;
        }

        bool falling = !player.IsGrounded && player.Velocity.y < -minFallSpeed;
        bool counts = hit.kind == HitKind.Thrown || (hit.kind == HitKind.Melee && falling);
        if (!counts)
        {
            JuiceFX fx = JuiceFX.Get();
            if (fx != null)
            {
                fx.Scuff(hit.point, -hit.direction, Vector3.up, 0.5f);
            }
            EnemySounds.PlayAt(EnemySounds.Ricochet, hit.point, 0.5f, 0.6f);
            return;
        }

        Cracks++;
        lastCrackTime = Time.time;
        crackShake = 1f;
        EnemySounds.PlayAt(crackSound != null ? crackSound : EnemySounds.StoneCrack, hit.point, 1f, 1f + Cracks * 0.12f);
        JuiceFX juice = JuiceFX.Get();
        if (juice != null)
        {
            juice.ImpactBurst(hit.point, -hit.direction, 0.5f + Cracks * 0.12f);
        }
        if (Cracks < cracksToShatter)
        {
            crackFX.AddCrack(hit.point, Cracks);
        }
        Emit(StillwalkerEvent.Cracked);

        if (Cracks >= cracksToShatter)
        {
            Die(hit);
            return;
        }

        // jerks away from the hit, even while you're looking at it (not while stuck after a launch though)
        if (CanDodge)
        {
            Vector3 push = new Vector3(hit.direction.x, 0f, hit.direction.z);
            Vector3 away = transform.position - player.Feet;
            away.y = 0f;
            Dodge(DodgeKind.Flinch, push.sqrMagnitude > 0.04f ? push : away);
        }
    }

    protected override bool UseRagdollOnDeath => false;

    protected override void OnDeath(EnemyHit hit)
    {
        Emit(StillwalkerEvent.Shattered);
        Brain.Request(shattered);
        crackFX.Clear();
        marker.enabled = false;
        SpawnPieces(hit);

        // drop a stuck axe instead of leaving it floating where the body was
        ThrownAxe.DropAllStuckIn(transform);

        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            r.enabled = false;
        }
        foreach (Collider c in ownColliders)
        {
            if (c != null)
            {
                c.enabled = false;
            }
        }
        if (nav != null)
        {
            nav.enabled = false;
        }
    }

    void SpawnPieces(EnemyHit hit)
    {
        Renderer bodyRenderer = null;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
        {
            if (r.name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) < 0 && !(r is LineRenderer)) { bodyRenderer = r; break; }
        }
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

        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTrauma(0.7f);
        }
    }

    void Emit(StillwalkerEvent e)
    {
        Events?.Invoke(e);
        if (Policy != null)
        {
            Policy.OnStillwalkerEvent(e);
        }
    }
}
