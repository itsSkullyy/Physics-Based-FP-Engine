using System;
using UnityEngine;

// Speedrun-style clock for a traversal course. Starts counting the instant the scene
// loads and keeps going until something (LevelGoal, FinishZone, ...) calls FinishRun -
// that is the "always running" clock CourseHUD pins to the top of the screen. StartZone
// is still there for a level that wants a manual reset gate, but nothing has to touch it
// for the clock to start.
//
// Runs on unscaled time on purpose. An axe hit's hitstop or ImpactFrames' freeze both drop
// Time.timeScale for a beat - if the clock used scaled time those moments would be nearly
// free, which would make juice usage the optimal strategy instead of a readable side effect
// of good play. Unscaled means the number on screen matches a stopwatch in your hand.
public class CourseTimer : MonoBehaviour
{
    public static CourseTimer Instance { get; private set; }

    [Tooltip("PlayerPrefs key the best time is saved under. Bump the suffix to reset " +
             "everyone's record if you change the course layout enough that old times " +
             "are no longer comparable.")]
    public string bestTimeKey = "course.besttime.v1";

    public bool Running { get; private set; }
    public float Elapsed { get; private set; }
    public float BestTime { get; private set; } = -1f;
    public bool HasBestTime => BestTime >= 0f;

    /// PauseMenu sets this while paused. Separate from Running so a pause doesn't count
    /// as a finished/reset run - the clock just holds still and picks back up on resume.
    public bool Paused { get; set; }

    /// Fires the moment a run starts (StartZone crossed).
    public event Action RunStarted;

    /// Fires with the finished time and whether it beat the previous best.
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

    /// Called by StartZone. Re-entering the start resets the clock, so a botched run is
    /// just a walk back to the gate rather than needing a scene reload.
    public void StartRun()
    {
        Running = true;
        Elapsed = 0f;
        RunStarted?.Invoke();
    }

    /// Called by FinishZone / LevelGoal. A no-op if no run is in progress, so standing in
    /// the finish trigger (or a stray second OnTriggerEnter) can never double-count a time.
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

    /// Shared "0:00:000" (minutes:seconds:milliseconds) formatting so the HUD clock and
    /// the level-complete readout always agree on how a time looks.
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
