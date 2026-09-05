using System;
using System.IO;
using AMCCA.Core.Configuration;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using Microsoft.Extensions.DependencyInjection;

namespace AMCCA.App;

/// <summary>
/// Shared composition for the two hosts — the WPF operator console (<see cref="App"/>) and the headless
/// orchestrator (<see cref="Program"/>). Both resolve the same data directory, load the same
/// <c>config.yaml</c>, and register the same Core singletons, so they cannot drift.
/// </summary>
internal static class Composition
{
    public static (string DbDir, string DbPath) ResolvePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbDir = Path.Combine(localAppData, "AMCCA");
        Directory.CreateDirectory(dbDir);
        return (dbDir, Path.Combine(dbDir, "amcca.db"));
    }

    /// <summary>
    /// SPEC/03 "Deployed configuration file location", SPEC/04, SPEC/49 gates 1-2: validate config.yaml
    /// against the bundled schema if the operator placed one next to the database; otherwise fall back
    /// to AmccaConfig's built-in safe defaults (DryRun=true, publishing disabled).
    /// </summary>
    public static AmccaConfig LoadConfig(string dbDir)
    {
        var configPath = Path.Combine(dbDir, "config.yaml");
        if (File.Exists(configPath))
        {
            return ConfigService.CreateWithBundledSchema().LoadFromYaml(File.ReadAllText(configPath));
        }
        return new AmccaConfig { DataRoot = dbDir };
    }

    /// <summary>The Core singletons both hosts need. UI services and the preflight are added by the console only.</summary>
    public static void AddAmccaCore(IServiceCollection services, DatabaseConnectionFactory connectionFactory, AmccaConfig config)
    {
        services.AddSingleton(connectionFactory);
        services.AddSingleton(config);
        services.AddSingleton<MigrationService>();
        services.AddSingleton<ISecretStore, WindowsDpapiSecretStore>();

        services.AddSingleton<IAuditStore, AuditStore>();
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton(_ => StateMachineRegistry.CreateFromBundledDefinition());
        services.AddSingleton<ProductionService>();
        services.AddSingleton<BudgetManager>();
        services.AddSingleton<ApprovalManager>();
        services.AddSingleton<PolicyEngine>();
        services.AddSingleton<JobManager>();
        services.AddSingleton<OperatorControlService>();
    }
}
