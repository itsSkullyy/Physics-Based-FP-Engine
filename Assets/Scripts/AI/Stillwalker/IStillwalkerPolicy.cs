using UnityEngine;

// Inputs for deciding where to go (same for the utility brain and ML-Agents).
public struct StillwalkerSenses
{
    // where you are and where you're going (its local space)
    public Vector3 toPlayerLocal;
    public Vector3 toPredictedLocal;
    public Vector3 playerVelocityLocal;
    public float playerHeightAbove;
    public float playerVerticalSpeed;
    public bool playerOnGround;
    public float playerGroundedTime;
    public float playerAirTime;
    public float distance;

    // how you're moving right now
    public bool playerSwinging;
    public bool playerZipping;
    public bool playerWallRunning;
    public bool playerSliding;

    // your eyes
    public bool seen;
    public float lookAngle01;   // 0 = dead centre of your screen, 1 = right behind you
    public bool exposed;        // nothing between your camera and it, wherever you're looking

    // threats
    public bool zipAtMe;
    public float axeThreat01;   // 1 = a thrown axe is about to hit it

    // itself
    public int cracks;
    public bool oneHitAway;
    public float healProgress01;
    public bool launchReady;
    public Vector3 myVelocityLocal;

    // 8 directions around it (local, starting forward, clockwise): how far it can run, and
    // whether that spot is hidden from you
    public float[] escapeRoom01;
    public bool[] escapeHidden;

    // what kind of player you are, averaged over the last few seconds
    public PlayerProfile profile;
}

// Slow running averages of how the player plays, so the policy can tell a careful
// player from one who dives straight at it.
public struct PlayerProfile
{
    public float speed01;        // how fast they usually move
    public float airborne01;     // how much of the time they're in the air
    public float watching01;     // how much of the time they're looking at it
    public float approach01;     // 0.5 = keeping distance, 1 = closing in, 0 = backing off
    public float aggression01;   // how often they're coming down on top of it
}

public enum StillwalkerEvent
{
    PlayerLandedInRange,
    PlayerLandedOutOfRange,
    LaunchHit,
    LaunchMissed,
    ContactHit,
    Cracked,
    Shattered,
    Dodged,
    Swatted,
    Healed
}

// Lets the ML agent pick where to go. Falls back to the hand-written rules if missing.
public interface IStillwalkerPolicy
{
    bool PolicyActive { get; }

    // true while ML-Agents is training it, the rules then stay exactly as they were trained with
    bool Training { get; }

    // near zero = stand still, fast = chase (or run flat out while hurt)
    void Decide(in StillwalkerSenses senses, out Vector3 moveDirWorld, out bool fast);

    void OnStillwalkerEvent(StillwalkerEvent e);
}
