using System;
using UnityEngine;

// Speedrun-style clock for a traversal course. Starts counting the instant the scene
// loads and keeps going until something (LevelGoal, FinishZone, ...) calls FinishRun.
// Runs on unscaled time so hitstop/impact-freeze moments don't make the clock free.
public class CourseTimer : MonoBehaviour
{
    public static CourseTimer Instance { get; private set; }

    [Tooltip("PlayerPrefs key the best time is saved under. Bump the suffix to reset records.")]
    public string bestTimeKey = "course.besttime.v1";

    public bool Running { get; private set; }
    public float Elapsed { get; private set; }
    public float BestTime { get; private set; } = -1f;
    public bool HasBestTime => BestTime >= 0f;

    /// PauseMenu sets this while paused, separate from Running so a pause doesn't count
    /// as a finished/reset run.
    public bool Paused { get; set; }

    public event Action RunStarted;
    public event Action<float, bool> RunFinished;

    public static CourseTimer Get()
    {
        if (Instance != null) return Instance;

        CourseTimer found = FindFirstObjectByType<CourseTimer>();
        if (found != null) { Instance = found; return Instance; }

        GameObject go = new GameObject("CourseTimer");
        Instance = go.AddComponent<CourseTimer>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        if (PlayerPrefs.HasKey(bestTimeKey))
            BestTime = PlayerPrefs.GetFloat(bestTimeKey);

        Running = true;
        Elapsed = 0f;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!Running || Paused) return;
        Elapsed += Time.unscaledDeltaTime;
    }

    /// Called by StartZone. Re-entering the start resets the clock.
    public void StartRun()
    {
        Running = true;
        Elapsed = 0f;
        RunStarted?.Invoke();
    }

    /// Called by FinishZone / LevelGoal. No-op if no run is in progress.
    /// Returns whether this run beat the previous best.
    public bool FinishRun()
    {
        if (!Running) return false;
        Running = false;

        bool newBest = !HasBestTime || Elapsed < BestTime;
        if (newBest)
        {
            BestTime = Elapsed;
            PlayerPrefs.SetFloat(bestTimeKey, BestTime);
            PlayerPrefs.Save();
        }

        RunFinished?.Invoke(Elapsed, newBest);
        return newBest;
    }

    /// Shared "0:00:000" (minutes:seconds:milliseconds) formatting.
    public static string Format(float seconds)
    {
        seconds = Mathf.Max(0f, seconds);
        int minutes = Mathf.FloorToInt(seconds / 60f);
        float remainder = seconds - minutes * 60f;
        int wholeSeconds = Mathf.FloorToInt(remainder);
        int millis = Mathf.FloorToInt((remainder - wholeSeconds) * 1000f);
        return string.Format("{0}:{1:00}:{2:000}", minutes, wholeSeconds, millis);
    }
}
