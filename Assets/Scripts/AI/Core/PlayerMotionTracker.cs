using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

// Tracks the player's movement for the AI: prediction, landing point, how predictable
// they're moving, landings and noises. F8 records runs to CSV, which the Stillwalker
// training scene plays back as a fake player. Scripted mode lets a training bot drive it.
[DefaultExecutionOrder(50)]
public class PlayerMotionTracker : MonoBehaviour
{
    public enum Mode { Live, Playback, Scripted }

    [Header("Mode")]
    public Mode mode = Mode.Live;

    [Header("Live Refs (auto-found)")]
    public FirstPersonCharacterController controller;
    public Rigidbody body;

    [Header("Ground Rule")]
    [Tooltip("Ground only 'counts' after this long, so wall kicks and bunny hops that brush the floor don't count as landing.")]
    public float groundGrace = 0.1f;

    [Header("Prediction")]
    public float historySeconds = 1.5f;
    [Tooltip("Turning more than this many degrees inside the window counts as fully unpredictable.")]
    public float unpredictableTurnAngle = 55f;
    public float predictabilityWindow = 0.35f;

    [Header("Recording")]
    public Key recordKey = Key.F8;
    public bool showRecordingLabel = true;

    [Header("Playback")]
    public TextAsset playbackData;
    public bool loopPlayback = true;
    [Tooltip("Height from the recorded centre down to the feet.")]
    public float playbackFeetOffset = 0.9f;
    [Tooltip("On: the run starts wherever this object is. Off: recorded world positions are replayed shifted by Playback Offset (training arenas that copy the arena the run was recorded in).")]
    public bool playbackFromHere = true;
    public Vector3 playbackOffset;

    // ---------------------------------------------------------------- state

    [Flags]
    public enum Flags
    {
        None = 0, Grounded = 1, WallRun = 2, Slide = 4, AirSlide = 8,
        Swing = 16, Zip = 32, Vault = 64, Dart = 128
    }

    public struct Sample
    {
        public float time;
        public Vector3 center;
        public Vector3 velocity;
        public Flags flags;
        public Vector3 camForward;
    }

    public Vector3 Center { get; private set; }
    public Vector3 Velocity { get; private set; }
    public Flags State { get; private set; }
    public Vector3 CameraPosition { get; private set; }
    public Vector3 CameraForward { get; private set; } = Vector3.forward;
    public float FeetOffset { get; private set; } = 0.9f;
    public Vector3 Feet => Center - Vector3.up * FeetOffset;

    public bool IsGrounded => (State & Flags.Grounded) != 0;
    public bool IsWallRunning => (State & Flags.WallRun) != 0;
    public bool IsSliding => (State & Flags.Slide) != 0;
    public bool IsAirSliding => (State & Flags.AirSlide) != 0;
    public bool IsCrouching => (State & (Flags.Slide | Flags.AirSlide)) != 0;
    public bool IsSwinging => (State & Flags.Swing) != 0;
    public bool IsZipping => (State & Flags.Zip) != 0;
    public bool IsVaulting => (State & Flags.Vault) != 0;
    public bool IsDarting => (State & Flags.Dart) != 0;

    public bool OnGround => IsGrounded && !IsVaulting && GroundedTime >= groundGrace;

    public float GroundedTime { get; private set; }
    public float AirTime { get; private set; }
    public float StationaryTime { get; private set; }
    public float Speed => new Vector3(Velocity.x, 0f, Velocity.z).magnitude;
    public float Predictability01 { get; private set; } = 1f;
    public bool IsRecording => recording;
    public bool IsDead => health != null && health.IsDead;

    public event Action Landed;

    PlayerHealth health;
    Collider mainCollider;
    float fallMultiplier = 2.3f;
    LayerMask groundMask = ~0;

    readonly List<Sample> history = new List<Sample>();
    bool wasOnGround;

    bool recording;
    StringBuilder recordBuffer;
    float recordStart;

    readonly List<Sample> playback = new List<Sample>();
    float playbackTime;
    Vector3 playbackOrigin;

    Sample scripted;

    public static PlayerMotionTracker Find()
    {
        PlayerMotionTracker t = FindFirstObjectByType<PlayerMotionTracker>();
        if (t != null)
        {
            return t;
        }

        FirstPersonCharacterController c = FindFirstObjectByType<FirstPersonCharacterController>();
        return c != null ? c.gameObject.AddComponent<PlayerMotionTracker>() : null;
    }

    void Awake()
    {
        if (mode == Mode.Live)
        {
            FindLivePlayerParts();
        }
        else
        {
            LoadPlayback();
            scripted = CurrentPlaybackSample();
        }

        SampleNow();
    }

    // the real player's body, collider and movement settings (for jump landing prediction)
    void FindLivePlayerParts()
    {
        if (controller == null)
        {
            controller = GetComponent<FirstPersonCharacterController>();
        }
        if (body == null)
        {
            body = GetComponent<Rigidbody>();
        }
        health = GetComponent<PlayerHealth>();
        mainCollider = GetComponent<CapsuleCollider>();
        if (mainCollider == null)
        {
            mainCollider = GetComponent<Collider>();
        }

        if (controller != null)
        {
            fallMultiplier = controller.fallMultiplier;
            groundMask = controller.groundMask;
        }
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        if (mode == Mode.Playback)
        {
            StepPlayback(dt);
        }
        SampleNow();
        UpdateTimers(dt);
        UpdatePredictability(dt);
        CheckLanding();

        if (mode == Mode.Live)
        {
            EmitNoises();
        }
        if (recording)
        {
            WriteSample();
        }
    }

    // how long it's been on the ground, in the air and standing still
    void UpdateTimers(float dt)
    {
        if (IsGrounded)
        {
            GroundedTime += dt;
            AirTime = 0f;
        }
        else
        {
            AirTime += dt;
            GroundedTime = 0f;
        }

        StationaryTime = Speed < 1.5f ? StationaryTime + dt : 0f;
    }

    void CheckLanding()
    {
        bool onGround = OnGround;
        if (onGround && !wasOnGround)
        {
            Landed?.Invoke();
        }
        wasOnGround = onGround;
    }

    // ---------------------------------------------------------------- noise

    Flags lastFlags;
    float fallSpeed;

    void EmitNoises()
    {
        AIDirector d = AIDirector.Instance;
        Flags now = State;
        if (d != null)
        {
            if (Rising(now, Flags.Grounded))
            {
                d.MakeNoise(Feet, 8f + Mathf.Min(fallSpeed, 20f) * 1.2f, "land");
            }
            if (Rising(now, Flags.WallRun))
            {
                d.MakeNoise(Center, 10f, "wallrun");
            }
            if (Rising(now, Flags.Swing) || Rising(now, Flags.Zip))
            {
                d.MakeNoise(Center, 14f, "grapple");
            }
            if (Rising(now, Flags.Dart))
            {
                d.MakeNoise(Center, 10f, "dart");
            }
            if (Rising(now, Flags.Slide))
            {
                d.MakeNoise(Feet, 6f, "slide");
            }
        }

        fallSpeed = IsGrounded ? 0f : Mathf.Max(fallSpeed, -Velocity.y);
        lastFlags = now;
    }

    bool Rising(Flags now, Flags f) => (now & f) != 0 && (lastFlags & f) == 0;

    void Update()
    {
        if (mode != Mode.Live)
        {
            return;
        }

        Keyboard kb = Keyboard.current;
        if (kb != null && recordKey != Key.None && kb[recordKey].wasPressedThisFrame)
        {
            if (recording)
            {
                StopRecording();
            }
            else
            {
                StartRecording();
            }
        }
    }

    // ---------------------------------------------------------------- sampling

    void SampleNow()
    {
        Sample s;

        if (mode == Mode.Live)
        {
            if (controller == null || body == null)
            {
                return;
            }

            Bounds b = mainCollider != null ? mainCollider.bounds : new Bounds(body.position, Vector3.one);
            s.center = b.center;
            FeetOffset = b.extents.y;
            s.velocity = body.linearVelocity;
            s.flags = Flags.None;
            if (controller.IsGrounded)
            {
                s.flags |= Flags.Grounded;
            }
            if (controller.IsWallRunning)
            {
                s.flags |= Flags.WallRun;
            }
            if (controller.IsSliding)
            {
                s.flags |= Flags.Slide;
            }
            if (controller.IsAirSliding)
            {
                s.flags |= Flags.AirSlide;
            }
            if (controller.IsSwinging)
            {
                s.flags |= Flags.Swing;
            }
            if (controller.IsZipping)
            {
                s.flags |= Flags.Zip;
            }
            if (controller.IsVaulting)
            {
                s.flags |= Flags.Vault;
            }
            if (controller.IsDarting)
            {
                s.flags |= Flags.Dart;
            }

            Transform cam = controller.cameraTransform;
            s.camForward = cam != null ? cam.forward : transform.forward;
            CameraPosition = cam != null ? cam.position : s.center + Vector3.up * 0.6f;
        }
        else
        {
            s = mode == Mode.Scripted ? scripted : CurrentPlaybackSample();
            FeetOffset = playbackFeetOffset;
            CameraPosition = s.center + Vector3.up * 0.6f;
        }

        s.time = Time.time;
        Center = s.center;
        Velocity = s.velocity;
        State = s.flags;
        CameraForward = s.camForward.sqrMagnitude > 0.001f ? s.camForward.normalized : Vector3.forward;

        history.Add(s);
        float cutoff = Time.time - historySeconds;
        while (history.Count > 2 && history[0].time < cutoff)
        {
            history.RemoveAt(0);
        }
    }

    void UpdatePredictability(float dt)
    {
        float target;

        if (IsDarting)
        {
            target = 0f;
        }
        else if (Speed < 2f && IsGrounded)
        {
            target = 1f;
        }
        else
        {
            Sample past = SampleAgo(predictabilityWindow);
            Vector3 a = past.velocity;
            Vector3 b = Velocity;
            if (a.sqrMagnitude < 1f || b.sqrMagnitude < 1f)
            {
                target = 0.5f;
            }
            else
            {
                // flat heading only, vertical snaps are checked separately
                Vector3 fa = new Vector3(a.x, 0f, a.z);
                Vector3 fb = new Vector3(b.x, 0f, b.z);
                float turn = fa.sqrMagnitude > 0.5f && fb.sqrMagnitude > 0.5f ? Vector3.Angle(fa, fb) : 0f;
                float vertSnap = Mathf.Max(0f, b.y - a.y - 2f);
                target = 1f - Mathf.Clamp01(turn / unpredictableTurnAngle + vertSnap / 8f);
            }
        }

        // drops fast, recovers slow
        float rate = target < Predictability01 ? 12f : 2.5f;
        Predictability01 = Mathf.MoveTowards(Predictability01, target, rate * dt);
    }

    public float LookAngleTo(Vector3 point)
    {
        Vector3 to = point - CameraPosition;
        return to.sqrMagnitude < 0.0001f ? 0f : Vector3.Angle(CameraForward, to);
    }

    public Sample SampleAgo(float seconds)
    {
        if (history.Count == 0)
        {
            return default;
        }
        float t = Time.time - seconds;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].time <= t)
            {
                return history[i];
            }
        }
        return history[0];
    }

    // ---------------------------------------------------------------- prediction

    // only really good for about a second
    public Vector3 Predict(float seconds)
    {
        if (seconds <= 0f)
        {
            return Center;
        }

        if (IsSwinging || IsZipping)
        {
            return Center + Velocity * seconds * 0.8f;
        }

        if (IsWallRunning)
        {
            Vector3 v = Velocity;
            v.y *= 0.3f;
            return Center + v * seconds;
        }

        if (IsGrounded)
        {
            Vector3 flat = new Vector3(Velocity.x, 0f, Velocity.z);
            return Center + flat * seconds;
        }

        Simulate(seconds, false, out Vector3 p, out _);
        return p;
    }

    public bool PredictLanding(out Vector3 feetPoint, out float time, float maxTime = 3f)
    {
        if (IsGrounded)
        {
            feetPoint = Feet;
            time = 0f;
            return true;
        }

        bool landed = Simulate(maxTime, true, out Vector3 center, out time);
        feetPoint = center - Vector3.up * FeetOffset;
        return landed;
    }

    bool Simulate(float maxTime, bool stopOnLand, out Vector3 center, out float time)
    {
        const float step = 0.04f;
        Vector3 p = Center;
        Vector3 v = Velocity;
        float g = Physics.gravity.y;

        for (time = 0f; time < maxTime; time += step)
        {
            float scale = v.y < 0f ? fallMultiplier : 1f;
            v.y += g * scale * step;
            Vector3 next = p + v * step;

            Vector3 feetA = p - Vector3.up * FeetOffset;
            Vector3 feetB = next - Vector3.up * FeetOffset;
            if (v.y < 0f && GroundCast(feetA, feetB, out RaycastHit hit))
            {
                center = hit.point + Vector3.up * FeetOffset;
                if (stopOnLand)
                {
                    return true;
                }

                Vector3 flat = new Vector3(v.x, 0f, v.z);
                center += flat * (maxTime - time) * 0.8f;
                return true;
            }
            p = next;
        }

        center = p;
        return false;
    }

    bool GroundCast(Vector3 from, Vector3 to, out RaycastHit best)
    {
        best = default;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.0001f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, groundMask, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        foreach (RaycastHit h in hits)
        {
            if (body != null && h.rigidbody == body)
            {
                continue;
            }
            if (h.collider.GetComponentInParent<EnemyAgent>() != null)
            {
                continue;
            }
            if (h.distance < bestDist)
            {
                bestDist = h.distance;
                best = h;
                found = true;
            }
        }
        return found;
    }

    // ---------------------------------------------------------------- recording

    void StartRecording()
    {
        recording = true;
        recordStart = Time.time;
        recordBuffer = new StringBuilder();
        recordBuffer.AppendLine("t,cx,cy,cz,vx,vy,vz,flags,fx,fy,fz");
    }

    void StopRecording()
    {
        recording = false;
        if (recordBuffer == null)
        {
            return;
        }

        string dir = Path.Combine(Application.persistentDataPath, "MotionRecordings");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
        File.WriteAllText(path, recordBuffer.ToString());
        recordBuffer = null;

        Debug.Log($"[PlayerMotionTracker] Saved {path}. Drop it into the project as a TextAsset to use it for training.");
    }

    void WriteSample()
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        Vector3 c = Center, v = Velocity, f = CameraForward;
        recordBuffer.Append((Time.time - recordStart).ToString("F3", ci)).Append(',')
            .Append(c.x.ToString("F3", ci)).Append(',').Append(c.y.ToString("F3", ci)).Append(',').Append(c.z.ToString("F3", ci)).Append(',')
            .Append(v.x.ToString("F3", ci)).Append(',').Append(v.y.ToString("F3", ci)).Append(',').Append(v.z.ToString("F3", ci)).Append(',')
            .Append((int)State).Append(',')
            .Append(f.x.ToString("F3", ci)).Append(',').Append(f.y.ToString("F3", ci)).Append(',').Append(f.z.ToString("F3", ci))
            .AppendLine();
    }

    void OnGUI()
    {
        if (!recording || !showRecordingLabel)
        {
            return;
        }
        GUI.color = Color.red;
        GUI.Label(new Rect(12, Screen.height - 32, 300, 24), "REC motion (F8 to stop)");
        GUI.color = Color.white;
    }

    void OnDisable()
    {
        if (recording)
        {
            StopRecording();
        }
    }

    // ---------------------------------------------------------------- playback

    void LoadPlayback()
    {
        playback.Clear();
        if (playbackData == null)
        {
            Debug.LogWarning("PlayerMotionTracker is in Playback mode with no playbackData.", this);
            return;
        }

        CultureInfo ci = CultureInfo.InvariantCulture;
        string[] lines = playbackData.text.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            string[] p = lines[i].Trim().Split(',');
            if (p.Length < 11)
            {
                continue;
            }

            Sample s;
            s.time = float.Parse(p[0], ci);
            s.center = new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci));
            s.velocity = new Vector3(float.Parse(p[4], ci), float.Parse(p[5], ci), float.Parse(p[6], ci));
            s.flags = (Flags)int.Parse(p[7], ci);
            s.camForward = new Vector3(float.Parse(p[8], ci), float.Parse(p[9], ci), float.Parse(p[10], ci));
            playback.Add(s);
        }

        // start from here, or replay at the recorded spot plus an offset
        playbackOrigin = playbackFromHere
            ? transform.position - (playback.Count > 0 ? playback[0].center : Vector3.zero)
            : playbackOffset;
    }

    public void SetPlayback(TextAsset data)
    {
        playbackData = data;
        LoadPlayback();
        RestartPlayback(0f);
    }

    // Scripted mode: call before this FixedUpdate runs (execution order 50) to set where the fake player is
    public void Drive(Vector3 center, Vector3 velocity, Flags flags, Vector3 camForward)
    {
        scripted = new Sample { center = center, velocity = velocity, flags = flags, camForward = camForward };
        transform.position = center;
    }

    // where the recording is right now, even before the next FixedUpdate samples it
    public Vector3 PlaybackCenter => CurrentPlaybackSample().center;

    // swaps between replaying a recording and being driven, keeping the current spot
    public void SetScripted(bool on)
    {
        Mode want = on ? Mode.Scripted : Mode.Playback;
        if (mode == want)
        {
            return;
        }
        scripted = new Sample { center = Center, velocity = Velocity, flags = State, camForward = CameraForward };
        mode = want;
        history.Clear();
        GroundedTime = AirTime = StationaryTime = 0f;
    }

    public void RestartPlayback(float normalized)
    {
        if (playback.Count == 0)
        {
            return;
        }
        float length = playback[playback.Count - 1].time;
        playbackTime = Mathf.Clamp01(normalized) * length;
        history.Clear();
        GroundedTime = AirTime = StationaryTime = 0f;
    }

    void StepPlayback(float dt)
    {
        if (playback.Count == 0)
        {
            return;
        }
        playbackTime += dt;

        float length = playback[playback.Count - 1].time;
        if (playbackTime > length)
        {
            playbackTime = loopPlayback ? playbackTime % Mathf.Max(0.01f, length) : length;
        }

        transform.position = CurrentPlaybackSample().center;
    }

    Sample CurrentPlaybackSample()
    {
        if (playback.Count == 0)
        {
            return new Sample { center = transform.position, camForward = transform.forward };
        }

        int hi = 1;
        while (hi < playback.Count - 1 && playback[hi].time < playbackTime)
        {
            hi++;
        }
        Sample a = playback[Mathf.Max(0, hi - 1)];
        Sample b = playback[hi];
        float t = Mathf.InverseLerp(a.time, b.time, playbackTime);

        Sample s;
        s.time = playbackTime;
        s.center = Vector3.Lerp(a.center, b.center, t) + playbackOrigin;
        s.velocity = Vector3.Lerp(a.velocity, b.velocity, t);
        s.flags = t < 0.5f ? a.flags : b.flags;
        s.camForward = Vector3.Slerp(a.camForward, b.camForward, t);
        return s;
    }
}
