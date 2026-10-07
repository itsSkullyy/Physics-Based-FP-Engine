using UnityEngine;

// Inputs for deciding where to stalk (same for the utility brain and ML-Agents).
public struct StillwalkerSenses
{
    public Vector3 toPlayerLocal;
    public Vector3 toPredictedLocal;
    public float playerHeightAbove;
    public float playerVerticalSpeed;
    public bool playerOnGround;
    public float playerGroundedTime;
    public float playerAirTime;
    public int cracks;
    public bool launchReady;
    public bool seen;
    public float distance;
}

public enum StillwalkerEvent
{
    PlayerLandedInRange,
    PlayerLandedOutOfRange,
    LaunchHit,
    LaunchMissed,
    ContactHit,
    Cracked,
    Shattered
}

// Lets the ML agent pick where to stalk. Falls back to the utility brain if missing.
public interface IStillwalkerPolicy
{
    bool PolicyActive { get; }

    // zero = stand still, fast = chase
    void Decide(in StillwalkerSenses senses, out Vector3 moveDirWorld, out bool fast);

    void OnStillwalkerEvent(StillwalkerEvent e);
}
