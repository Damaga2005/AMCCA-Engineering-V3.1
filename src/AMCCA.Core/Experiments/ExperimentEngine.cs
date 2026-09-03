using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Memory;
using Dapper;

namespace AMCCA.Core.Experiments;

public class ExperimentEngine
{
    private readonly DatabaseConnectionFactory _factory;
    private readonly MemoryRetrievalService? _memoryService;

    public ExperimentEngine(DatabaseConnectionFactory factory, MemoryRetrievalService? memoryService = null)
    {
        _factory = factory;
        _memoryService = memoryService;
    }

    public async Task<string> CreateExperimentAsync(
        string hypothesis,
        string metric,
        int minSample,
        IReadOnlyList<(string Label, string ParametersJson)> variants)
    {
        if (variants == null || variants.Count < 2)
            throw new ArgumentException("An experiment requires at least 2 variants (A/B/n)");

        var experimentId = "exp-" + UlidGenerator.NewUlid();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = await _factory.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        await conn.ExecuteAsync(@"
            INSERT INTO experiments (id, hypothesis, state, metric, min_sample, created_at, updated_at)
            VALUES (@Id, @Hypothesis, 'DRAFT', @Metric, @MinSample, @Now, @Now);
        ", new { Id = experimentId, Hypothesis = hypothesis, Metric = metric, MinSample = minSample, Now = now }, transaction: tx);

        foreach (var v in variants)
        {
            var varId = "var-" + UlidGenerator.NewUlid();
            await conn.ExecuteAsync(@"
                INSERT INTO experiment_variants (id, experiment_id, label, parameters_json)
                VALUES (@Id, @ExperimentId, @Label, @Params);
            ", new { Id = varId, ExperimentId = experimentId, Label = v.Label, Params = v.ParametersJson }, transaction: tx);
        }

        tx.Commit();
        return experimentId;
    }

    public async Task StartExperimentAsync(string experimentId)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        var rows = await conn.ExecuteAsync(@"
            UPDATE experiments
            SET state = 'RUNNING', started_at = datetime('now'), updated_at = datetime('now')
            WHERE id = @Id AND state = 'DRAFT';
        ", new { Id = experimentId });

        if (rows == 0)
        {
            throw new InvalidOperationException($"Experiment {experimentId} cannot be started (must be in DRAFT state)");
        }
    }

    public ExperimentVariant AssignVariant(string experimentId, string subjectId, IReadOnlyList<ExperimentVariant> variants)
    {
        if (variants == null || variants.Count == 0)
            throw new ArgumentException("No variants available for assignment");

        // Deterministic A/B/n assignment based on SHA-256(experimentId + ":" + subjectId)
        var sorted = variants.OrderBy(v => v.Label, StringComparer.Ordinal).ToList();
        var inputBytes = Encoding.UTF8.GetBytes($"{experimentId}:{subjectId}");
        var hashBytes = SHA256.HashData(inputBytes);

        var bucket = BitConverter.ToUInt32(hashBytes, 0);
        int index = (int)(bucket % (uint)sorted.Count);

        return sorted[index];
    }

    public async Task<ExperimentAnalysis> AnalyzeExperimentAsync(string experimentId)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();

        var exp = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
            SELECT id, hypothesis, state, metric, min_sample AS MinSample
            FROM experiments
            WHERE id = @Id
        ", new { Id = experimentId });

        if (exp == null)
            throw new KeyNotFoundException($"Experiment {experimentId} not found");

        int minSample = (int)exp.MinSample;
        string metric = (string)exp.metric;

        var variants = (await conn.QueryAsync<dynamic>(@"
            SELECT id, experiment_id, label, parameters_json, production_id
            FROM experiment_variants
            WHERE experiment_id = @Id
            ORDER BY label
        ", new { Id = experimentId })).ToList();

        // Query only API_MEASURED observations (SPEC/48: count of API_MEASURED observations, not elapsed time)
        var observationsByVariant = new Dictionary<string, List<double>>();

        foreach (var v in variants)
        {
            string label = (string)v.label;
            string? prodId = (string?)v.production_id;
            var values = new List<double>();

            if (!string.IsNullOrEmpty(prodId))
            {
                var obs = await conn.QueryAsync<double>(@"
                    SELECT a.value
                    FROM analytics_snapshots a
                    WHERE a.production_id = @ProdId
                      AND a.metric = @Metric
                      AND a.provenance = 'API_MEASURED'
                ", new { ProdId = prodId, Metric = metric });

                values.AddRange(obs);
            }

            observationsByVariant[label] = values;
        }

        int totalSample = observationsByVariant.Values.Sum(list => list.Count);
        bool meetsMinSample = totalSample >= minSample;

        if (variants.Count < 2 || totalSample < 2)
        {
            return new ExperimentAnalysis(
                experimentId,
                totalSample,
                meetsMinSample,
                PValue: 1.0,
                EffectSize: 0.0,
                IsStatisticallySignificant: false,
                WinningVariantLabel: null,
                Recommendation: "INSUFFICIENT_DATA",
                EmittedMemoryConfidence: null
            );
        }

        var control = observationsByVariant.First().Value;
        var treatment = observationsByVariant.Skip(1).First().Value;

        var (pValue, effectSize) = CalculateTwoSampleWelch(control, treatment);
        bool isSignificant = pValue < 0.05 && meetsMinSample;

        string? winner = null;
        if (isSignificant && effectSize > 0)
        {
            winner = variants[1].label;
        }
        else if (isSignificant && effectSize < 0)
        {
            winner = variants[0].label;
        }

        string recommendation = isSignificant
            ? $"ADOPT_VARIANT_{winner}"
            : (meetsMinSample ? "NO_SIGNIFICANT_DIFFERENCE" : "CONTINUE_DATA_COLLECTION");

        double? emittedConfidence = null;
        if (isSignificant)
        {
            // Confidence derived from sample volume and effect magnitude (SPEC/22 & SPEC/48)
            emittedConfidence = Math.Min(0.95, 0.50 + (0.25 * Math.Min(1.0, totalSample / (double)(minSample * 2))) + (0.20 * Math.Min(1.0, Math.Abs(effectSize))));
        }

        return new ExperimentAnalysis(
            experimentId,
            totalSample,
            meetsMinSample,
            pValue,
            effectSize,
            isSignificant,
            winner,
            recommendation,
            emittedConfidence
        );
    }

    public async Task ConcludeExperimentAsync(string experimentId)
    {
        var analysis = await AnalyzeExperimentAsync(experimentId);

        // SPEC/48: "An experiment cannot be CONCLUDED with fewer than min_sample measured observations"
        if (!analysis.MeetsMinSample)
        {
            throw new AmccaException(
                AmccaErrors.Pol001,
                ErrorCategory.Policy,
                $"SPEC/48 violation: Experiment {experimentId} cannot be concluded with sample size {analysis.TotalSampleSize} < required minimum.");
        }

        using var conn = await _factory.CreateOpenConnectionAsync();
        using var tx = conn.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        await conn.ExecuteAsync(@"
            UPDATE experiments
            SET state = 'CONCLUDED', concluded_at = @Now, updated_at = @Now
            WHERE id = @Id;
        ", new { Id = experimentId, Now = now }, transaction: tx);

        // Record conclusion on winning variant
        if (analysis.WinningVariantLabel != null)
        {
            await conn.ExecuteAsync(@"
                UPDATE experiment_variants
                SET result_json = @ResultJson
                WHERE experiment_id = @ExpId AND label = @Label;
            ", new
            {
                ExpId = experimentId,
                Label = analysis.WinningVariantLabel,
                ResultJson = JsonSerializer.Serialize(new
                {
                    status = "WINNER",
                    p_value = analysis.PValue,
                    effect_size = analysis.EffectSize,
                    confidence = analysis.EmittedMemoryConfidence
                })
            }, transaction: tx);
        }

        tx.Commit();

        // Emit durable memory record if significant (SPEC/48: Results update memory_records with confidence)
        if (analysis.IsStatisticallySignificant && analysis.EmittedMemoryConfidence.HasValue && _memoryService != null)
        {
            await _memoryService.StoreMemoryAsync(new MemoryRecord(
                Id: "mem-exp-" + UlidGenerator.NewUlid(),
                Scope: "EXPERIMENTS",
                Key: $"experiment:{experimentId}:winner",
                ValueJson: JsonSerializer.Serialize(new
                {
                    experiment_id = experimentId,
                    winner = analysis.WinningVariantLabel,
                    effect_size = analysis.EffectSize,
                    sample_size = analysis.TotalSampleSize
                }),
                EvidenceRef: experimentId,
                Confidence: analysis.EmittedMemoryConfidence.Value,
                SchemaVersion: "3.1.0",
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow
            ));
        }
    }

    private static (double PValue, double EffectSize) CalculateTwoSampleWelch(List<double> sample1, List<double> sample2)
    {
        if (sample1.Count < 2 || sample2.Count < 2)
            return (1.0, 0.0);

        double m1 = sample1.Average();
        double m2 = sample2.Average();

        double s1 = sample1.Sum(x => Math.Pow(x - m1, 2)) / (sample1.Count - 1);
        double s2 = sample2.Sum(x => Math.Pow(x - m2, 2)) / (sample2.Count - 1);

        double pooledStd = Math.Sqrt((s1 + s2) / 2.0);
        double effectSize = pooledStd > 0.0001 ? (m2 - m1) / pooledStd : 0.0;

        double seDiff = Math.Sqrt((s1 / sample1.Count) + (s2 / sample2.Count));
        if (seDiff <= 0.00001)
            return (m1 == m2 ? 1.0 : 0.0, effectSize);

        double t = (m2 - m1) / seDiff;
        double absT = Math.Abs(t);

        // Approximate two-tailed p-value using standard normal / Student's asymptotic approximation
        double p = 2.0 * (1.0 - NormalCdf(absT));
        return (Math.Max(0.0001, Math.Min(1.0, p)), effectSize);
    }

    private static double NormalCdf(double x)
    {
        // Abramowitz and Stegun approximation 7.1.26
        double a1 = 0.254829592;
        double a2 = -0.284496736;
        double a3 = 1.421413741;
        double a4 = -1.453152027;
        double a5 = 1.061405429;
        double p = 0.3275911;

        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}
