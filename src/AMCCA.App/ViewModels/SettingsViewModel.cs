using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Contracts;
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
            _notificationService.AddNotification(
                $"Failed to load settings: {ex.Message} Retry, or restart the application if this persists.",
                "Error");
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
        catch (AmccaException ex)
        {
            // SPEC/60 obligation 6: ex.Message already carries the SPEC/05 code (AmccaException's own
            // constructor embeds it), so only the operator action needs adding here.
            _notificationService.AddNotification(
                $"{ex.Message} The kill switch was not changed; refresh this screen to see its current state.",
                "Error");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification(
                $"Failed to save settings: {ex.Message} Retry, or refresh this screen to see the current state.",
                "Error");
        }
    }
}
