using System.Collections.Generic;
using System.Text;

// Key/value memory. Boards chain (follower -> squad -> global) and reads fall through
// to the parent. Each key has a version number so BT.Observe can tell when it changed.
public sealed class Blackboard
{
    readonly Dictionary<string, object> values = new Dictionary<string, object>();
    readonly Dictionary<string, int> versions = new Dictionary<string, int>();

    public Blackboard Parent { get; set; }

    public Blackboard(Blackboard parent = null)
    {
        Parent = parent;
    }

    public void Set<T>(string key, T value)
    {
        if (values.TryGetValue(key, out object old) && Equals(old, value)) return;

        values[key] = value;
        versions[key] = versions.TryGetValue(key, out int v) ? v + 1 : 1;
    }

    public T Get<T>(string key, T fallback = default)
    {
        return TryGet(key, out T v) ? v : fallback;
    }

    public bool TryGet<T>(string key, out T value)
    {
        if (values.TryGetValue(key, out object o) && o is T typed)
        {
            value = typed;
            return true;
        }

        if (Parent != null) return Parent.TryGet(key, out value);

        value = default;
        return false;
    }

    public bool Has(string key) => values.ContainsKey(key) || (Parent != null && Parent.Has(key));

    public int Version(string key)
    {
        if (versions.TryGetValue(key, out int v)) return v;
        return Parent != null ? Parent.Version(key) * 31 + 7 : 0;
    }

    public void Remove(string key)
    {
        if (!values.Remove(key)) return;
        versions[key] = versions.TryGetValue(key, out int v) ? v + 1 : 1;
    }

    public void Clear()
    {
        values.Clear();
        versions.Clear();
    }

    public IEnumerable<KeyValuePair<string, object>> LocalEntries => values;

    public string Describe(string indent = "  ")
    {
        StringBuilder sb = new StringBuilder();
        foreach (var kv in values)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(indent).Append(kv.Key).Append(" = ").Append(Format(kv.Value));
        }
        return sb.ToString();
    }

    static string Format(object o)
    {
        switch (o)
        {
            case null: return "null";
            case float f: return f.ToString("0.00");
            case UnityEngine.Vector3 v: return $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
            default: return o.ToString();
        }
    }
}

public static class BB
{
    // Per agent
    public const string PlayerVisible = "PlayerVisible";
    public const string LastSeenPos   = "LastSeenPos";
    public const string LastSeenTime  = "LastSeenTime";
    public const string AimPoint      = "AimPoint";
    public const string FirePos       = "FirePos";
    public const string FlankPos      = "FlankPos";
    public const string CoverPos      = "CoverPos";
    public const string FleePos       = "FleePos";
    public const string AssignedSpot  = "AssignedSpot";
    public const string HasToken      = "HasToken";
    public const string TimeAtPos     = "TimeAtPos";
    public const string AimedAt       = "AimedAt";
    public const string ThreatDir     = "ThreatDir";
    public const string Intent        = "Intent";
    public const string InCover       = "InCover";
    public const string HurtAt        = "HurtAt";
    public const string HeardPos      = "HeardPos";
    public const string HeardAt       = "HeardAt";
    public const string SearchPos     = "SearchPos";
    public const string Awareness     = "Awareness";
    public const string WatchDir      = "WatchDir";

    // Stillwalker
    public const string Seen          = "Seen";
    public const string LandedAt      = "LandedAt";
    public const string StalkTarget   = "StalkTarget";
    public const string LaunchTarget  = "LaunchTarget";
    public const string AmbushPos     = "AmbushPos";
    public const string PathDistance  = "PathDistance";

    // Squad board (one per Grunt leader)
    public const string SquadOrder    = "SquadOrder";
    public const string LeaderAlive   = "LeaderAlive";
    public const string SquadBroken   = "SquadBroken";
    public const string LeaderEngaged = "LeaderEngaged";
    public const string KnownPos      = "KnownPos";
    public const string KnownVel      = "KnownVel";
    public const string KnownAt       = "KnownAt";
    public const string Formation     = "Formation";
    public const string Tactic        = "Tactic";
    public const string Alert         = "Alert";

    // Global board (AIDirector)
    public const string PlayerDisarmed   = "PlayerDisarmed";
    public const string SquadLastKnown   = "SquadLastKnown";
    public const string SquadLastKnownAt = "SquadLastKnownAt";
    public const string ActiveShooters   = "ActiveShooters";
}
