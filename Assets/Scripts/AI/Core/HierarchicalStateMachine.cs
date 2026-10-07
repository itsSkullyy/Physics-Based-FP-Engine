using System;
using System.Collections.Generic;
using System.Text;

// Hierarchical state machine. Parent transitions are checked before the children's
// so an interrupt only has to go on the parent.
public class HState
{
    public readonly string Name;
    public HState Parent { get; private set; }
    public HState ActiveChild { get; internal set; }
    public HStateMachine Machine { get; internal set; }
    public float TimeInState { get; internal set; }

    public Action OnEnter;
    public Action OnExit;
    public Action<float> OnTick;
    public Action<float> OnFixedTick;

    readonly List<HState> children = new List<HState>();
    readonly List<Transition> transitions = new List<Transition>();
    HState initialChild;

    struct Transition
    {
        public HState target;
        public Func<bool> when;
    }

    public HState(string name)
    {
        Name = name;
    }

    public IReadOnlyList<HState> Children => children;
    public bool IsLeaf => children.Count == 0;
    public bool IsActive => Machine != null && Machine.IsInState(this);

    public T Add<T>(T child, bool initial = false) where T : HState
    {
        child.Parent = this;
        children.Add(child);
        if (initial || initialChild == null) initialChild = child;
        return child;
    }

    public HState To(HState target, Func<bool> when)
    {
        transitions.Add(new Transition { target = target, when = when });
        return this;
    }

    internal HState InitialChild => initialChild;

    internal HState FirstReadyTransition()
    {
        for (int i = 0; i < transitions.Count; i++)
        {
            Transition t = transitions[i];
            if (Machine != null && Machine.IsInState(t.target)) continue;
            if (t.when()) return t.target;
        }
        return null;
    }

    protected internal virtual void Enter() => OnEnter?.Invoke();
    protected internal virtual void Exit() => OnExit?.Invoke();
    protected internal virtual void Tick(float dt) => OnTick?.Invoke(dt);
    protected internal virtual void FixedTick(float dt) => OnFixedTick?.Invoke(dt);

    public bool IsAncestorOf(HState other)
    {
        for (HState s = other?.Parent; s != null; s = s.Parent)
            if (s == this) return true;
        return false;
    }
}

public class HStateMachine
{
    public HState Root { get; }
    public HState Leaf { get; private set; }

    public event Action<HState, HState> Changed;

    HState pending;
    bool started;

    readonly List<HState> scratchPath = new List<HState>();

    public HStateMachine(HState root)
    {
        Root = root;
        Assign(root);
    }

    void Assign(HState s)
    {
        s.Machine = this;
        foreach (HState c in s.Children) Assign(c);
    }

    public void Start()
    {
        if (started) return;
        started = true;

        Root.TimeInState = 0f;
        Root.Enter();
        Leaf = DrillDown(Root);
    }

    // applied at the start of the next Tick
    public void Request(HState target)
    {
        pending = target;
    }

    // outside a tick only
    public void ForceChange(HState target)
    {
        if (!started) Start();
        ChangeTo(target);
    }

    public void Tick(float dt)
    {
        if (!started) Start();

        if (pending != null)
        {
            HState p = pending;
            pending = null;
            ChangeTo(p);
        }

        // parents first
        for (HState s = Root; s != null; s = s.ActiveChild)
        {
            // skips targets that are already active, otherwise it re-enters every frame
            HState target = s.FirstReadyTransition();
            if (target != null)
            {
                ChangeTo(target);
                break;
            }
        }

        for (HState s = Root; s != null; s = s.ActiveChild)
        {
            s.TimeInState += dt;
            s.Tick(dt);
            if (pending != null) break;
        }
    }

    public void FixedTick(float dt)
    {
        if (!started) return;
        for (HState s = Root; s != null; s = s.ActiveChild)
            s.FixedTick(dt);
    }

    public bool IsInState(HState state)
    {
        for (HState s = Root; s != null; s = s.ActiveChild)
            if (s == state) return true;
        return false;
    }

    void ChangeTo(HState target)
    {
        if (target == null) return;

        HState from = Leaf;

        // common ancestor
        HState lca = target.Parent;
        while (lca != null && !(lca == Leaf || lca.IsAncestorOf(Leaf)))
            lca = lca.Parent;

        for (HState s = Leaf; s != null && s != lca; s = s.Parent)
        {
            s.Exit();
            s.ActiveChild = null;
        }

        scratchPath.Clear();
        for (HState s = target; s != null && s != lca; s = s.Parent)
            scratchPath.Add(s);

        if (lca != null) lca.ActiveChild = scratchPath.Count > 0 ? scratchPath[scratchPath.Count - 1] : null;

        for (int i = scratchPath.Count - 1; i >= 0; i--)
        {
            HState s = scratchPath[i];
            s.TimeInState = 0f;
            s.ActiveChild = i > 0 ? scratchPath[i - 1] : null;
            s.Enter();
        }

        Leaf = DrillDown(target);
        Changed?.Invoke(from, Leaf);
    }

    HState DrillDown(HState s)
    {
        while (s.InitialChild != null)
        {
            HState child = s.InitialChild;
            s.ActiveChild = child;
            child.TimeInState = 0f;
            child.Enter();
            s = child;
        }
        return s;
    }

    public string ActivePath
    {
        get
        {
            StringBuilder sb = new StringBuilder();
            for (HState s = Root.ActiveChild; s != null; s = s.ActiveChild)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(s.Name);
            }
            return sb.ToString();
        }
    }
}
