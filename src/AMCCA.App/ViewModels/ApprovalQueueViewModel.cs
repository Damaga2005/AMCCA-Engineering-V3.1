using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.App.ViewModels;

public record ApprovalItem(
    string Id,
    string ProductionId,
    string Action,
    string State,
    string CreatedAt);

public class ApprovalQueueViewModel : ViewModelBase
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    private ApprovalItem? _selectedApproval;
    private string _approvalReason = "Operator approved release compliance";

    public ObservableCollection<ApprovalItem> Approvals { get; } = new();

    public ApprovalItem? SelectedApproval
    {
        get => _selectedApproval;
        set => SetProperty(ref _selectedApproval, value);
    }

    public string ApprovalReason
    {
        get => _approvalReason;
        set => SetProperty(ref _approvalReason, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }

    public ApprovalQueueViewModel(
        DatabaseConnectionFactory connectionFactory,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _connectionFactory = connectionFactory;
        _dialogService = dialogService;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadApprovalsAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => SelectedApproval != null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => SelectedApproval != null);

        _ = LoadApprovalsAsync();
    }

    public async Task LoadApprovalsAsync()
    {
        Approvals.Clear();
        try
        {
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            var rows = await conn.QueryAsync<ApprovalItem>(
                "SELECT id AS Id, production_id AS ProductionId, action AS Action, state AS State, created_at AS CreatedAt FROM approvals WHERE state = 'PENDING' ORDER BY created_at ASC;");

            foreach (var r in rows)
            {
                Approvals.Add(r);
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load approvals: {ex.Message}", "Error");
        }
    }

    public async Task ApproveAsync()
    {
        if (SelectedApproval == null) return;

        try
        {
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync(@"
                UPDATE approvals
                SET state = 'APPROVED', decided_by = 'operator', decided_at = datetime('now')
                WHERE id = @Id;
            ", new { Id = SelectedApproval.Id });

            _notificationService.AddNotification($"Approval {SelectedApproval.Id} approved.", "Success");
            await LoadApprovalsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to approve: {ex.Message}", "Error");
        }
    }

    public async Task RejectAsync()
    {
        if (SelectedApproval == null) return;

        try
        {
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync(@"
                UPDATE approvals
                SET state = 'REJECTED', decided_by = 'operator', decided_at = datetime('now')
                WHERE id = @Id;
            ", new { Id = SelectedApproval.Id });

            _notificationService.AddNotification($"Approval {SelectedApproval.Id} rejected.", "Warning");
            await LoadApprovalsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to reject: {ex.Message}", "Error");
        }
    }
}
