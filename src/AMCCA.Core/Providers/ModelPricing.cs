using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Providers;

/// <summary>
/// A resolved, priced-per-1M-tokens rate for one provider+model, backed by a materialised
/// <c>pricing_snapshots</c> row. SPEC/21: a cost cannot be computed against a price lacking
/// <see cref="RetrievedAt"/> and <see cref="SourceRef"/>, so both are non-optional here.
/// </summary>
public sealed record ModelPrice(
    string PricingSnapshotId,
    string Provider,
    string ModelId,
    decimal InputPer1MTokens,
    decimal OutputPer1MTokens,
    string Currency,
    string EffectiveAt,
    string RetrievedAt,
    string SourceRef);

/// <summary>
/// Resolves the current token price for a provider+model. The only source AgentRuntime prices a model
/// call against (D-034). A null result is honest "no price on file", not zero — the caller records the
/// cost event as ESTIMATED_UNRECONCILED (SPEC/21) rather than inventing a number.
/// </summary>
public interface IModelPricing
{
    Task<ModelPrice?> ResolveAsync(string provider, string modelId, CancellationToken ct = default);
}

/// <summary>The wired-nowhere default: never has a price. Keeps AgentRuntime working with no pricing
/// configured (every model call becomes an unpriced, ESTIMATED_UNRECONCILED cost event).</summary>
public sealed class NullModelPricing : IModelPricing
{
    public static readonly NullModelPricing Instance = new();
    public Task<ModelPrice?> ResolveAsync(string provider, string modelId, CancellationToken ct = default)
        => Task.FromResult<ModelPrice?>(null);
}

/// <summary>
/// Materialises <c>config.providers.gateway.model_pricing</c> into <c>pricing_snapshots</c> once, then
/// resolves prices from that table. Provider prices are external and volatile (SPEC/21): the operator
/// supplies them in config with their own retrieved_at + source_ref, and this class is the ingestion
/// pipeline migration 009 disclosed as missing.
/// </summary>
public sealed class PricingSnapshotModelPricing : IModelPricing
{
    public const string UnitInput = "TOKENS_IN_PER_1M";
    public const string UnitOutput = "TOKENS_OUT_PER_1M";

    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _providerId;
    private readonly IReadOnlyList<ModelPricingConfig> _configuredPrices;
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private bool _seeded;

    public PricingSnapshotModelPricing(
        DatabaseConnectionFactory connectionFactory,
        string providerId,
        IReadOnlyList<ModelPricingConfig> configuredPrices)
    {
        _connectionFactory = connectionFactory;
        _providerId = providerId;
        _configuredPrices = configuredPrices ?? Array.Empty<ModelPricingConfig>();
    }

    public async Task<ModelPrice?> ResolveAsync(string provider, string modelId, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        // Most recent effective snapshot per unit for this provider+model.
        var rows = (await connection.QueryAsync<(string Id, string Unit, string UnitPrice, string Currency, string EffectiveAt, string RetrievedAt, string SourceRef)>(
            new CommandDefinition(@"
                SELECT id AS Id, unit AS Unit, unit_price AS UnitPrice, currency AS Currency,
                       effective_at AS EffectiveAt, retrieved_at AS RetrievedAt, source_ref AS SourceRef
                FROM pricing_snapshots
                WHERE provider = @Provider AND model_id = @ModelId AND unit IN (@In, @Out)
                ORDER BY effective_at DESC;",
                new { Provider = provider, ModelId = modelId, In = UnitInput, Out = UnitOutput },
                cancellationToken: ct))).AsList();

        (string Id, string Price, string Cur, string Eff, string Ret, string Src)? input = null;
        (string Id, string Price, string Cur, string Eff, string Ret, string Src)? output = null;
        foreach (var r in rows)
        {
            if (r.Unit == UnitInput && input is null)
                input = (r.Id, r.UnitPrice, r.Currency, r.EffectiveAt, r.RetrievedAt, r.SourceRef);
            else if (r.Unit == UnitOutput && output is null)
                output = (r.Id, r.UnitPrice, r.Currency, r.EffectiveAt, r.RetrievedAt, r.SourceRef);
        }

        if (input is null || output is null) return null;
        if (!Money.TryParse(input.Value.Price, out var inPrice)) return null;
        if (!Money.TryParse(output.Value.Price, out var outPrice)) return null;
        if (string.IsNullOrWhiteSpace(input.Value.Ret) || string.IsNullOrWhiteSpace(input.Value.Src)) return null;

        return new ModelPrice(
            PricingSnapshotId: input.Value.Id,
            Provider: provider,
            ModelId: modelId,
            InputPer1MTokens: inPrice,
            OutputPer1MTokens: outPrice,
            Currency: input.Value.Cur,
            EffectiveAt: input.Value.Eff,
            RetrievedAt: input.Value.Ret,
            SourceRef: input.Value.Src);
    }

    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (_seeded) return;
        await _seedGate.WaitAsync(ct);
        try
        {
            if (_seeded) return;
            if (_configuredPrices.Count > 0)
            {
                using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
                using var tx = connection.BeginTransaction();
                foreach (var p in _configuredPrices)
                {
                    if (string.IsNullOrWhiteSpace(p.ModelId)) continue;
                    var effectiveAt = string.IsNullOrWhiteSpace(p.EffectiveAt) ? p.RetrievedAt : p.EffectiveAt!;
                    await UpsertSnapshotAsync(connection, tx, p.ModelId, UnitInput, p.InputPer1MTokens, p.Currency, effectiveAt, p.RetrievedAt, p.SourceRef);
                    await UpsertSnapshotAsync(connection, tx, p.ModelId, UnitOutput, p.OutputPer1MTokens, p.Currency, effectiveAt, p.RetrievedAt, p.SourceRef);
                }
                tx.Commit();
            }
            _seeded = true;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private async Task UpsertSnapshotAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction tx,
        string modelId, string unit, string unitPrice, string currency,
        string effectiveAt, string retrievedAt, string sourceRef)
    {
        // pricing_snapshots is immutable-by-intent (SPEC/21): a row for the same
        // (provider, model_id, unit, effective_at) is never rewritten, only inserted once.
        await connection.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO pricing_snapshots (id, provider, model_id, unit, unit_price, currency, effective_at, retrieved_at, source_ref, created_at)
            VALUES (@Id, @Provider, @ModelId, @Unit, @UnitPrice, @Currency, @EffectiveAt, @RetrievedAt, @SourceRef, @Now)
            ON CONFLICT(provider, model_id, unit, effective_at) DO NOTHING;",
            new
            {
                Id = UlidGenerator.NewUlid(),
                Provider = _providerId,
                ModelId = modelId,
                Unit = unit,
                UnitPrice = unitPrice,
                Currency = currency,
                EffectiveAt = effectiveAt,
                RetrievedAt = retrievedAt,
                SourceRef = sourceRef,
                Now = DateTimeOffset.UtcNow.ToString("O"),
            }, transaction: tx));
    }
}
