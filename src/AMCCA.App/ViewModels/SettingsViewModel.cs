using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Operator;
using AMCCA.Core.Security;

namespace AMCCA.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly OperatorControlService _operatorControlService;
    private readonly ISecretStore _secretStore;
    private readonly INotificationService _notificationService;

    private string _databasePath = string.Empty;
    private bool _globalKillSwitch;
    private bool _secretStoreAvailable;
    private string _packageVersion = "3.1.0";

    public string DatabasePath
    {
        get => _databasePath;
        set => SetProperty(ref _databasePath, value);
    }

    public bool GlobalKillSwitch
    {
        get => _globalKillSwitch;
        set => SetProperty(ref _globalKillSwitch, value);
    }

    public bool SecretStoreAvailable
    {
        get => _secretStoreAvailable;
        set => SetProperty(ref _secretStoreAvailable, value);
    }

    public string PackageVersion
    {
        get => _packageVersion;
        set => SetProperty(ref _packageVersion, value);
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand RefreshCommand { get; }

    public SettingsViewModel(
        OperatorControlService operatorControlService,
        ISecretStore secretStore,
        INotificationService notificationService)
    {
        _operatorControlService = operatorControlService;
        _secretStore = secretStore;
        _notificationService = notificationService;

        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        RefreshCommand = new AsyncRelayCommand(LoadSettingsAsync);

        _ = LoadSettingsAsync();
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            SecretStoreAvailable = await _secretStore.IsReachableAsync();
            var status = await _operatorControlService.GetSystemStatusAsync();
            GlobalKillSwitch = status.GlobalKillSwitchActive;
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load settings: {ex.Message}", "Error");
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            var correlationId = Guid.NewGuid().ToString("N");
            await _operatorControlService.ToggleGlobalKillSwitchAsync(
                operatorId: "operator",
                active: GlobalKillSwitch,
                reason: GlobalKillSwitch ? "Operator engaged kill switch from Settings" : "Operator cleared kill switch from Settings",
                correlationId: correlationId);

            _notificationService.AddNotification("Settings saved successfully.", "Success");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to save settings: {ex.Message}", "Error");
        }
    }
}
