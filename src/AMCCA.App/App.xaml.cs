using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.App.ViewModels;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using AMCCA.Core.Preflight;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using Microsoft.Extensions.DependencyInjection;

namespace AMCCA.App;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    public static IServiceProvider? ServiceProvider { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        try
        {
            ConfigureServices(services);
        }
        catch (AmccaException ex)
        {
            // config.yaml failed SPEC/49 gate 1 (schema) or gate 2 (literal credential) before the DI
            // container even exists -- abort exactly as PreflightService itself would.
            ShowAbortAndShutdown(PreflightStatus.Abort, new[] { ex.Message });
            return;
        }

        _serviceProvider = services.BuildServiceProvider();
        ServiceProvider = _serviceProvider;

        // SEC-05: fail closed if the resolved secret store is not production-grade.
        SecretStoreGuard.EnsureProductionGrade(_serviceProvider.GetService<ISecretStore>());

        // SPEC/60: "The UI thread performs no I/O, no database access and no waiting." Show the window
        // right away -- MainViewModel starts in its "starting up" state, so it renders a checks overlay
        // and touches no database until the preflight below has run the migrations that create it.
        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // SPEC/49: system startup preflight, before recovery. Migrations (gates 4-5) are applied as
        // part of this call. Task.Run keeps its synchronous prefix and any pool continuations
        // (gate 8's Process.WaitForExitAsync genuinely suspends) off the UI thread; awaiting the
        // result -- rather than blocking on it -- lets the dispatcher keep pumping the window we
        // just showed.
        var config = _serviceProvider.GetRequiredService<AmccaConfig>();
        var secretStore = _serviceProvider.GetRequiredService<ISecretStore>();
        var preflightService = _serviceProvider.GetRequiredService<IPreflightService>();

        PreflightReport report;
        try
        {
            report = await Task.Run(() => preflightService.RunSystemStartupPreflightAsync(config, secretStore));
        }
        catch (Exception ex)
        {
            mainWindow.Close();
            ShowAbortAndShutdown(PreflightStatus.Abort, new[] { $"Preflight threw before completing: {ex.Message}" });
            return;
        }

        if (!report.IsStartupPermitted)
        {
            mainWindow.Close();
            ShowAbortAndShutdown(report.Status, report.FailureDetails);
            return;
        }

        // Preflight passed (or degraded): the database now exists. Hand control to the window --
        // navigate to the Dashboard, take the first status reading, surface any degraded warnings.
        await mainViewModel.CompleteStartupAsync(
            degraded: report.Status == PreflightStatus.Degraded,
            warnings: report.Warnings);
    }

    private void ShowAbortAndShutdown(PreflightStatus status, IReadOnlyList<string> failureDetails)
    {
        var details = string.Join(Environment.NewLine, failureDetails);
        MessageBox.Show(
            $"AMCCA cannot start (SPEC/49 preflight, status {status}):{Environment.NewLine}{details}",
            "Preflight failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
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

        // SPEC/03 "Deployed configuration file location", SPEC/04, SPEC/49 gates 1-2: validate
        // config.yaml against the bundled schema if the operator has placed one next to the database;
        // otherwise fall back to AmccaConfig's built-in safe defaults (DryRun=true, publishing disabled)
        // scoped to this install's data directory.
        var configPath = Path.Combine(dbDir, "config.yaml");
        AmccaConfig config;
        if (File.Exists(configPath))
        {
            var configService = ConfigService.CreateWithBundledSchema();
            config = configService.LoadFromYaml(File.ReadAllText(configPath));
        }
        else
        {
            config = new AmccaConfig { DataRoot = dbDir };
        }
        services.AddSingleton(config);

        // Domain / Operator services (SPEC/09, SPEC/12, SPEC/60, DEF-001/DEF-002: UI must never write
        // productions/approvals/settings directly -- every mutation goes through a domain service).
        services.AddSingleton<IAuditStore, AuditStore>();
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton(_ => StateMachineRegistry.CreateFromBundledDefinition());
        services.AddSingleton<ProductionService>();
        services.AddSingleton<BudgetManager>();
        services.AddSingleton<ApprovalManager>();
        services.AddSingleton<PolicyEngine>();
        services.AddSingleton<JobManager>();
        services.AddSingleton<OperatorControlService>();
        services.AddSingleton<IPreflightService, PreflightService>();

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
        services.AddTransient<ProductionInspectorViewModel>();
        services.AddTransient<JobQueueViewModel>();
        services.AddTransient<ApprovalQueueViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AuditLogViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }
}
