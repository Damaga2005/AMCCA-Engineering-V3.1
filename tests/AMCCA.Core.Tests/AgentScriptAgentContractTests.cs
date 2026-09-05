using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Providers;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class AgentScriptAgentContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;
    private readonly ArtifactStore _artifacts;

    public AgentScriptAgentContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AGSCR_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "agscr.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        var reg = new StateMachineRegistry(File.ReadAllText(Path.Combine(FindRepoRoot(), "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, reg, new EventStore(_factory));
        _artifacts = new ArtifactStore(_factory, Path.Combine(_testDir, "data"));
    }

    public void Dispose()
    {
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
        private readonly Queue<string> _r;
        public string ProviderId => "scripted";
        public ScriptedGateway(params string[] r) => _r = new(r);
        public Task<ProviderProbeResult> ProbeCapabilityAsync(string p, string m, string c, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, 1));
        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest req, CancellationToken ct = default)
            => Task.FromResult(new GatewayTextResponse(_r.Count > 0 ? _r.Dequeue() : "{\"final\": \"x\"}", "req", 5, 5));
    }

    private async Task<(string Pid, string ClaimId)> SeedProductionWithVerifiedClaimAsync()
    {
        var pid = (await _productions.CreateProductionAsync("A topic", "en", "AUTONOMOUS", "corr")).Id;
        var claimId = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
              VALUES (@Id, @Pid, 'the verified fact', 'VERIFIED', 'MATERIAL', 'GENERAL', 0, '3.1.0', @Now);",
            new { Id = claimId, Pid = pid, Now = DateTimeOffset.UtcNow.ToString("O") });
        return (pid, claimId);
    }

    private AgentScriptAgent Agent(IProviderGateway gw)
        => new(_productions, _factory, new AuditStore(_factory), gw, _artifacts);

    [Fact]
    public async Task GeneratesAScript_FromVerifiedClaims_AndPersistsItAsTheCurrentArtifact()
    {
        var (pid, claimId) = await SeedProductionWithVerifiedClaimAsync();
        var final = JsonSerializer.Serialize(new
        {
            final = new
            {
                estimated_spoken_duration_sec = 45,
                lines = new object[]
                {
                    new { line_number = 1, text = "Hook line", claim_id = (string?)null, is_material_fact = false },
                    new { line_number = 2, text = "the verified fact", claim_id = claimId, is_material_fact = true, uncertainty_wording_present = false },
                },
            },
        });

        var script = await Agent(new ScriptedGateway(final)).GenerateScriptAsync(pid, "corr-s");

        script.Lines.Should().HaveCount(2);
        script.EstimatedSpokenDurationSec.Should().Be(45);
        (await _artifacts.GetCurrentTextAsync(pid, "SCRIPT")).Should().NotBeNull();
    }

    [Fact]
    public async Task NoVerifiedClaims_Throws()
    {
        var pid = (await _productions.CreateProductionAsync("t", "en", "AUTONOMOUS", "corr")).Id;

        var act = async () => await Agent(new ScriptedGateway("{\"final\": {\"lines\": []}}")).GenerateScriptAsync(pid, "corr-s");

        await act.Should().ThrowAsync<AmccaException>();
    }

    [Fact]
    public async Task ScriptStageHandler_WithThisAgent_ValidatesTheScriptAndAdvances()
    {
        var (pid, claimId) = await SeedProductionWithVerifiedClaimAsync();
        var final = JsonSerializer.Serialize(new
        {
            final = new
            {
                lines = new object[]
                {
                    new { line_number = 1, text = "the verified fact", claim_id = claimId, is_material_fact = true, uncertainty_wording_present = false },
                },
            },
        });
        var handler = new ScriptStageHandler(_factory, Agent(new ScriptedGateway(final)));

        var result = await handler.HandleAsync(new StageContext(
            new Production { Id = pid, State = "SCRIPTING", AutonomyMode = "AUTONOMOUS" }, "corr-h"));

        result.Kind.Should().Be(StageOutcomeKind.Advance);
    }
}
