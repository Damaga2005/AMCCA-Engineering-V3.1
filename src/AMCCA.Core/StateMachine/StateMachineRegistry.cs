using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.StateMachine;

public class StateMachineRegistry
{
    private readonly Dictionary<string, StateDefinition> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransitionDefinition> _transitionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), List<TransitionDefinition>> _transitionsByEndpoints = new();
    private readonly HashSet<string> _terminalStates = new(StringComparer.Ordinal);
    private readonly string _initialState;

    public IReadOnlyCollection<StateDefinition> States => _states.Values;
    public IReadOnlyCollection<TransitionDefinition> Transitions => _transitionsById.Values;
    public IReadOnlySet<string> TerminalStates => _terminalStates;
    public string InitialState => _initialState;

    public StateMachineRegistry(string stateMachineJson)
    {
        using var doc = JsonDocument.Parse(stateMachineJson);
        var root = doc.RootElement;

        _initialState = root.GetProperty("initial_state").GetString() ?? "INIT";

        foreach (var term in root.GetProperty("terminal_states").EnumerateArray())
        {
            var termName = term.GetString();
            if (!string.IsNullOrEmpty(termName))
            {
                _terminalStates.Add(termName);
            }
        }

        foreach (var s in root.GetProperty("states").EnumerateArray())
        {
            var name = s.GetProperty("name").GetString()!;
            var kind = s.GetProperty("kind").GetString()!;
            var desc = s.GetProperty("description").GetString()!;
            _states[name] = new StateDefinition(name, kind, desc);
        }

        foreach (var t in root.GetProperty("transitions").EnumerateArray())
        {
            var id = t.GetProperty("id").GetString()!;
            var from = t.GetProperty("from").GetString()!;
            var to = t.GetProperty("to").GetString()!;
            var trigger = t.GetProperty("trigger").GetString()!;
            var guard = t.GetProperty("guard").GetString()!;
            var actor = t.GetProperty("actor").GetString()!;

            var def = new TransitionDefinition(id, from, to, trigger, guard, actor);
            _transitionsById[id] = def;

            var key = (from, to);
            if (!_transitionsByEndpoints.TryGetValue(key, out var list))
            {
                list = new List<TransitionDefinition>();
                _transitionsByEndpoints[key] = list;
            }
            list.Add(def);
        }
    }

    public TransitionDefinition? FindTransitionById(string id)
    {
        _transitionsById.TryGetValue(id, out var def);
        return def;
    }

    public TransitionDefinition? FindTransition(string from, string to, string? trigger = null)
    {
        if (!_transitionsByEndpoints.TryGetValue((from, to), out var list) || list.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(trigger))
        {
            var matched = list.FirstOrDefault(t => string.Equals(t.Trigger, trigger, StringComparison.OrdinalIgnoreCase));
            if (matched != null) return matched;
        }

        return list[0];
    }

    public TransitionDefinition ValidateTransition(string from, string to, string? currentBlockedFrom = null, string? trigger = null)
    {
        // 1. Terminal states have no outbound transitions (SPEC/12, SPEC/13, AMCCA-STM-003)
        if (_terminalStates.Contains(from))
        {
            throw new AmccaException(
                AmccaErrors.Stm003,
                ErrorCategory.Internal,
                $"Attempted outbound transition from terminal state '{from}' to '{to}'. Terminal states have no outbound transitions.");
        }

        // 2. Transition must exist in canonical matrix (SPEC/13, AMCCA-STM-001)
        var transition = FindTransition(from, to, trigger);
        if (transition == null)
        {
            throw new AmccaException(
                AmccaErrors.Stm001,
                ErrorCategory.Internal,
                $"Illegal state transition from '{from}' to '{to}'. Transition is not listed in canonical state machine matrix (SPEC/13).");
        }

        // 3. Resuming from BLOCKED is only legal to the recorded blocked_from state (SPEC/12, AMCCA-STM-002)
        if (string.Equals(from, "BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(currentBlockedFrom) && !string.Equals(to, currentBlockedFrom, StringComparison.OrdinalIgnoreCase))
            {
                throw new AmccaException(
                    AmccaErrors.Stm002,
                    ErrorCategory.Internal,
                    $"Illegal resume from BLOCKED to '{to}'. Resuming is only legal to the recorded origin state '{currentBlockedFrom}' (SPEC/12).");
            }
        }

        return transition;
    }
}
