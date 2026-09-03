using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Publishing;

public interface IPlatformAdapter
{
    string PlatformId { get; }
    Task<PublicationEvidenceResult> PollAuthoritativeEvidenceAsync(string externalId, CancellationToken ct = default);
}
