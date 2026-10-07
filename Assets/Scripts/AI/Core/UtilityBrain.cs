using System;
using System.Collections.Generic;
using UnityEngine;

// Utility AI (infinite axis style). Considerations go through response curves and get
// multiplied, with Dave Mark's compensation factor so actions with more considerations
// aren't punished for it.
public class Consideration
{
    public readonly string Name;
    readonly Func<float> input;
    readonly AnimationCurve curve;

    public float LastInput { get; private set; }
    public float LastScore { get; private set; }

    public Consideration(string name, Func<float> input, AnimationCurve curve)
    {
        Name = name;
        this.input = input;
        this.curve = curve;
    }

    public float Evaluate()
    {
        LastInput = Mathf.Clamp01(input());
        LastScore = curve != null ? Mathf.Clamp01(curve.Evaluate(LastInput)) : LastInput;
        return LastScore;
    }
}

public class UtilityAction
{
    public readonly string Name;
    public float Weight = 1f;
    public float Score { get; private set; }

    readonly List<Consideration> considerations = new List<Consideration>();
    Func<bool> gate;

    public IReadOnlyList<Consideration> Considerations => considerations;

    public UtilityAction(string name, float weight = 1f)
    {
        Name = name;
        Weight = weight;
    }

    public UtilityAction Consider(string name, Func<float> input, AnimationCurve curve)
    {
        considerations.Add(new Consideration(name, input, curve));
        return this;
    }

    public UtilityAction When(Func<bool> requirement)
    {
        gate = requirement;
        return this;
    }

    public float Evaluate()
    {
        if (gate != null && !gate())
        {
            Score = 0f;
            return 0f;
        }

        float result = Weight;
        int n = considerations.Count;
        float makeUp = n > 0 ? 1f - 1f / n : 0f;

        for (int i = 0; i < n; i++)
        {
            float v = considerations[i].Evaluate();
            v += (1f - v) * makeUp * v;
            result *= v;
            if (result <= 0f) break;
        }

        Score = result;
        return result;
    }
}

public class UtilityBrain
{
    public UtilityAction Current { get; private set; }

    // bonus for the current action so it doesn't flicker between two
    public float Stickiness = 1.2f;
    public float Interval = 0.25f;
    public float MinimumScore = 0.02f;

    public event Action<UtilityAction, UtilityAction> Changed;

    readonly List<UtilityAction> actions = new List<UtilityAction>();
    float timer;

    public IReadOnlyList<UtilityAction> Actions => actions;

    public UtilityAction Add(UtilityAction a)
    {
        actions.Add(a);
        return a;
    }

    // true if the action changed
    UtilityAction forcedAction;
    float forcedUntil;

    // F6 menu
    public void Force(UtilityAction action, float seconds)
    {
        forcedAction = action;
        forcedUntil = Time.time + seconds;
        timer = 0f;
    }

    public bool Tick(float dt, bool force = false)
    {
        if (forcedAction != null)
        {
            if (Time.time < forcedUntil)
            {
                if (Current == forcedAction) return false;
                UtilityAction was = Current;
                Current = forcedAction;
                Changed?.Invoke(was, forcedAction);
                return true;
            }
            forcedAction = null;
        }

        timer -= dt;
        if (!force && timer > 0f) return false;
        timer = Interval;

        UtilityAction best = null;
        float bestScore = MinimumScore;

        foreach (UtilityAction a in actions)
        {
            float s = a.Evaluate();
            if (a == Current) s *= Stickiness;
            if (s > bestScore)
            {
                bestScore = s;
                best = a;
            }
        }

        if (best == Current) return false;

        UtilityAction old = Current;
        Current = best;
        Changed?.Invoke(old, best);
        return true;
    }

    public void Clear()
    {
        Current = null;
        timer = 0f;
    }
}

public static class Curves
{
    public static AnimationCurve Linear() => AnimationCurve.Linear(0f, 0f, 1f, 1f);
    public static AnimationCurve Inverse() => AnimationCurve.Linear(0f, 1f, 1f, 0f);
    public static AnimationCurve Constant(float v) => AnimationCurve.Constant(0f, 1f, v);

    public static AnimationCurve Rising(float mid, float steepness = 10f) =>
        FromFunc(x => 1f / (1f + Mathf.Exp(-steepness * (x - mid))));

    public static AnimationCurve Falling(float mid, float steepness = 10f) =>
        FromFunc(x => 1f - 1f / (1f + Mathf.Exp(-steepness * (x - mid))));

    public static AnimationCurve Bell(float center, float width) =>
        FromFunc(x =>
        {
            float d = (x - center) / Mathf.Max(0.001f, width);
            return Mathf.Exp(-d * d * 3f);
        });

    public static AnimationCurve FromFunc(Func<float, float> f, int samples = 16)
    {
        Keyframe[] keys = new Keyframe[samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            float x = i / (float)samples;
            keys[i] = new Keyframe(x, Mathf.Clamp01(f(x)));
        }

        AnimationCurve c = new AnimationCurve(keys);
        for (int i = 0; i < keys.Length; i++)
            c.SmoothTangents(i, 0f);
        return c;
    }
}
