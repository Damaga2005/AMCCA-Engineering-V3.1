using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class NoInlineProductionDdlRegressionTests
{
    private static readonly string[] ForbiddenProductionTables = new[]
    {
        "productions",
        "state_transitions",
        "jobs",
        "leases",
        "intents",
        "reconciliation_attempts",
        "sources",
        "claims",
        "claim_sources",
        "platform_accounts",
        "publications",
        "budgets",
        "approvals",
        "policy_decisions",
        "revenue_events",
        "cost_events",
        "model_registry",
        "prompt_templates",
        "prompt_versions",
        "agent_runs"
    };

    [Fact]
    public void AUDIT004_TestSuitesMustNotContainInlineProductionDdl_MustUseMigrationService()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var testDir = Path.Combine(repoRoot, "tests", "AMCCA.Core.Tests");

        var testFiles = Directory.GetFiles(testDir, "*.cs")
            .Where(f => !f.EndsWith("NoInlineProductionDdlRegressionTests.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        testFiles.Should().NotBeEmpty();

        var violations = new System.Collections.Generic.List<string>();

        foreach (var file in testFiles)
        {
            var content = File.ReadAllText(file);
            foreach (var table in ForbiddenProductionTables)
            {
                var pattern = $@"(?i)CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?{table}\b";
                if (Regex.IsMatch(content, pattern))
                {
                    violations.Add($"{Path.GetFileName(file)} contains inline DDL for table '{table}'");
                }
            }
        }

        violations.Should().BeEmpty(
            "test suites MUST NOT declare production tables with inline DDL. " +
            "They must run canonical schema migrations via MigrationService.UpgradeAsync() (AUDIT-004, SPEC/03).");
    }
}
