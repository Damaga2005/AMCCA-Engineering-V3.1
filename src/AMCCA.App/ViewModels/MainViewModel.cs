using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;

namespace AMCCA.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private ViewModelBase? _currentView;
    private string _statusText = "Ready - Zero Trust Enforced";
    private int _pendingApprovalsCount;
    private bool _isKillSwitchActive;

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
        set => SetProperty(ref _pendingApprovalsCount, value);
    }

    public bool IsKillSwitchActive
    {
        get => _isKillSwitchActive;
        set => SetProperty(ref _isKillSwitchActive, value);
    }

    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateProductionsCommand { get; }
    public ICommand NavigateProductionInspectorCommand { get; }
    public ICommand NavigateApprovalQueueCommand { get; }
    public ICommand NavigateAuditLogCommand { get; }
    public ICommand NavigateSettingsCommand { get; }

    public MainViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
        _navigationService.Navigated += vm => CurrentView = vm;

        NavigateDashboardCommand = new RelayCommand(() => _navigationService.NavigateTo<DashboardViewModel>());
        NavigateProductionsCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionsViewModel>());
        NavigateProductionInspectorCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionInspectorViewModel>());
        NavigateApprovalQueueCommand = new RelayCommand(() => _navigationService.NavigateTo<ApprovalQueueViewModel>());
        NavigateAuditLogCommand = new RelayCommand(() => _navigationService.NavigateTo<AuditLogViewModel>());
        NavigateSettingsCommand = new RelayCommand(() => _navigationService.NavigateTo<SettingsViewModel>());
    }
}
