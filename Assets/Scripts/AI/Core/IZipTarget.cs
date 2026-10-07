using UnityEngine;

// Moving zip targets (enemies). The zip follows ZipPoint. OnZipArrive returns true if
// it set the player's velocity itself.
public interface IZipTarget
{
    bool ZipTargetValid { get; }
    Vector3 ZipPoint { get; }
    float ZipArrivalRadius { get; }
    bool OnZipArrive(FirstPersonCharacterController controller, Rigidbody body);
}
