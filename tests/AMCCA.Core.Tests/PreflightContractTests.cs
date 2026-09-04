using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Preflight;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PreflightContractTests
{
    private readonly string _repoRoot;
    private readonly string _schemaJson;
    private readonly string _exampleYaml;

    public PreflightContractTests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");
        _schemaJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "config.schema.json"));
        _exampleYaml = File.ReadAllText(Path.Combine(_repoRoot, "CONFIG", "config.example.yaml"));
    }

    [Fact]
    public async Task SystemPreflight_WithValidConfigAndReachableSecretStore_ReturnsPass()
    {
        var configService = new ConfigService(_schemaJson);
        var config = configService.LoadFromYaml(_exampleYaml);

        var secretStore = new InMemorySecretStore();
        var preflightService = new PreflightService();

        var report = await preflightService.RunSystemStartupPreflightAsync(config, secretStore);

        report.Status.Should().Be(PreflightStatus.Pass);
        report.IsStartupPermitted.Should().BeTrue();
    }

    [Fact]
    public async Task SystemPreflight_WithUnreachableSecretStore_AbortsStartup()
    {
        var configService = new ConfigService(_schemaJson);
        var config = configService.LoadFromYaml(_exampleYaml);

        var unreachableSecretStore = new UnreachableSecretStoreFake();
        var preflightService = new PreflightService();

        var report = await preflightService.RunSystemStartupPreflightAsync(config, unreachableSecretStore);

        report.Status.Should().Be(PreflightStatus.Abort);
        report.IsStartupPermitted.Should().BeFalse();
        report.FailureDetails.Should().Contain(d => d.Contains("Secret store unreachable"));
    }

    private class UnreachableSecretStoreFake : ISecretStore
    {
        public Task<string?> GetSecretAsync(SecretReference secretRef, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("Store unreachable");

        public Task SetSecretAsync(SecretReference secretRef, string value, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("Store unreachable");

        public Task<bool> IsReachableAsync(System.Threading.CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
