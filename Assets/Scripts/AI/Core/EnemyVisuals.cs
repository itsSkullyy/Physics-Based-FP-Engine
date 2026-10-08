using UnityEngine;

// Runtime materials/lines for bullets and lasers (same unlit shader as JuiceFX).
public static class EnemyVisuals
{
    public static Material Unlit(Color color)
    {
        Material m = new Material(JuiceFX.UnlitColorShader());
        SetColor(m, color);
        return m;
    }

    public static void SetColor(Material m, Color c)
    {
        if (m == null)
        {
            return;
        }
        if (m.HasProperty("_Color"))
        {
            m.SetColor("_Color", c);
        }
        if (m.HasProperty("_BaseColor"))
        {
            m.SetColor("_BaseColor", c);
        }
    }

    public static LineRenderer MakeLine(Transform parent, string name, Color color, float width)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        LineRenderer lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = width;
        lr.endWidth = width * 0.5f;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;

        Shader s = Shader.Find("Sprites/Default");
        lr.material = s != null ? new Material(s) : Unlit(color);
        lr.startColor = color;
        lr.endColor = color;
        lr.enabled = false;
        return lr;
    }
}
