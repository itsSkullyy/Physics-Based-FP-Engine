using System;
using System.Collections.Generic;
using System.Text;

// GOAP. World state is bool facts in a 64 bit mask, A* over actions until the goal's
// facts hold. Each action runs as its own behaviour tree.
public struct WorldState
{
    public ulong Values;
    public ulong Mask;

    public static WorldState Empty => default;

    public bool Get(int fact) => (Values & (1UL << fact)) != 0;
    public bool Knows(int fact) => (Mask & (1UL << fact)) != 0;

    public WorldState With(int fact, bool value)
    {
        ulong bit = 1UL << fact;
        WorldState s = this;
        s.Mask |= bit;
        if (value) s.Values |= bit;
        else s.Values &= ~bit;
        return s;
    }

    public bool Satisfies(WorldState required)
    {
        return ((Values ^ required.Values) & required.Mask) == 0;
    }

    public WorldState Apply(WorldState effects)
    {
        WorldState s;
        s.Values = (Values & ~effects.Mask) | (effects.Values & effects.Mask);
        s.Mask = Mask | effects.Mask;
        return s;
    }

    public int Mismatches(WorldState required)
    {
        ulong diff = (Values ^ required.Values) & required.Mask;
        int count = 0;
        while (diff != 0) { diff &= diff - 1; count++; }
        return count;
    }

    public string Describe(string[] names)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            if (!Knows(i)) continue;
            if (sb.Length > 0) sb.Append(' ');
            if (!Get(i)) sb.Append('!');
            sb.Append(names != null && i < names.Length ? names[i] : i.ToString());
        }
        return sb.ToString();
    }
}

public class GoapAction
{
    public readonly string Name;
    public WorldState Preconditions;
    public WorldState Effects;

    public Func<float> Cost = () => 1f;

    // for checks that aren't facts
    public Func<bool> IsPossible = () => true;

    // new tree each time the action starts
    public Func<BTNode> CreateBehaviour;

    public GoapAction(string name)
    {
        Name = name;
    }

    public GoapAction Requires(int fact, bool value = true)
    {
        Preconditions = Preconditions.With(fact, value);
        return this;
    }

    public GoapAction Causes(int fact, bool value = true)
    {
        Effects = Effects.With(fact, value);
        return this;
    }
}

public class GoapGoal
{
    public readonly string Name;
    public WorldState Desired;
    public Func<float> Priority = () => 1f;

    public GoapGoal(string name)
    {
        Name = name;
    }

    public GoapGoal Wants(int fact, bool value = true)
    {
        Desired = Desired.With(fact, value);
        return this;
    }
}

public static class GoapPlanner
{
    class Node
    {
        public WorldState state;
        public float g;
        public float f;
        public Node parent;
        public GoapAction action;
        public int depth;
    }

    public static List<GoapAction> Plan(WorldState start, GoapGoal goal, IReadOnlyList<GoapAction> actions,
                                        int maxDepth = 6, int maxExpansions = 400)
    {
        if (start.Satisfies(goal.Desired)) return new List<GoapAction>();

        List<GoapAction> usable = new List<GoapAction>();
        foreach (GoapAction a in actions)
            if (a.IsPossible()) usable.Add(a);

        List<Node> open = new List<Node>();
        HashSet<ulong> closed = new HashSet<ulong>();

        open.Add(new Node { state = start, g = 0f, f = start.Mismatches(goal.Desired) });

        int expansions = 0;
        while (open.Count > 0 && expansions++ < maxExpansions)
        {
            int bestIndex = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].f < open[bestIndex].f) bestIndex = i;

            Node current = open[bestIndex];
            open.RemoveAt(bestIndex);

            if (current.state.Satisfies(goal.Desired))
                return Unwind(current);

            ulong key = current.state.Values ^ (current.state.Mask * 0x9E3779B97F4A7C15UL);
            if (!closed.Add(key)) continue;
            if (current.depth >= maxDepth) continue;

            foreach (GoapAction a in usable)
            {
                if (!current.state.Satisfies(a.Preconditions)) continue;

                WorldState next = current.state.Apply(a.Effects);
                if (next.Values == current.state.Values && next.Mask == current.state.Mask) continue;

                float g = current.g + Math.Max(0.01f, a.Cost());
                open.Add(new Node
                {
                    state = next,
                    g = g,
                    f = g + next.Mismatches(goal.Desired),
                    parent = current,
                    action = a,
                    depth = current.depth + 1
                });
            }
        }

        return null;
    }

    static List<GoapAction> Unwind(Node n)
    {
        List<GoapAction> plan = new List<GoapAction>();
        for (; n != null && n.action != null; n = n.parent)
            plan.Add(n.action);
        plan.Reverse();
        return plan;
    }
}

public class GoapAgent
{
    public readonly List<GoapAction> Actions = new List<GoapAction>();
    public readonly List<GoapGoal> Goals = new List<GoapGoal>();

    public GoapGoal CurrentGoal { get; private set; }
    public GoapAction CurrentAction { get; private set; }
    public BTNode CurrentBehaviour { get; private set; }
    public float GoalCheckInterval = 0.5f;

    public event Action<GoapGoal, List<GoapAction>> Replanned;

    readonly Func<WorldState> sense;
    readonly string[] factNames;
    readonly Queue<GoapAction> plan = new Queue<GoapAction>();
    readonly List<GoapAction> lastPlan = new List<GoapAction>();
    bool invalid = true;
    float goalTimer;

    public GoapAgent(Func<WorldState> sense, string[] factNames)
    {
        this.sense = sense;
        this.factNames = factNames;
    }

    public void Invalidate() => invalid = true;

    bool forced;

    // F6 menu: run one action straight away
    public void Force(GoapAction action)
    {
        CurrentBehaviour?.Abort();
        plan.Clear();
        lastPlan.Clear();
        lastPlan.Add(action);
        CurrentAction = action;
        CurrentBehaviour = action.CreateBehaviour != null ? action.CreateBehaviour() : null;
        forced = true;
    }

    public void Stop()
    {
        forced = false;
        CurrentBehaviour?.Abort();
        CurrentBehaviour = null;
        CurrentAction = null;
        plan.Clear();
        invalid = true;
    }

    public void Tick(float dt)
    {
        if (forced)
        {
            if (CurrentBehaviour != null && CurrentBehaviour.Tick() == BTStatus.Running) return;
            forced = false;
            CurrentBehaviour = null;
            CurrentAction = null;
            invalid = true;
            return;
        }

        goalTimer -= dt;
        if (goalTimer <= 0f)
        {
            goalTimer = GoalCheckInterval;
            GoapGoal best = PickGoal();
            if (best != CurrentGoal) invalid = true;
        }

        if (invalid) Replan();
        if (CurrentAction == null && !Advance()) return;

        BTStatus s = CurrentBehaviour != null ? CurrentBehaviour.Tick() : BTStatus.Success;
        if (s == BTStatus.Running) return;

        if (s == BTStatus.Failure)
        {
            invalid = true;
            CurrentBehaviour = null;
            CurrentAction = null;
            return;
        }

        CurrentBehaviour = null;
        CurrentAction = null;
        if (plan.Count == 0) invalid = true;
    }

    GoapGoal PickGoal()
    {
        GoapGoal best = null;
        float bestPriority = 0f;
        WorldState now = sense();

        foreach (GoapGoal g in Goals)
        {
            if (now.Satisfies(g.Desired)) continue;
            float p = g.Priority();
            if (p > bestPriority)
            {
                bestPriority = p;
                best = g;
            }
        }
        return best;
    }

    void Replan()
    {
        invalid = false;
        CurrentBehaviour?.Abort();
        CurrentBehaviour = null;
        CurrentAction = null;
        plan.Clear();
        lastPlan.Clear();

        WorldState now = sense();

        // highest priority goal that has a plan
        List<GoapGoal> ordered = new List<GoapGoal>(Goals);
        ordered.Sort((a, b) => b.Priority().CompareTo(a.Priority()));

        CurrentGoal = null;
        foreach (GoapGoal g in ordered)
        {
            if (g.Priority() <= 0f || now.Satisfies(g.Desired)) continue;

            List<GoapAction> p = GoapPlanner.Plan(now, g, Actions);
            if (p == null || p.Count == 0) continue;

            CurrentGoal = g;
            foreach (GoapAction a in p) plan.Enqueue(a);
            lastPlan.AddRange(p);
            break;
        }

        Replanned?.Invoke(CurrentGoal, lastPlan);
    }

    bool Advance()
    {
        if (plan.Count == 0) return false;

        CurrentAction = plan.Dequeue();
        CurrentBehaviour = CurrentAction.CreateBehaviour != null ? CurrentAction.CreateBehaviour() : null;
        return true;
    }

    public string Describe()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(CurrentGoal != null ? CurrentGoal.Name : "(no goal)");
        sb.Append(": ");
        for (int i = 0; i < lastPlan.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            bool active = lastPlan[i] == CurrentAction;
            if (active) sb.Append('[');
            sb.Append(lastPlan[i].Name);
            if (active) sb.Append(']');
        }
        return sb.ToString();
    }

    public string DescribeWorld() => sense().Describe(factNames);
}
