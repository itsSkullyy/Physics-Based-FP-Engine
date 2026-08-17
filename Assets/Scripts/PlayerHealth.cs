using System;
using UnityEngine;
using UnityEngine.InputSystem;

// Player health. Put this on the Player root (same object as the Rigidbody).
//
// There is nothing in the game that hurts you yet, so this is deliberately a plain
// container with a clean API to hook damage into later:
//
//   health.Damage(12f);
//   health.Heal(25f);
//   health.Kill();
//   health.Damaged += amount => ...;
//   health.Died    += () => ...;
//
// The Deathplane routes through Kill() so death always takes one code path and the HUD
// only ever has one thing to read.
[DefaultExecutionOrder(-200)]
public class PlayerHealth : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;
    public bool invulnerable = false;

    [Header("Regen")]
    public bool regenerate = true;
    [Tooltip("Seconds without taking damage before health starts coming back.")]
    public float regenDelay = 5f;
    public float regenPerSecond = 12f;

    [Header("Respawn")]
    public Transform respawnPoint;
    public bool respawnOnDeath = true;

    [Header("Feedback")]
    public float damageShake = 0.35f;
    public float deathShake = 0.8f;

    [Header("Debug")]
    [Tooltip("K takes 10 damage, L heals 10, J kills. Handy for checking the HUD.")]
    public bool debugKeys = false;

    public float Current { get; private set; }
    public float Normalized => maxHealth > 0.01f ? Mathf.Clamp01(Current / maxHealth) : 0f;
    public bool IsDead { get; private set; }

    /// Fires with the amount actually taken off.
    public event Action<float> Damaged;
    public event Action<float> Healed;
    public event Action Died;
    public event Action Respawned;

    Rigidbody body;
    float regenTimer;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        if (body == null) body = GetComponentInChildren<Rigidbody>();

        Current = maxHealth;
    }

    void Update()
    {
        if (debugKeys) HandleDebugKeys();

        if (!regenerate || IsDead || Current >= maxHealth) return;

        regenTimer += Time.deltaTime;
        if (regenTimer < regenDelay) return;

        Current = Mathf.Min(maxHealth, Current + regenPerSecond * Time.deltaTime);
    }

    void HandleDebugKeys()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.kKey.wasPressedThisFrame) Damage(10f);
        if (kb.lKey.wasPressedThisFrame) Heal(10f);
        if (kb.jKey.wasPressedThisFrame) Kill();
    }

    // ---------------------------------------------------------------- API

    public void Damage(float amount)
    {
        if (IsDead || invulnerable || amount <= 0f) return;

        float taken = Mathf.Min(amount, Current);
        Current -= taken;
        regenTimer = 0f;

        if (CameraShaker.Instance != null && damageShake > 0f)
        {
            float t = Mathf.Clamp01(taken / Mathf.Max(1f, maxHealth * 0.35f));
            CameraShaker.Instance.AddTrauma(damageShake * t);
        }

        Damaged?.Invoke(taken);

        if (Current <= 0.001f) Die();
    }

    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f) return;

        float given = Mathf.Min(amount, maxHealth - Current);
        if (given <= 0f) return;

        Current += given;
        Healed?.Invoke(given);
    }

    public void Kill()
    {
        if (IsDead) return;

        Current = 0f;
        Die();
    }

    void Die()
    {
        IsDead = true;

        if (CameraShaker.Instance != null && deathShake > 0f)
            CameraShaker.Instance.AddTrauma(deathShake);

        Died?.Invoke();

        if (respawnOnDeath) Respawn();
    }

    /// Full reset: back to max health and back to the respawn point.
    public void Respawn()
    {
        IsDead = false;
        Current = maxHealth;
        regenTimer = 0f;

        if (respawnPoint != null) Teleport(respawnPoint.position);
        else Debug.LogWarning("PlayerHealth has no respawnPoint, so the player was left where they died.", this);

        Respawned?.Invoke();
    }

    /// Moves the player without touching their health. Used by the deathplane when it is
    /// set to hurt rather than kill.
    public void Teleport(Vector3 position)
    {
        if (body != null)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = position;
        }

        transform.position = position;
        Physics.SyncTransforms();
    }
}
