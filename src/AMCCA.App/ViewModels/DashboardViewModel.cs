using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Operator;

namespace AMCCA.App.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly OperatorControlService _operatorControlService;
    private readonly INavigationService _navigationService;
    private readonly INotificationService _notificationService;
    private int _activeProductionsCount;
    private int _pendingApprovalsCount;
    private int _verifiedPublicationsCount;

    public int ActiveProductionsCount
    {
        get => _activeProductionsCount;
        set => SetProperty(ref _activeProductionsCount, value);
    }

    public int PendingApprovalsCount
    {
        get => _pendingApprovalsCount;
        set => SetProperty(ref _pendingApprovalsCount, value);
    }

    public int VerifiedPublicationsCount
    {
        get => _verifiedPublicationsCount;
        set => SetProperty(ref _verifiedPublicationsCount, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand NewProductionCommand { get; }
    public ICommand ViewApprovalsCommand { get; }

    public DashboardViewModel(
        OperatorControlService operatorControlService,
        INavigationService navigationService,
        INotificationService notificationService)
    {
        _operatorControlService = operatorControlService;
        _navigationService = navigationService;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewProductionCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionsViewModel>());
        ViewApprovalsCommand = new RelayCommand(() => _navigationService.NavigateTo<ApprovalQueueViewModel>());

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            // Every count comes from the one service that already computes them. These used to be
            // hand-rolled SQL here, which is exactly how they drifted from the canonical numbers
            // (DEF-005: this screen once excluded 'PUBLISHED', a non-existent state, and counted
            // ARCHIVED productions as active).
            var status = await _operatorControlService.GetSystemStatusAsync();
            ActiveProductionsCount = status.ActiveProductionsCount;
            PendingApprovalsCount = status.PendingApprovalsCount;
            VerifiedPublicationsCount = status.VerifiedPublicationsCount;
        }
        catch (Exception ex)
        {
            // SPEC/60 obligation 6: a failed load needs an operator action, not a silently blank screen.
            _notificationService.AddNotification(
                $"Failed to load dashboard counts: {ex.Message} Use Refresh to retry.",
                "Error");
        }
    }
}
