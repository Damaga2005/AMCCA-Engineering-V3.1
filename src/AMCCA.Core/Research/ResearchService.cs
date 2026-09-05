using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Research;

using System.Net.Http;
using System.Security.Cryptography;
using AMCCA.Core.Security;

public class ResearchService : IDisposable
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly ISafeHttpClientFactory _clientFactory;
    private readonly HttpClient _httpClient;

    public ResearchService(DatabaseConnectionFactory connectionFactory, ISafeHttpClientFactory? clientFactory = null)
    {
        _connectionFactory = connectionFactory;
        _clientFactory = clientFactory ?? SafeHttpClientFactory.Default;
        _httpClient = _clientFactory.CreateClient();
    }

    public async Task<Source> FetchAndIngestSourceAsync(
        string url,
        string publisher,
        string trustTier,
        bool robotsAllowed,
        CancellationToken ct = default)
    {
        var uri = new Uri(url);
        // Pre-flight check
        SsrfValidator.ValidateDestinationUri(uri);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AmccaException amccaEx)
        {
            throw amccaEx;
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var contentBytes = await response.Content.ReadAsByteArrayAsync(ct);
            var contentHash = Convert.ToHexString(SHA256.HashData(contentBytes)).ToLowerInvariant();

            var source = new Source
            {
                Id = UlidGenerator.NewUlid(),
                Url = url,
                Publisher = publisher,
                PublishedAt = DateTimeOffset.UtcNow.ToString("O"),
                RetrievedAt = DateTimeOffset.UtcNow.ToString("O"),
                ContentHash = contentHash,
                TrustTier = trustTier,
                RobotsAllowed = robotsAllowed,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            await InsertSourceAsync(source, ct);
            return source;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task InsertSourceAsync(Source source, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(source.Id))
        {
            source.Id = UlidGenerator.NewUlid();
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(source.CreatedAt)) source.CreatedAt = now;
        if (string.IsNullOrEmpty(source.RetrievedAt)) source.RetrievedAt = now;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO sources (
                id, url, publisher, published_at, retrieved_at,
                content_hash, trust_tier, robots_allowed, created_at
            ) VALUES (
                @Id, @Url, @Publisher, @PublishedAt, @RetrievedAt,
                @ContentHash, @TrustTier, @RobotsAllowed, @CreatedAt
            );
        ";
        await connection.ExecuteAsync(sql, new
        {
            source.Id,
            source.Url,
            source.Publisher,
            source.PublishedAt,
            source.RetrievedAt,
            source.ContentHash,
            source.TrustTier,
            RobotsAllowed = source.RobotsAllowed ? 1 : 0,
            source.CreatedAt
        });
    }

    public async Task InsertClaimWithSourceAsync(
        Claim claim,
        string sourceId,
        string relation,
        string? excerptHash = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(claim.Id))
        {
            claim.Id = UlidGenerator.NewUlid();
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(claim.CreatedAt)) claim.CreatedAt = now;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string claimSql = @"
            INSERT INTO claims (
                id, production_id, text, status, materiality,
                subject_class, contains_personal_data, schema_version, created_at
            ) VALUES (
                @Id, @ProductionId, @Text, @Status, @Materiality,
                @SubjectClass, @ContainsPersonalData, @SchemaVersion, @CreatedAt
            );
        ";
        await connection.ExecuteAsync(claimSql, new
        {
            claim.Id,
            claim.ProductionId,
            claim.Text,
            claim.Status,
            claim.Materiality,
            claim.SubjectClass,
            ContainsPersonalData = claim.ContainsPersonalData ? 1 : 0,
            claim.SchemaVersion,
            claim.CreatedAt
        }, transaction: tx);

        const string linkSql = @"
            INSERT INTO claim_sources (claim_id, source_id, relation, excerpt_hash)
            VALUES (@ClaimId, @SourceId, @Relation, @ExcerptHash);
        ";
        await connection.ExecuteAsync(linkSql, new
        {
            ClaimId = claim.Id,
            SourceId = sourceId,
            Relation = relation,
            ExcerptHash = excerptHash
        }, transaction: tx);

        tx.Commit();
    }

    public sealed record ClaimEvaluationSummary(int Verified, int Estimated, int Disputed, int Unknown)
    {
        public int Total => Verified + Estimated + Disputed + Unknown;
    }

    /// <summary>
    /// Re-runs <see cref="ClaimValidator"/> over every claim of a production against its linked sources
    /// (SPEC/26) and writes the resulting <c>status</c> back. Returns the post-evaluation status
    /// breakdown. This is what the research agent calls after recording claims + sources to see which
    /// are verified.
    /// </summary>
    public async Task<ClaimEvaluationSummary> EvaluateAllClaimsAsync(string productionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        var claims = (await connection.QueryAsync<Claim>(new CommandDefinition(
            @"SELECT id AS Id, production_id AS ProductionId, text AS Text, status AS Status,
                     materiality AS Materiality, subject_class AS SubjectClass,
                     contains_personal_data AS ContainsPersonalData, schema_version AS SchemaVersion,
                     created_at AS CreatedAt
              FROM claims WHERE production_id = @Id;",
            new { Id = productionId }, cancellationToken: ct))).ToList();

        var links = (await connection.QueryAsync<(string ClaimId, string Relation, string SourceId)>(new CommandDefinition(
            @"SELECT cs.claim_id AS ClaimId, cs.relation AS Relation, cs.source_id AS SourceId
              FROM claim_sources cs
              JOIN claims c ON c.id = cs.claim_id
              WHERE c.production_id = @Id;",
            new { Id = productionId }, cancellationToken: ct))).ToList();

        var sources = (await connection.QueryAsync<Source>(new CommandDefinition(
            @"SELECT id AS Id, url AS Url, publisher AS Publisher, published_at AS PublishedAt,
                     retrieved_at AS RetrievedAt, content_hash AS ContentHash, trust_tier AS TrustTier,
                     robots_allowed AS RobotsAllowed, created_at AS CreatedAt
              FROM sources
              WHERE id IN (SELECT source_id FROM claim_sources cs JOIN claims c ON c.id = cs.claim_id WHERE c.production_id = @Id);",
            new { Id = productionId }, cancellationToken: ct))).ToDictionary(s => s.Id);

        int verified = 0, estimated = 0, disputed = 0, unknown = 0;
        using var tx = connection.BeginTransaction();
        foreach (var claim in claims)
        {
            var claimSources = links
                .Where(l => l.ClaimId == claim.Id && sources.ContainsKey(l.SourceId))
                .Select(l => (sources[l.SourceId], l.Relation));

            var status = ClaimValidator.EvaluateClaimStatus(claim, claimSources);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE claims SET status = @Status WHERE id = @Id;",
                new { Status = status, Id = claim.Id }, transaction: tx, cancellationToken: ct));

            switch (status)
            {
                case "VERIFIED": verified++; break;
                case "ESTIMATED": estimated++; break;
                case "DISPUTED": disputed++; break;
                default: unknown++; break;
            }
        }
        tx.Commit();

        return new ClaimEvaluationSummary(verified, estimated, disputed, unknown);
    }

    /// <summary>Links an already-recorded claim to another source (a claim may cite several).</summary>
    public async Task LinkClaimSourceAsync(string claimId, string sourceId, string relation, string? excerptHash = null, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            @"INSERT OR IGNORE INTO claim_sources (claim_id, source_id, relation, excerpt_hash)
              VALUES (@ClaimId, @SourceId, @Relation, @ExcerptHash);",
            new { ClaimId = claimId, SourceId = sourceId, Relation = relation, ExcerptHash = excerptHash },
            cancellationToken: ct));
    }

    public async Task<Claim?> GetClaimAsync(string claimId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                production_id AS ProductionId,
                text AS Text,
                status AS Status,
                materiality AS Materiality,
                subject_class AS SubjectClass,
                contains_personal_data AS ContainsPersonalData,
                schema_version AS SchemaVersion,
                created_at AS CreatedAt
            FROM claims
            WHERE id = @Id;
        ";
        var row = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new { Id = claimId });
        if (row == null) return null;

        return new Claim
        {
            Id = row.Id,
            ProductionId = row.ProductionId,
            Text = row.Text,
            Status = row.Status,
            Materiality = row.Materiality,
            SubjectClass = row.SubjectClass,
            ContainsPersonalData = row.ContainsPersonalData == 1,
            SchemaVersion = row.SchemaVersion,
            CreatedAt = row.CreatedAt
        };
    }
}
