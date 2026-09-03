using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Memory;

public class MemoryRetrievalService
{
    private readonly DatabaseConnectionFactory _factory;

    public MemoryRetrievalService(DatabaseConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task StoreMemoryAsync(MemoryRecord record)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(@"
            INSERT INTO memory_records (id, scope, key, value_json, evidence_ref, confidence, schema_version, created_at, updated_at)
            VALUES (@Id, @Scope, @Key, @ValueJson, @EvidenceRef, @Confidence, @SchemaVersion, @CreatedAt, @UpdatedAt)
            ON CONFLICT(scope, key) DO UPDATE SET
                value_json = excluded.value_json,
                evidence_ref = excluded.evidence_ref,
                confidence = excluded.confidence,
                schema_version = excluded.schema_version,
                updated_at = excluded.updated_at;
        ", new
        {
            record.Id,
            record.Scope,
            record.Key,
            record.ValueJson,
            record.EvidenceRef,
            record.Confidence,
            record.SchemaVersion,
            CreatedAt = record.CreatedAt.ToUniversalTime().ToString("o"),
            UpdatedAt = record.UpdatedAt.ToUniversalTime().ToString("o")
        });
    }

    public async Task<IReadOnlyList<MemorySearchResult>> RetrieveAsync(MemoryQuery query)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();

        // 1. Deterministic scope isolation (SPEC/22: Memory never crosses niches/languages)
        var rows = (await conn.QueryAsync<dynamic>(@"
            SELECT id, scope, key, value_json AS ValueJson, evidence_ref AS EvidenceRef, confidence, schema_version AS SchemaVersion, created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM memory_records
            WHERE scope = @Scope
        ", new { query.Scope })).ToList();

        var records = new List<MemoryRecord>();
        foreach (var r in rows)
        {
            records.Add(new MemoryRecord(
                (string)r.id,
                (string)r.scope,
                (string)r.key,
                (string)r.ValueJson,
                (string?)r.EvidenceRef,
                (double)r.confidence,
                (string)r.SchemaVersion,
                DateTime.Parse((string)r.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                DateTime.Parse((string)r.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            ));
        }

        // 2. Filter unverified memory or failed production references (SPEC/22)
        if (query.ExcludeFailedProductions)
        {
            var filtered = new List<MemoryRecord>();
            foreach (var rec in records)
            {
                if (!string.IsNullOrEmpty(rec.EvidenceRef) && rec.EvidenceRef.StartsWith("prod-"))
                {
                    var prodState = await conn.ExecuteScalarAsync<string?>(
                        "SELECT state FROM productions WHERE id = @Id",
                        new { Id = rec.EvidenceRef });

                    if (prodState == "FAILED" || prodState == "CANCELLED")
                    {
                        continue; // Exclude memory originating from failed productions
                    }
                }
                filtered.Add(rec);
            }
            records = filtered;
        }

        var scored = new List<MemorySearchResult>();
        var now = DateTime.UtcNow;

        foreach (var rec in records)
        {
            // 3. Confidence floor (SPEC/22: < 0.5 cannot drive autonomous decisions)
            if (query.AutonomousDecision && rec.Confidence < 0.5)
            {
                continue;
            }

            // 4. Time decay based on configured half-life (SPEC/22)
            var elapsedDays = Math.Max(0, (now - rec.UpdatedAt).TotalDays);
            var decayFactor = Math.Pow(0.5, elapsedDays / Math.Max(1.0, query.HalfLifeDays));
            var decayedConfidence = rec.Confidence * decayFactor;

            if (decayedConfidence < query.MinConfidence)
            {
                continue;
            }

            // 5. Similarity scoring
            var similarity = ComputeSimilarity(query.QueryText, rec.Key + " " + rec.ValueJson);
            if (similarity <= 0.0 && !string.IsNullOrWhiteSpace(query.QueryText))
            {
                continue;
            }

            var compositeScore = (0.5 * similarity) + (0.5 * decayedConfidence);
            scored.Add(new MemorySearchResult(rec, similarity, decayedConfidence, compositeScore));
        }

        // Order by composite score descending
        var ordered = scored.OrderByDescending(s => s.CompositeScore).ToList();

        // 6. Deduplication: prune records with highly overlapping keys/content
        var deduplicated = new List<MemorySearchResult>();
        foreach (var item in ordered)
        {
            bool isDuplicate = false;
            foreach (var kept in deduplicated)
            {
                var jaccard = ComputeJaccard(item.Record.Key, kept.Record.Key);
                var base1 = NormalizeKeyBase(item.Record.Key);
                var base2 = NormalizeKeyBase(kept.Record.Key);
                if (jaccard >= 0.75 || (base1.Length > 3 && base1 == base2))
                {
                    isDuplicate = true;
                    break;
                }
            }
            if (!isDuplicate)
            {
                deduplicated.Add(item);
            }
        }

        // 7. Budget / token limit enforcement
        var budgeted = new List<MemorySearchResult>();
        int currentTokens = 0;
        foreach (var item in deduplicated)
        {
            var estimatedTokens = Math.Max(1, (item.Record.Key.Length + item.Record.ValueJson.Length) / 4);
            if (currentTokens + estimatedTokens <= query.MaxTokens)
            {
                budgeted.Add(item);
                currentTokens += estimatedTokens;
            }
        }

        return budgeted;
    }

    private static double ComputeSimilarity(string text1, string text2)
    {
        if (string.IsNullOrWhiteSpace(text1) || string.IsNullOrWhiteSpace(text2))
            return 1.0;

        var tokens1 = Tokenize(text1);
        var tokens2 = Tokenize(text2);

        if (tokens1.Count == 0 || tokens2.Count == 0)
            return 0.0;

        var exactMatches = tokens1.Intersect(tokens2).Count();

        int partialMatches = 0;
        foreach (var t1 in tokens1)
        {
            if (tokens2.Any(t2 => t2.Contains(t1) || t1.Contains(t2)))
            {
                partialMatches++;
            }
        }

        var jaccard = (double)exactMatches / tokens1.Union(tokens2).Count();
        var coverage = (double)partialMatches / tokens1.Count;

        return Math.Max(jaccard, coverage);
    }

    private static double ComputeJaccard(string text1, string text2)
    {
        var tokens1 = Tokenize(text1);
        var tokens2 = Tokenize(text2);
        if (tokens1.Count == 0 || tokens2.Count == 0)
            return 0.0;
        return (double)tokens1.Intersect(tokens2).Count() / tokens1.Union(tokens2).Count();
    }

    private static string NormalizeKeyBase(string key)
    {
        return Regex.Replace(key.ToLowerInvariant(), @"(_v\d+|\d+)$", "");
    }

    private static HashSet<string> Tokenize(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(s => s.Length > 0)
            .ToHashSet();
    }
}
