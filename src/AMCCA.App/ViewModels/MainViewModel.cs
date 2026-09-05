using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Configuration;
using AMCCA.Core.Operator;

namespace AMCCA.App.ViewModels;

/// <summary>
/// Hosts the chrome every screen shares (sidebar navigation, and now the SPEC/60 status strip), so
/// obligations 1 and 2 -- the kill switch reachable in one action and autonomy/publishing state visible
/// -- are satisfied once here rather than duplicated into all six screen view models.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private readonly OperatorControlService _operatorControlService;
    private readonly AmccaConfig _config;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;

    private ViewModelBase? _currentView;
    private string _statusText = "Ready - Zero Trust Enforced";
    private int _pendingApprovalsCount;
    private bool _isKillSwitchActive;
    private string _autonomyMode = "-";
    private bool _publishingEnabled;

    public ViewModelBase? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public int PendingApprovalsCount
    {
        get => _pendingApprovalsCount;
        private set => SetProperty(ref _pendingApprovalsCount, value);
    }

    /// <summary>
    /// SPEC/60 obligation 1: reachable in one action from every screen. This is a presentation hint --
    /// clicking ToggleKillSwitchCommand re-asks OperatorControlService for the current state and toggles
    /// from that, not from a value that might be stale by the time the operator clicks (SPEC/62: the UI
    /// never caches a decision).
    /// </summary>
    public bool IsKillSwitchActive
    {
        get => _isKillSwitchActive;
        private set => SetProperty(ref _isKillSwitchActive, value);
    }

    /// <summary>SPEC/60 obligation 2: autonomy mode is visible on every screen, not buried in settings.</summary>
    public string AutonomyMode
    {
        get => _autonomyMode;
        private set => SetProperty(ref _autonomyMode, value);
    }

    /// <summary>SPEC/60 obligation 2: publishing state is visible on every screen, not buried in settings.</summary>
    public bool PublishingEnabled
    {
        get => _publishingEnabled;
        private set => SetProperty(ref _publishingEnabled, value);
    }

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateProductionsCommand { get; }
    public ICommand NavigateProductionInspectorCommand { get; }
    public ICommand NavigateJobQueueCommand { get; }
    public ICommand NavigateApprovalQueueCommand { get; }
    public ICommand NavigateAuditLogCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand ToggleKillSwitchCommand { get; }
    public ICommand RefreshStatusCommand { get; }

    public MainViewModel(
        INavigationService navigationService,
        OperatorControlService operatorControlService,
        AmccaConfig config,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _navigationService = navigationService;
        _operatorControlService = operatorControlService;
        _config = config;
        _dialogService = dialogService;
        _notificationService = notificationService;

        // SPEC/62: updates are event-driven rather than polled. Switching screens is the natural
        // moment to re-ask -- it is the point at which a stale kill switch or autonomy reading would
        // most mislead an operator about to act on the new screen.
        _navigationService.Navigated += vm =>
        {
            CurrentView = vm;
            _ = RefreshStatusAsync();
        };

        NavigateDashboardCommand = new RelayCommand(() => _navigationService.NavigateTo<DashboardViewModel>());
        NavigateProductionsCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionsViewModel>());
        NavigateProductionInspectorCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionInspectorViewModel>());
        NavigateJobQueueCommand = new RelayCommand(() => _navigationService.NavigateTo<JobQueueViewModel>());
        NavigateApprovalQueueCommand = new RelayCommand(() => _navigationService.NavigateTo<ApprovalQueueViewModel>());
        NavigateAuditLogCommand = new RelayCommand(() => _navigationService.NavigateTo<AuditLogViewModel>());
        NavigateSettingsCommand = new RelayCommand(() => _navigationService.NavigateTo<SettingsViewModel>());
        ToggleKillSwitchCommand = new AsyncRelayCommand(ToggleKillSwitchAsync);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync);

        _ = RefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _operatorControlService.GetSystemStatusAsync();
            IsKillSwitchActive = status.GlobalKillSwitchActive;
            AutonomyMode = status.AutonomyMode;
            PendingApprovalsCount = status.PendingApprovalsCount;
            PublishingEnabled = _config.PublishingEnabled;
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to refresh system status: {ex.Message}", "Error");
        }
    }

    public async Task ToggleKillSwitchAsync()
    {
        // Re-ask rather than trust the cached IsKillSwitchActive: another screen (Settings) or another
        // operator session could have changed it since this was last refreshed.
        var current = await _operatorControlService.GetSystemStatusAsync();
        var activating = !current.GlobalKillSwitchActive;

        var confirmed = await _dialogService.ShowConfirmAsync(
            activating ? "Engage Kill Switch" : "Clear Kill Switch",
            activating
                ? "This blocks every protected action system-wide until cleared. Continue?"
                : "This allows protected actions to proceed again. Continue?");
        if (!confirmed) return;

        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _operatorControlService.ToggleGlobalKillSwitchAsync(
                operatorId: "operator",
                active: activating,
                reason: activating
                    ? "Operator engaged the global kill switch from the navigation bar"
                    : "Operator cleared the global kill switch from the navigation bar",
                correlationId: correlationId);

            await RefreshStatusAsync();
            _notificationService.AddNotification(
                activating ? "Global kill switch engaged." : "Global kill switch cleared.",
                activating ? "Warning" : "Success");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to toggle kill switch: {ex.Message}", "Error");
        }
    }
}
