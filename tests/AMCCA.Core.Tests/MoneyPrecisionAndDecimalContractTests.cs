using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Monetization;
using AMCCA.Core.Policy;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class MoneyPrecisionAndDecimalContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly RevenueService _revenueService;
    private readonly BudgetManager _budgetManager;

    public MoneyPrecisionAndDecimalContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_MONEY_DEF011_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "money_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);

        var migrator = new MigrationService(_factory, _testDir);
        migrator.UpgradeAsync().GetAwaiter().GetResult();

        _revenueService = new RevenueService(_factory);
        _budgetManager = new BudgetManager(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void DEF011_DecimalArithmetic_IsExact_WhereasFloatHasBinaryDrift()
    {
        // Prove that 0.1 + 0.2 != 0.3 in double, but 0.1m + 0.2m == 0.3m in decimal (D-023)
        double d1 = 0.1;
        double d2 = 0.2;
        (d1 + d2).Should().NotBe(0.3, "double has IEEE-754 precision drift");

        decimal m1 = 0.1m;
        decimal m2 = 0.2m;
        (m1 + m2).Should().Be(0.3m, "decimal has exact base-10 representation");
    }

    [Fact]
    public void DEF011_Format_ProducesExactSixFractionalDigits()
    {
        Money.Format(1.5m).Should().Be("1.500000");
        Money.Format(0m).Should().Be("0.000000");
        Money.Format(12345.6789m).Should().Be("12345.678900");
    }

    [Fact]
    public void DEF011_Parse_ValidSixFractionalDigits_Succeeds()
    {
        Money.Parse("1.500000").Should().Be(1.5m);
        Money.Parse("0.000000").Should().Be(0m);
        Money.Parse("100.250000").Should().Be(100.25m);
        Money.Parse("-5.000000").Should().Be(-5.0m);
    }

    [Fact]
    public void DEF011_Parse_WrongPrecision_ThrowsFormatException()
    {
        // D-023: Reject values without exactly 6 fractional digits
        var act1 = () => Money.Parse("1.50");
        act1.Should().Throw<FormatException>();

        var act2 = () => Money.Parse("1.5000000");
        act2.Should().Throw<FormatException>();

        var act3 = () => Money.Parse("100");
        act3.Should().Throw<FormatException>();
    }

    [Fact]
    public void DEF011_ScientificNotation_NaN_And_Infinity_AreStrictlyProhibited()
    {
        var actSci1 = () => Money.Parse("1e6");
        actSci1.Should().Throw<FormatException>();

        var actSci2 = () => Money.Parse("1.5e-2");
        actSci2.Should().Throw<FormatException>();

        var actNan = () => Money.Parse("NaN");
        actNan.Should().Throw<FormatException>();

        var actInf = () => Money.Parse("Infinity");
        actInf.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task DEF011_EndToEndProfitCalculation_ExactDecimalNoDrift()
    {
        var prodId = "prod-money-1";

        // Record revenues with fractions: 0.100000 and 0.200000
        await _revenueService.RecordRevenueAsync(
            productionId: prodId,
            state: "CONFIRMED",
            provenance: "OFFICIAL_API",
            grossAmount: 0.100000m,
            feeAmount: 0.000000m,
            netAmount: 0.100000m,
            currency: "EUR");

        await _revenueService.RecordRevenueAsync(
            productionId: prodId,
            state: "CONFIRMED",
            provenance: "OFFICIAL_API",
            grossAmount: 0.200000m,
            feeAmount: 0.000000m,
            netAmount: 0.200000m,
            currency: "EUR");

        // Record settlement cost: 0.050000 and 0.050000
        await _revenueService.RecordCostAsync(prodId, "SETTLEMENT", 0.050000m, "EUR", "openai");
        await _revenueService.RecordCostAsync(prodId, "SETTLEMENT", 0.050000m, "EUR", "openai");

        var profit = await _revenueService.ComputeProfitAsync(prodId);

        profit.ConfirmedRevenue.Should().Be(0.300000m, "0.1 + 0.2 must sum to exact 0.3 without floating point loss");
        profit.SettledCost.Should().Be(0.100000m);
        profit.NetProfit.Should().Be(0.200000m);
    }

    [Fact]
    public void DEF011_NoMonetaryModel_ContainsDoubleOrFloat()
    {
        var types = new[]
        {
            typeof(ProfitSummary),
            typeof(RevenueRecord),
            typeof(CostRecord),
            typeof(BudgetRecord)
        };

        foreach (var type in types)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                prop.PropertyType.Should().NotBe(typeof(double), $"{type.Name}.{prop.Name} must NOT be double (D-023)");
                prop.PropertyType.Should().NotBe(typeof(float), $"{type.Name}.{prop.Name} must NOT be float (D-023)");
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                field.FieldType.Should().NotBe(typeof(double), $"{type.Name}.{field.Name} must NOT be double (D-023)");
                field.FieldType.Should().NotBe(typeof(float), $"{type.Name}.{field.Name} must NOT be float (D-023)");
            }
        }
    }
}
