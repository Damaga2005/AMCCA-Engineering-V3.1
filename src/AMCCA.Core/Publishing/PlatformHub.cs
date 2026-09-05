using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Publishing;

public class PlatformHub
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public PlatformHub(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<string> RegisterAccountAsync(
        string platform,
        string accountHandle,
        string credentialSecretRef,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO platform_accounts (id, platform, account_handle, credential_secret_ref, state, created_at, updated_at)
            VALUES (@Id, @Platform, @AccountHandle, @CredentialSecretRef, 'CONNECTED', @Now, @Now);
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = id,
            Platform = platform,
            AccountHandle = accountHandle,
            CredentialSecretRef = credentialSecretRef,
            Now = now
        });

        return id;
    }

    public async Task<PublicationRecord> CreatePublicationAsync(
        string productionId,
        string platform,
        string accountId,
        string contentVersionId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var pub = new PublicationRecord
        {
            Id = id,
            ProductionId = productionId,
            Platform = platform,
            AccountId = accountId,
            ContentVersionId = contentVersionId,
            State = "INTENT_CREATED",
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };

        await InsertPublicationDirectAsync(pub, ct);
        return pub;
    }

    public async Task InsertPublicationDirectAsync(PublicationRecord pub, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(pub.Id)) pub.Id = UlidGenerator.NewUlid();
        if (string.IsNullOrEmpty(pub.CreatedAt)) pub.CreatedAt = now;
        if (string.IsNullOrEmpty(pub.UpdatedAt)) pub.UpdatedAt = now;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO publications (
                id, production_id, platform, account_id, content_version_id,
                state, idempotency_key, provider_request_id, external_id,
                external_url, evidence_source, evidence_retrieved_at,
                created_at, updated_at
            ) VALUES (
                @Id, @ProductionId, @Platform, @AccountId, @ContentVersionId,
                @State, @IdempotencyKey, @ProviderRequestId, @ExternalId,
                @ExternalUrl, @EvidenceSource, @EvidenceRetrievedAt,
                @CreatedAt, @UpdatedAt
            );
        ";
        await connection.ExecuteAsync(sql, pub);
    }

    public async Task<bool> VerifyPublicationAsync(
        string publicationId,
        string externalId,
        IPlatformAdapter adapter,
        CancellationToken ct = default)
    {
        // 1. Poll authoritative evidence from the platform
        var evidence = await adapter.PollAuthoritativeEvidenceAsync(externalId, ct);
        if (!evidence.IsPublished)
        {
            return false;
        }

        // 2. Set authoritative evidence and transition to VERIFIED (satisfies DB CHECK constraint)
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            UPDATE publications
            SET state = 'VERIFIED',
                external_id = @ExternalId,
                external_url = @ExternalUrl,
                evidence_source = @EvidenceSource,
                evidence_retrieved_at = @EvidenceRetrievedAt,
                updated_at = @Now
            WHERE id = @Id;
        ";
        var rows = await connection.ExecuteAsync(sql, new
        {
            Id = publicationId,
            ExternalId = externalId,
            ExternalUrl = evidence.ExternalUrl,
            EvidenceSource = evidence.EvidenceSource,
            EvidenceRetrievedAt = evidence.RetrievedAt,
            Now = now
        });

        return rows > 0;
    }

    public async Task<PublicationRecord?> GetPublicationAsync(string id, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                production_id AS ProductionId,
                platform AS Platform,
                account_id AS AccountId,
                content_version_id AS ContentVersionId,
                state AS State,
                idempotency_key AS IdempotencyKey,
                provider_request_id AS ProviderRequestId,
                external_id AS ExternalId,
                external_url AS ExternalUrl,
                evidence_source AS EvidenceSource,
                evidence_retrieved_at AS EvidenceRetrievedAt,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM publications
            WHERE id = @Id;
        ";
        return await connection.QuerySingleOrDefaultAsync<PublicationRecord>(sql, new { Id = id });
    }
}
