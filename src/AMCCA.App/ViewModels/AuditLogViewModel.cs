using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.App.ViewModels;

public record AuditLogItem(
    string AuditId,
    string Action,
    string ActorType,
    string ActorId,
    string SubjectType,
    string SubjectId,
    string Outcome,
    string OccurredAt);

public class AuditLogViewModel : ViewModelBase
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly INotificationService _notificationService;

    private string _filterQuery = string.Empty;

    public ObservableCollection<AuditLogItem> Entries { get; } = new();

    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (SetProperty(ref _filterQuery, value))
            {
                _ = LoadAuditLogAsync();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public AuditLogViewModel(DatabaseConnectionFactory connectionFactory, INotificationService notificationService)
    {
        _connectionFactory = connectionFactory;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadAuditLogAsync);
        _ = LoadAuditLogAsync();
    }

    public async Task LoadAuditLogAsync()
    {
        Entries.Clear();
        try
        {
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            string sql;
            object param;

            if (string.IsNullOrWhiteSpace(FilterQuery))
            {
                sql = @"
                    SELECT audit_id AS AuditId, action AS Action, actor_type AS ActorType, actor_id AS ActorId,
                           subject_type AS SubjectType, subject_id AS SubjectId, outcome AS Outcome, occurred_at AS OccurredAt
                    FROM audit_log
                    ORDER BY occurred_at DESC
                    LIMIT 100;
                ";
                param = new { };
            }
            else
            {
                sql = @"
                    SELECT audit_id AS AuditId, action AS Action, actor_type AS ActorType, actor_id AS ActorId,
                           subject_type AS SubjectType, subject_id AS SubjectId, outcome AS Outcome, occurred_at AS OccurredAt
                    FROM audit_log
                    WHERE action LIKE @Pattern OR subject_id LIKE @Pattern OR actor_id LIKE @Pattern
                    ORDER BY occurred_at DESC
                    LIMIT 100;
                ";
                param = new { Pattern = $"%{FilterQuery}%" };
            }

            var rows = await conn.QueryAsync<AuditLogItem>(sql, param);
            foreach (var r in rows)
            {
                Entries.Add(r);
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load audit logs: {ex.Message}", "Error");
        }
    }
}
