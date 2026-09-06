using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.App.Jobs;
using AMCCA.App.Orchestration;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Providers;
using AMCCA.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

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

        var logDir = Path.Combine(dbDir, "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDir, "orchestrator-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:o} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: true);
        Composition.AddAmccaCore(builder.Services, connectionFactory, config);

        // The model provider gateway, if config.providers.gateway is enabled and complete. Registered
        // only when present; the stage-handler agents block a production when it is absent rather than
        // faking a model call.
        var gateway = ProviderGatewayComposer.Compose(config, new WindowsDpapiSecretStore());
        if (gateway is not null)
        {
            builder.Services.AddSingleton(gateway);
        }
        builder.Services.AddSingleton<AMCCA.Core.Research.ResearchService>();
        builder.Services.AddSingleton(sp => new AMCCA.Core.Artifacts.ArtifactStore(
            sp.GetRequiredService<DatabaseConnectionFactory>(), config.DataRoot));

        builder.Services.AddSingleton(sp =>
        {
            var cf = sp.GetRequiredService<DatabaseConnectionFactory>();
            var gw = sp.GetService<IProviderGateway>();
            var prods = sp.GetRequiredService<AMCCA.Core.Domain.ProductionService>();
            var audit = sp.GetRequiredService<AMCCA.Core.Events.IAuditStore>();

            AMCCA.Core.Orchestration.Handlers.IResearchAgent? researchAgent = gw is null ? null
                : new AMCCA.Core.Orchestration.Handlers.AgentResearchAgent(
                    prods, sp.GetRequiredService<AMCCA.Core.Research.ResearchService>(), audit, gw);

            AMCCA.Core.Orchestration.Handlers.IScriptAgent? scriptAgent = gw is null ? null
                : new AMCCA.Core.Orchestration.Handlers.AgentScriptAgent(
                    prods, cf, audit, gw, sp.GetRequiredService<AMCCA.Core.Artifacts.ArtifactStore>());

            var registry = new StageHandlerRegistry();
            registry.Register("INIT", new InitStageHandler());
            // RESEARCHING / SCRIPTING run their generative agent (when a provider is configured) then
            // the deterministic verification (SPEC/26, SPEC/32).
            registry.Register("RESEARCHING", new ResearchStageHandler(cf, researchAgent));
            registry.Register("SCRIPTING", new ScriptStageHandler(cf, scriptAgent));
            // Pure bookkeeping states between producing stages.
            var advance = new NoWorkAdvanceHandler();
            registry.Register("RESEARCH_VERIFIED", advance);
            registry.Register("CONCEPT_SELECTED", advance);
            registry.Register("SCRIPT_VERIFIED", advance);
            registry.Register("STORYBOARD_VERIFIED", advance);
            registry.Register("ASSETS_READY", advance);
            registry.Register("AUDIO_READY", advance);
            registry.Register("CANDIDATE_RENDERED", advance);

            // Media producing stages (A5). No image/audio provider exists, so these block for an
            // operator until an IMediaStageAgent / IEditAgent is wired. EDITING enqueues the RENDER
            // job (A7), which is real, once an editor produces its input.
            registry.Register("STORYBOARDING", new MediaProducingStageHandler(cf, "STORYBOARDING", "STORYBOARD", agent: null));
            registry.Register("ASSET_GENERATION", new MediaProducingStageHandler(cf, "ASSET_GENERATION", "ASSET_MANIFEST", agent: null));
            registry.Register("AUDIO_GENERATION", new MediaProducingStageHandler(cf, "AUDIO_GENERATION", "AUDIO", agent: null));
            registry.Register("EDITING", new EditingStageHandler(cf, sp.GetRequiredService<JobManager>(), agent: null));

            // QA stages (SPEC/35). The media QA stages check a CURRENT RENDER artifact — they block
            // until the media stages (A5) produce one; CONTENT_QA / SCORING run on the SCRIPT.
            var artifacts = sp.GetRequiredService<AMCCA.Core.Artifacts.ArtifactStore>();
            var thresholds = AMCCA.Core.QA.QaThresholdProfileRegistry.FromConfig(config.Policy?.Qa);
            AMCCA.Core.Orchestration.Handlers.QaStageHandler Qa(string stage, AMCCA.Core.QA.IQaStageCheck check)
                => new(cf, stage, check, thresholds);
            registry.Register("TECHNICAL_QA", Qa("TECHNICAL_QA", new AMCCA.Core.QA.RenderPresenceQaCheck(cf)));
            registry.Register("VISUAL_QA", Qa("VISUAL_QA", new AMCCA.Core.QA.RenderPresenceQaCheck(cf)));
            registry.Register("AUDIO_QA", Qa("AUDIO_QA", new AMCCA.Core.QA.RenderPresenceQaCheck(cf)));
            registry.Register("CONTENT_QA", Qa("CONTENT_QA", new AMCCA.Core.QA.ContentQaCheck(cf, artifacts)));
            registry.Register("RETENTION_QA", Qa("RETENTION_QA", new AMCCA.Core.QA.RenderPresenceQaCheck(cf)));
            registry.Register("COMPLIANCE_QA", Qa("COMPLIANCE_QA", new AMCCA.Core.QA.ComplianceQaCheck(cf)));
            registry.Register("SCORING", Qa("SCORING", new AMCCA.Core.QA.ScoringCheck(cf)));

            // STORYBOARDING onward (media stages) are added in A5; the engine blocks a production at the
            // first unhandled state (AMCCA-ORC-001).
            return registry;
        });
        builder.Services.AddSingleton<OrchestratorEngine>();
        builder.Services.AddHostedService<OrchestratorHostedService>();

        // Job worker pool (SPEC/14, SPEC/16, SPEC/17).
        builder.Services.AddSingleton(sp =>
        {
            var reg = new JobHandlerRegistry();
            reg.Register("RENDER", new AMCCA.Core.Media.RenderMediaJobHandler(
                sp.GetRequiredService<AMCCA.Core.Artifacts.ArtifactStore>(),
                new AMCCA.Core.Media.ProcessFfmpegRunner(),
                config.DataRoot));
            return reg;
        });
        builder.Services.AddSingleton(JobWorkerOptions.Default);
        builder.Services.AddSingleton<JobWorkerEngine>();
        builder.Services.AddHostedService<JobWorkerHostedService>();
        builder.Services.AddHostedService<SystemHealthReporter>();

        var host = builder.Build();

        host.Services.GetRequiredService<MigrationService>().UpgradeAsync().GetAwaiter().GetResult();

        host.Run();
        return 0;
    }
}
