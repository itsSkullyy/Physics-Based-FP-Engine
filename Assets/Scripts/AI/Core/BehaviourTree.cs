using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Behaviour tree built in code. OnStop always runs when a node stops (finished, failed
// or aborted) so leaves can release tokens etc.
public enum BTStatus { Running, Success, Failure }

public abstract class BTNode
{
    public string Name;
    public BTStatus LastStatus { get; private set; } = BTStatus.Failure;
    public bool IsRunning { get; private set; }

    static readonly List<BTNode> NoChildren = new List<BTNode>();

    protected BTNode(string name)
    {
        Name = name;
    }

    public BTStatus Tick()
    {
        if (!IsRunning)
        {
            IsRunning = true;
            OnStart();
        }

        BTStatus s = OnTick();
        LastStatus = s;

        if (s != BTStatus.Running)
        {
            IsRunning = false;
            OnStop();
        }

        return s;
    }

    public void Abort()
    {
        if (!IsRunning) return;

        foreach (BTNode c in Children)
            c.Abort();

        IsRunning = false;
        OnStop();
    }

    public virtual IReadOnlyList<BTNode> Children => NoChildren;

    protected virtual void OnStart() { }
    protected virtual void OnStop() { }
    protected abstract BTStatus OnTick();
}

// ---------------------------------------------------------------- composites

public abstract class BTComposite : BTNode
{
    protected readonly List<BTNode> children;

    protected BTComposite(string name, BTNode[] nodes) : base(name)
    {
        children = new List<BTNode>(nodes);
    }

    public override IReadOnlyList<BTNode> Children => children;

    protected void AbortAllExcept(int keep)
    {
        for (int i = 0; i < children.Count; i++)
            if (i != keep) children[i].Abort();
    }
}

public class BTSequence : BTComposite
{
    int index;

    public BTSequence(string name, params BTNode[] nodes) : base(name, nodes) { }

    protected override void OnStart() => index = 0;

    protected override BTStatus OnTick()
    {
        while (index < children.Count)
        {
            BTStatus s = children[index].Tick();
            if (s == BTStatus.Running) return BTStatus.Running;
            if (s == BTStatus.Failure) return BTStatus.Failure;
            index++;
        }
        return BTStatus.Success;
    }
}

// reactive = re-checks from the top every tick
public class BTSelector : BTComposite
{
    readonly bool reactive;
    int index;

    public BTSelector(string name, bool reactive, params BTNode[] nodes) : base(name, nodes)
    {
        this.reactive = reactive;
    }

    protected override void OnStart() => index = 0;

    protected override BTStatus OnTick()
    {
        int start = reactive ? 0 : index;

        for (int i = start; i < children.Count; i++)
        {
            BTStatus s = children[i].Tick();
            if (s == BTStatus.Failure) continue;

            if (reactive) AbortAllExcept(i);
            index = i;
            return s;
        }

        return BTStatus.Failure;
    }
}

public class BTParallel : BTComposite
{
    public enum Policy { RequireOne, RequireAll }
    readonly Policy policy;
    readonly bool[] finished;

    public BTParallel(string name, Policy policy, params BTNode[] nodes) : base(name, nodes)
    {
        this.policy = policy;
        finished = new bool[nodes.Length];
    }

    protected override void OnStart()
    {
        for (int i = 0; i < finished.Length; i++) finished[i] = false;
    }

    protected override BTStatus OnTick()
    {
        int successes = 0;

        for (int i = 0; i < children.Count; i++)
        {
            BTNode c = children[i];
            if (finished[i])
            {
                successes++;
                continue;
            }

            BTStatus s = c.Tick();
            if (s == BTStatus.Success) finished[i] = true;
            if (s == BTStatus.Failure)
            {
                AbortAllExcept(-1);
                return BTStatus.Failure;
            }
            if (s == BTStatus.Success)
            {
                successes++;
                if (policy == Policy.RequireOne)
                {
                    AbortAllExcept(-1);
                    return BTStatus.Success;
                }
            }
        }

        return successes >= children.Count ? BTStatus.Success : BTStatus.Running;
    }
}

// ---------------------------------------------------------------- decorators

public abstract class BTDecorator : BTNode
{
    protected readonly BTNode child;
    readonly List<BTNode> list;

    protected BTDecorator(string name, BTNode child) : base(name)
    {
        this.child = child;
        list = new List<BTNode> { child };
    }

    public override IReadOnlyList<BTNode> Children => list;
}

public class BTInverter : BTDecorator
{
    public BTInverter(BTNode child) : base("Not", child) { }

    protected override BTStatus OnTick()
    {
        BTStatus s = child.Tick();
        if (s == BTStatus.Running) return s;
        return s == BTStatus.Success ? BTStatus.Failure : BTStatus.Success;
    }
}

public class BTSucceeder : BTDecorator
{
    public BTSucceeder(BTNode child) : base("Always", child) { }

    protected override BTStatus OnTick()
    {
        BTStatus s = child.Tick();
        return s == BTStatus.Running ? s : BTStatus.Success;
    }
}

public class BTGuard : BTDecorator
{
    readonly Func<bool> condition;

    public BTGuard(string name, Func<bool> condition, BTNode child) : base(name, child)
    {
        this.condition = condition;
    }

    protected override BTStatus OnTick()
    {
        if (!condition())
        {
            child.Abort();
            return BTStatus.Failure;
        }
        return child.Tick();
    }
}

// Restarts the child when any of the watched blackboard keys change (squad orders).
public class BTObserver : BTDecorator
{
    readonly Blackboard board;
    readonly string[] keys;
    readonly int[] seen;

    public BTObserver(string name, Blackboard board, string[] keys, BTNode child) : base(name, child)
    {
        this.board = board;
        this.keys = keys;
        seen = new int[keys.Length];
    }

    protected override void OnStart() => Snapshot();

    protected override BTStatus OnTick()
    {
        if (Changed())
        {
            child.Abort();
            Snapshot();
        }
        return child.Tick();
    }

    bool Changed()
    {
        for (int i = 0; i < keys.Length; i++)
            if (board.Version(keys[i]) != seen[i]) return true;
        return false;
    }

    void Snapshot()
    {
        for (int i = 0; i < keys.Length; i++) seen[i] = board.Version(keys[i]);
    }
}

public class BTCooldown : BTDecorator
{
    readonly float seconds;
    float readyAt;

    public BTCooldown(float seconds, BTNode child) : base("Cooldown", child)
    {
        this.seconds = seconds;
    }

    protected override BTStatus OnTick()
    {
        if (Time.time < readyAt) return BTStatus.Failure;

        BTStatus s = child.Tick();
        if (s != BTStatus.Running) readyAt = Time.time + seconds;
        return s;
    }
}

public class BTTimeLimit : BTDecorator
{
    readonly float seconds;
    float elapsed;

    public BTTimeLimit(float seconds, BTNode child) : base("TimeLimit", child)
    {
        this.seconds = seconds;
    }

    protected override void OnStart() => elapsed = 0f;

    protected override BTStatus OnTick()
    {
        elapsed += Time.deltaTime;
        if (elapsed > seconds)
        {
            child.Abort();
            return BTStatus.Failure;
        }
        return child.Tick();
    }
}

// count < 0 = forever
public class BTRepeat : BTDecorator
{
    readonly int count;
    int done;

    public BTRepeat(int count, BTNode child) : base("Repeat", child)
    {
        this.count = count;
    }

    protected override void OnStart() => done = 0;

    protected override BTStatus OnTick()
    {
        BTStatus s = child.Tick();
        if (s == BTStatus.Failure) return BTStatus.Failure;
        if (s == BTStatus.Success)
        {
            done++;
            if (count >= 0 && done >= count) return BTStatus.Success;
        }
        return BTStatus.Running;
    }
}

// ---------------------------------------------------------------- leaves

public class BTCondition : BTNode
{
    readonly Func<bool> condition;

    public BTCondition(string name, Func<bool> condition) : base(name)
    {
        this.condition = condition;
    }

    protected override BTStatus OnTick() => condition() ? BTStatus.Success : BTStatus.Failure;
}

public class BTAction : BTNode
{
    readonly Func<BTStatus> tick;
    readonly Action start;
    readonly Action stop;

    public BTAction(string name, Func<BTStatus> tick, Action start = null, Action stop = null) : base(name)
    {
        this.tick = tick;
        this.start = start;
        this.stop = stop;
    }

    protected override void OnStart() => start?.Invoke();
    protected override void OnStop() => stop?.Invoke();
    protected override BTStatus OnTick() => tick();
}

public class BTWait : BTNode
{
    readonly Func<float> duration;
    float elapsed;
    float target;

    public BTWait(string name, Func<float> duration) : base(name)
    {
        this.duration = duration;
    }

    protected override void OnStart()
    {
        elapsed = 0f;
        target = duration();
    }

    protected override BTStatus OnTick()
    {
        elapsed += Time.deltaTime;
        return elapsed >= target ? BTStatus.Success : BTStatus.Running;
    }
}

// ---------------------------------------------------------------- tree + builders

public class BehaviourTree
{
    public readonly BTNode Root;

    public BehaviourTree(BTNode root)
    {
        Root = root;
    }

    public BTStatus Tick() => Root.Tick();

    public void Abort() => Root.Abort();

    // for the debug overlay
    public string RunningPath
    {
        get
        {
            StringBuilder sb = new StringBuilder();
            BTNode n = Root;
            while (n != null)
            {
                if (sb.Length > 0) sb.Append('>');
                sb.Append(n.Name);

                BTNode next = null;
                foreach (BTNode c in n.Children)
                    if (c.IsRunning) { next = c; break; }
                n = next;
            }
            return sb.ToString();
        }
    }
}

public static class BT
{
    public static BTSequence Sequence(string name, params BTNode[] nodes) => new BTSequence(name, nodes);
    public static BTSelector Selector(string name, params BTNode[] nodes) => new BTSelector(name, false, nodes);
    public static BTSelector Reactive(string name, params BTNode[] nodes) => new BTSelector(name, true, nodes);
    public static BTParallel Parallel(string name, BTParallel.Policy policy, params BTNode[] nodes) => new BTParallel(name, policy, nodes);

    public static BTCondition Condition(string name, Func<bool> c) => new BTCondition(name, c);
    public static BTAction Action(string name, Func<BTStatus> tick, Action start = null, Action stop = null) => new BTAction(name, tick, start, stop);
    public static BTAction Do(string name, Action once) => new BTAction(name, () => { once(); return BTStatus.Success; });
    public static BTWait Wait(string name, float seconds) => new BTWait(name, () => seconds);
    public static BTWait Wait(string name, Func<float> seconds) => new BTWait(name, seconds);

    public static BTCondition Check<T>(Blackboard board, string key, T expected, T fallback = default) =>
        new BTCondition(key + "==" + expected, () => Equals(board.Get(key, fallback), expected));

    public static BTObserver Observe(string name, Blackboard board, string[] keys, BTNode child) =>
        new BTObserver(name, board, keys, child);

    public static BTGuard Guard(string name, Func<bool> c, BTNode child) => new BTGuard(name, c, child);
    public static BTInverter Not(BTNode child) => new BTInverter(child);
    public static BTSucceeder Always(BTNode child) => new BTSucceeder(child);
    public static BTCooldown Cooldown(float seconds, BTNode child) => new BTCooldown(seconds, child);
    public static BTTimeLimit TimeLimit(float seconds, BTNode child) => new BTTimeLimit(seconds, child);
    public static BTRepeat Repeat(int count, BTNode child) => new BTRepeat(count, child);
}
