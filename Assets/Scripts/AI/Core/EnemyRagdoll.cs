using UnityEngine;
using UnityEngine.AI;

// Goes limp on death. Uses the bone rigidbodies if there's a ragdoll, otherwise the
// whole enemy tips over as one body.
public class EnemyRagdoll : MonoBehaviour
{
    [Tooltip("Mass of the whole body when there's no bone ragdoll.")]
    public float fallbackMass = 60f;
    [Tooltip("Scales every death impulse.")]
    public float impulseScale = 1f;

    Rigidbody rootBody;
    Rigidbody[] bones = new Rigidbody[0];
    Collider rootCollider;
    Animator anim;
    NavMeshAgent nav;

    public bool HasBones => bones.Length > 0;
    public bool IsLimp { get; private set; }

    public Vector3 BodyCenter
    {
        get
        {
            if (IsLimp && HasBones && bones[0] != null) return bones[0].worldCenterOfMass;
            return rootBody != null ? rootBody.worldCenterOfMass : transform.position;
        }
    }

    void Awake()
    {
        Init();
    }

    public void Init()
    {
        rootBody = GetComponent<Rigidbody>();
        if (rootBody == null)
        {
            rootBody = gameObject.AddComponent<Rigidbody>();
        }
        rootBody.isKinematic = true;
        rootBody.interpolation = RigidbodyInterpolation.Interpolate;

        rootCollider = GetComponent<Collider>();
        anim = GetComponentInChildren<Animator>();
        nav = GetComponent<NavMeshAgent>();

        Rigidbody[] all = GetComponentsInChildren<Rigidbody>(true);
        int count = 0;
        foreach (Rigidbody rb in all) if (rb != rootBody) count++;

        bones = new Rigidbody[count];
        int i = 0;
        foreach (Rigidbody rb in all)
        {
            if (rb == rootBody) continue;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            bones[i++] = rb;
        }
    }

    // impulse is a velocity change
    public void Activate(Vector3 impulse, Vector3 point)
    {
        if (IsLimp) return;
        IsLimp = true;

        impulse *= impulseScale;

        if (nav != null && nav.enabled) nav.enabled = false;

        if (HasBones)
        {
            if (anim != null) anim.enabled = false;
            if (rootCollider != null) rootCollider.enabled = false;

            Rigidbody nearest = null;
            float best = float.MaxValue;
            foreach (Rigidbody b in bones)
            {
                b.isKinematic = false;
                b.linearVelocity = impulse * 0.35f;
                float d = (b.worldCenterOfMass - point).sqrMagnitude;
                if (d < best) { best = d; nearest = b; }
            }
            if (nearest != null) nearest.AddForce(impulse * 0.9f, ForceMode.VelocityChange);
        }
        else
        {
            if (anim != null) anim.enabled = false;
            rootBody.mass = fallbackMass;
            rootBody.isKinematic = false;
            rootBody.constraints = RigidbodyConstraints.None;
            rootBody.AddForce(impulse, ForceMode.VelocityChange);

            // off centre so it topples
            Vector3 lever = point - rootBody.worldCenterOfMass;
            rootBody.AddTorque(Vector3.Cross(lever, impulse) * 0.6f, ForceMode.VelocityChange);
        }
    }

    public void Restore(Vector3 position, Quaternion rotation)
    {
        IsLimp = false;

        foreach (Rigidbody b in bones)
        {
            if (!b.isKinematic)
            {
                b.linearVelocity = Vector3.zero;
                b.angularVelocity = Vector3.zero;
            }
            b.isKinematic = true;
        }

        if (!rootBody.isKinematic)
        {
            rootBody.linearVelocity = Vector3.zero;
            rootBody.angularVelocity = Vector3.zero;
        }
        rootBody.isKinematic = true;

        transform.SetPositionAndRotation(position, rotation);

        if (rootCollider != null) rootCollider.enabled = true;
        if (anim != null)
        {
            anim.enabled = true;
            anim.Rebind();
            anim.Update(0f);
        }

        if (nav != null)
        {
            nav.enabled = true;
            if (nav.isOnNavMesh) nav.Warp(position);
        }
    }
}
