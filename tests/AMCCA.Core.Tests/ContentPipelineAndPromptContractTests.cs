using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Prompts;
using AMCCA.Core.Research;
using AMCCA.Core.Scripts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ContentPipelineAndPromptContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly PromptService _promptService;

    public ContentPipelineAndPromptContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_CONTENT_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "content_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _promptService = new PromptService(_factory);
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
    public async Task UnpromptedRun_WithoutPinnedPromptVersion_IsStrictlyRejected()
    {
        // Exit criterion: "unprompted runs rejected" (D-004, SPEC/38)
        var act = async () => await _promptService.RecordAgentRunAsync(
            agentId: "ScriptAgent",
            agentVersion: "1.0.0",
            promptVersionId: null!, // unprompted
            modelId: "gpt-4o",
            modelParamsHash: "param-hash",
            inputHash: "input-hash");

        await act.Should().ThrowAsync<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Ai004);
    }

    [Fact]
    public async Task PromptVersioning_CreatesImmutableContentHashedVersions_AndPinsRun()
    {
        var template = await _promptService.CreateTemplateAsync("script-generator", "Generates video scripts from research");
        var version = await _promptService.CreateVersionAsync(template.Id, versionNo: 1, bodyText: "Write a script based on evidence: {{claims}}");

        version.BodySha256.Should().NotBeNullOrWhiteSpace();
        version.VersionNo.Should().Be(1);

        // Run with pinned prompt version succeeds
        var run = await _promptService.RecordAgentRunAsync(
            agentId: "ScriptAgent",
            agentVersion: "1.0.0",
            promptVersionId: version.Id,
            modelId: "gpt-4o",
            modelParamsHash: "param-hash",
            inputHash: "input-hash");

        run.Should().NotBeNull();
        run.PromptVersionId.Should().Be(version.Id);
    }

    [Fact]
    public void ScriptAsserting_UnmappedMaterialFact_IsRejectedWithRes001()
    {
        // Exit criterion: "Every script assertion maps to a claim"
        var script = new ScriptDocument(
            ProductionId: "prod-1",
            Lines: new List<ScriptLine>
            {
                new(LineNumber: 1, Text: "Welcome to today's news update.", ClaimId: null, IsMaterialFact: false, UncertaintyWordingPresent: false),
                new(LineNumber: 2, Text: "The central bank lowered interest rates by 50 basis points.", ClaimId: null, IsMaterialFact: true, UncertaintyWordingPresent: false) // Unmapped material fact!
            });

        var claims = new Dictionary<string, Claim>();

        var act = () => ScriptValidator.ValidateScriptAssertions(script, claims);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Res001);
    }

    [Fact]
    public void ScriptAsserting_UnknownClaim_IsRejected()
    {
        var claimId = UlidGenerator.NewUlid();
        var claims = new Dictionary<string, Claim>
        {
            [claimId] = new Claim { Id = claimId, Text = "Unverified rumor", Status = "UNKNOWN", Materiality = "MATERIAL" }
        };

        var script = new ScriptDocument(
            ProductionId: "prod-1",
            Lines: new List<ScriptLine>
            {
                new(LineNumber: 1, Text: "Reports claim an announcement is coming.", ClaimId: claimId, IsMaterialFact: true, UncertaintyWordingPresent: false)
            });

        var act = () => ScriptValidator.ValidateScriptAssertions(script, claims);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Res001);
    }

    [Fact]
    public void ScriptAsserting_EstimatedClaimWithoutUncertaintyWording_IsRejected()
    {
        var claimId = UlidGenerator.NewUlid();
        var claims = new Dictionary<string, Claim>
        {
            [claimId] = new Claim { Id = claimId, Text = "Revenue grew by 15%", Status = "ESTIMATED", Materiality = "MATERIAL" }
        };

        // Asserting estimated claim as definitive fact without uncertainty wording
        var script = new ScriptDocument(
            ProductionId: "prod-1",
            Lines: new List<ScriptLine>
            {
                new(LineNumber: 1, Text: "Revenue grew by 15% exactly.", ClaimId: claimId, IsMaterialFact: true, UncertaintyWordingPresent: false)
            });

        var act = () => ScriptValidator.ValidateScriptAssertions(script, claims);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Res001);
    }

    [Fact]
    public void Script_WithAllMaterialAssertionsMappedToVerifiedClaims_Passes()
    {
        var claimId = UlidGenerator.NewUlid();
        var claims = new Dictionary<string, Claim>
        {
            [claimId] = new Claim { Id = claimId, Text = "NASA launched the Europa Clipper mission.", Status = "VERIFIED", Materiality = "MATERIAL" }
        };

        var script = new ScriptDocument(
            ProductionId: "prod-1",
            Lines: new List<ScriptLine>
            {
                new(LineNumber: 1, Text: "Hello space enthusiasts!", ClaimId: null, IsMaterialFact: false, UncertaintyWordingPresent: false),
                new(LineNumber: 2, Text: "NASA has officially launched Europa Clipper.", ClaimId: claimId, IsMaterialFact: true, UncertaintyWordingPresent: false)
            });

        var act = () => ScriptValidator.ValidateScriptAssertions(script, claims);

        act.Should().NotThrow();
    }
}
