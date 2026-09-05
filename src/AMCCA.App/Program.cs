using System;
using System.Threading.Tasks;
using AMCCA.App.Jobs;
using AMCCA.App.Orchestration;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using AMCCA.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AMCCA.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine("3.1.0");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--headless")
        {
            Console.WriteLine("AMCCA Engineering V3.1 Runtime (Headless)");
            Console.WriteLine("System initialized successfully.");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--orchestrator")
        {
            return RunOrchestrator(args);
        }

        var app = new App();
        return app.Run();
    }

    /// <summary>
    /// Headless host: runs migrations, then the orchestrator BackgroundService that drives productions
    /// through the SPEC/13 state machine until Ctrl+C.
    /// </summary>
    private static int RunOrchestrator(string[] args)
    {
        var (dbDir, dbPath) = Composition.ResolvePaths();
        var connectionFactory = new DatabaseConnectionFactory(dbPath);
        var config = Composition.LoadConfig(dbDir);

        var builder = Host.CreateApplicationBuilder(args);
        Composition.AddAmccaCore(builder.Services, connectionFactory, config);

        builder.Services.AddSingleton(sp =>
        {
            var registry = new StageHandlerRegistry();
            registry.Register("INIT", new InitStageHandler());
            // Handlers for RESEARCHING, SCRIPTING, ASSET_GENERATION, the QA stages, etc. are added as
            // they are built (P0.2 / P0.3). Until then the engine drives a production to the first
            // unhandled state and blocks it for an operator (AMCCA-ORC-001).
            return registry;
        });
        builder.Services.AddSingleton<OrchestratorEngine>();
        builder.Services.AddHostedService<OrchestratorHostedService>();

        // Job worker pool (SPEC/14, SPEC/16, SPEC/17). No handlers registered yet — an enqueued job
        // requeues and, after max_attempts, dead-letters for an operator (P0.3 adds the handlers).
        builder.Services.AddSingleton(new JobHandlerRegistry());
        builder.Services.AddSingleton(JobWorkerOptions.Default);
        builder.Services.AddSingleton<JobWorkerEngine>();
        builder.Services.AddHostedService<JobWorkerHostedService>();

        var host = builder.Build();

        host.Services.GetRequiredService<MigrationService>().UpgradeAsync().GetAwaiter().GetResult();

        host.Run();
        return 0;
    }
}
