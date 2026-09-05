using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Events;

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
    private readonly IAuditStore _auditStore;
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

    public AuditLogViewModel(IAuditStore auditStore, INotificationService notificationService)
    {
        _auditStore = auditStore;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadAuditLogAsync);
        _ = LoadAuditLogAsync();
    }

    public async Task LoadAuditLogAsync()
    {
        Entries.Clear();
        try
        {
            var rows = await _auditStore.SearchAuditLogsAsync(FilterQuery, limit: 100);
            foreach (var r in rows)
            {
                Entries.Add(new AuditLogItem(
                    r.AuditId, r.Action, r.ActorType, r.ActorId,
                    r.SubjectType ?? string.Empty, r.SubjectId ?? string.Empty,
                    r.Outcome, r.OccurredAt));
            }
        }
        catch (Exception ex)
        {
            // SPEC/60 obligation 6: a failure needs an operator action, not just a message. Retrying or
            // clearing the filter are the only two things this screen can do about a failed query.
            _notificationService.AddNotification(
                $"Failed to load audit logs: {ex.Message} Retry, or clear the filter and refresh.",
                "Error");
        }
    }
}
