using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Providers;
using AMCCA.Core.Research;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class AgentResearchAgentContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;
    private readonly ResearchService _research;

    public AgentResearchAgentContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGRES_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "agres.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        var reg = new StateMachineRegistry(File.ReadAllText(Path.Combine(FindRepoRoot(), "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, reg, new EventStore(_factory));
        _research = new ResearchService(_factory);
    }

    public void Dispose()
    {
        _research.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d) && !File.Exists(Path.Combine(d, "BUILD_ORDER.md"))) d = Directory.GetParent(d)?.FullName;
        return d!;
    }

    private sealed class ScriptedGateway : IProviderGateway
    {
        private readonly Queue<string> _responses;
        public string ProviderId => "scripted";
        public ScriptedGateway(IEnumerable<string> r) => _responses = new(r);
        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, 1));
        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, CancellationToken ct = default)
            => Task.FromResult(new GatewayTextResponse(
                _responses.Count > 0 ? _responses.Dequeue() : "{\"final\": \"end of script\"}", "req", 5, 5));
    }

    private async Task<(string Pid, string S1, string S2)> SeedProductionAndSourcesAsync()
    {
        var pid = (await _productions.CreateProductionAsync("The topic", "en", "AUTONOMOUS", "corr")).Id;
        async Task<string> Src(string pub)
        {
            var s = new Source { Id = UlidGenerator.NewUlid(), Url = $"https://{pub}.example/x", Publisher = pub,
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"), ContentHash = new string('a', 64), TrustTier = "SECONDARY" };
            await _research.InsertSourceAsync(s);
            return s.Id;
        }
        return (pid, await Src("alpha"), await Src("beta"));
    }

    [Fact]
    public async Task RunsTheLoop_RecordsAClaimAgainstSeededSources_AndVerifiesIt()
    {
        var (pid, s1, s2) = await SeedProductionAndSourcesAsync();
        var gw = new ScriptedGateway(new[]
        {
            $"{{\"tool\": \"record_claim\", \"input\": {{\"text\": \"a material fact\", \"materiality\": \"MATERIAL\", " +
            $"\"sources\": [{{\"source_id\": \"{s1}\", \"relation\": \"SUPPORTS\"}}, {{\"source_id\": \"{s2}\", \"relation\": \"SUPPORTS\"}}]}}}}",
            "{\"tool\": \"evaluate_claims\", \"input\": {}}",
            "{\"final\": \"all material claims verified\"}",
        });
        var agent = new AgentResearchAgent(_productions, _research, new AuditStore(_factory), gw);

        await agent.PerformResearchAsync(pid, "corr-run");

        using var conn = await _factory.CreateOpenConnectionAsync();
        var counts = await conn.QuerySingleAsync<(int Total, int Verified)>(
            @"SELECT COUNT(*) AS Total, COALESCE(SUM(CASE WHEN status='VERIFIED' THEN 1 ELSE 0 END),0) AS Verified
              FROM claims WHERE production_id=@Id AND materiality='MATERIAL';", new { Id = pid });
        counts.Total.Should().Be(1);
        counts.Verified.Should().Be(1, "the loop recorded the claim, cited two independent sources, and evaluated it");
    }

    [Fact]
    public async Task ThenResearchStageHandler_SeesTheVerifiedResearch_AndAdvances()
    {
        var (pid, s1, s2) = await SeedProductionAndSourcesAsync();
        var gw = new ScriptedGateway(new[]
        {
            $"{{\"tool\": \"record_claim\", \"input\": {{\"text\": \"fact\", \"materiality\": \"MATERIAL\", " +
            $"\"sources\": [{{\"source_id\": \"{s1}\"}}, {{\"source_id\": \"{s2}\"}}]}}}}",
            "{\"tool\": \"evaluate_claims\", \"input\": {}}",
            "{\"final\": \"done\"}",
        });
        var handler = new ResearchStageHandler(_factory, new AgentResearchAgent(_productions, _research, new AuditStore(_factory), gw));

        var result = await handler.HandleAsync(new StageContext(
            new Production { Id = pid, State = "RESEARCHING", AutonomyMode = "AUTONOMOUS" }, "corr-h"));

        result.Kind.Should().Be(AMCCA.Core.Orchestration.StageOutcomeKind.Advance);
    }
}
