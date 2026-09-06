using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Experiments;
using AMCCA.Core.Genome;
using AMCCA.Core.Memory;
using Dapper;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class MemoryGenomeExperimentContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrationService;
    private readonly MemoryRetrievalService _memoryService;
    private readonly GenomeMutationService _genomeService;
    private readonly ExperimentEngine _experimentEngine;

    public MemoryGenomeExperimentContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_AUDIT003_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "audit003.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
        _migrationService = new MigrationService(_factory, _testDir);
        _migrationService.UpgradeAsync().GetAwaiter().GetResult();

        _memoryService = new MemoryRetrievalService(_factory);
        _genomeService = new GenomeMutationService();
        _experimentEngine = new ExperimentEngine(_factory, _memoryService);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    #region MemoryRetrievalService (SPEC/22)

    [Fact]
    public async Task Memory_ScopeIsolation_NeverCrossesNichesWithoutRule()
    {
        // SPEC/22: "Memory never crosses niches or languages without an explicit generalisation rule"
        var recFinance = new MemoryRecord("mem-1", "niche:finance:es", "hook_pattern_a", "{\"retention\":0.72}", null, 0.85, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);
        var recGaming = new MemoryRecord("mem-2", "niche:gaming:en", "hook_pattern_b", "{\"retention\":0.81}", null, 0.90, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);

        await _memoryService.StoreMemoryAsync(recFinance);
        await _memoryService.StoreMemoryAsync(recGaming);

        var queryFinance = new MemoryQuery("niche:finance:es", "hook_pattern");
        var resultsFinance = await _memoryService.RetrieveAsync(queryFinance);

        resultsFinance.Should().HaveCount(1);
        resultsFinance.Single().Record.Scope.Should().Be("niche:finance:es");
        resultsFinance.Single().Record.Key.Should().Be("hook_pattern_a");

        var queryGaming = new MemoryQuery("niche:gaming:en", "hook_pattern");
        var resultsGaming = await _memoryService.RetrieveAsync(queryGaming);

        resultsGaming.Should().HaveCount(1);
        resultsGaming.Single().Record.Scope.Should().Be("niche:gaming:en");
        resultsGaming.Single().Record.Key.Should().Be("hook_pattern_b");
    }

    [Fact]
    public async Task Memory_ConfidenceFloor_AutonomousDecisionRefusesBelowPointFive()
    {
        // SPEC/22: "A record derived from a single production, or from an unmeasured outcome, carries confidence below 0.5 and cannot drive an autonomous decision"
        var lowConfidence = new MemoryRecord("mem-low", "general", "untested_pattern", "{\"score\":0.5}", null, 0.45, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);
        var highConfidence = new MemoryRecord("mem-high", "general", "tested_pattern", "{\"score\":0.9}", null, 0.75, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);

        await _memoryService.StoreMemoryAsync(lowConfidence);
        await _memoryService.StoreMemoryAsync(highConfidence);

        // Autonomous query must strictly filter out < 0.5
        var autoQuery = new MemoryQuery("general", "pattern", AutonomousDecision: true);
        var autoResults = await _memoryService.RetrieveAsync(autoQuery);

        autoResults.Should().HaveCount(1);
        autoResults.Single().Record.Key.Should().Be("tested_pattern");

        // Non-autonomous (operator suggestion) query includes low confidence
        var opQuery = new MemoryQuery("general", "pattern", MinConfidence: 0.3, AutonomousDecision: false);
        var opResults = await _memoryService.RetrieveAsync(opQuery);

        opResults.Should().HaveCount(2);
    }

    [Fact]
    public async Task Memory_ExclusionOfFailedProductions_ExcludedFromRetrieval()
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-failed-1', 'FAILED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
            INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
            VALUES ('prod-ok-1', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
        ");

        var recFromFailed = new MemoryRecord("mem-fail", "niche:crypto", "failed_hook", "{\"ctr\":0.01}", "prod-failed-1", 0.70, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);
        var recFromOk = new MemoryRecord("mem-ok", "niche:crypto", "ok_hook", "{\"ctr\":0.15}", "prod-ok-1", 0.70, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);

        await _memoryService.StoreMemoryAsync(recFromFailed);
        await _memoryService.StoreMemoryAsync(recFromOk);

        var query = new MemoryQuery("niche:crypto", "hook", ExcludeFailedProductions: true);
        var results = await _memoryService.RetrieveAsync(query);

        results.Should().HaveCount(1);
        results.Single().Record.Key.Should().Be("ok_hook");
    }

    [Fact]
    public async Task Memory_TimeDecay_ReducesConfidenceAccordingToHalfLife()
    {
        // 60 days elapsed with 30-day half-life: decay factor is 0.5^2 = 0.25
        var oldTime = DateTime.UtcNow.AddDays(-60);
        var oldRecord = new MemoryRecord("mem-old", "scope:tech", "aged_pattern", "{\"val\":1}", null, 0.80, "3.1.0", oldTime, oldTime);

        await _memoryService.StoreMemoryAsync(oldRecord);

        // Query with default minConfidence 0.5: 0.8 * 0.25 = 0.20 < 0.5 -> excluded
        var query = new MemoryQuery("scope:tech", "aged", MinConfidence: 0.5, HalfLifeDays: 30.0);
        var results = await _memoryService.RetrieveAsync(query);
        results.Should().BeEmpty("decayed confidence (0.20) must fall below threshold 0.5");

        // Query with lower threshold 0.15: returns record with decayed confidence
        var queryLenient = new MemoryQuery("scope:tech", "aged", MinConfidence: 0.15, AutonomousDecision: false, HalfLifeDays: 30.0);
        var resultsLenient = await _memoryService.RetrieveAsync(queryLenient);
        resultsLenient.Should().HaveCount(1);
        resultsLenient.Single().DecayedConfidence.Should().BeApproximately(0.20, 0.05);
    }

    [Fact]
    public async Task Memory_Deduplication_RemovesNearDuplicateEntries()
    {
        var rec1 = new MemoryRecord("mem-d1", "scope:news", "hook_breaking_alert_v1", "{\"style\":\"urgent\"}", null, 0.90, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);
        var rec2 = new MemoryRecord("mem-d2", "scope:news", "hook_breaking_alert_v2", "{\"style\":\"urgent_v2\"}", null, 0.70, "3.1.0", DateTime.UtcNow, DateTime.UtcNow);

        await _memoryService.StoreMemoryAsync(rec1);
        await _memoryService.StoreMemoryAsync(rec2);

        var query = new MemoryQuery("scope:news", "breaking alert");
        var results = await _memoryService.RetrieveAsync(query);

        // One record kept due to deduplication; highest composite score retained
        results.Should().HaveCount(1);
        results.Single().Record.Confidence.Should().Be(0.90);
    }

    [Fact]
    public async Task Memory_TokenBudget_StrictlyEnforcesMaxTokens()
    {
        // Insert 10 records with 100-character payload each (~25 tokens each)
        for (int i = 0; i < 10; i++)
        {
            var payload = new string('x', 100);
            await _memoryService.StoreMemoryAsync(new MemoryRecord(
                $"mem-b-{i}", "scope:budget", $"key_item_{i}", payload, null, 0.80, "3.1.0", DateTime.UtcNow, DateTime.UtcNow));
        }

        // Budget allowed: only 60 tokens (~2 records)
        var query = new MemoryQuery("scope:budget", "key", MaxTokens: 60);
        var results = await _memoryService.RetrieveAsync(query);

        results.Count.Should().BeInRange(1, 3);
        int totalTokens = results.Sum(r => (r.Record.Key.Length + r.Record.ValueJson.Length) / 4);
        totalTokens.Should().BeLessThanOrEqualTo(60);
    }

    #endregion

    #region GenomeMutationService (SPEC/48)

    [Fact]
    public void Genome_ControlledMutation_SingleDimensionAllowed()
    {
        var baseline = new ContentGenome(
            HookPattern: "QUESTION",
            PacingProfile: "FAST",
            VoiceProfile: "ENERGETIC",
            VisualStyle: "MINIMALIST",
            DurationSeconds: 45,
            DisclosurePlacement: "OPENING_5S",
            CutFrequencyPerMinute: 15.0,
            EnergyScore: 0.8,
            IsSynthetic: true
        );

        var invariants = new ChannelInvariants(MaxDurationSeconds: 60, MinDurationSeconds: 15);

        // Mutate single dimension: DurationSeconds 45 -> 50
        var result = _genomeService.MutateSingleDimension(baseline, "DurationSeconds", 50, invariants);

        result.Success.Should().BeTrue();
        result.MutatedGenome.Should().NotBeNull();
        result.MutatedGenome!.DurationSeconds.Should().Be(50);
        result.MutatedDimension.Should().Be("DurationSeconds");
        result.Drift.Should().BeGreaterThan(0.0).And.BeLessThan(1.0);
    }

    [Fact]
    public void Genome_MultiDimensionalMutation_StrictlyRejectedPerSpec48()
    {
        var baseline = new ContentGenome(
            HookPattern: "QUESTION",
            PacingProfile: "FAST",
            VoiceProfile: "ENERGETIC",
            VisualStyle: "MINIMALIST",
            DurationSeconds: 45,
            DisclosurePlacement: "OPENING_5S",
            CutFrequencyPerMinute: 15.0,
            EnergyScore: 0.8,
            IsSynthetic: true
        );

        // Candidate mutates both PacingProfile AND DurationSeconds
        var candidate = baseline with
        {
            PacingProfile = "DOCUMENTARY",
            DurationSeconds = 55
        };

        var invariants = new ChannelInvariants();
        var result = _genomeService.ValidateVariantMutation(baseline, candidate, invariants);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("SPEC/48 violation");
    }

    [Fact]
    public void Genome_DriftCalculation_IsDeterministicAndBounded()
    {
        var g1 = new ContentGenome("PATTERN_A", "FAST", "VOICE_A", "STYLE_A", 30, "OPENING_5S", 12.0, 0.7, true);
        var g2 = new ContentGenome("PATTERN_B", "BALANCED", "VOICE_B", "STYLE_B", 45, "PERSISTENT_CORNER", 18.0, 0.9, true);

        var drift1 = _genomeService.ComputeDrift(g1, g2);
        var drift2 = _genomeService.ComputeDrift(g1, g2);

        drift1.Should().Be(drift2, "drift computation must be strictly deterministic");
        drift1.Should().BeInRange(0.0, 1.0);

        var zeroDrift = _genomeService.ComputeDrift(g1, g1);
        zeroDrift.Should().Be(0.0);
    }

    [Fact]
    public void Genome_ChannelInvariant_MaxDurationExceeded_Rejected()
    {
        var baseline = new ContentGenome("PATTERN_A", "FAST", "VOICE_A", "STYLE_A", 45, "OPENING_5S", 12.0, 0.7, false);
        var invariants = new ChannelInvariants(MaxDurationSeconds: 60);

        var result = _genomeService.MutateSingleDimension(baseline, "DurationSeconds", 75, invariants);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds channel maximum 60s");
    }

    [Fact]
    public void Genome_ChannelInvariant_MissingSyntheticDisclosure_Rejected()
    {
        // SPEC/45: Synthetic content requires explicit disclosure placement
        var baseline = new ContentGenome("PATTERN_A", "FAST", "VOICE_A", "STYLE_A", 45, "OPENING_5S", 12.0, 0.7, IsSynthetic: true);
        var invariants = new ChannelInvariants(RequiresSyntheticDisclosure: true);

        var result = _genomeService.MutateSingleDimension(baseline, "DisclosurePlacement", "NONE", invariants);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("synthetic content requires explicit disclosure placement");
    }

    #endregion

    #region ExperimentEngine (SPEC/48)

    [Fact]
    public void Experiment_DeterministicVariantAssignment_ReproducibleAcrossInvocations()
    {
        var variants = new List<ExperimentVariant>
        {
            new("var-1", "exp-100", "A", "{}", null, null),
            new("var-2", "exp-100", "B", "{}", null, null),
            new("var-3", "exp-100", "C", "{}", null, null)
        };

        // Same subject ID must always yield the exact same variant
        var assigned1 = _experimentEngine.AssignVariant("exp-100", "subject-user-42", variants);
        var assigned2 = _experimentEngine.AssignVariant("exp-100", "subject-user-42", variants);
        var assigned3 = _experimentEngine.AssignVariant("exp-100", "subject-user-42", variants);

        assigned1.Label.Should().Be(assigned2.Label);
        assigned2.Label.Should().Be(assigned3.Label);

        // Different subject IDs distribute across variants
        var assignedOther = _experimentEngine.AssignVariant("exp-100", "subject-user-999", variants);
        assignedOther.Should().NotBeNull();
    }

    [Fact]
    public async Task Experiment_StoppingRule_RefusesConclusionWithInsufficientSample()
    {
        var expId = await _experimentEngine.CreateExperimentAsync(
            hypothesis: "Fast pacing increases 30s retention",
            metric: "retention_30s",
            minSample: 100, // min_sample = 100
            variants: new[] { ("A", "{}"), ("B", "{}") }
        );

        await _experimentEngine.StartExperimentAsync(expId);

        // Only insert 5 observations
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-exp-a', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-a', 'prod-exp-a', 'youtube', 'acc-a', 'ver-a', 'PUBLISHED', 'idem-exp-a', '3.1.0', datetime('now'), datetime('now'));
            ");

            var varA = await conn.QuerySingleAsync<string>("SELECT id FROM experiment_variants WHERE experiment_id = @Id AND label = 'A'", new { Id = expId });
            await conn.ExecuteAsync("UPDATE experiment_variants SET production_id = 'prod-exp-a' WHERE id = @Id", new { Id = varA });

            for (int i = 0; i < 5; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                    VALUES (@Id, 'prod-exp-a', 'pub-a', 'retention_30s', 0.65, 'API_MEASURED', '3.1.0', datetime('now'));
                ", new { Id = UlidGenerator.NewUlid() });
            }
        }

        // SPEC/48: Cannot conclude with fewer than min_sample measured observations
        var act = async () => await _experimentEngine.ConcludeExperimentAsync(expId);
        await act.Should().ThrowAsync<AmccaException>()
            .Where(ex => ex.ErrorCode == AmccaErrors.Pol001 && ex.Message.Contains("cannot be concluded with sample size"));
    }

    [Fact]
    public async Task Experiment_MetricAttribution_OnlyCountsApiMeasuredObservations()
    {
        var expId = await _experimentEngine.CreateExperimentAsync(
            hypothesis: "Testing provenance filter",
            metric: "ctr",
            minSample: 10,
            variants: new[] { ("A", "{}"), ("B", "{}") }
        );

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-attr-a', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-1', 'prod-attr-a', 'youtube', 'acc-1', 'ver-1', 'PUBLISHED', 'idem-attr-1', '3.1.0', datetime('now'), datetime('now'));
            ");

            var varA = await conn.QuerySingleAsync<string>("SELECT id FROM experiment_variants WHERE experiment_id = @Id AND label = 'A'", new { Id = expId });
            await conn.ExecuteAsync("UPDATE experiment_variants SET production_id = 'prod-attr-a' WHERE id = @Id", new { Id = varA });

            // 5 API_MEASURED and 5 ESTIMATED
            for (int i = 0; i < 5; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                    VALUES (@Id, 'prod-attr-a', 'pub-1', 'ctr', 0.10, 'API_MEASURED', '3.1.0', datetime('now'));
                ", new { Id = UlidGenerator.NewUlid() });
            }
            for (int i = 0; i < 5; i++)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                    VALUES (@Id, 'prod-attr-a', 'pub-1', 'ctr', 0.10, 'ESTIMATED', '3.1.0', datetime('now'));
                ", new { Id = UlidGenerator.NewUlid() });
            }
        }

        var analysis = await _experimentEngine.AnalyzeExperimentAsync(expId);
        analysis.TotalSampleSize.Should().Be(5, "only API_MEASURED observations may count towards experiment sample (SPEC/48)");
        analysis.MeetsMinSample.Should().BeFalse();
    }

    [Fact]
    public async Task Experiment_PoweredAndSignificant_AdoptsVariantAndEmitsDurableMemoryRecord()
    {
        var expId = await _experimentEngine.CreateExperimentAsync(
            hypothesis: "High energy hook boosts retention",
            metric: "retention",
            minSample: 40,
            variants: new[] { ("A", "{\"style\":\"control\"}"), ("B", "{\"style\":\"high_energy\"}") }
        );

        await _experimentEngine.StartExperimentAsync(expId);

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-sig-a', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
                VALUES ('prod-sig-b', 'PUBLICATION_VERIFIED', 0, 1, 'AUTONOMOUS', 'en', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-a', 'prod-sig-a', 'youtube', 'acc-a', 'ver-a', 'PUBLISHED', 'idem-sig-a', '3.1.0', datetime('now'), datetime('now'));
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-b', 'prod-sig-b', 'youtube', 'acc-b', 'ver-b', 'PUBLISHED', 'idem-sig-b', '3.1.0', datetime('now'), datetime('now'));
            ");

            var varA = await conn.QuerySingleAsync<string>("SELECT id FROM experiment_variants WHERE experiment_id = @Id AND label = 'A'", new { Id = expId });
            var varB = await conn.QuerySingleAsync<string>("SELECT id FROM experiment_variants WHERE experiment_id = @Id AND label = 'B'", new { Id = expId });
            await conn.ExecuteAsync("UPDATE experiment_variants SET production_id = 'prod-sig-a' WHERE id = @Id", new { Id = varA });
            await conn.ExecuteAsync("UPDATE experiment_variants SET production_id = 'prod-sig-b' WHERE id = @Id", new { Id = varB });

            var rand = new Random(42);
            // Control group (A): mean ~0.40
            for (int i = 0; i < 30; i++)
            {
                var val = 0.40 + (rand.NextDouble() * 0.05);
                await conn.ExecuteAsync(@"
                    INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                    VALUES (@Id, 'prod-sig-a', 'pub-a', 'retention', @Val, 'API_MEASURED', '3.1.0', datetime('now'));
                ", new { Id = UlidGenerator.NewUlid(), Val = val });
            }

            // Treatment group (B): mean ~0.75 (strong significant effect)
            for (int i = 0; i < 30; i++)
            {
                var val = 0.75 + (rand.NextDouble() * 0.05);
                await conn.ExecuteAsync(@"
                    INSERT INTO analytics_snapshots (id, production_id, publication_id, metric, value, provenance, schema_version, observed_at)
                    VALUES (@Id, 'prod-sig-b', 'pub-b', 'retention', @Val, 'API_MEASURED', '3.1.0', datetime('now'));
                ", new { Id = UlidGenerator.NewUlid(), Val = val });
            }
        }

        var analysis = await _experimentEngine.AnalyzeExperimentAsync(expId);
        analysis.TotalSampleSize.Should().Be(60);
        analysis.MeetsMinSample.Should().BeTrue();
        analysis.IsStatisticallySignificant.Should().BeTrue();
        analysis.WinningVariantLabel.Should().Be("B");
        analysis.EmittedMemoryConfidence.Should().BeGreaterThanOrEqualTo(0.5);

        // Conclude experiment successfully
        await _experimentEngine.ConcludeExperimentAsync(expId);

        using (var verifyConn = await _factory.CreateOpenConnectionAsync())
        {
            var state = await verifyConn.ExecuteScalarAsync<string>("SELECT state FROM experiments WHERE id = @Id", new { Id = expId });
            state.Should().Be("CONCLUDED");

            var winnerResult = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT result_json FROM experiment_variants WHERE experiment_id = @Id AND label = 'B'",
                new { Id = expId });
            winnerResult.Should().Contain("WINNER");
        }

        // Verify emitted durable memory record in memory_records
        var memoryQuery = new MemoryQuery("EXPERIMENTS", "winner", AutonomousDecision: true);
        var memories = await _memoryService.RetrieveAsync(memoryQuery);

        memories.Should().NotBeEmpty();
        var winnerMemory = memories.FirstOrDefault(m => m.Record.Key.Contains(expId));
        winnerMemory.Should().NotBeNull();
        winnerMemory!.Record.Confidence.Should().BeGreaterThanOrEqualTo(0.5);
    }

    #endregion
}
