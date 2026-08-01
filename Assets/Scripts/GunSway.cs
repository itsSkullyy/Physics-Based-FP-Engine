using UnityEngine;
using UnityEngine.InputSystem;

// Weapon sway and bob. Attach to GunHolder under CameraFX.
public class GunSway : MonoBehaviour
{
    public FirstPersonCharacterController controller;

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

    void Start()
    {
        basePos = transform.localPosition;
        baseRot = transform.localRotation;
    }

    void Update()
    {
        Vector2 look = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;

        Vector3 swayPos = new Vector3(
            Mathf.Clamp(-look.x * swayAmount, -maxSway, maxSway),
            Mathf.Clamp(-look.y * swayAmount, -maxSway, maxSway),
            0f);

        Quaternion swayRot = Quaternion.Euler(
            Mathf.Clamp(look.y * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount, -maxRotSway, maxRotSway),
            Mathf.Clamp(-look.x * rotSwayAmount * 0.5f, -maxRotSway, maxRotSway));

        Vector3 bobPos = Vector3.zero;
        if (controller != null && controller.IsGrounded && !controller.IsSliding &&
            controller.CurrentSpeed > 1f)
        {
            float speedFactor = controller.CurrentSpeed / Mathf.Max(1f, controller.baseSpeed);
            bobTimer += Time.deltaTime * bobFrequency * Mathf.Min(speedFactor, 2f);
            bobPos.y = Mathf.Sin(bobTimer * 2f) * bobAmount * speedFactor;
            bobPos.x = Mathf.Cos(bobTimer) * bobSideAmount * speedFactor;
        }
        else
        {
            bobTimer = 0f;
        }

        if (controller != null)
        {
            float vy = controller.Velocity.y;
            bobPos.y += Mathf.Clamp(-vy * verticalKick, -maxVerticalKick, maxVerticalKick);
        }

        float t = 1f - Mathf.Exp(-swaySmooth * Time.deltaTime);
        transform.localPosition = Vector3.Lerp(transform.localPosition, basePos + swayPos + bobPos, t);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, baseRot * swayRot, t);
    }
}