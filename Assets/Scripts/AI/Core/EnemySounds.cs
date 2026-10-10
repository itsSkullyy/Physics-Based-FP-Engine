using UnityEngine;
using static Synth;

// Enemy sounds made with Synth. Enemies have clip fields that override these.
// Number after the seed is loudness in dB.
public static class EnemySounds
{
    public static AudioClip Gunshot => Make("Gunshot", 0.25f, 11, -5f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float blast = lp.Next(Noise(rnd), Mathf.Lerp(3000f, 700f, t / 0.15f)) * Env(t, 0.0005f, 32f);
            float body = o.Sine(Mathf.Lerp(110f, 55f, t / 0.12f)) * Env(t, 0.001f, 24f);
            return Sat(blast * 1.3f + body, 1.8f);
        };
    });

    public static AudioClip LaserCharge => Make("LaserCharge", 0.5f, 12, -14f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) => lp.Next(o.Saw(Mathf.Lerp(180f, 520f, t / 0.5f)), 1400f) * Mathf.Clamp01(t * 8f);
    });

    public static AudioClip StoneStep => Make("StoneStep", 0.16f, 13, -10f, () => (t, rnd) =>
    {
        float grind = Mathf.Sin(2f * Mathf.PI * 55f * t + Noise(rnd) * 1.5f);
        return grind * Mathf.Exp(-t * 18f);
    });

    public static AudioClip StoneCrack => Make("StoneCrack", 0.3f, 14, -4f, () =>
    {
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float snap = lp.Next(Noise(rnd), 2500f) * Mathf.Exp(-t * 55f);
            float ring = Mathf.Sin(2f * Mathf.PI * 210f * t) * Mathf.Exp(-t * 14f) * 0.4f;
            return snap + ring;
        };
    });

    public static AudioClip Beep => Make("Beep", 0.07f, 18, -10f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float env = Mathf.Clamp01(t * 200f) * Mathf.Clamp01((0.07f - t) * 120f);
            return lp.Next(o.Square(1000f), 2200f) * env;
        };
    });

    public static AudioClip Explosion => Make("Explosion", 1.4f, 19, 0f, () =>
    {
        LowPass lp = new LowPass();
        LowPass crackLp = new LowPass();
        Osc o = new Osc();
        return (t, rnd) =>
        {
            float noise = Noise(rnd);
            float crack = crackLp.Next(noise, 2500f) * Mathf.Exp(-t * 22f);
            float body = o.Sine(Mathf.Lerp(70f, 32f, Mathf.Clamp01(t / 0.6f))) * Mathf.Exp(-t * 3.2f);
            float rumble = lp.Next(noise, 280f) * 4f * Mathf.Exp(-t * 2.2f);
            return Sat(crack * 0.6f + body * 0.9f + rumble, 1.6f);
        };
    });

    public static AudioClip Ricochet => Make("Ricochet", 0.22f, 17, -15f, () =>
    {
        Osc o = new Osc();
        return (t, rnd) => o.Sine(Mathf.Lerp(1500f, 800f, t / 0.22f)) * Mathf.Exp(-t * 18f);
    });

    // ---------------------------------------------------------------- Grunts

    public static AudioClip GruntStep => Make("GruntStep", 0.13f, 20, -15f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        return (t, rnd) =>
        {
            float n = Noise(rnd);
            float boot = o.Sine(Mathf.Lerp(100f, 55f, t / 0.1f)) * Env(t, 0.002f, 35f);
            float rattle = (lp.Next(n, 2000f) - hp.Next(n, 700f)) * Env(t - 0.02f, 0.003f, 45f) * (t > 0.02f ? 1f : 0f);
            return boot + rattle * 0.4f;
        };
    });

    public static AudioClip RadioBlip => Make("RadioBlip", 0.16f, 21, -19f, () =>
    {
        Osc o = new Osc();
        LowPass tone = new LowPass();
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        return (t, rnd) =>
        {
            float chirp = tone.Next(o.Square(t < 0.05f ? 700f : 520f), 1200f) * (t < 0.09f ? 1f : 0f);
            float n = Noise(rnd);
            float hiss = (lp.Next(n, 2200f) - hp.Next(n, 600f)) * Env(t - 0.08f, 0.005f, 30f) * (t > 0.08f ? 1f : 0f);
            return chirp + hiss * 0.6f;
        };
    });

    public static AudioClip AlertSting => Make("AlertSting", 0.45f, 22, -9f, () =>
    {
        Osc a = new Osc();
        Osc sub = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float f = t < 0.08f ? 220f : 330f;
            float tone = lp.Next(a.Saw(f), 1100f) + sub.Sine(f * 0.5f) * 0.6f;
            return tone * Env(t, 0.003f, 6f);
        };
    });

    public static AudioClip SuspiciousHum => Make("SuspiciousHum", 0.35f, 23, -15f, () =>
    {
        Osc o = new Osc();
        return (t, rnd) =>
        {
            float f = t < 0.15f ? 220f : Mathf.Lerp(220f, 300f, (t - 0.15f) / 0.2f);
            return o.Tri(f) * Mathf.Clamp01(t * 30f) * Mathf.Clamp01((0.35f - t) * 12f);
        };
    });

    public static AudioClip BulletWhiz => Make("BulletWhiz", 0.22f, 24, -12f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float tone = o.Sine(Mathf.Lerp(900f, 350f, t / 0.22f)) * 0.4f;
            float air = lp.Next(Noise(rnd), 1600f);
            return (tone + air) * Hump(t, 0.22f);
        };
    });

    public static AudioClip GrenadeBounce => Make("GrenadeBounce", 0.1f, 25, -13f, () =>
    {
        LowPass lp = new LowPass();
        return (t, rnd) =>
            lp.Next(Mathf.Sin(2f * Mathf.PI * 520f * t + 1.2f * Mathf.Sin(2f * Mathf.PI * 1190f * t)), 1500f) * Env(t, 0.0005f, 50f)
            + Noise(rnd) * Env(t, 0.0005f, 220f) * 0.2f;
    });

    public static AudioClip BodyFall => Make("BodyFall", 0.4f, 26, -9f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float thud = o.Sine(Mathf.Lerp(60f, 34f, t / 0.3f)) * Env(t, 0.003f, 11f);
            float flop = lp.Next(Noise(rnd), 600f) * Env(t, 0.002f, 16f);
            return Sat(thud + flop * 1.3f, 1.5f);
        };
    });

    public static AudioClip Boing => Make("Boing", 0.35f, 27, -7f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, rnd) =>
        {
            float f = 80f + 130f * (1f - Mathf.Exp(-t * 14f)) + 18f * Mathf.Sin(2f * Mathf.PI * 20f * t) * Mathf.Exp(-t * 6f);
            float hit = lp.Next(Noise(rnd), 900f) * Env(t, 0.0005f, 60f);
            return Sat(o.Sine(f) * Env(t, 0.002f, 9f) + hit, 1.5f);
        };
    });

    public static AudioClip Slice => Make("Slice", 0.25f, 28, -5f, () =>
    {
        LowPass top = new LowPass();
        LowPass bottom = new LowPass();
        Osc o = new Osc();
        return (t, rnd) =>
        {
            float n = Noise(rnd);
            float swish = (top.Next(n, 1800f) - bottom.Next(n, 250f)) * Hump(t, 0.12f);
            float thud = o.Sine(Mathf.Lerp(90f, 40f, t / 0.2f)) * Env(t - 0.03f, 0.002f, 18f) * (t > 0.03f ? 1f : 0f);
            return Sat(swish * 0.8f + thud, 1.8f);
        };
    });

    public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f,
                              float maxDistance = 45f)
    {
        Synth.PlayAt(clip, position, volume, pitch, maxDistance);
    }
}
