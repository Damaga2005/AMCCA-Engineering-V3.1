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
                AmccaErrors.Bud002,
                ErrorCategory.Budget,
                $"Budget exceeded on budget '{budgetId}'. Reservation of {amount:F2} refused (SPEC/20, D-003).");
        }
    }

    public async Task CreateOrUpdateBudgetAsync(
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
            VALUES (@Id, @Window, @ScopeId, @LimitAmount, '0.000000', '0.000000', @Currency, @Now, @Now)
            ON CONFLICT(id) DO UPDATE SET limit_amount = @LimitAmount, updated_at = @Now;
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = scopeId,
            Window = window,
            ScopeId = scopeId,
            LimitAmount = Money.Format(limitAmount),
            Currency = currency,
            Now = now
        });
    }

    public async Task<bool> ReserveAsync(
        string window,
        string scopeId,
        decimal amount,
        string correlationId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string selectSql = @"
            SELECT id, limit_amount AS LimitAmount, reserved AS Reserved, spent AS Spent
            FROM budgets
            WHERE scope_id = @ScopeId AND window = @Window;
        ";
        var b = await connection.QuerySingleOrDefaultAsync<dynamic>(selectSql, new { ScopeId = scopeId, Window = window }, transaction: tx);
        if (b == null)
        {
            tx.Rollback();
            throw new AmccaException(AmccaErrors.Bud002, ErrorCategory.Budget, $"Budget not found for scope {scopeId}");
        }

        string limitStr = (string)b.LimitAmount;
        string resStr = (string)b.Reserved;
        string spentStr = (string)b.Spent;

        decimal limit = Money.TryParse(limitStr, out decimal lVal) ? lVal : decimal.Parse(limitStr, System.Globalization.CultureInfo.InvariantCulture);
        decimal reserved = Money.TryParse(resStr, out decimal rVal) ? rVal : decimal.Parse(resStr, System.Globalization.CultureInfo.InvariantCulture);
        decimal spent = Money.TryParse(spentStr, out decimal sVal) ? sVal : decimal.Parse(spentStr, System.Globalization.CultureInfo.InvariantCulture);

        if (reserved + spent + amount > limit)
        {
            tx.Rollback();
            throw new AmccaException(AmccaErrors.Bud002, ErrorCategory.Budget, $"Reservation of {amount} exceeds budget limit {limit}");
        }

        var newReserved = reserved + amount;

        const string updateSql = @"
            UPDATE budgets
            SET reserved = @NewReserved,
                updated_at = @Now
            WHERE id = @Id AND reserved = @OldReserved;
        ";
        var rows = await connection.ExecuteAsync(updateSql, new
        {
            NewReserved = Money.Format(newReserved),
            OldReserved = b.Reserved.ToString(),
            Now = now,
            Id = (string)b.id
        }, transaction: tx);

        if (rows > 0)
        {
            tx.Commit();
            return true;
        }

        tx.Rollback();
        throw new AmccaException(AmccaErrors.Bud002, ErrorCategory.Budget, "Concurrent reservation conflict");
    }

    public async Task<bool> SettleAsync(
        string window,
        string scopeId,
        decimal amount,
        string? jobId = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string selectSql = @"
            SELECT id, reserved, spent
            FROM budgets
            WHERE scope_id = @ScopeId AND window = @Window;
        ";
        var b = await connection.QuerySingleOrDefaultAsync<dynamic>(selectSql, new { ScopeId = scopeId, Window = window }, transaction: tx);
        if (b == null)
        {
            tx.Rollback();
            return false;
        }

        string resStr = (string)b.reserved;
        string spentStr = (string)b.spent;

        decimal reserved = Money.TryParse(resStr, out decimal rVal) ? rVal : decimal.Parse(resStr, System.Globalization.CultureInfo.InvariantCulture);
        decimal spent = Money.TryParse(spentStr, out decimal sVal) ? sVal : decimal.Parse(spentStr, System.Globalization.CultureInfo.InvariantCulture);

        var newReserved = Math.Max(0m, reserved - amount);
        var newSpent = spent + amount;

        const string updateSql = @"
            UPDATE budgets
            SET reserved = @NewReserved,
                spent = @NewSpent,
                updated_at = @Now
            WHERE id = @Id;
        ";
        await connection.ExecuteAsync(updateSql, new
        {
            Id = (string)b.id,
            NewReserved = Money.Format(newReserved),
            NewSpent = Money.Format(newSpent),
            Now = now
        }, transaction: tx);

        tx.Commit();
        return true;
    }
}
