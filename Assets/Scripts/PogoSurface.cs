using UnityEngine;

// Marks a floor or wall the axe can bounce you off (the pogo / mace bounce). Swing at
// anything else and the axe just hits it. Put it on the object with the collider, or on a
// parent of it. Give pogo surfaces their own colour (the blockout uses orange) so players
// learn to spot them.
//
// Enemies always bounce you: that's part of fighting them, not traversal.
public class PogoSurface : MonoBehaviour
{
    public static bool Allows(Collider c)
    {
        if (c == null)
        {
            return false;
        }
        return c.GetComponentInParent<PogoSurface>() != null
            || c.GetComponentInParent<EnemyAgent>() != null;
    }
}
