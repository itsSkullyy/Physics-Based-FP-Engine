using UnityEngine;

// Grunt tracer. Slow enough to dodge, axe swing deflects it. Sphere sweeps instead of a
// collider so it can't tunnel.
public class GruntBullet : MonoBehaviour
{
    public static Color NormalColor = new Color(1f, 0.85f, 0.3f, 1f);
    public static Color DeflectedColor = new Color(0.4f, 0.9f, 1f, 1f);

    static Mesh sharedMesh;
    static Material normalMat;
    static Material deflectedMat;

    Vector3 velocity;
    float damage;
    float life = 3f;
    float radius = 0.09f;
    bool deflected;
    GruntBase owner;

    float deflectRange;
    float deflectAngle;

    MeshRenderer body;
    TrailRenderer trail;

    public static GruntBullet Spawn(Vector3 position, Vector3 direction, float speed, float damage,
                                    GruntBase owner, float deflectRange, float deflectAngle)
    {
        GameObject go = new GameObject("GruntBullet");
        go.transform.position = position;
        go.transform.rotation = Quaternion.LookRotation(direction);

        GruntBullet b = go.AddComponent<GruntBullet>();
        b.velocity = direction.normalized * speed;
        b.damage = damage;
        b.owner = owner;
        b.deflectRange = deflectRange;
        b.deflectAngle = deflectAngle;
        b.BuildVisual();
        return b;
    }

    void BuildVisual()
    {
        if (sharedMesh == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sharedMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
        }
        if (normalMat == null)
        {
            normalMat = EnemyVisuals.Unlit(NormalColor);
        }
        if (deflectedMat == null)
        {
            deflectedMat = EnemyVisuals.Unlit(DeflectedColor);
        }

        GameObject vis = new GameObject("Visual");
        vis.transform.SetParent(transform, false);
        vis.transform.localScale = new Vector3(0.09f, 0.09f, 0.55f);
        vis.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
        body = vis.AddComponent<MeshRenderer>();
        body.sharedMaterial = normalMat;
        body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        trail = gameObject.AddComponent<TrailRenderer>();
        trail.time = 0.09f;
        trail.startWidth = 0.07f;
        trail.endWidth = 0f;
        trail.material = normalMat;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.minVertexDistance = 0.1f;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
        {
            return;
        }

        life -= dt;
        if (life <= 0f)
        {
            Destroy(gameObject);
            return;
        }

        if (!deflected)
        {
            TryDeflect();
        }
        Fly(dt);
    }

    // sweeps a sphere along this frame's movement instead of using a collider, so it can't tunnel
    void Fly(float dt)
    {
        Vector3 pos = transform.position;
        Vector3 step = velocity * dt;
        float len = step.magnitude;

        if (len > 0.0001f && Sweep(pos, step / len, len, out RaycastHit hit))
        {
            HandleHit(hit);
            return;
        }

        if (!deflected && !whizzed)
        {
            Whiz(pos, step);
        }

        transform.position = pos + step;
        transform.rotation = Quaternion.LookRotation(velocity);
    }

    bool whizzed;

    void Whiz(Vector3 from, Vector3 step)
    {
        AIDirector d = AIDirector.Instance;
        if (d == null || d.Player == null)
        {
            return;
        }

        Vector3 head = d.Player.CameraPosition;
        float len2 = step.sqrMagnitude;
        float t = len2 > 0f ? Mathf.Clamp01(Vector3.Dot(head - from, step) / len2) : 0f;
        Vector3 closest = from + step * t;
        if ((closest - head).sqrMagnitude > 2.5f * 2.5f || t >= 1f && Vector3.Dot(head - from, step) > len2)
        {
            return;
        }

        whizzed = true;
        EnemySounds.PlayAt(EnemySounds.BulletWhiz, closest, 1f, Random.Range(0.9f, 1.15f), 12f);
    }

    bool Sweep(Vector3 from, Vector3 dir, float dist, out RaycastHit best)
    {
        best = default;
        RaycastHit[] hits = Physics.SphereCastAll(from, radius, dir, dist, ~0, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (h.collider == null)
            {
                continue;
            }

            EnemyAgent enemy = h.collider.GetComponentInParent<EnemyAgent>();
            if (enemy != null)
            {
                // no friendly fire unless deflected
                if (!deflected || enemy.IsDead)
                {
                    continue;
                }
            }

            if (h.distance < bestDist)
            {
                bestDist = h.distance;
                best = h;
                found = true;
            }
        }
        return found;
    }

    void HandleHit(RaycastHit hit)
    {
        AIDirector d = AIDirector.Instance;
        Vector3 point = hit.distance > 0f ? hit.point : transform.position;
        Vector3 normal = hit.normal.sqrMagnitude > 0.001f ? hit.normal : -velocity.normalized;

        if (d != null && d.PlayerBody != null && hit.rigidbody == d.PlayerBody)
        {
            if (!deflected && d.PlayerHealth != null)
            {
                d.PlayerHealth.Damage(damage);
                if (CameraShaker.Instance != null)
                {
                    CameraShaker.Instance.AddKick(Vector3.zero, new Vector3(-2.5f, Random.Range(-2f, 2f), 0f));
                }
            }
            Destroy(gameObject);
            return;
        }

        EnemyAgent enemy = hit.collider.GetComponentInParent<EnemyAgent>();
        if (enemy != null && deflected)
        {
            enemy.TakeHit(new EnemyHit
            {
                kind = HitKind.Deflect,
                point = point,
                direction = velocity.normalized,
                force = enemy.deathImpulse * 1.2f
            });
            Destroy(gameObject);
            return;
        }

        JuiceFX fx = JuiceFX.Instance;
        if (fx != null)
        {
            fx.Scuff(point, normal, Vector3.Reflect(velocity.normalized, normal), 0.35f);
        }
        if (Random.value < 0.35f)
        {
            EnemySounds.PlayAt(EnemySounds.Ricochet, point, 0.6f, Random.Range(0.85f, 1.2f));
        }

        Destroy(gameObject);
    }

    void TryDeflect()
    {
        AIDirector d = AIDirector.Instance;
        if (d == null || d.Axe == null || d.Player == null)
        {
            return;
        }
        if (!d.Axe.IsSwinging || d.Player.IsDead)
        {
            return;
        }

        Vector3 cam = d.Player.CameraPosition;
        Vector3 to = transform.position - cam;
        if (to.magnitude > deflectRange)
        {
            return;
        }
        if (Vector3.Angle(d.Player.CameraForward, to) > deflectAngle)
        {
            return;
        }

        deflected = true;

        Vector3 target = owner != null && !owner.IsDead
            ? owner.ChestPosition
            : cam + d.Player.CameraForward * 60f;
        velocity = (target - transform.position).normalized * velocity.magnitude * 1.6f;
        life = 3f;

        if (body != null)
        {
            body.sharedMaterial = deflectedMat;
        }
        if (trail != null)
        {
            trail.material = deflectedMat;
        }

        JuiceFX fx = JuiceFX.Get();
        if (fx != null)
        {
            fx.ImpactBurst(transform.position, -to.normalized, 0.55f);
            fx.Hitstop(0.05f);
        }
        if (CameraShaker.Instance != null)
        {
            CameraShaker.Instance.AddTrauma(0.2f);
        }
        EnemySounds.PlayAt(EnemySounds.Ricochet, transform.position, 1f, 1.2f);

        if (owner != null && !owner.IsDead && d != null)
        {
            d.Say(owner, "WHAT?!", "deflect");
        }
    }
}
