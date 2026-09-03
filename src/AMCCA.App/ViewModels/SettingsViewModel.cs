using System;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using AMCCA.Core.Security;
using Dapper;

namespace AMCCA.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly DatabaseConnectionFactory _connectionFactory;
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
        DatabaseConnectionFactory connectionFactory,
        ISecretStore secretStore,
        INotificationService notificationService)
    {
        _connectionFactory = connectionFactory;
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
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            var ksVal = await conn.ExecuteScalarAsync<string>(
                "SELECT value_json FROM settings WHERE key = 'kill_switch.global';");
            GlobalKillSwitch = ksVal?.Contains("true") ?? false;
        }
        catch { }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            var json = GlobalKillSwitch ? "{\"active\":true}" : "{\"active\":false}";
            await conn.ExecuteAsync(@"
                INSERT INTO settings (key, value_json, schema_version, updated_by, updated_at)
                VALUES ('kill_switch.global', @Json, '3.1.0', 'operator', datetime('now'))
                ON CONFLICT(key) DO UPDATE SET value_json = @Json, updated_at = datetime('now');
            ", new { Json = json });

            _notificationService.AddNotification("Settings saved successfully.", "Success");
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to save settings: {ex.Message}", "Error");
        }
    }
}
