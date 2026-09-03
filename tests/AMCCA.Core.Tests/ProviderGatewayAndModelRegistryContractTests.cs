using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Providers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ProviderGatewayAndModelRegistryContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ModelRegistry _modelRegistry;

    public ProviderGatewayAndModelRegistryContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_GATEWAY_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "gateway_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        using (var conn = _factory.CreateOpenConnectionAsync().GetAwaiter().GetResult())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS model_registry (
                    id TEXT PRIMARY KEY,
                    provider TEXT NOT NULL,
                    model_id TEXT NOT NULL,
                    capability TEXT NOT NULL,
                    protocol TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 0,
                    constraints_json TEXT NOT NULL,
                    pricing_snapshot_id TEXT NULL,
                    last_verified_at TEXT NULL,
                    fallback_order INTEGER NOT NULL DEFAULT 100,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(provider, model_id, capability),
                    CHECK(enabled = 0 OR last_verified_at IS NOT NULL)
                );
            ";
            cmd.ExecuteNonQuery();
        }

        _modelRegistry = new ModelRegistry(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    [Fact]
    public async Task ModelCannotBeEnabled_WithoutSuccessfulCapabilityProbe_RejectedByDbCheck()
    {
        // Exit criterion: "A model cannot be enabled without a successful capability probe"
        // Direct attempt to insert an enabled model with last_verified_at = NULL violates CHECK(enabled = 0 OR last_verified_at IS NOT NULL)
        var entry = new ModelRegistryEntry
        {
            Id = UlidGenerator.NewUlid(),
            Provider = "omnirouters",
            ModelId = "claude-3-5-sonnet",
            Capability = "text",
            Protocol = "openai-compatible",
            Enabled = true, // invalid without probe
            ConstraintsJson = "{}",
            LastVerifiedAt = null,
            FallbackOrder = 10
        };

        var act = async () => await _modelRegistry.InsertModelAsync(entry);

        await act.Should().ThrowAsync<SqliteException>()
            .Where(e => e.SqliteErrorCode == 19); // Constraint violation
    }

    [Fact]
    public async Task SuccessfulProbe_SetsLastVerifiedAt_AndEnablesModel()
    {
        var gateway = new FakeProbeGateway(probeShouldSucceed: true);

        var entry = new ModelRegistryEntry
        {
            Id = UlidGenerator.NewUlid(),
            Provider = "omnirouters",
            ModelId = "gpt-4o",
            Capability = "text",
            Protocol = "openai-compatible",
            Enabled = false,
            ConstraintsJson = "{}",
            LastVerifiedAt = null,
            FallbackOrder = 10
        };
        await _modelRegistry.InsertModelAsync(entry);

        var enabled = await _modelRegistry.VerifyAndEnableModelAsync("omnirouters", "gpt-4o", "text", gateway);

        enabled.Should().BeTrue();
        var queried = await _modelRegistry.GetModelAsync("omnirouters", "gpt-4o", "text");
        queried.Should().NotBeNull();
        queried!.Enabled.Should().BeTrue();
        queried.LastVerifiedAt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FailedProbe_LeavesModelDisabled_WithoutVerificationTimestamp()
    {
        var gateway = new FakeProbeGateway(probeShouldSucceed: false);

        var entry = new ModelRegistryEntry
        {
            Id = UlidGenerator.NewUlid(),
            Provider = "omnirouters",
            ModelId = "broken-model",
            Capability = "text",
            Protocol = "openai-compatible",
            Enabled = false,
            ConstraintsJson = "{}",
            LastVerifiedAt = null,
            FallbackOrder = 50
        };
        await _modelRegistry.InsertModelAsync(entry);

        var enabled = await _modelRegistry.VerifyAndEnableModelAsync("omnirouters", "broken-model", "text", gateway);

        enabled.Should().BeFalse();
        var queried = await _modelRegistry.GetModelAsync("omnirouters", "broken-model", "text");
        queried!.Enabled.Should().BeFalse();
        queried.LastVerifiedAt.Should().BeNull();
    }

    [Fact]
    public void TwoDistinctGatewayImplementations_ExistAndImplementPort()
    {
        // D-013 / SPEC/23: "A second IProviderGateway implementation MUST exist before autonomous mode is enabled"
        IProviderGateway primary = new OmniRoutersGatewayAdapter("https://api.omnirouters.example", "secret://amcca/key");
        IProviderGateway secondary = new DirectOpenAiCompatibleGatewayAdapter("https://api.openai.example/v1", "secret://amcca/key");

        primary.ProviderId.Should().Be("omnirouters");
        secondary.ProviderId.Should().Be("direct-openai-compatible");

        primary.Should().NotBeSameAs(secondary);
    }

    private class FakeProbeGateway : IProviderGateway
    {
        private readonly bool _probeShouldSucceed;
        public string ProviderId => "fake-gateway";

        public FakeProbeGateway(bool probeShouldSucceed)
        {
            _probeShouldSucceed = probeShouldSucceed;
        }

        public Task<ProviderProbeResult> ProbeCapabilityAsync(string provider, string modelId, string capability, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new ProviderProbeResult(
                Success: _probeShouldSucceed,
                LatencyMs: 150,
                ErrorMessage: _probeShouldSucceed ? null : "Capability probe failed: HTTP 503"));
        }

        public Task<GatewayTextResponse> GenerateTextAsync(GatewayTextRequest request, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(new GatewayTextResponse(
                Text: "Generated text",
                ProviderRequestId: "req-123",
                InputTokens: 100,
                OutputTokens: 50));
        }
    }
}
