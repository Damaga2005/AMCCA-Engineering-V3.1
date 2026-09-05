namespace AMCCA.Core.Monetization;

public class RevenueRecord
{
    public string Id { get; set; } = string.Empty;
    public string? ProductionId { get; set; }
    public string? PublicationId { get; set; }
    public string? ProgramId { get; set; }
    public string State { get; set; } = "PENDING"; // PENDING, CONFIRMED, DISPUTED, REVERSED
    public string Provenance { get; set; } = "OFFICIAL_API"; // OFFICIAL_API, STATEMENT_IMPORT, MANUAL_CONFIRMED
    public decimal GrossAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public decimal NetAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? StatementRef { get; set; }
    public string OccurredAt { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public class CostRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProductionId { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public string Kind { get; set; } = "SETTLEMENT"; // ESTIMATE, RESERVATION, SETTLEMENT, RELEASE, ADJUSTMENT
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Provider { get; set; } = string.Empty;
    public string OccurredAt { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public record ProfitSummary(
    decimal ConfirmedRevenue,
    decimal SettledCost,
    decimal NetProfit,
    string Currency);
