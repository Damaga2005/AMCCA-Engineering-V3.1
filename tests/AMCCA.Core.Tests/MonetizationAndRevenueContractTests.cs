using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Monetization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class MonetizationAndRevenueContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly RevenueService _revenueService;

    public MonetizationAndRevenueContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_REV_TESTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "revenue_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _revenueService = new RevenueService(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    [Fact]
    public async Task PhantomRevenue_WithEstimatedProvenance_IsRejectedByDbCheck()
    {
        // Exit criterion: "No phantom revenue; revenue recognized only from verified events" (D-030, I-13)
        // CHECK(provenance <> 'ESTIMATED')
        var phantomRevenue = new RevenueRecord
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = "prod-1",
            State = "CONFIRMED",
            Provenance = "ESTIMATED", // Disallowed!
            GrossAmount = 100.00m,
            FeeAmount = 0.00m,
            NetAmount = 100.00m,
            Currency = "EUR",
            StatementRef = null,
            OccurredAt = DateTimeOffset.UtcNow.ToString("O")
        };

        var act = async () => await _revenueService.InsertRevenueDirectAsync(phantomRevenue);

        await act.Should().ThrowAsync<SqliteException>()
            .Where(e => e.SqliteErrorCode == 19); // Constraint violation
    }

    [Fact]
    public async Task ProfitCalculation_IncludesOnlyConfirmedRevenueAndSettledCosts()
    {
        // SPEC/20, D-030: "profit = sum(revenue_events where state = CONFIRMED) - sum(cost_events where kind = SETTLEMENT)"
        var prodId = "prod-profit-1";

        // 1. Confirmed revenue: 150.00 EUR
        await _revenueService.RecordRevenueAsync(
            productionId: prodId,
            state: "CONFIRMED",
            provenance: "OFFICIAL_API",
            grossAmount: 160.00m,
            feeAmount: 10.00m,
            netAmount: 150.00m,
            currency: "EUR",
            statementRef: "stmt-001");

        // 2. Pending revenue: 50.00 EUR (must NOT be counted in profit!)
        await _revenueService.RecordRevenueAsync(
            productionId: prodId,
            state: "PENDING",
            provenance: "OFFICIAL_API",
            grossAmount: 50.00m,
            feeAmount: 0.00m,
            netAmount: 50.00m,
            currency: "EUR",
            statementRef: "stmt-002");

        // 3. Settled cost: 40.00 EUR
        await _revenueService.RecordCostAsync(
            productionId: prodId,
            kind: "SETTLEMENT",
            amount: 40.00m,
            currency: "EUR",
            provider: "elevenlabs");

        // 4. Reserved cost: 20.00 EUR (must NOT be counted in settled spend!)
        await _revenueService.RecordCostAsync(
            productionId: prodId,
            kind: "RESERVATION",
            amount: 20.00m,
            currency: "EUR",
            provider: "runway");

        var summary = await _revenueService.ComputeProfitAsync(prodId);

        summary.ConfirmedRevenue.Should().Be(150.00m, "pending revenue must not enter profit calculation");
        summary.SettledCost.Should().Be(40.00m, "reserved cost must not enter profit calculation");
        summary.NetProfit.Should().Be(110.00m, "profit = confirmed revenue - settled attributable cost (150 - 40 = 110)");
    }

    [Fact]
    public async Task ReversedRevenue_IsExcludedFromProfit()
    {
        var prodId = "prod-profit-2";

        await _revenueService.RecordRevenueAsync(
            productionId: prodId,
            state: "REVERSED",
            provenance: "STATEMENT_IMPORT",
            grossAmount: 80.00m,
            feeAmount: 0.00m,
            netAmount: 80.00m,
            currency: "EUR",
            statementRef: "stmt-rev");

        var summary = await _revenueService.ComputeProfitAsync(prodId);

        summary.ConfirmedRevenue.Should().Be(0.00m);
        summary.NetProfit.Should().Be(0.00m);
    }
}
