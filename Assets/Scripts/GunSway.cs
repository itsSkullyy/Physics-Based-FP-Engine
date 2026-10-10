using UnityEngine;

// Weapon sway and bob. Attach to GunHolder under CameraFX.
public class GunSway : MonoBehaviour
{
    public FirstPersonCharacterController controller;
    public PlayerInputRouter input;

    [Header("Look Sway")]
    public float swayAmount = 0.008f;
    public float maxSway = 0.05f;
    public float rotSwayAmount = 2.5f;
    public float maxRotSway = 8f;
    public float swaySmooth = 10f;

    [Header("Movement Bob")]
    public float bobFrequency = 9f;
    public float bobAmount = 0.012f;
    public float bobSideAmount = 0.008f;

    [Header("Jump / Fall Kick")]
    public float verticalKick = 0.006f;
    public float maxVerticalKick = 0.08f;

    Vector3 basePos;
    Quaternion baseRot;
    float bobTimer;

    void Awake()
    {
        if (controller == null)
        {
            controller = GetComponentInParent<FirstPersonCharacterController>();
        }
        if (input == null)
        {
            input = PlayerInputRouter.Resolve(this);
        }
    }

    void Start()
    {
        basePos = transform.localPosition;
        baseRot = transform.localRotation;
    }

    void Update()
    {
        Vector2 look = input != null ? input.LookDelta : Vector2.zero;

        Vector3 swayPos = LookSwayOffset(look);
        Quaternion swayRot = LookSwayRotation(look);
        Vector3 bobPos = WalkBob() + FallKick();

        float t = 1f - Mathf.Exp(-swaySmooth * Time.deltaTime);
        transform.localPosition = Vector3.Lerp(transform.localPosition, basePos + swayPos + bobPos, t);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, baseRot * swayRot, t);
    }

    // the gun lags behind where you're looking
    Vector3 LookSwayOffset(Vector2 look)
    {
        return new Vector3(
            Mathf.Clamp(-look.x * swayAmount, -maxSway, maxSway),
            Mathf.Clamp(-look.y * swayAmount, -maxSway, maxSway),
            0f);
    }

    Quaternion LookSwayRotation(Vector2 look)
    {
        return Quaternion.Euler(
            Mathf.Clamp(look.y * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount * 0.5f, -maxRotSway, maxRotSway));
    }

    // figure-eight bob while running on the ground, faster and bigger the faster you go
    Vector3 WalkBob()
    {
        bool running = controller != null && controller.IsGrounded && !controller.IsSliding
                       && controller.CurrentSpeed > 1f;
        if (!running)
        {
            bobTimer = 0f;
            return Vector3.zero;
        }

        float speedFactor = controller.CurrentSpeed / Mathf.Max(1f, controller.baseSpeed);
        bobTimer += Time.deltaTime * bobFrequency * Mathf.Min(speedFactor, 2f);
        return new Vector3(
            Mathf.Cos(bobTimer) * bobSideAmount * speedFactor,
            Mathf.Sin(bobTimer * 2f) * bobAmount * speedFactor,
            0f);
    }

    // gun drifts up while falling and down while rising
    Vector3 FallKick()
    {
        if (controller == null)
        {
            return Vector3.zero;
        }
        float vy = controller.Velocity.y;
        return new Vector3(0f, Mathf.Clamp(-vy * verticalKick, -maxVerticalKick, maxVerticalKick), 0f);
    }
}