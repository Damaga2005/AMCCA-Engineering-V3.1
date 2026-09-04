using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.App.ViewModels;

public class DashboardViewModel : ViewModelBase
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly INavigationService _navigationService;
    private int _activeProductionsCount;
    private int _pendingApprovalsCount;
    private int _verifiedPublicationsCount;
    private bool _globalKillSwitch;

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

    public bool GlobalKillSwitch
    {
        get => _globalKillSwitch;
        set => SetProperty(ref _globalKillSwitch, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand NewProductionCommand { get; }
    public ICommand ViewApprovalsCommand { get; }

    public DashboardViewModel(DatabaseConnectionFactory connectionFactory, INavigationService navigationService)
    {
        _connectionFactory = connectionFactory;
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
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            ActiveProductionsCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM productions WHERE state NOT IN ('PUBLISHED', 'FAILED', 'CANCELLED');");

            PendingApprovalsCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM approvals WHERE state = 'PENDING';");

            VerifiedPublicationsCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM publications WHERE state = 'VERIFIED';");
        }
        catch { }
    }
}
