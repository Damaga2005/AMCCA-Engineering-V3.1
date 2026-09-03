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
            GrossAmount = (double)rev.GrossAmount,
            FeeAmount = (double)rev.FeeAmount,
            NetAmount = (double)rev.NetAmount,
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
            Amount = (double)amount,
            Currency = currency,
            Provider = provider,
            Now = now
        });
    }

    public async Task<ProfitSummary> ComputeProfitAsync(string productionId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        // SPEC/20, D-030: "profit = sum(revenue_events where state = CONFIRMED) - sum(cost_events where kind = SETTLEMENT)"
        const string sql = @"
            SELECT
                (SELECT COALESCE(SUM(net_amount), 0.0) FROM revenue_events WHERE production_id = @Id AND state = 'CONFIRMED') AS ConfirmedRevenue,
                (SELECT COALESCE(SUM(amount), 0.0) FROM cost_events WHERE production_id = @Id AND kind = 'SETTLEMENT') AS SettledCost;
        ";
        var result = await connection.QuerySingleAsync<dynamic>(sql, new { Id = productionId });

        decimal confirmed = (decimal)(double)result.ConfirmedRevenue;
        decimal settled = (decimal)(double)result.SettledCost;
        decimal netProfit = confirmed - settled;

        return new ProfitSummary(confirmed, settled, netProfit, "EUR");
    }
}
