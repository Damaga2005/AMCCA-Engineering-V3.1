using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Monetization;

public class RevenueService
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public RevenueService(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<RevenueRecord> RecordRevenueAsync(
        string productionId,
        string state,
        string provenance,
        decimal grossAmount,
        decimal feeAmount,
        decimal netAmount,
        string currency,
        string? statementRef = null,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var rec = new RevenueRecord
        {
            Id = id,
            ProductionId = productionId,
            State = state,
            Provenance = provenance,
            GrossAmount = grossAmount,
            FeeAmount = feeAmount,
            NetAmount = netAmount,
            Currency = currency,
            StatementRef = statementRef,
            OccurredAt = now,
            CreatedAt = now
        };

        await InsertRevenueDirectAsync(rec, ct);
        return rec;
    }

    public async Task InsertRevenueDirectAsync(RevenueRecord rev, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrEmpty(rev.Id)) rev.Id = UlidGenerator.NewUlid();
        if (string.IsNullOrEmpty(rev.CreatedAt)) rev.CreatedAt = now;
        if (string.IsNullOrEmpty(rev.OccurredAt)) rev.OccurredAt = now;

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO revenue_events (
                id, production_id, publication_id, program_id, state,
                provenance, gross_amount, fee_amount, net_amount,
                currency, statement_ref, occurred_at, created_at
            ) VALUES (
                @Id, @ProductionId, @PublicationId, @ProgramId, @State,
                @Provenance, @GrossAmount, @FeeAmount, @NetAmount,
                @Currency, @StatementRef, @OccurredAt, @CreatedAt
            );
        ";
        await connection.ExecuteAsync(sql, new
        {
            rev.Id,
            rev.ProductionId,
            rev.PublicationId,
            rev.ProgramId,
            rev.State,
            rev.Provenance,
            GrossAmount = Money.Format(rev.GrossAmount),
            FeeAmount = Money.Format(rev.FeeAmount),
            NetAmount = Money.Format(rev.NetAmount),
            rev.Currency,
            rev.StatementRef,
            rev.OccurredAt,
            rev.CreatedAt
        });
    }

    public async Task RecordCostAsync(
        string productionId,
        string kind,
        decimal amount,
        string currency,
        string provider,
        string? jobId = null,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO cost_events (
                id, production_id, job_id, kind, amount, currency, provider, occurred_at, created_at
            ) VALUES (
                @Id, @ProductionId, @JobId, @Kind, @Amount, @Currency, @Provider, @Now, @Now
            );
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = id,
            ProductionId = productionId,
            JobId = jobId,
            Kind = kind,
            Amount = Money.Format(amount),
            Currency = currency,
            Provider = provider,
            Now = now
        });
    }

    public async Task<ProfitSummary> ComputeProfitAsync(string productionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        // SPEC/20, D-023, D-030: exact decimal aggregation, double/float forbidden
        const string revSql = "SELECT net_amount FROM revenue_events WHERE production_id = @Id AND state = 'CONFIRMED';";
        var revenues = (await connection.QueryAsync<string>(revSql, new { Id = productionId })).ToList();

        const string costSql = "SELECT amount FROM cost_events WHERE production_id = @Id AND kind = 'SETTLEMENT';";
        var costs = (await connection.QueryAsync<string>(costSql, new { Id = productionId })).ToList();

        decimal confirmed = 0m;
        foreach (var r in revenues)
        {
            if (Money.TryParse(r, out var val)) confirmed += val;
            else if (decimal.TryParse(r, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fallback)) confirmed += fallback;
        }

        decimal settled = 0m;
        foreach (var c in costs)
        {
            if (Money.TryParse(c, out var val)) settled += val;
            else if (decimal.TryParse(c, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fallback)) settled += fallback;
        }

        decimal netProfit = confirmed - settled;
        return new ProfitSummary(confirmed, settled, netProfit, "EUR");
    }
}
