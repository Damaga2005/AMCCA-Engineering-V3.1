using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Orchestration;

/// <summary>
/// Maps a production state name to the <see cref="IStageHandler"/> that does that state's work. States
/// without a handler resolve to <see cref="UnhandledStageHandler"/>, which blocks the production for an
/// operator (AMCCA-ORC-001) rather than silently sailing past unbuilt work.
/// </summary>
public sealed class StageHandlerRegistry
{
    private readonly Dictionary<string, IStageHandler> _handlers = new(StringComparer.Ordinal);
    private static readonly IStageHandler Unhandled = new UnhandledStageHandler();

    public StageHandlerRegistry Register(string stateName, IStageHandler handler)
    {
        _handlers[stateName] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public bool HasHandler(string stateName) => _handlers.ContainsKey(stateName);

    public IStageHandler Resolve(string stateName)
        => _handlers.TryGetValue(stateName, out var handler) ? handler : Unhandled;
}

/// <summary>Fallback for a state with no registered handler: block for an operator, never advance.</summary>
public sealed class UnhandledStageHandler : IStageHandler
{
    public Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
        => Task.FromResult(StageResult.Blocked(
            AmccaErrors.Orc001,
            $"No stage handler is registered for state '{context.Production.State}'. " +
            "An operator must supply the stage result, or a handler must be added for this state."));
}
