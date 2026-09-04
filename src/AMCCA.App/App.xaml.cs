using System;
using System.IO;
using System.Windows;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.App.ViewModels;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using Microsoft.Extensions.DependencyInjection;

namespace AMCCA.App;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    public static IServiceProvider? ServiceProvider { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();
        ServiceProvider = _serviceProvider;

        // SEC-05: fail closed if the resolved secret store is not production-grade.
        SecretStoreGuard.EnsureProductionGrade(_serviceProvider.GetService<ISecretStore>());

        // Ensure database exists and schema migrated
        var migrationService = _serviceProvider.GetRequiredService<MigrationService>();
        migrationService.UpgradeAsync().GetAwaiter().GetResult();

        var navService = _serviceProvider.GetRequiredService<INavigationService>();
        navService.NavigateTo<DashboardViewModel>();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbDir = Path.Combine(localAppData, "AMCCA");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "amcca.db");

        var connectionFactory = new DatabaseConnectionFactory(dbPath);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<MigrationService>();
        services.AddSingleton<ISecretStore, WindowsDpapiSecretStore>();

        // UI Services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();

        // Navigation
        services.AddSingleton<INavigationService>(sp =>
        {
            return new NavigationService(type => (ViewModelBase)sp.GetRequiredService(type));
        });

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ProductionsViewModel>();
        services.AddTransient<ApprovalQueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AuditLogViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }
}
