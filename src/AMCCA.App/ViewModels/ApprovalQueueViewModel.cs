using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Contracts;
using AMCCA.Core.Operator;

namespace AMCCA.App.ViewModels;

public record ApprovalItem(
    string Id,
    string ProductionId,
    string Action,
    string State,
    string CreatedAt,
    string ExpiresAt,
    string? Subject,
    decimal? CostCeiling)
{
    // SPEC/60 obligation 5: "Every approval request shows the exact action, subject, cost ceiling and
    // expiry being approved." A legacy or scope-less approval has no subject/cost ceiling to show;
    // this states that plainly instead of leaving a blank cell that reads as a loading glitch.
    public string SubjectDisplay => Subject ?? "(no scope recorded)";
    public string CostCeilingDisplay => CostCeiling is { } c
        ? c.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
        : "(no scope recorded)";
}

public class ApprovalQueueViewModel : ViewModelBase
{
    private readonly OperatorControlService _operatorControlService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    private ApprovalItem? _selectedApproval;
    private string _approvalReason = "Operator approved release compliance";

    // The constructor starts a load, and so do Refresh and every decision. Each load takes a token and
    // only applies its results if no newer load has started since, so a slow earlier query cannot repaint
    // the queue with approvals the operator has already decided.
    private int _loadRequestToken;

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
        OperatorControlService operatorControlService,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _operatorControlService = operatorControlService;
        _dialogService = dialogService;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadApprovalsAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => SelectedApproval != null);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => SelectedApproval != null);

        _ = LoadApprovalsAsync();
    }

    public async Task LoadApprovalsAsync()
    {
        var token = ++_loadRequestToken;
        try
        {
            var pending = await _operatorControlService.GetPendingApprovalsAsync();

            if (token != _loadRequestToken)
            {
                return; // a newer load has started; its results are the ones that count
            }

            Approvals.Clear();
            foreach (var p in pending)
            {
                Approvals.Add(new ApprovalItem(p.Id, p.ProductionId, p.Action, p.State, p.CreatedAt, p.ExpiresAt, p.Subject, p.CostCeiling));
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification(
                $"Failed to load approvals: {ex.Message} Retry the refresh.",
                "Error");
        }
    }

    public async Task ApproveAsync()
    {
        if (SelectedApproval == null) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _operatorControlService.SubmitApprovalDecisionAsync(
                operatorId: "operator",
                approvalId: SelectedApproval.Id,
                approved: true,
                reason: ApprovalReason,
                correlationId: correlationId);

            _notificationService.AddNotification($"Approval {SelectedApproval.Id} approved.", "Success");
            await LoadApprovalsAsync();
        }
        catch (AmccaException ex)
        {
            // SPEC/60 obligation 6 / SPEC/62: the decision is re-asked atomically inside
            // SubmitApprovalDecisionAsync, so a rejection here means the approval's real state has moved
            // (expired, already decided) since the screen loaded -- refreshing is the correct next action.
            _notificationService.AddNotification(
                $"{ex.Message} Refresh the queue to see the approval's current state before retrying.",
                "Error");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to approve: {ex.Message} Refresh the queue and retry.", "Error");
        }
    }

    public async Task RejectAsync()
    {
        if (SelectedApproval == null) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _operatorControlService.SubmitApprovalDecisionAsync(
                operatorId: "operator",
                approvalId: SelectedApproval.Id,
                approved: false,
                reason: ApprovalReason,
                correlationId: correlationId);

            _notificationService.AddNotification($"Approval {SelectedApproval.Id} rejected.", "Warning");
            await LoadApprovalsAsync();
        }
        catch (AmccaException ex)
        {
            _notificationService.AddNotification(
                $"{ex.Message} Refresh the queue to see the approval's current state before retrying.",
                "Error");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to reject: {ex.Message} Refresh the queue and retry.", "Error");
        }
    }
}
