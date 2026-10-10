using System;
using UnityEngine;
using UnityEngine.SceneManagement;

// Speedrun-style clock for a traversal course. Starts counting the instant the scene
// loads and keeps going until something (LevelGoal, FinishZone, ...) calls FinishRun.
// Coming into another level through a portal starts it again for that level (LevelReset).
// Runs on unscaled time so hitstop/impact-freeze moments don't make the clock free.
public class CourseTimer : MonoBehaviour
{
    public static CourseTimer Instance { get; private set; }

    [Tooltip("PlayerPrefs key the best time is saved under, with the level's scene name added on the end. Bump the suffix to reset records.")]
    public string bestTimeKey = "course.besttime.v1";

    public bool Running { get; private set; }
    public float Elapsed { get; private set; }
    public float BestTime { get; private set; } = -1f;
    public bool HasBestTime => BestTime >= 0f;
    /// Scene name of the level being timed. Each level keeps its own best time.
    public string Level { get; private set; }

    /// PauseMenu sets this while paused, separate from Running so a pause doesn't count
    /// as a finished/reset run.
    public bool Paused { get; set; }

    public event Action RunStarted;
    public event Action<float, bool> RunFinished;

    public static CourseTimer Get()
    {
        if (Instance != null)
        {
            return Instance;
        }

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

        UseLevel(StartingLevel());
        Running = true;
        Elapsed = 0f;
    }

    // The scene this timer was placed in. If PortalManager has already carried the player
    // (and this with it) into DontDestroyOnLoad, the scene it tracks the player in.
    string StartingLevel()
    {
        string own = gameObject.scene.name;
        if (own != "DontDestroyOnLoad")
        {
            return own;
        }
        string space = PortalManager.PlayerSpace;
        return !string.IsNullOrEmpty(space) ? space : SceneManager.GetActiveScene().name;
    }

    void UseLevel(string level)
    {
        Level = level;
        string key = BestKey;
        BestTime = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : -1f;
    }

    string BestKey => bestTimeKey + "." + Level;

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void Update()
    {
        if (!Running || Paused)
        {
            return;
        }
        Elapsed += Time.unscaledDeltaTime;
    }

    /// Called by StartZone. Re-entering the start resets the clock.
    public void StartRun()
    {
        Running = true;
        Elapsed = 0f;
        RunStarted?.Invoke();
    }

    /// Fresh run of a different level, with that level's best time. LevelReset calls this
    /// when the player comes through a portal into it.
    public void StartRun(string level)
    {
        UseLevel(level);
        StartRun();
    }

    /// Called by FinishZone / LevelGoal. No-op if no run is in progress.
    /// Returns whether this run beat the previous best.
    public bool FinishRun()
    {
        if (!Running)
        {
            return false;
        }
        Running = false;

        bool newBest = !HasBestTime || Elapsed < BestTime;
        if (newBest)
        {
            BestTime = Elapsed;
            PlayerPrefs.SetFloat(BestKey, BestTime);
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
