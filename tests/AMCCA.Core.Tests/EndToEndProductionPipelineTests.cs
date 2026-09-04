using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Policy;
using AMCCA.Core.QA;
using AMCCA.Core.Research;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class EndToEndProductionPipelineTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly StateMachineRegistry _stateMachine;
    private readonly EventStore _eventStore;
    private readonly ProductionService _prodService;
    private readonly ResearchService _researchService;
    private readonly JobManager _jobManager;
    private readonly BudgetManager _budgetManager;

    public EndToEndProductionPipelineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "e2e_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var smJsonPath = Path.Combine(repoRoot, "SCHEMAS", "state-machine.json");
        var smJson = File.ReadAllText(smJsonPath);
        _stateMachine = new StateMachineRegistry(smJson);

        _eventStore = new EventStore(_factory);
        _prodService = new ProductionService(_factory, _stateMachine, _eventStore);
        _researchService = new ResearchService(_factory);
        _jobManager = new JobManager(_factory);
        _budgetManager = new BudgetManager(_factory);
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
        }
    }

    [Fact]
    public async Task DEF023_CompleteEndToEndProductionPipeline_FromInitToFinalVerified()
    {
        // 1. Initial creation (INIT)
        var prod = await _prodService.CreateProductionAsync("Documental Historia", "es-ES", "AUTONOMOUS", "corr-e2e-001");
        prod.State.Should().Be("INIT");
        prod.AggregateVersion.Should().Be(0);

        // 2. Transition INIT -> RESEARCHING
        prod = await _prodService.TransitionAsync(prod.Id, "RESEARCHING", "HUMAN", "corr-e2e-002");
        prod.State.Should().Be("RESEARCHING");
        prod.AggregateVersion.Should().Be(1);

        // 3. Evidence plane: Ingest source and claim
        var source = new Source
        {
            Id = UlidGenerator.NewUlid(),
            Url = "https://archive.org/documentary-source-1",
            Publisher = "Archive Trust",
            TrustTier = "PRIMARY",
            RobotsAllowed = true,
            ContentHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        };
        await _researchService.InsertSourceAsync(source);

        var claimRecord = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = prod.Id,
            Text = "El archivo historico confirma el evento de 1920.",
            Status = "VERIFIED",
            Materiality = "MATERIAL",
            SubjectClass = "HISTORY",
            ContainsPersonalData = false
        };
        await _researchService.InsertClaimWithSourceAsync(claimRecord, source.Id, "SUPPORTS");
        var fetchedClaim = await _researchService.GetClaimAsync(claimRecord.Id);
        fetchedClaim.Should().NotBeNull();
        fetchedClaim!.Status.Should().Be("VERIFIED");

        // 4. Budget reservation
        await _budgetManager.CreateBudgetAsync("production-pool", "MONTHLY", prod.Id, 100.00m, "USD");
        var reserved = await _budgetManager.TryReserveBudgetAsync("production-pool", 15.50m, "corr-e2e-res");
        reserved.Should().BeTrue();

        // 5. Canonical transition path: RESEARCHING -> RESEARCH_VERIFIED -> CONCEPT_SELECTED -> SCRIPTING -> SCRIPT_VERIFIED
        prod = await _prodService.TransitionAsync(prod.Id, "RESEARCH_VERIFIED", "HUMAN", "corr-e2e-003");
        prod.State.Should().Be("RESEARCH_VERIFIED");
        prod.AggregateVersion.Should().Be(2);

        prod = await _prodService.TransitionAsync(prod.Id, "CONCEPT_SELECTED", "HUMAN", "corr-e2e-004");
        prod.State.Should().Be("CONCEPT_SELECTED");
        prod.AggregateVersion.Should().Be(3);

        prod = await _prodService.TransitionAsync(prod.Id, "SCRIPTING", "HUMAN", "corr-e2e-005");
        prod.State.Should().Be("SCRIPTING");
        prod.AggregateVersion.Should().Be(4);

        // 6. Durable Job Claim & Execution
        var job = await _jobManager.EnqueueJobAsync("generate_script", "idemp-e2e-script", "corr-e2e-006", "{}", priority: 2, maxAttempts: 3);
        var jobClaim = await _jobManager.TryClaimNextJobAsync("render-worker-1", TimeSpan.FromMinutes(2));
        jobClaim.Should().NotBeNull();
        jobClaim!.JobId.Should().Be(job.Id);
        await _jobManager.CompleteJobOrThrowAsync(job.Id, "render-worker-1", jobClaim.FenceToken);

        // 7. Transition SCRIPTING -> SCRIPT_VERIFIED
        prod = await _prodService.TransitionAsync(prod.Id, "SCRIPT_VERIFIED", "HUMAN", "corr-e2e-007");
        prod.State.Should().Be("SCRIPT_VERIFIED");
        prod.AggregateVersion.Should().Be(5);

        // 8. Deterministic QA Engine Evaluation
        var artifactVersionId = UlidGenerator.NewUlid();
        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var artId = UlidGenerator.NewUlid();
            await connection.ExecuteAsync("INSERT INTO artifacts (id, production_id, kind, created_at, updated_at) VALUES (@Id, @ProdId, 'SCRIPT', datetime('now'), datetime('now'));",
                new { Id = artId, ProdId = prod.Id });
            await connection.ExecuteAsync("INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at) VALUES (@Id, @ArtId, 1, 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855', 1024, 'script.json', 'CURRENT', datetime('now'));",
                new { Id = artifactVersionId, ArtId = artId });
        }

        var criticalScores = new CriticalScores(9.0, 9.0, 9.0, 9.0, 9.0);
        var qaFindings = new[]
        {
            new QaFinding(
                UlidGenerator.NewUlid(),
                UlidGenerator.NewUlid(),
                "DETERMINISTIC_AUDIO_SYNC",
                CheckKind.DETERMINISTIC,
                CheckStatus.PASS,
                Severity.LOW,
                artifactVersionId,
                null, null, null,
                "Audio sync validated within 10ms")
        };
        var verdict = QaVerdictEvaluator.EvaluateVerdict(9.5, criticalScores, qaFindings, hasDeterministicChecks: true);
        verdict.Should().Be("PASS");

        // 9. Assert Unbroken Audit Trail and Event Log Integrity
        using (var connection = await _factory.CreateOpenConnectionAsync())
        {
            var eventCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM events WHERE aggregate_id = @ProdId;",
                new { ProdId = prod.Id });
            eventCount.Should().Be(6, "1 creation event + 5 state change events");

            var transitionCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM state_transitions WHERE production_id = @ProdId;",
                new { ProdId = prod.Id });
            transitionCount.Should().Be(5, "exactly 5 state transitions recorded in history");
        }
    }
}
