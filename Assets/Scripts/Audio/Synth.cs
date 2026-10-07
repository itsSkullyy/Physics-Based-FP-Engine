using System;
using System.Collections.Generic;
using UnityEngine;

// Builds AudioClips from code and caches them. Clips are normalised (RMS) to the dB
// each recipe asks for. Plays through pooled 2D and 3D AudioSources.
public static class Synth
{
    public const int Rate = 44100;

    public static float Volume = 1f;

    static readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();

    // ---------------------------------------------------------------- building blocks

    // keeps phase so sweeps are smooth
    public sealed class Osc
    {
        double phase;

        float Step(float freq)
        {
            phase += freq / Rate;
            phase -= Math.Floor(phase);
            return (float)phase;
        }

        public float Sine(float freq) => Mathf.Sin(2f * Mathf.PI * Step(freq));
        public float Saw(float freq) => Step(freq) * 2f - 1f;
        public float Square(float freq) => Step(freq) < 0.5f ? 1f : -1f;
        public float Tri(float freq) => 1f - 4f * Mathf.Abs(Step(freq) - 0.5f);
    }

    // one pole low pass (input minus this = high pass)
    public sealed class LowPass
    {
        float y;

        public float Next(float x, float cutoffHz)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * Mathf.Max(1f, cutoffHz) / Rate);
            y += (x - y) * a;
            return y;
        }
    }

    public static float Noise(System.Random r) => (float)(r.NextDouble() * 2.0 - 1.0);

    public static float Env(float t, float attack, float decay) =>
        Mathf.Clamp01(t / Mathf.Max(0.0001f, attack)) * Mathf.Exp(-t * decay);

    // soft clip
    public static float Sat(float x, float drive) => (float)(Math.Tanh(x * drive) / Math.Tanh(drive));

    public static float Hump(float t, float length) =>
        t < 0f || t > length ? 0f : Mathf.Sin(Mathf.PI * t / length);

    // ---------------------------------------------------------------- clips

    // factory runs once per clip so filters/oscillators inside keep their state
    public static AudioClip Make(string name, float seconds, int seed, float db, Func<Func<float, System.Random, float>> factory)
    {
        if (cache.TryGetValue(name, out AudioClip c) && c != null) return c;

        int count = Mathf.CeilToInt(seconds * Rate);
        float[] data = Render(count, seed, factory());
        for (int i = 0; i < count; i++)
            data[i] *= Mathf.Clamp01((count - i) / (Rate * 0.01f));

        Normalise(data, db);
        return Store(name, data);
    }

    // tail crossfaded into the start so it loops
    public static AudioClip MakeLoop(string name, float seconds, int seed, float db, Func<Func<float, System.Random, float>> factory)
    {
        if (cache.TryGetValue(name, out AudioClip c) && c != null) return c;

        int count = Mathf.CeilToInt(seconds * Rate);
        int fade = Mathf.Min(count / 4, Mathf.CeilToInt(0.08f * Rate));
        float[] raw = Render(count + fade, seed, factory());
        float[] data = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = raw[i];
            if (i < fade)
            {
                float k = i / (float)fade;
                v = v * k + raw[count + i] * (1f - k);
            }
            data[i] = v;
        }

        Normalise(data, db);
        return Store(name, data);
    }

    const float ReferenceRms = 0.2f;

    static void Normalise(float[] data, float db)
    {
        double sum = 0;
        float peak = 0f;
        foreach (float v in data)
        {
            sum += v * v;
            peak = Mathf.Max(peak, Mathf.Abs(v));
        }
        float rms = (float)Math.Sqrt(sum / Mathf.Max(1, data.Length));
        if (rms < 1e-6f || peak < 1e-6f) return;

        float target = ReferenceRms * Mathf.Pow(10f, db / 20f);
        float gain = Mathf.Min(target / rms, 0.95f / peak);
        for (int i = 0; i < data.Length; i++) data[i] = Mathf.Clamp(data[i] * gain, -1f, 1f);
    }

    static float[] Render(int count, int seed, Func<float, System.Random, float> wave)
    {
        float[] data = new float[count];
        System.Random rnd = new System.Random(seed);
        for (int i = 0; i < count; i++) data[i] = wave(i / (float)Rate, rnd);
        return data;
    }

    static AudioClip Store(string name, float[] data)
    {
        AudioClip c = AudioClip.Create("Synth_" + name, data.Length, 1, Rate, false);
        c.SetData(data, 0);
        cache[name] = c;
        return c;
    }

    // ---------------------------------------------------------------- playback

    const int Pool2D = 16;
    const int Pool3D = 40;

    static GameObject root;
    static AudioSource[] sources2D;
    static AudioSource[] sources3D;
    static int next2D, next3D;

    static void EnsurePools()
    {
        if (root != null) return;

        root = new GameObject("SynthAudio");
        root.hideFlags = HideFlags.HideInHierarchy;
        UnityEngine.Object.DontDestroyOnLoad(root);

        sources2D = new AudioSource[Pool2D];
        for (int i = 0; i < Pool2D; i++)
        {
            AudioSource s = root.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            sources2D[i] = s;
        }

        sources3D = new AudioSource[Pool3D];
        for (int i = 0; i < Pool3D; i++)
        {
            GameObject go = new GameObject("Sfx3D");
            go.transform.SetParent(root.transform, false);
            AudioSource s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.dopplerLevel = 0f;
            sources3D[i] = s;
        }
    }

    public static void Play(AudioClip clip, float volume = 1f, float pitch = 1f)
    {
        if (clip == null || Volume <= 0f) return;
        EnsurePools();

        AudioSource s = Take(sources2D, ref next2D);
        s.clip = clip;
        s.volume = volume * Volume;
        s.pitch = pitch;
        s.Play();
    }

    public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float maxDistance = 45f)
    {
        if (clip == null || Volume <= 0f) return;
        EnsurePools();

        AudioSource s = Take(sources3D, ref next3D);
        s.transform.position = position;
        s.clip = clip;
        s.volume = volume * Volume;
        s.pitch = pitch;
        s.minDistance = Mathf.Min(2f, maxDistance * 0.25f);
        s.maxDistance = maxDistance;
        s.Play();
    }

    static AudioSource Take(AudioSource[] pool, ref int next)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            int k = (next + i) % pool.Length;
            if (!pool[k].isPlaying)
            {
                next = (k + 1) % pool.Length;
                return pool[k];
            }
        }
        AudioSource s = pool[next];
        next = (next + 1) % pool.Length;
        return s;
    }

    public static AudioSource MakeLoopSource(AudioClip clip, Transform parent, bool spatial, float maxDistance = 30f)
    {
        GameObject go = new GameObject("SynthLoop_" + (clip != null ? clip.name : "none"));
        if (parent != null) go.transform.SetParent(parent, false);
        AudioSource s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = true;
        s.playOnAwake = false;
        s.volume = 0f;
        s.spatialBlend = spatial ? 1f : 0f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 2f;
        s.maxDistance = maxDistance;
        s.dopplerLevel = 0f;
        return s;
    }

    // call every frame
    public static void Drive(AudioSource s, float volume, float pitch, float rate = 10f)
    {
        if (s == null) return;
        float k = 1f - Mathf.Exp(-rate * Time.unscaledDeltaTime);
        s.volume = Mathf.Lerp(s.volume, volume * Volume, k);
        s.pitch = Mathf.Lerp(s.pitch, pitch, k);

        if (s.volume > 0.005f && !s.isPlaying) s.Play();
        else if (s.volume <= 0.005f && volume <= 0f && s.isPlaying) s.Stop();
    }
}
