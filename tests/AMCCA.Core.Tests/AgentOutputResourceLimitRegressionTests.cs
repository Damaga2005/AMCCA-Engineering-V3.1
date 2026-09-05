using System.Collections.Generic;
using System.Text;
using AMCCA.Core.Agents;
using AMCCA.Core.Contracts;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-07 — <see cref="AgentRuntime.ValidateAgentOutput"/> applies defensive size / depth /
/// count limits before schema evaluation. Oversized or pathological output is rejected with a
/// controlled AMCCA-AI-003, never an OutOfMemoryException or stack overflow.
/// </summary>
public class AgentOutputResourceLimitRegressionTests
{
    private readonly AgentRuntime _runtime = new(new AMCCA.Core.Tools.ToolRegistry(), new NullAuditStore());

    private sealed class NullAuditStore : AMCCA.Core.Events.IAuditStore
    {
        public System.Threading.Tasks.Task AppendAuditAsync(AMCCA.Core.Events.AuditRecord record, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<AMCCA.Core.Events.AuditRecord>> GetAuditLogsAsync(
            string? correlationId = null, string? action = null, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<AMCCA.Core.Events.AuditRecord>>(
                System.Array.Empty<AMCCA.Core.Events.AuditRecord>());

        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<AMCCA.Core.Events.AuditRecord>> SearchAuditLogsAsync(
            string? query, int limit = 100, System.Threading.CancellationToken ct = default)
            => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<AMCCA.Core.Events.AuditRecord>>(
                System.Array.Empty<AMCCA.Core.Events.AuditRecord>());
    }

    private static AgentContract Contract(string? schema = null)
        => new("agent-sec07", "1.0", new HashSet<string>(), new HashSet<string>(), 1m, 10, schema);

    private void ValidateShouldThrowAi003(string outputJson, string? schema = null)
    {
        var act = () => _runtime.ValidateAgentOutput(Contract(schema), outputJson);
        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Ai003);
    }

    [Fact]
    public void OversizedDocument_IsRejected()
        => ValidateShouldThrowAi003("\"" + new string('a', 600 * 1024) + "\"");

    [Fact]
    public void ExcessiveNesting_IsRejected_WithoutStackOverflow()
    {
        var sb = new StringBuilder();
        const int depth = 500;
        for (int i = 0; i < depth; i++) sb.Append("{\"a\":");
        sb.Append("1");
        for (int i = 0; i < depth; i++) sb.Append('}');
        ValidateShouldThrowAi003(sb.ToString());
    }

    [Fact]
    public void GiganticArray_IsRejected()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 20_000; i++) sb.Append(i == 0 ? "1" : ",1");
        sb.Append(']');
        ValidateShouldThrowAi003(sb.ToString());
    }

    [Fact]
    public void TooManyProperties_IsRejected()
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < 20_000; i++) sb.Append(i == 0 ? $"\"k{i}\":1" : $",\"k{i}\":1");
        sb.Append('}');
        ValidateShouldThrowAi003(sb.ToString());
    }

    [Fact]
    public void GiganticString_IsRejected()
        => ValidateShouldThrowAi003("{\"field\":\"" + new string('x', 200_000) + "\"}");

    [Fact]
    public void MalformedJson_IsRejected_Controlled()
        => ValidateShouldThrowAi003("{ not : valid", schema: "{\"type\":\"object\"}");

    [Fact]
    public void ValidOutputWithinLimits_Passes()
    {
        var schema = "{\"type\":\"object\",\"required\":[\"hook_text\"],\"properties\":{\"hook_text\":{\"type\":\"string\"}}}";
        var act = () => _runtime.ValidateAgentOutput(Contract(schema), "{\"hook_text\":\"a punchy opening line\"}");
        act.Should().NotThrow();
    }

    [Fact]
    public void ModestNesting_WithinDepthLimit_Passes()
    {
        var sb = new StringBuilder();
        const int depth = 40;
        for (int i = 0; i < depth; i++) sb.Append("{\"a\":");
        sb.Append("1");
        for (int i = 0; i < depth; i++) sb.Append('}');

        var act = () => _runtime.ValidateAgentOutput(Contract(), sb.ToString());
        act.Should().NotThrow();
    }
}
