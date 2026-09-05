using System;
using System.IO;
using AMCCA.Core.Configuration;
using AMCCA.Core.QA;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class PolicyConfigTypingTests
{
    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d) && !File.Exists(Path.Combine(d, "BUILD_ORDER.md"))) d = Directory.GetParent(d)?.FullName;
        return d!;
    }

    [Fact]
    public void CanonicalExampleConfig_DeserializesThePolicyBlockIntoTypedFields()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoRoot(), "CONFIG", "config.example.yaml"));
        var config = ConfigService.CreateWithBundledSchema().LoadFromYaml(yaml);

        config.Policy.Should().NotBeNull();
        config.Policy!.Qa!.OverallMin.Should().Be(8.5);
        config.Policy.Qa.CriticalMin.Should().Be(8.0);
        config.Policy.Research!.MinSources.Should().Be(2);
        config.Policy.Rework!.MinSeverity.Should().Be("MEDIUM");
        config.Policy.Reconcile!.IntervalSeconds.Should().Be(300);
    }

    [Fact]
    public void QaThresholdProfileRegistry_FromConfig_UsesConfiguredBase_OrDefaults()
    {
        var configured = QaThresholdProfileRegistry.FromConfig(new QaPolicyConfig { OverallMin = 9.1, CriticalMin = 8.7 });
        configured.Resolve("default").Should().Be(new QaThresholdProfile("default", 9.1, 8.7));

        var defaulted = QaThresholdProfileRegistry.FromConfig(null);
        defaulted.Resolve("default").Should().Be(new QaThresholdProfile("default", 8.5, 8.0));
    }
}
