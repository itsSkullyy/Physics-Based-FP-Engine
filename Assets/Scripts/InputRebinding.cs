using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Runtime rebinding. Nothing here knows about UI, so drive it from an in-game
// options menu, the sample RebindMenu, or an editor tool.
//
//   var op = InputRebinding.Begin(router.jump, index, gamepad: false, (ok, msg) => { ... });
//   op is disposed for you when it finishes or cancels; hold onto it only to abort
//   early with op.Cancel().
public static class InputRebinding
{
    public class Result
    {
        public bool success;
        public string display;          // what the player just bound, e.g. "Left Shift"
        public List<Conflict> conflicts = new List<Conflict>();
    }

    public struct Conflict
    {
        public GameAction action;
        public int bindingIndex;
        public string label;
    }

    /// Listens for the next control the player actuates and writes it into the given
    /// binding. gamepad restricts listening to pad controls so a keyboard rebind can't
    /// eat a stick twitch, and vice versa.
    public static InputActionRebindingExtensions.RebindingOperation Begin(
        GameAction target,
        int bindingIndex,
        bool gamepad,
        Action<Result> onFinish,
        PlayerInputRouter router = null)
    {
        InputAction action = target?.Action;
        if (action == null || bindingIndex < 0 || bindingIndex >= action.bindings.Count)
        {
            onFinish?.Invoke(new Result { success = false });
            return null;
        }

        bool wasEnabled = action.enabled;
        action.Disable();

        InputActionRebindingExtensions.RebindingOperation op =
            action.PerformInteractiveRebinding(bindingIndex)
                .WithControlsExcluding("<Mouse>/position")
                .WithControlsExcluding("<Mouse>/delta")
                .WithControlsExcluding("<Mouse>/scroll")
                .WithControlsExcluding("<Keyboard>/anyKey")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnCancel(o => Finish(o, target, bindingIndex, wasEnabled, false, onFinish, router))
                .OnComplete(o => Finish(o, target, bindingIndex, wasEnabled, true, onFinish, router));

        if (gamepad)
        {
            op.WithControlsHavingToMatchPath("<Gamepad>");
        }
        else
        {
            op.WithControlsExcluding("<Gamepad>");
        }

        op.Start();
        return op;
    }

    static void Finish(
        InputActionRebindingExtensions.RebindingOperation op,
        GameAction target,
        int bindingIndex,
        bool reEnable,
        bool success,
        Action<Result> onFinish,
        PlayerInputRouter router)
    {
        Result result = new Result { success = success };

        if (success)
        {
            result.display = target.DisplayAt(bindingIndex);
            if (router != null)
                result.conflicts = FindConflicts(router, target, bindingIndex);
        }

        op.Dispose();

        if (reEnable) target.Enable();

        onFinish?.Invoke(result);
    }

    /// Every other binding that now resolves to the same control. Reported rather than
    /// auto-cleared, since some overlaps (slide and dart sharing a key) are intentional.
    public static List<Conflict> FindConflicts(PlayerInputRouter router, GameAction target, int bindingIndex)
    {
        List<Conflict> found = new List<Conflict>();

        string path = target.EffectivePath(bindingIndex);
        if (string.IsNullOrEmpty(path) || router == null) return found;

        foreach (PlayerInputRouter.Entry entry in router.Registry)
        {
            GameAction ga = entry.action;
            InputAction a = ga?.Action;
            if (a == null) continue;

            for (int i = 0; i < a.bindings.Count; i++)
            {
                if (ga == target && i == bindingIndex) continue;
                if (a.bindings[i].isComposite) continue;
                if (a.bindings[i].effectivePath != path) continue;

                found.Add(new Conflict { action = ga, bindingIndex = i, label = entry.label });
            }
        }

        return found;
    }

    /// Drops the override on one binding, returning it to whatever the defaults say.
    public static void ResetBinding(GameAction target, int bindingIndex)
    {
        InputAction action = target?.Action;
        if (action == null) return;

        bool wasEnabled = action.enabled;
        if (wasEnabled) action.Disable();
        action.ApplyBindingOverride(bindingIndex, default(UnityEngine.InputSystem.InputBinding));
        if (wasEnabled) action.Enable();
    }
}
