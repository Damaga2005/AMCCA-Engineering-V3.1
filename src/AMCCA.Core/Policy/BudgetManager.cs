using System;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Policy;

public class BudgetManager
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public BudgetManager(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateBudgetAsync(
        string id,
        string window,
        string scopeId,
        decimal limitAmount,
        string currency = "EUR",
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO budgets (id, window, scope_id, limit_amount, reserved, spent, currency, created_at, updated_at)
            VALUES (@Id, @Window, @ScopeId, @LimitAmount, '0.000000', '0.000000', @Currency, @Now, @Now);
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = id,
            Window = window,
            ScopeId = scopeId,
            LimitAmount = Money.Format(limitAmount),
            Currency = currency,
            Now = now
        });
    }

    public async Task<bool> TryReserveBudgetAsync(
        string budgetId,
        decimal amount,
        string correlationId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string selectSql = @"
            SELECT id, window, scope_id AS ScopeId, limit_amount AS LimitAmount, reserved AS Reserved, spent AS Spent, currency AS Currency
            FROM budgets
            WHERE id = @BudgetId;
        ";
        var b = await connection.QuerySingleOrDefaultAsync<dynamic>(selectSql, new { BudgetId = budgetId }, transaction: tx);
        if (b == null)
        {
            tx.Rollback();
            return false;
        }

        string limStr = b.LimitAmount.ToString();
        string resStr = b.Reserved.ToString();
        string spStr = b.Spent.ToString();

        decimal limit = Money.TryParse(limStr, out var lVal) ? lVal : decimal.Parse(limStr, System.Globalization.CultureInfo.InvariantCulture);
        decimal reserved = Money.TryParse(resStr, out var rVal) ? rVal : decimal.Parse(resStr, System.Globalization.CultureInfo.InvariantCulture);
        decimal spent = Money.TryParse(spStr, out var sVal) ? sVal : decimal.Parse(spStr, System.Globalization.CultureInfo.InvariantCulture);

        if (reserved + spent + amount > limit)
        {
            tx.Rollback();
            return false;
        }

        var newReserved = reserved + amount;

        const string updateSql = @"
            UPDATE budgets
            SET reserved = @NewReserved,
                updated_at = @Now
            WHERE id = @BudgetId AND reserved = @OldReserved;
        ";
        var rows = await connection.ExecuteAsync(updateSql, new
        {
            NewReserved = Money.Format(newReserved),
            OldReserved = resStr,
            Now = now,
            BudgetId = budgetId
        }, transaction: tx);

        if (rows > 0)
        {
            tx.Commit();
            return true;
        }

        tx.Rollback();
        return false;
    }

    public async Task ReserveBudgetOrThrowAsync(
        string budgetId,
        decimal amount,
        string correlationId,
        CancellationToken ct = default)
    {
        var success = await TryReserveBudgetAsync(budgetId, amount, correlationId, ct);
        if (!success)
        {
            throw new AmccaException(
                AmccaErrors.Cst002,
                ErrorCategory.Validation,
                $"Budget exceeded on budget '{budgetId}'. Reservation of {amount:F2} refused (SPEC/20, D-003).");
        }
    }
}
