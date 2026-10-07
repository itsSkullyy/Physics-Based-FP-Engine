using System.Collections.Generic;
using UnityEngine;

// Leader grenade. Red cube that beeps faster until it blows. Hit it with the axe to
// send it at the nearest Grunt.
[RequireComponent(typeof(Rigidbody))]
public class GruntGrenade : MonoBehaviour
{
    public struct Settings
    {
        public float fuse;
        public float radius;
        public float maxDamage;
        public float minDamage;
        public float knockback;
        public bool hurtsGrunts;
    }

    static Mesh cubeMesh;
    static PhysicsMaterial bouncy;

    public static Color BodyColor = new Color(0.9f, 0.08f, 0.06f);
    public static Color BlinkColor = new Color(1f, 0.95f, 0.85f);

    Settings settings;
    float fuseLeft;
    float beepTimer;
    float blinkLeft;
    bool batted;
    bool exploded;
    GruntBase thrower;

    Rigidbody rb;
    Material mat;
    Vector3 baseScale;

    public static GruntGrenade Spawn(Vector3 position, Vector3 velocity, GruntBase thrower, Settings settings)
    {
        if (cubeMesh == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
        }
        if (bouncy == null)
        {
            bouncy = new PhysicsMaterial("GrenadeBounce")
            {
                bounciness = 0.35f,
                dynamicFriction = 0.6f,
                staticFriction = 0.6f,
                bounceCombine = PhysicsMaterialCombine.Maximum
            };
        }

        GameObject go = new GameObject("Grenade");
        go.transform.position = position;
        go.transform.rotation = Random.rotation;
        go.transform.localScale = Vector3.one * 0.24f;

        go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.sharedMaterial = bouncy;

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.mass = 0.4f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        GruntGrenade g = go.AddComponent<GruntGrenade>();
        g.Init(velocity, thrower, settings, mr);
        return g;
    }

    void Init(Vector3 velocity, GruntBase owner, Settings s, MeshRenderer mr)
    {
        rb = GetComponent<Rigidbody>();
        settings = s;
        fuseLeft = s.fuse;
        thrower = owner;
        baseScale = transform.localScale;

        mat = EnemyVisuals.Unlit(BodyColor);
        mr.sharedMaterial = mat;

        rb.linearVelocity = velocity;
        rb.angularVelocity = Random.insideUnitSphere * 12f;

        if (owner != null)
        {
            Collider mine = GetComponent<Collider>();
            foreach (Collider c in owner.GetComponentsInChildren<Collider>())
                if (c != null) Physics.IgnoreCollision(mine, c);
        }
    }

    void Update()
    {
        if (exploded) return;

        fuseLeft -= Time.deltaTime;
        if (fuseLeft <= 0f)
        {
            Explode();
            return;
        }

        float t = Mathf.Clamp01(fuseLeft / Mathf.Max(0.01f, settings.fuse));
        beepTimer -= Time.deltaTime;
        if (beepTimer <= 0f)
        {
            beepTimer = Mathf.Lerp(0.06f, 0.45f, t);
            blinkLeft = Mathf.Min(0.05f, beepTimer * 0.5f);
            EnemySounds.PlayAt(EnemySounds.Beep, transform.position, 1f, Mathf.Lerp(1.35f, 1f, t), 25f);
        }

        blinkLeft -= Time.deltaTime;
        bool lit = blinkLeft > 0f;
        EnemyVisuals.SetColor(mat, lit ? BlinkColor : BodyColor);
        transform.localScale = baseScale * (lit ? 1.25f : 1f);
    }

    // ---------------------------------------------------------------- batting it back

    // Sent by BattleAxe via SendMessageUpwards when a swing connects.
    void OnAxeHit(Vector3 point)
    {
        if (exploded) return;

        AIDirector d = AIDirector.Instance;
        PlayerMotionTracker p = d != null ? d.Player : null;
        Vector3 camPos = p != null ? p.CameraPosition : transform.position;
        Vector3 camFwd = p != null ? p.CameraForward : transform.forward;

        GruntBase target = NearestGrunt(camPos, camFwd);
        if (target != null)
        {
            Vector3 aim = target.ChestPosition;
            float dist = Vector3.Distance(transform.position, aim);
            float flight = Mathf.Clamp(dist / 22f, 0.3f, 1.1f);
            rb.linearVelocity = (aim - transform.position) / flight - 0.5f * Physics.gravity * flight;
            fuseLeft = flight + 0.05f;
            if (d != null) d.Say(target, "INCOMING!", "incoming" + target.GetInstanceID());
        }
        else
        {
            rb.linearVelocity = camFwd * 22f + Vector3.up * 4f;
            fuseLeft = Mathf.Max(fuseLeft, 0.8f);
        }

        batted = true;
        beepTimer = 0f;
        rb.angularVelocity = Random.insideUnitSphere * 25f;

        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(point, -camFwd, 0.7f);
            fx.Hitstop(0.06f);
        }
        if (CameraShaker.Instance != null) CameraShaker.Instance.AddTrauma(0.25f);
        EnemySounds.PlayAt(EnemySounds.Ricochet, transform.position, 1f, 0.8f);
    }

    // nearest grunt, favouring ones in front of the camera
    static GruntBase NearestGrunt(Vector3 camPos, Vector3 camFwd)
    {
        AIDirector d = AIDirector.Instance;
        if (d == null) return null;

        GruntBase best = null;
        float bestScore = float.MaxValue;
        foreach (EnemyAgent e in d.Enemies)
        {
            if (!(e is GruntBase g) || g.IsDead) continue;
            Vector3 to = g.ChestPosition - camPos;
            float dist = to.magnitude;
            if (dist > 45f) continue;

            float facing = Vector3.Dot(camFwd, to / Mathf.Max(0.01f, dist));
            float score = dist * (1f + (1f - facing) * 1.5f);
            if (score < bestScore)
            {
                bestScore = score;
                best = g;
            }
        }
        return best;
    }

    void OnCollisionEnter(Collision c)
    {
        float hit = c.relativeVelocity.magnitude;
        if (hit > 1.5f)
            EnemySounds.PlayAt(EnemySounds.GrenadeBounce, transform.position, Mathf.Clamp01(hit / 10f), Random.Range(0.9f, 1.1f), 25f);

        if (batted && c.collider.GetComponentInParent<GruntBase>() != null) Explode();
    }

    // ---------------------------------------------------------------- boom

    void Explode()
    {
        if (exploded) return;
        exploded = true;

        Vector3 pos = transform.position;
        float r = settings.radius;
        AIDirector d = AIDirector.Instance;

        ExplosionFX.Get().Explode(pos, r);

        if (CameraShaker.Instance != null) CameraShaker.Instance.AddTraumaAtPoint(pos, 0.9f, r, r * 6f);

        // Player
        if (d != null && d.Player != null && d.PlayerHealth != null)
        {
            Vector3 to = d.Player.Center - pos;
            float dist = to.magnitude;
            if (dist <= r * 2f) ImpactFrames.Hit(pos, Mathf.Lerp(1f, 0.4f, dist / (r * 2f)));
            if (dist <= r && Clear(pos + Vector3.up * 0.3f, d.Player.Center))
            {
                float k = 1f - dist / r;
                d.PlayerHealth.Damage(Mathf.Lerp(settings.minDamage, settings.maxDamage, k));
                if (d.PlayerBody != null)
                {
                    Vector3 dir = to.sqrMagnitude > 0.01f ? to.normalized : Vector3.up;
                    d.PlayerBody.linearVelocity += dir * settings.knockback * k + Vector3.up * 6f * k;
                    if (d.Controller != null) d.Controller.SuppressJumpHold();
                }
                JuiceFX fx = JuiceFX.Get();
                if (fx != null) fx.Hitstop(0.08f);
            }
        }

        // Enemies, double hit near the middle
        if (d != null && settings.hurtsGrunts)
        {
            foreach (EnemyAgent e in d.Enemies.ToArrayCopy())
            {
                if (e == null || e.IsDead) continue;
                float dist = Vector3.Distance(pos, e.ChestPosition);
                if (dist > r * 0.8f || !Clear(pos + Vector3.up * 0.3f, e.ChestPosition)) continue;

                Vector3 dir = (e.ChestPosition - pos).normalized;
                EnemyHit hit = new EnemyHit { kind = HitKind.Other, point = e.ChestPosition, direction = dir, force = 10f };
                e.TakeHit(hit);
                if (dist < r * 0.4f) e.TakeHit(hit);
            }
        }

        // Breakable walls and loose physics.
        HashSet<BreakableWall> walls = new HashSet<BreakableWall>();
        foreach (Collider c in Physics.OverlapSphere(pos, r, ~0, QueryTriggerInteraction.Ignore))
        {
            BreakableWall w = c.GetComponentInParent<BreakableWall>();
            if (w != null && walls.Add(w)) w.SendMessage("OnAxeHit", pos, SendMessageOptions.DontRequireReceiver);

            Rigidbody body = c.attachedRigidbody;
            if (body != null && !body.isKinematic && (d == null || body != d.PlayerBody))
                body.AddExplosionForce(settings.knockback * 2f, pos, r, 1f, ForceMode.VelocityChange);
        }

        Destroy(gameObject);
    }

    static bool Clear(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float len = dir.magnitude;
        if (len < 0.01f) return true;
        foreach (RaycastHit h in Physics.RaycastAll(from, dir / len, len, ~0, QueryTriggerInteraction.Ignore))
            if (h.rigidbody == null) return false;
        return true;
    }
}

static class EnemyListExtensions
{
    public static List<EnemyAgent> ToArrayCopy(this IReadOnlyList<EnemyAgent> list) => new List<EnemyAgent>(list);
}
