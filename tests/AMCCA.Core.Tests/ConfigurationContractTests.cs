using System;
using System.IO;
using System.Text.Json;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class ConfigurationContractTests
{
    private readonly string _repoRoot;
    private readonly string _schemaJson;
    private readonly string _exampleYaml;

    public ConfigurationContractTests()
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
    public void CanonicalExampleConfig_ValidatesSuccessfully()
    {
        var configService = new ConfigService(_schemaJson);
        var config = configService.LoadFromYaml(_exampleYaml);

        config.Should().NotBeNull();
        config.SchemaVersion.Should().Be("3.1.0");
        config.Environment.Should().Be("DEVELOPMENT");
        config.AutonomyMode.Should().Be("MANUAL");
        config.PublishingEnabled.Should().BeFalse();
        config.DryRun.Should().BeTrue();
        config.Budgets.Daily.Should().Be("25.000000");
        config.Budgets.Monthly.Should().Be("300.000000");
    }

    [Fact]
    public void MissingRequiredField_AbortsWithCfg001()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = @"
schema_version: ""3.1.0""
environment: DEVELOPMENT
autonomy_mode: MANUAL
publishing_enabled: false
data_root: ""/tmp/data""
currency: EUR
";
        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg001);
    }

    [Fact]
    public void UnexpectedAdditionalProperty_AbortsWithCfg001()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml + "\nunexpected_custom_field: \"should_fail\"\n";

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg001);
    }

    [Fact]
    public void LiteralSecretInConfig_AbortsWithSec002()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml.Replace("secret://amcca/gateway_api_key", "sk-proj-literal-insecure-api-key-12345");

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec002);
    }

    [Fact]
    public void DailyBudgetExceedingMonthlyBudget_AbortsWithCfg004()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml.Replace(@"daily:          ""25.000000""", @"daily:          ""500.000000""");

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg004);
    }

    [Fact]
    public void PerProductionExceedingDailyBudget_AbortsWithCfg004()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml.Replace(@"per_production: ""5.000000""", @"per_production: ""50.000000""");

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg004);
    }

    [Theory]
    [InlineData(85, 70, 100)] // warn > pause
    [InlineData(70, 95, 90)]  // pause > block
    [InlineData(70, 85, 105)] // block > 100
    public void InconsistentThresholdOrder_AbortsWithCfg004(int warn, int pause, int block)
    {
        var configService = new ConfigService(_schemaJson);
        var modifiedYaml = _exampleYaml
            .Replace("warn_percent:   70", $"warn_percent:   {warn}")
            .Replace("pause_percent:  85", $"pause_percent:  {pause}")
            .Replace("block_percent:  100", $"block_percent:  {block}");

        var act = () => configService.LoadFromYaml(modifiedYaml);

        act.Should().Throw<AmccaException>();
    }

    [Fact]
    public void AutonomousModeWithUnverifiedGateway_AbortsWithCfg001()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml
            .Replace("autonomy_mode: MANUAL", "autonomy_mode: AUTONOMOUS")
            .Replace("capabilities_verified: false", "capabilities_verified: false");

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg001);
    }

    [Fact]
    public void PublishingEnabledInDevelopmentEnvironment_AbortsWithCfg001()
    {
        var configService = new ConfigService(_schemaJson);
        var invalidYaml = _exampleYaml
            .Replace("publishing_enabled: false", "publishing_enabled: true")
            .Replace("environment: DEVELOPMENT", "environment: DEVELOPMENT");

        var act = () => configService.LoadFromYaml(invalidYaml);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Cfg001);
    }
}
