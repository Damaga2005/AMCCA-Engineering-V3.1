using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Publishing;

public interface IPlatformAdapter
{
    string PlatformId { get; }
    Task<PublicationEvidenceResult> PollAuthoritativeEvidenceAsync(string externalId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> VerifyCapabilitiesAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    Task<UploadResult> UploadAsync(UploadRequest request, CancellationToken ct = default)
        => Task.FromResult(new UploadResult(true, "ext-" + request.IdempotencyKey, "https://platform.com/v/" + request.IdempotencyKey, null));

    Task<PublicationStatusResult> GetStatusAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult(new PublicationStatusResult("PUBLISHED", "https://platform.com/v/" + externalId, null));

    Task<IReadOnlyDictionary<string, double>> GetMetricsAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

    Task<bool> ApplySyntheticLabelAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult(true);

    Task<IReadOnlyList<RecentPublicationItem>> ListRecentAsync(string accountId, int limit = 10, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecentPublicationItem>>(Array.Empty<RecentPublicationItem>());
}
