using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.QA;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class QaStageHandlerContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;
    private readonly ArtifactStore _artifacts;
    private readonly QaThresholdProfileRegistry _thresholds = QaThresholdProfileRegistry.Base(8.5, 8.0);

    public QaStageHandlerContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_QASH_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "qash.db"));
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

    private async Task<string> NewProductionAsync()
        => (await _productions.CreateProductionAsync("t", "en", "AUTONOMOUS", "corr")).Id;

    private static StageContext Ctx(string pid, string state)
        => new(new Production { Id = pid, State = state, AutonomyMode = "AUTONOMOUS" }, "corr-qa");

    private async Task SeedClaimAsync(string pid, string id, string status)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO claims (id, production_id, text, status, materiality, subject_class, contains_personal_data, schema_version, created_at)
              VALUES (@Id, @Pid, 'fact', @Status, 'MATERIAL', 'GENERAL', 0, '3.1.0', @Now);",
            new { Id = id, Pid = pid, Status = status, Now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private async Task SeedScriptAsync(string pid, params object[] lines)
        => await _artifacts.PutTextVersionAsync(pid, "SCRIPT", JsonSerializer.Serialize(new { lines }));

    private async Task<string> SeedRenderAsync(string pid, int bytes = 4)
    {
        var f = Path.Combine(_testDir, $"r-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(f, new byte[bytes]);
        return await _artifacts.PutExistingFileVersionAsync(pid, "RENDER", f, "mp4");
    }

    private QaStageHandler Qa(string stage, IQaStageCheck check) => new(_factory, stage, check, _thresholds);

    // ---- CONTENT_QA ----------------------------------------------------

    [Fact]
    public async Task ContentQa_ValidScript_Passes_AndWritesAPassReport()
    {
        var pid = await NewProductionAsync();
        var claimId = "01J" + new string('A', 23);
        await SeedClaimAsync(pid, claimId, "VERIFIED");
        await SeedScriptAsync(pid,
            new { line_number = 1, text = "hook", claim_id = (string?)null, is_material_fact = false },
            new { line_number = 2, text = "fact", claim_id = claimId, is_material_fact = true, uncertainty_wording_present = false });

        var result = await Qa("CONTENT_QA", new ContentQaCheck(_factory, _artifacts)).HandleAsync(Ctx(pid, "CONTENT_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Advance);
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<string>("SELECT verdict FROM qa_reports WHERE production_id=@P AND stage='CONTENT_QA';", new { P = pid }))
            .Should().Be("PASS");
    }

    [Fact]
    public async Task ContentQa_UnbackedMaterialFact_Fails_AndRoutesToRework()
    {
        var pid = await NewProductionAsync();
        await SeedScriptAsync(pid, new { line_number = 1, text = "an unbacked material claim", claim_id = (string?)null, is_material_fact = true });

        var result = await Qa("CONTENT_QA", new ContentQaCheck(_factory, _artifacts)).HandleAsync(Ctx(pid, "CONTENT_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Defect);
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qa_findings f JOIN qa_reports r ON r.report_id=f.report_id WHERE r.production_id=@P AND f.severity='CRITICAL';",
            new { P = pid })).Should().Be(1);
    }

    [Fact]
    public async Task ContentQa_ProhibitedTerm_Fails()
    {
        var pid = await NewProductionAsync();
        await SeedScriptAsync(pid, new { line_number = 1, text = "This is a get rich quick scheme", claim_id = (string?)null, is_material_fact = false });

        var result = await Qa("CONTENT_QA", new ContentQaCheck(_factory, _artifacts)).HandleAsync(Ctx(pid, "CONTENT_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Defect);
    }

    [Fact]
    public async Task Qa_WithNoArtifactToCheck_Blocks()
    {
        var pid = await NewProductionAsync();

        var result = await Qa("TECHNICAL_QA", new RenderPresenceQaCheck(_factory)).HandleAsync(Ctx(pid, "TECHNICAL_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Blocked);
    }

    // ---- COMPLIANCE_QA -----------------------------------------------

    [Fact]
    public async Task ComplianceQa_AllRightsGreen_Passes()
    {
        var pid = await NewProductionAsync();
        await SeedRenderAsync(pid);
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO rights_records (id, production_id, asset_hash, status, license, provenance, commercial_use, modification, attribution_required, restrictions_json, schema_version, evaluated_at)
                  VALUES (@Id, @Pid, @Hash, 'GREEN', 'CC0', 'GENERATED', 'ALLOWED', 'ALLOWED', 0, '{}', '3.1.0', @Now);",
                new { Id = UlidGenerator.NewUlid(), Pid = pid, Hash = new string('a', 64), Now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var result = await Qa("COMPLIANCE_QA", new ComplianceQaCheck(_factory)).HandleAsync(Ctx(pid, "COMPLIANCE_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task ComplianceQa_NonGreenRights_Fails()
    {
        var pid = await NewProductionAsync();
        await SeedRenderAsync(pid);
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO rights_records (id, production_id, asset_hash, status, license, provenance, commercial_use, modification, attribution_required, restrictions_json, schema_version, evaluated_at)
                  VALUES (@Id, @Pid, @Hash, 'RED', 'UNKNOWN', 'UNKNOWN', 'DENIED', 'DENIED', 1, '{}', '3.1.0', @Now);",
                new { Id = UlidGenerator.NewUlid(), Pid = pid, Hash = new string('b', 64), Now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var result = await Qa("COMPLIANCE_QA", new ComplianceQaCheck(_factory)).HandleAsync(Ctx(pid, "COMPLIANCE_QA"));

        result.Kind.Should().Be(StageOutcomeKind.Defect);
    }

    // ---- SCORING ----------------------------------------------------

    [Fact]
    public async Task Scoring_AllStageReportsPass_Advances()
    {
        var pid = await NewProductionAsync();
        var scriptVersion = await SeedScriptVersionAsync(pid);
        await SeedQaReportAsync(pid, scriptVersion, "CONTENT_QA", 9.2, "PASS");
        await SeedQaReportAsync(pid, scriptVersion, "TECHNICAL_QA", 8.9, "PASS");

        var result = await Qa("SCORING", new ScoringCheck(_factory)).HandleAsync(Ctx(pid, "SCORING"));

        result.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task Scoring_AFailedStageReport_RoutesToRework()
    {
        var pid = await NewProductionAsync();
        var scriptVersion = await SeedScriptVersionAsync(pid);
        await SeedQaReportAsync(pid, scriptVersion, "CONTENT_QA", 9.2, "PASS");
        await SeedQaReportAsync(pid, scriptVersion, "VISUAL_QA", 3.0, "FAIL");

        var result = await Qa("SCORING", new ScoringCheck(_factory)).HandleAsync(Ctx(pid, "SCORING"));

        result.Kind.Should().Be(StageOutcomeKind.Defect);
    }

    private async Task<string> SeedScriptVersionAsync(string pid)
    {
        await SeedScriptAsync(pid, new { line_number = 1, text = "x", claim_id = (string?)null, is_material_fact = false });
        using var conn = await _factory.CreateOpenConnectionAsync();
        return (await conn.ExecuteScalarAsync<string>(
            @"SELECT av.id FROM artifact_versions av JOIN artifacts a ON a.id=av.artifact_id
              WHERE a.production_id=@P AND a.kind='SCRIPT' AND av.state='CURRENT';", new { P = pid }))!;
    }

    private async Task SeedQaReportAsync(string pid, string artifactVersionId, string stage, double score, string verdict)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO qa_reports (report_id, production_id, artifact_version_id, stage, overall_score, critical_scores_json, verdict, threshold_profile_id, schema_version, evaluated_at)
              VALUES (@Rid, @Pid, @Avid, @Stage, @Score, '{}', @Verdict, 'default', '3.1.0', @Now);",
            new { Rid = UlidGenerator.NewUlid(), Pid = pid, Avid = artifactVersionId, Stage = stage, Score = score, Verdict = verdict, Now = DateTimeOffset.UtcNow.ToString("O") });
    }
}
