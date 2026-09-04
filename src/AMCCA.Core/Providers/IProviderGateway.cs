using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AMCCA.Core.Providers;

public interface IProviderGateway
{
    string ProviderId { get; }
    Task<ProviderProbeResult> ProbeCapabilityAsync(string provider, string modelId, string capability, CancellationToken ct = default);
    Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default);

    async IAsyncEnumerable<string> StreamTextAsync(GatewayTextRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var resp = await GenerateTextAsync(request, ct);
        yield return resp.Text;
    }
}
