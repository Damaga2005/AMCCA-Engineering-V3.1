using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Providers;

public class ModelRegistry
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public ModelRegistry(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InsertModelAsync(ModelRegistryEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.Id))
        {
            entry.Id = UlidGenerator.NewUlid();
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(entry.CreatedAt)) entry.CreatedAt = now;
        if (string.IsNullOrEmpty(entry.UpdatedAt)) entry.UpdatedAt = now;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO model_registry (
                id, provider, model_id, capability, protocol, enabled,
                constraints_json, pricing_snapshot_id, last_verified_at,
                fallback_order, created_at, updated_at
            ) VALUES (
                @Id, @Provider, @ModelId, @Capability, @Protocol, @Enabled,
                @ConstraintsJson, @PricingSnapshotId, @LastVerifiedAt,
                @FallbackOrder, @CreatedAt, @UpdatedAt
            );
        ";
        await connection.ExecuteAsync(sql, new
        {
            entry.Id,
            entry.Provider,
            entry.ModelId,
            entry.Capability,
            entry.Protocol,
            Enabled = entry.Enabled ? 1 : 0,
            entry.ConstraintsJson,
            entry.PricingSnapshotId,
            entry.LastVerifiedAt,
            entry.FallbackOrder,
            entry.CreatedAt,
            entry.UpdatedAt
        });
    }

    public async Task<bool> VerifyAndEnableModelAsync(
        string provider,
        string modelId,
        string capability,
        IProviderGateway gateway,
        CancellationToken ct = default)
    {
        // 1. Run live capability probe
        var probeResult = await gateway.ProbeCapabilityAsync(provider, modelId, capability, ct);
        if (!probeResult.Success)
        {
            return false;
        }

        // 2. Set last_verified_at and enable in database (satisfies CHECK constraint)
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            UPDATE model_registry
            SET enabled = 1,
                last_verified_at = @Now,
                updated_at = @Now
            WHERE provider = @Provider AND model_id = @ModelId AND capability = @Capability;
        ";
        var rows = await connection.ExecuteAsync(sql, new
        {
            Now = now,
            Provider = provider,
            ModelId = modelId,
            Capability = capability
        });

        return rows > 0;
    }

    public async Task<ModelRegistryEntry?> GetModelAsync(
        string provider,
        string modelId,
        string capability,
        CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            SELECT
                id AS Id,
                provider AS Provider,
                model_id AS ModelId,
                capability AS Capability,
                protocol AS Protocol,
                enabled AS Enabled,
                constraints_json AS ConstraintsJson,
                pricing_snapshot_id AS PricingSnapshotId,
                last_verified_at AS LastVerifiedAt,
                fallback_order AS FallbackOrder,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt
            FROM model_registry
            WHERE provider = @Provider AND model_id = @ModelId AND capability = @Capability;
        ";
        var row = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new
        {
            Provider = provider,
            ModelId = modelId,
            Capability = capability
        });

        if (row == null) return null;

        return new ModelRegistryEntry
        {
            Id = row.Id,
            Provider = row.Provider,
            ModelId = row.ModelId,
            Capability = row.Capability,
            Protocol = row.Protocol,
            Enabled = row.Enabled == 1,
            ConstraintsJson = row.ConstraintsJson,
            PricingSnapshotId = row.PricingSnapshotId,
            LastVerifiedAt = row.LastVerifiedAt,
            FallbackOrder = row.FallbackOrder,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };
    }
}
