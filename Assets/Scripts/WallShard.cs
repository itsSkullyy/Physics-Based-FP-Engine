using UnityEngine;

// One fragment of a shattered wall. Cheap by design: a shared mesh, no per-frame script
// cost once it settles, and it removes itself so the scene never fills with debris.
// Created and configured by BreakableWall at shatter time.
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(MeshRenderer))]
public class WallShard : MonoBehaviour
{
    Rigidbody rb;
    MeshRenderer rend;

    float life;
    float fadeTime;
    float age;
    bool settled;
    float settleCheckTimer;

    Vector3 startScale;

    public void Init(Mesh mesh, Material material, Vector3 worldPos, Quaternion worldRot,
                     Vector3 worldScale, Vector3 impulse, Vector3 torque,
                     float lifetime, float fadeSeconds, PhysicsMaterial physMat)
    {
        rb = GetComponent<Rigidbody>();
        rend = GetComponent<MeshRenderer>();

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null)
        {
            mf = gameObject.AddComponent<MeshFilter>();
        }
        mf.sharedMesh = mesh;
        rend.sharedMaterial = material;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        transform.SetPositionAndRotation(worldPos, worldRot);
        transform.localScale = worldScale;
        startScale = worldScale;

        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc == null)
        {
            mc = gameObject.AddComponent<MeshCollider>();
        }
        mc.sharedMesh = mesh;
        mc.convex = true;                     // required for a non-kinematic rigidbody
        if (physMat != null)
        {
            mc.material = physMat;
        }

        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        rb.AddForce(impulse, ForceMode.VelocityChange);
        rb.AddTorque(torque, ForceMode.VelocityChange);

        life = lifetime;
        fadeTime = Mathf.Max(0.01f, fadeSeconds);
        age = 0f;
        settled = false;
    }

    void Update()
    {
        age += Time.deltaTime;
        if (ShrinkOut())
        {
            return;
        }
        CheckSettled(Time.deltaTime);
    }

    // Shrinks out instead of popping - opaque cel materials can't fade by alpha.
    // True once it's gone.
    bool ShrinkOut()
    {
        float remaining = life - age;
        if (remaining > fadeTime)
        {
            return false;
        }

        float t = Mathf.Clamp01(remaining / fadeTime);
        transform.localScale = startScale * t;
        if (t > 0f)
        {
            return false;
        }

        Destroy(gameObject);
        return true;
    }

    // puts the rigidbody to sleep once it stops moving so settled shards cost nothing
    void CheckSettled(float dt)
    {
        if (settled)
        {
            return;
        }

        settleCheckTimer -= dt;
        if (settleCheckTimer > 0f)
        {
            return;
        }
        settleCheckTimer = 0.25f;

        if (rb != null && rb.linearVelocity.sqrMagnitude < 0.04f && rb.angularVelocity.sqrMagnitude < 0.04f)
        {
            rb.Sleep();
            settled = true;
        }
    }
}