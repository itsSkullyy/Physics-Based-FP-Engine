using UnityEngine;

// One fragment of a shattered wall. Kept deliberately cheap: a shared mesh, no per-frame
// script cost once it settles, and it removes itself so the scene never fills with debris.
//
// You do not add this by hand - BreakableWall creates and configures them at shatter time.
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

    // The thrown axe sticks to whatever its sweep mask includes. Putting shards on that
    // mask (done by BreakableWall) is what lets the axe embed in a flying piece.
    public void Init(Mesh mesh, Material material, Vector3 worldPos, Quaternion worldRot,
                     Vector3 worldScale, Vector3 impulse, Vector3 torque,
                     float lifetime, float fadeSeconds, PhysicsMaterial physMat)
    {
        rb = GetComponent<Rigidbody>();
        rend = GetComponent<MeshRenderer>();

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        rend.sharedMaterial = material;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        transform.SetPositionAndRotation(worldPos, worldRot);
        transform.localScale = worldScale;
        startScale = worldScale;

        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = true;                     // required for a non-kinematic rigidbody
        if (physMat != null) mc.material = physMat;

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

        float remaining = life - age;
        if (remaining <= fadeTime)
        {
            // Sink and shrink out instead of popping. Cheaper than material alpha and
            // it works on opaque cel materials that cannot blend.
            float t = Mathf.Clamp01(remaining / fadeTime);
            transform.localScale = startScale * t;

            if (t <= 0f)
            {
                Destroy(gameObject);
                return;
            }
        }

        // Once a shard stops moving, freeze it so dozens of them cost nothing.
        if (!settled)
        {
            settleCheckTimer -= Time.deltaTime;
            if (settleCheckTimer <= 0f)
            {
                settleCheckTimer = 0.25f;
                if (rb != null && rb.linearVelocity.sqrMagnitude < 0.04f &&
                    rb.angularVelocity.sqrMagnitude < 0.04f)
                {
                    rb.Sleep();
                    settled = true;
                }
            }
        }
    }
}