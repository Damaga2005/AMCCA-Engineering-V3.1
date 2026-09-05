using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using AMCCA.Core.Operator;
using Dapper;

namespace AMCCA.App.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly OperatorControlService _operatorControlService;
    private readonly INavigationService _navigationService;
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
        DatabaseConnectionFactory connectionFactory,
        OperatorControlService operatorControlService,
        INavigationService navigationService)
    {
        _connectionFactory = connectionFactory;
        _operatorControlService = operatorControlService;
        _navigationService = navigationService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewProductionCommand = new RelayCommand(() => _navigationService.NavigateTo<ProductionsViewModel>());
        ViewApprovalsCommand = new RelayCommand(() => _navigationService.NavigateTo<ApprovalQueueViewModel>());

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            // Active productions and pending approvals both used to be computed here with their own
            // hand-rolled SQL, which is exactly how they drifted from OperatorControlService's numbers
            // for the same two concepts (DEF-005: this screen once excluded 'PUBLISHED', a state that
            // does not exist in the canonical machine, and counted ARCHIVED productions as active).
            // Reusing the one place that already computes them removes that drift risk instead of just
            // fixing this copy's SQL to match today.
            var status = await _operatorControlService.GetSystemStatusAsync();
            ActiveProductionsCount = status.ActiveProductionsCount;
            PendingApprovalsCount = status.PendingApprovalsCount;

            // OperatorControlService has no notion of publications; this is the only count still local.
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            VerifiedPublicationsCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM publications WHERE state = 'VERIFIED';");
        }
        catch { }
    }
}
