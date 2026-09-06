using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;
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

        ConfigureGlobalExceptionHandling();

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
        try
        {
            await mainViewModel.CompleteStartupAsync(
                degraded: report.Status == PreflightStatus.Degraded,
                warnings: report.Warnings);
        }
        catch (Exception ex)
        {
            // Startup itself already passed; a failure completing it is not fatal -- keep the window
            // up so the operator can retry, but do not let the async-void frame crash the process.
            Serilog.Log.Error(ex, "CompleteStartupAsync failed");
            MessageBox.Show(
                $"AMCCA started but the dashboard failed to load: {ex.Message}{Environment.NewLine}Use Refresh to retry.",
                "Startup warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Last-resort net for exceptions that escape an async-void frame (ICommand handlers, event
    /// handlers) or an unawaited Task. Without this a single unexpected throw on the UI thread ends
    /// the process with no log -- unacceptable for an app meant to run unattended for days.
    /// </summary>
    private void ConfigureGlobalExceptionHandling()
    {
        try
        {
            var (dbDir, _) = Composition.ResolvePaths();
            var logDir = Path.Combine(dbDir, "logs");
            Directory.CreateDirectory(logDir);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(logDir, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            // A logging sink we cannot open must not stop the app from starting.
            System.Diagnostics.Debug.WriteLine($"App logger init failed: {ex}");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Serilog.Log.Error(args.Exception, "Unhandled dispatcher exception");
            MessageBox.Show(
                $"AMCCA could not complete the last action ({args.Exception.GetType().Name}: {args.Exception.Message}).{Environment.NewLine}"
                + $"The action was cancelled and AMCCA is still running. Details are in the app log under logs/.",
                "Action failed", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Serilog.Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Serilog.Log.Fatal(ex, "Unhandled AppDomain exception (terminating={Terminating})", args.IsTerminating);
            }
        };
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
        var (dbDir, dbPath) = Composition.ResolvePaths();
        var connectionFactory = new DatabaseConnectionFactory(dbPath);
        var config = Composition.LoadConfig(dbDir);

        // Core singletons shared with the headless orchestrator host (see Composition / Program).
        // DEF-001/DEF-002: the UI never writes productions/approvals/settings directly -- every mutation
        // goes through one of these domain services.
        Composition.AddAmccaCore(services, connectionFactory, config);

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
