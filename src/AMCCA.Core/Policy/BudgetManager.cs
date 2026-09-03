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
            VALUES (@Id, @Window, @ScopeId, @LimitAmount, 0.0, 0.0, @Currency, @Now, @Now);
        ";
        await connection.ExecuteAsync(sql, new
        {
            Id = id,
            Window = window,
            ScopeId = scopeId,
            LimitAmount = (double)limitAmount,
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

        // SPEC/20 (TX-3): "One statement. The limit check is in the WHERE clause, so two concurrent workers cannot both pass it"
        const string sql = @"
            UPDATE budgets
            SET reserved = reserved + @Amount,
                updated_at = @Now
            WHERE id = @BudgetId
              AND (reserved + spent + @Amount) <= limit_amount;
        ";
        var rows = await connection.ExecuteAsync(sql, new
        {
            Amount = (double)amount,
            Now = now,
            BudgetId = budgetId
        });

        return rows > 0;
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
