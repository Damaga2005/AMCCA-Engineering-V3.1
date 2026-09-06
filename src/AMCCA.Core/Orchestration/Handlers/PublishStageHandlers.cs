using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Policy;

namespace AMCCA.Core.Orchestration.Handlers;

public enum PublishDispatchStatus { Accepted, Rejected, Ambiguous }
public enum PublishTrackStatus { Processing, Verified, Rejected, Ambiguous }

public sealed record PublishDispatchResult(PublishDispatchStatus Status, string Detail);
public sealed record PublishTrackResult(PublishTrackStatus Status, string Detail);

/// <summary>
/// The real platform dispatch: intent + committed side effect + evidence, through PlatformHub and the
/// per-platform adapters (SPEC/07, SPEC/15, SPEC/44). None of that has a live OAuth connection yet, so
/// the publish handlers have no publisher and block for an operator.
/// </summary>
public interface IPublisher
{
    Task<PublishDispatchResult> DispatchAsync(string productionId, string correlationId, CancellationToken ct = default);
    Task<PublishTrackResult> PollStatusAsync(string productionId, string correlationId, CancellationToken ct = default);
}

/// <summary>
/// READY_TO_PUBLISH: the orchestrator has already recorded an ALLOW policy decision for
/// <c>publication.dispatch</c> (SPEC/08). This consumes the single-use approval (SPEC/09) and dispatches
/// via <see cref="IPublisher"/>. No publisher -> BLOCKED; consume failure -> BLOCKED (AMCCA-POL-004);
/// dispatch Accepted -> advance to PUBLISHING, Ambiguous -> UNKNOWN_EXTERNAL_STATE, Rejected -> FAILED.
/// </summary>
public sealed class PublishStageHandler : IStageHandler
{
    private readonly ApprovalManager _approvals;
    private readonly IPublisher? _publisher;

    public PublishStageHandler(ApprovalManager approvals, IPublisher? publisher)
    {
        _approvals = approvals;
        _publisher = publisher;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        if (_publisher is null)
        {
            return StageResult.Blocked(AmccaErrors.Plt001,
                "READY_TO_PUBLISH needs a platform integration (OAuth-connected publisher); none is configured.");
        }

        try
        {
            var consumed = await _approvals.ValidateAndConsumeApprovalAsync(context.Production.Id, "publication.dispatch", ct);
            if (!consumed)
            {
                return StageResult.Blocked(AmccaErrors.Pol004,
                    "No valid single-use approval for publication.dispatch to consume (SPEC/09).");
            }
        }
        catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Pol004)
        {
            return StageResult.Blocked(AmccaErrors.Pol004, ex.Message);
        }

        var result = await _publisher.DispatchAsync(context.Production.Id, context.CorrelationId, ct);
        return result.Status switch
        {
            PublishDispatchStatus.Accepted => StageResult.Advance($"Publication dispatched: {result.Detail}"),
            PublishDispatchStatus.Ambiguous => StageResult.Ambiguous(AmccaErrors.Ai002, $"Dispatch outcome ambiguous: {result.Detail}"),
            _ => StageResult.Failed(AmccaErrors.Plt001, $"Publication rejected: {result.Detail}"),
        };
    }
}

/// <summary>
/// PUBLISHING / PUBLICATION_PROCESSING / PUBLICATION_VERIFIED: polls the platform via
/// <see cref="IPublisher"/> for the dispatch's progress. No publisher -> BLOCKED. Verified -> advance,
/// Processing -> noop (still in flight), Ambiguous -> UNKNOWN_EXTERNAL_STATE, Rejected -> FAILED.
/// </summary>
public sealed class PublishTrackingStageHandler : IStageHandler
{
    private readonly IPublisher? _publisher;

    public PublishTrackingStageHandler(IPublisher? publisher) => _publisher = publisher;

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        if (_publisher is null)
        {
            return StageResult.Blocked(AmccaErrors.Plt001,
                $"{context.Production.State} needs a platform integration to poll publication status; none is configured.");
        }

        var result = await _publisher.PollStatusAsync(context.Production.Id, context.CorrelationId, ct);

        if (result.Status == PublishTrackStatus.Rejected)
        {
            return StageResult.Failed(AmccaErrors.Plt001, $"Publication rejected by platform: {result.Detail}");
        }
        if (result.Status == PublishTrackStatus.Ambiguous)
        {
            return StageResult.Ambiguous(AmccaErrors.Ai002, $"Publication status ambiguous: {result.Detail}");
        }

        // PUBLISHING: any progress means "targets accepted" -> advance. PUBLICATION_PROCESSING: only a
        // Verified poll advances; still-Processing is a noop.
        var stillProcessingIsNoop = context.Production.State == "PUBLICATION_PROCESSING";
        if (result.Status == PublishTrackStatus.Processing && stillProcessingIsNoop)
        {
            return StageResult.Noop($"Publication still processing: {result.Detail}");
        }
        return StageResult.Advance($"Publication {result.Status}: {result.Detail}");
    }
}
