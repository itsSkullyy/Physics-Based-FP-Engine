using UnityEngine;

public enum FormationShape { Wedge, Line, Column, Ring }

// Formation slots. Offsets are local (x right, z forward) then rotated to face a
// direction. SquadTactics snaps them onto cover.
public static class Formations
{
    public static Vector3 Offset(FormationShape shape, int index, int count, float spacing)
    {
        int row = index / 2 + 1;
        float side = index % 2 == 0 ? -1f : 1f;

        switch (shape)
        {
            case FormationShape.Wedge:
                return new Vector3(side * row * spacing * 0.8f, 0f, -row * spacing * 0.8f);
            case FormationShape.Line:
                return new Vector3(side * row * spacing, 0f, 0f);
            case FormationShape.Column:
                return new Vector3(side * 0.6f, 0f, -(index + 1) * spacing * 0.8f);
            case FormationShape.Ring:
            {
                float a = index / (float)Mathf.Max(1, count) * Mathf.PI * 2f;
                return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * spacing;
            }
        }
        return Vector3.zero;
    }

    public static Vector3 World(FormationShape shape, int index, int count, Vector3 anchor, Vector3 forward, float spacing)
    {
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        return anchor + Quaternion.LookRotation(forward.normalized, Vector3.up) * Offset(shape, index, count, spacing);
    }
}
