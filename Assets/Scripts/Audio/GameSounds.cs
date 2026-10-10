using UnityEngine;
using static Synth;

// Player sounds made with Synth. Number after the seed is loudness in dB
// (0 = axe hits, about -16 = footsteps and loops).
public static class GameSounds
{
    // ---------------------------------------------------------------- moving

    public static AudioClip Footstep => Make("Footstep", 0.12f, 101, -17f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float thump = o.Sine(Mathf.Lerp(80f, 45f, t / 0.12f)) * Env(t, 0.002f, 35f);
            float scuff = lp.Next(Noise(r), 900f) * Env(t, 0.001f, 60f);
            return thump + scuff * 0.7f;
        };
    });

    public static AudioClip Jump => Make("Jump", 0.2f, 102, -13f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float air = lp.Next(Noise(r), Mathf.Lerp(300f, 1200f, t / 0.2f)) * Hump(t, 0.2f);
            float push = o.Sine(Mathf.Lerp(110f, 170f, t / 0.12f)) * Env(t, 0.004f, 28f);
            return air + push * 0.6f;
        };
    });

    public static AudioClip Land => Make("Land", 0.4f, 103, -5f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float thud = o.Sine(Mathf.Lerp(62f, 28f, t / 0.3f)) * Env(t, 0.002f, 10f);
            float crunch = lp.Next(Noise(r), 650f) * Env(t, 0.001f, 26f);
            return Sat(thud + crunch * 1.3f, 1.8f);
        };
    });

    public static AudioClip SlideStart => Make("SlideStart", 0.3f, 104, -13f, () =>
    {
        LowPass top = new LowPass();
        LowPass bottom = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            return (top.Next(n, 1400f) - bottom.Next(n, 180f)) * Env(t, 0.01f, 8f);
        };
    });

    public static AudioClip SlideLoop => MakeLoop("SlideLoop", 1.6f, 105, -16f, () =>
    {
        LowPass top = new LowPass();
        LowPass bottom = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float scrape = top.Next(n, 1100f) - bottom.Next(n, 150f);
            float grit = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 13f * t) * Mathf.Sin(2f * Mathf.PI * 3.1f * t);
            return scrape * grit;
        };
    });

    public static AudioClip WindLoop => MakeLoop("WindLoop", 3f, 106, -15f, () =>
    {
        LowPass a = new LowPass();
        LowPass b = new LowPass();
        return (t, r) =>
        {
            float cutoff = 380f + 220f * Mathf.Sin(2f * Mathf.PI * 0.67f * t) + 90f * Mathf.Sin(2f * Mathf.PI * 1.9f * t);
            return b.Next(a.Next(Noise(r), cutoff), cutoff * 1.4f);
        };
    });

    public static AudioClip WallKick => Make("WallKick", 0.25f, 107, -7f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float hit = o.Sine(Mathf.Lerp(110f, 45f, t / 0.12f)) * Env(t, 0.001f, 28f);
            float air = lp.Next(Noise(r), Mathf.Lerp(1500f, 350f, t / 0.25f)) * Env(t, 0.01f, 12f);
            return Sat(hit + air * 0.7f, 1.5f);
        };
    });

    public static AudioClip Dart => Make("Dart", 0.32f, 108, -8f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float air = lp.Next(Noise(r), Mathf.Lerp(2000f, 450f, t / 0.32f)) * Env(t, 0.006f, 9f);
            float push = o.Sine(Mathf.Lerp(160f, 70f, t / 0.2f)) * Env(t, 0.002f, 20f);
            return air + push * 0.6f;
        };
    });

    public static AudioClip Vault => Make("Vault", 0.16f, 109, -12f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float grab = lp.Next(Noise(r), 1100f) * Env(t, 0.001f, 40f);
            float push = o.Sine(Mathf.Lerp(150f, 100f, t / 0.16f)) * Env(t, 0.003f, 25f);
            return grab + push * 0.6f;
        };
    });

    // ---------------------------------------------------------------- grapple

    public static AudioClip GrappleClick => Make("GrappleClick", 0.12f, 110, -7f, () =>
    {
        Osc knock = new Osc();
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float band = lp.Next(n, 2200f) - hp.Next(n, 500f);
            float click = band * (Env(t, 0.0003f, 260f) + Env(t - 0.018f, 0.0003f, 300f) * 0.6f * (t > 0.018f ? 1f : 0f));
            float body = knock.Sine(Mathf.Lerp(160f, 90f, t / 0.08f)) * Env(t, 0.001f, 45f);
            return Sat(click * 1.6f + body * 0.7f, 1.4f);
        };
    });

    public static AudioClip GrappleReel => Make("GrappleReel", 0.45f, 111, -11f, () =>
    {
        Osc motor = new Osc();
        LowPass lp = new LowPass();
        LowPass tick = new LowPass();
        double ratchet = 0;
        return (t, r) =>
        {
            float rate = Mathf.Lerp(26f, 9f, t / 0.45f);
            ratchet += rate / Rate;
            float clickEnv = Mathf.Exp(-(float)(ratchet - System.Math.Floor(ratchet)) * 30f);
            float ticks = tick.Next(Noise(r), 900f) * clickEnv;
            float hum = lp.Next(motor.Saw(Mathf.Lerp(85f, 55f, t / 0.45f)), 350f);
            return (hum * 0.8f + ticks * 1.4f) * Env(t, 0.01f, 4f);
        };
    });

    public static AudioClip ReelLoop => MakeLoop("ReelLoop", 1f, 113, -12f, () =>
    {
        Osc motor = new Osc();
        Osc sub = new Osc();
        LowPass lp = new LowPass();
        LowPass tick = new LowPass();
        return (t, r) =>
        {
            float hum = lp.Next(motor.Saw(70f) + sub.Sine(35f) * 0.6f, 420f);
            float clicks = tick.Next(Noise(r), 800f) * Mathf.Exp(-((t * 20f) % 1f) * 25f);
            return hum + clicks * 1.2f;
        };
    });

    public static AudioClip GrappleRelease => Make("GrappleRelease", 0.08f, 112, -16f, () =>
    {
        LowPass lp = new LowPass();
        Osc o = new Osc();
        return (t, r) => lp.Next(Noise(r), 1400f) * Env(t, 0.0005f, 110f) + o.Sine(220f) * Env(t, 0.001f, 60f) * 0.4f;
    });

    // ---------------------------------------------------------------- axe

    public static AudioClip AxeSwing => Make("AxeSwing", 0.24f, 120, -12f, () =>
    {
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float cutoff = Mathf.Lerp(350f, 1600f, Hump(t, 0.24f));
            return (lp.Next(n, cutoff) - hp.Next(n, 120f)) * Hump(t, 0.24f);
        };
    });

    public static AudioClip AxeHitWall => Make("AxeHitWall", 0.45f, 121, -1f, () =>
    {
        Osc thump = new Osc();
        Osc sub = new Osc();
        LowPass crunch = new LowPass();
        LowPass knock = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float body = thump.Sine(Mathf.Lerp(95f, 42f, t / 0.18f)) * Env(t, 0.001f, 15f);
            float low = sub.Sine(48f) * Env(t, 0.003f, 9f);
            float grit = crunch.Next(n, 850f) * Env(t, 0.0005f, 28f);
            float impact = knock.Next(n, 1800f) * Env(t, 0.0003f, 120f);
            return Sat(body * 1.1f + low * 0.6f + grit * 1.4f + impact * 0.8f, 2.4f);
        };
    });

    public static AudioClip AxeHitFlesh => Make("AxeHitFlesh", 0.35f, 122, -1f, () =>
    {
        Osc thump = new Osc();
        LowPass wet = new LowPass();
        LowPass crack = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float body = thump.Sine(Mathf.Lerp(85f, 36f, t / 0.2f)) * Env(t, 0.001f, 16f);
            float squelch = wet.Next(n, 380f + 260f * Mathf.Sin(2f * Mathf.PI * 24f * t)) * Env(t, 0.002f, 18f);
            float bone = crack.Next(n, 1600f) * Env(t, 0.0003f, 90f);
            return Sat(body * 1.1f + squelch * 2.2f + bone * 0.6f, 2.6f);
        };
    });

    public static AudioClip AxeBounce => Make("AxeBounce", 0.3f, 123, -7f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float whump = o.Sine(Mathf.Lerp(55f, 120f, Mathf.Sqrt(t / 0.3f))) * Env(t, 0.003f, 9f);
            float air = lp.Next(Noise(r), Mathf.Lerp(400f, 1400f, t / 0.3f)) * Hump(t, 0.3f);
            return whump + air * 0.6f;
        };
    });

    public static AudioClip ChargeLoop => MakeLoop("ChargeLoop", 1f, 124, -18f, () =>
    {
        Osc a = new Osc();
        Osc b = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float hum = a.Saw(55f) + b.Sine(110f + 2f * Mathf.Sin(2f * Mathf.PI * 5f * t)) * 0.7f;
            return lp.Next(hum, 380f);
        };
    });

    public static AudioClip ChargeFull => Make("ChargeFull", 0.3f, 125, -9f, () =>
    {
        Osc a = new Osc();
        Osc b = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float clunk = (a.Sine(110f) + b.Sine(165f) * 0.6f) * Env(t, 0.002f, 14f);
            float click = lp.Next(Noise(r), 1800f) * Env(t, 0.0003f, 200f);
            return Sat(clunk + click * 1.2f, 1.5f);
        };
    });

    public static AudioClip AxeThrow => Make("AxeThrow", 0.38f, 126, -7f, () =>
    {
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        Osc o = new Osc();
        return (t, r) =>
        {
            float n = Noise(r);
            float whoosh = (lp.Next(n, Mathf.Lerp(1800f, 350f, t / 0.38f)) - hp.Next(n, 100f)) * Env(t, 0.01f, 7f);
            float heave = o.Sine(Mathf.Lerp(110f, 65f, t / 0.15f)) * Env(t, 0.002f, 26f);
            return whoosh + heave * 0.6f;
        };
    });

    public static AudioClip AxeSpinLoop => MakeLoop("AxeSpinLoop", 1f, 127, -14f, () =>
    {
        LowPass lp = new LowPass();
        LowPass hp = new LowPass();
        return (t, r) =>
        {
            float n = Noise(r);
            float band = lp.Next(n, 1100f) - hp.Next(n, 160f);
            float chop = Mathf.Pow(Mathf.Abs(Mathf.Sin(2f * Mathf.PI * 6f * t)), 3f);
            return band * (0.2f + chop);
        };
    });

    public static AudioClip AxeStick => Make("AxeStick", 0.7f, 128, -3f, () =>
    {
        Osc thunk = new Osc();
        Osc twang = new Osc();
        LowPass crunch = new LowPass();
        LowPass soft = new LowPass();
        return (t, r) =>
        {
            float body = thunk.Sine(Mathf.Lerp(90f, 40f, t / 0.15f)) * Env(t, 0.001f, 16f);
            float grit = crunch.Next(Noise(r), 900f) * Env(t, 0.0005f, 30f);
            float wob = soft.Next(twang.Tri(95f), 400f) * Env(t, 0.02f, 5f)
                        * (0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * 26f * t));
            return Sat(body * 1.1f + grit * 1.3f, 2.2f) + wob * 0.35f;
        };
    });

    public static AudioClip AxeClatter => Make("AxeClatter", 0.6f, 129, -10f, () =>
    {
        float[] hits = { 0f, 0.11f, 0.19f, 0.3f, 0.36f };
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float v = 0f;
            for (int i = 0; i < hits.Length; i++)
            {
                float dt = t - hits[i];
                if (dt < 0f)
                {
                    continue;
                }
                float level = 1f - i * 0.17f;
                v += Mathf.Sin(2f * Mathf.PI * (210f + i * 25f) * dt + 1.2f * Mathf.Sin(2f * Mathf.PI * 470f * dt)) * Env(dt, 0.0005f, 30f) * level;
            }
            return lp.Next(v, 900f);
        };
    });

    public static AudioClip AxeRecall => Make("AxeRecall", 0.35f, 130, -8f, () =>
    {
        LowPass lp = new LowPass();
        Osc o = new Osc();
        return (t, r) =>
        {
            float rip = lp.Next(Noise(r), Mathf.Lerp(300f, 1600f, t / 0.35f)) * Env(t, 0.003f, 9f);
            float pull = o.Sine(Mathf.Lerp(70f, 160f, t / 0.35f)) * Hump(t, 0.35f);
            return rip + pull * 0.5f;
        };
    });

    public static AudioClip AxeCatch => Make("AxeCatch", 0.18f, 131, -9f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float slap = lp.Next(Noise(r), 1100f) * Env(t, 0.0005f, 50f);
            float thump = o.Sine(Mathf.Lerp(130f, 65f, t / 0.1f)) * Env(t, 0.001f, 32f);
            return Sat(slap + thump, 1.5f);
        };
    });

    // ---------------------------------------------------------------- world

    public static AudioClip WallShatter => Make("WallShatter", 1.1f, 140, -2f, () =>
    {
        LowPass lp = new LowPass();
        Osc boom = new Osc();
        float[] bits = new float[14];
        System.Random pick = new System.Random(7);
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = 0.05f + (float)pick.NextDouble() * 0.8f;
        }
        return (t, r) =>
        {
            float crash = lp.Next(Noise(r), Mathf.Lerp(2200f, 300f, t / 0.6f)) * Env(t, 0.001f, 5f);
            float body = boom.Sine(Mathf.Lerp(70f, 30f, t / 0.4f)) * Env(t, 0.001f, 8f);
            float debris = 0f;
            for (int i = 0; i < bits.Length; i++)
            {
                float dt = t - bits[i];
                if (dt < 0f)
                {
                    continue;
                }
                debris += Mathf.Sin(2f * Mathf.PI * (140f + i * 23f) * dt) * Env(dt, 0.0005f, 50f);
            }
            return Sat(crash * 1.2f + body + debris * 0.15f * (1f - t), 1.8f);
        };
    });

    public static AudioClip Hurt => Make("Hurt", 0.28f, 141, -5f, () =>
    {
        Osc buzz = new Osc();
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            float thud = o.Sine(Mathf.Lerp(90f, 40f, t / 0.2f)) * Env(t, 0.001f, 16f);
            float sting = lp.Next(buzz.Square(Mathf.Lerp(130f, 85f, t / 0.28f)), 600f) * Env(t, 0.002f, 10f);
            return Sat(thud + sting * 0.5f, 1.6f);
        };
    });

    public static AudioClip Death => Make("Death", 1.1f, 142, -7f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) => lp.Next(o.Square(Mathf.Lerp(200f, 35f, Mathf.Pow(t / 1.1f, 0.7f))), 600f) * Env(t, 0.005f, 2.5f);
    });

    public static AudioClip Respawn => Make("Respawn", 0.6f, 143, -14f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        float[] notes = { 196f, 262f, 330f, 392f };
        return (t, r) =>
        {
            int i = Mathf.Min(notes.Length - 1, (int)(t / 0.09f));
            float local = t - i * 0.09f;
            return lp.Next(o.Tri(notes[i]), 1200f) * Env(local, 0.003f, i == notes.Length - 1 ? 6f : 18f);
        };
    });

    public static AudioClip RunStart => Make("RunStart", 0.3f, 144, -12f, () =>
    {
        Osc o = new Osc();
        LowPass lp = new LowPass();
        return (t, r) => lp.Next(o.Square(440f), 1200f) * Env(t, 0.002f, 9f);
    });

    public static AudioClip RunFinish => Make("RunFinish", 1f, 145, -10f, () => Jingle(new[] { 262f, 330f, 392f, 523f }, 0.1f, 4f));

    public static AudioClip NewBest => Make("NewBest", 1.4f, 146, -9f, () => Jingle(new[] { 262f, 330f, 392f, 523f, 659f, 784f }, 0.08f, 3f));

    public static AudioClip AbilityGet => Make("AbilityGet", 1.1f, 149, -8f, () => Jingle(new[] { 392f, 494f, 587f, 784f }, 0.07f, 3f));

    // rising rush of air while the slit forms, a thump when it tears wide, then a shimmer
    // that rings out. The tear lands at about 0.5s, same as Portal's slit phase.
    public static AudioClip PortalOpen => Make("PortalOpen", 2.4f, 147, -4f, () =>
    {
        LowPass air = new LowPass();
        Osc thump = new Osc();
        Osc a = new Osc();
        Osc b = new Osc();
        Osc c = new Osc();
        return (t, r) =>
        {
            float rise = Mathf.Clamp01(t / 0.5f);
            float whoosh = air.Next(Noise(r), Mathf.Lerp(180f, 2800f, rise * rise)) * Hump(t, 1.3f);

            float tear = t - 0.5f;
            if (tear < 0f)
            {
                return Sat(whoosh * 0.9f, 1.5f);
            }

            float boom = thump.Sine(Mathf.Lerp(95f, 38f, Mathf.Clamp01(tear / 0.45f))) * Env(tear, 0.002f, 5f);
            float wobble = 1f + 0.006f * Mathf.Sin(2f * Mathf.PI * 6f * t);
            float shimmer = (a.Sine(392f * wobble) + b.Sine(587f * wobble) * 0.7f + c.Sine(784f * wobble) * 0.5f)
                            * Env(tear, 0.12f, 1.5f) * 0.3f;
            return Sat(whoosh * 0.9f + boom + shimmer, 1.5f);
        };
    });

    // roughly the open sound backwards: shimmer falling away, air sucked in, a soft thud as
    // it shuts at about 0.95s (Portal's close takes 1s)
    public static AudioClip PortalClose => Make("PortalClose", 1.3f, 148, -6f, () =>
    {
        LowPass air = new LowPass();
        Osc a = new Osc();
        Osc b = new Osc();
        Osc thud = new Osc();
        return (t, r) =>
        {
            float fall = Mathf.Clamp01(t / 0.9f);
            float shimmer = (a.Sine(Mathf.Lerp(784f, 392f, fall)) + b.Sine(Mathf.Lerp(587f, 262f, fall)) * 0.7f)
                            * Hump(t, 0.95f) * 0.25f;
            float suck = air.Next(Noise(r), Mathf.Lerp(2400f, 200f, fall)) * Hump(t, 0.95f) * 0.8f;

            float shut = t - 0.95f;
            float boom = shut < 0f ? 0f : thud.Sine(Mathf.Lerp(120f, 50f, Mathf.Clamp01(shut / 0.2f))) * Env(shut, 0.002f, 14f);
            return Sat(shimmer + suck + boom, 1.5f);
        };
    });

    static System.Func<float, System.Random, float> Jingle(float[] notes, float step, float lastDecay)
    {
        Osc a = new Osc();
        Osc b = new Osc();
        LowPass lp = new LowPass();
        return (t, r) =>
        {
            int i = Mathf.Min(notes.Length - 1, (int)(t / step));
            float local = t - i * step;
            float decay = i == notes.Length - 1 ? lastDecay : 14f;
            float tone = a.Square(notes[i]) * 0.5f + b.Sine(notes[i] * 2f) * 0.5f;
            return lp.Next(tone, 1600f) * Env(local, 0.003f, decay);
        };
    }
}
