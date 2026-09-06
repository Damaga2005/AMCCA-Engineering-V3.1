using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Jobs;

/// <summary>Everything a job handler needs to do one job. <see cref="FenceToken"/> is the caller's proof of lease ownership.</summary>
public sealed record JobExecutionContext(JobRecord Job, long FenceToken, string WorkerId);

public enum JobResultKind
{
    /// <summary>The job's work completed. The worker marks it SUCCEEDED.</summary>
    Success,

    /// <summary>The job's work failed. The worker requeues it, or dead-letters it once attempts are exhausted (SPEC/14).</summary>
    Failure,
}

public sealed record JobResult(JobResultKind Kind, string? Detail = null)
{
    public static JobResult Success(string? detail = null) => new(JobResultKind.Success, detail);
    public static JobResult Failure(string detail) => new(JobResultKind.Failure, detail);
}

/// <summary>Does the work for one job <c>type</c>. Registered in <see cref="JobHandlerRegistry"/>.</summary>
public interface IJobHandler
{
    Task<JobResult> HandleAsync(JobExecutionContext context, CancellationToken ct = default);
}

/// <summary>
/// Maps a job <c>type</c> string to its handler. An unregistered type resolves to
/// <see cref="UnhandledJobHandler"/>, which fails the job — so it requeues and, after max_attempts,
/// dead-letters for an operator rather than looping forever or vanishing.
/// </summary>
public sealed class JobHandlerRegistry
{
    private readonly Dictionary<string, IJobHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IJobHandler Unhandled = new UnhandledJobHandler();

    public JobHandlerRegistry Register(string type, IJobHandler handler)
    {
        _handlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public bool HasHandler(string type) => _handlers.ContainsKey(type);

    public IJobHandler Resolve(string type)
        => _handlers.TryGetValue(type, out var handler) ? handler : Unhandled;
}

public sealed class UnhandledJobHandler : IJobHandler
{
    public Task<JobResult> HandleAsync(JobExecutionContext context, CancellationToken ct = default)
        => Task.FromResult(JobResult.Failure($"No job handler is registered for type '{context.Job.Type}'."));
}
