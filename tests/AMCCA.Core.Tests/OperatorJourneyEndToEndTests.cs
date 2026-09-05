using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.App.Services;
using AMCCA.App.ViewModels;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Jobs;
using AMCCA.Core.Monetization;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using AMCCA.Core.Preflight;
using AMCCA.Core.Research;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// DEF-006 / Phase I: an operator journey driven through the same layers the application uses.
///
/// The distinction this file exists to make is the one the audit drew: EndToEndProductionPipelineTests
/// calls Core services directly, seeds artifacts with raw SQL and asserts a QA verdict by invoking the
/// evaluator as a pure function -- it demonstrates that components interoperate, not that a user journey
/// works. Here every operator action goes through the view model the operator would actually click, and
/// every domain step goes through the same service the application resolves from its DI container. The
/// test itself never opens a database connection: it neither seeds state nor verifies outcomes with raw
/// SQL, which is the constraint Phase I sets for a product E2E.
///
/// WHAT THIS JOURNEY DELIBERATELY DOES NOT COVER, and why:
/// Trends (SPEC/29), Opportunity scoring, Hooks (SPEC/31), Storyboard and Assets (SPEC/32), Voice, the
/// Media render pipeline (SPEC/33), rights and duplicate gating (SPEC/36), real platform publishing and
/// verification (SPEC/40-44) and analytics/attribution (SPEC/47) are absent from this test because those
/// subsystems do not exist in this build. Standing in for them with fakes would manufacture exactly the
/// false green the audit warns about, so the journey stops where the implementation stops. Closing
/// DEF-006 fully requires those subsystems first; this closes the half that was achievable -- proving the
/// existing journey runs through the real layers rather than around them.
/// </summary>
public class OperatorJourneyEndToEndTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrationService;
    private readonly AmccaConfig _config;
    private readonly ISecretStore _secretStore;
    private readonly IPreflightService _preflightService;

    private readonly StateMachineRegistry _stateMachine;
    private readonly IEventStore _eventStore;
    private readonly IAuditStore _auditStore;
    private readonly ProductionService _productionService;
    private readonly ResearchService _researchService;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;
    private readonly PolicyEngine _policyEngine;
    private readonly JobManager _jobManager;
    private readonly RevenueService _revenueService;
    private readonly OperatorControlService _operatorControl;

    private readonly INotificationService _notifications;
    private readonly AutoConfirmDialogService _dialogs;

    public OperatorJourneyEndToEndTests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");

        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_JOURNEY_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        // Same object graph App.xaml.cs builds, in the same order.
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "journey.db"));
        _migrationService = new MigrationService(_factory, _testDir);
        _secretStore = new InMemorySecretStore();

        var schemaJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "config.schema.json"));
        var exampleYaml = File.ReadAllText(Path.Combine(_repoRoot, "CONFIG", "config.example.yaml"));
        _config = new ConfigService(schemaJson).LoadFromYaml(exampleYaml);
        _config.DataRoot = Path.Combine(_testDir, "data_root");

        _preflightService = new PreflightService(_factory, _migrationService);

        _stateMachine = new StateMachineRegistry(
            File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "state-machine.json")));
        _eventStore = new EventStore(_factory);
        _auditStore = new AuditStore(_factory);
        _productionService = new ProductionService(_factory, _stateMachine, _eventStore);
        _researchService = new ResearchService(_factory);
        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);
        _jobManager = new JobManager(_factory);
        _revenueService = new RevenueService(_factory);
        _operatorControl = new OperatorControlService(_factory, _auditStore, _policyEngine, _approvalManager, _jobManager);

        _notifications = new NotificationService();
        _dialogs = new AutoConfirmDialogService();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp dir
        }
    }

    [Fact]
    public async Task OperatorJourney_FromStartupToProfit_RunsThroughTheApplicationsOwnLayers()
    {
        // ---------------------------------------------------------------- 1. Startup (SPEC/49)
        // The preflight is the application's real entry gate, and it is what creates and migrates the
        // database (gates 4-5). Nothing in this test migrates behind its back.
        var preflight = await _preflightService.RunSystemStartupPreflightAsync(_config, _secretStore);
        preflight.IsStartupPermitted.Should().BeTrue(
            "the journey cannot begin if the application would refuse to start: {0}",
            string.Join("; ", preflight.FailureDetails));

        // ---------------------------------------------------- 2. Operator creates a production (SPEC/60)
        var productionsVm = new ProductionsViewModel(_productionService, _dialogs, _notifications);
        await productionsVm.LoadProductionsAsync();

        productionsVm.NewTopic = "Documental: el archivo de 1920";
        productionsVm.NewNiche = "history";
        await productionsVm.CreateProductionAsync();

        var listed = productionsVm.Productions.Should()
            .ContainSingle(p => p.Topic == "Documental: el archivo de 1920").Subject;
        listed.State.Should().Be("INIT");
        listed.NicheId.Should().Be("history");

        var productionId = listed.Id;

        // ------------------------------------------------- 3. Evidence plane: sources and claims (SPEC/26-27)
        var source = new Source
        {
            Id = UlidGenerator.NewUlid(),
            Url = "https://archive.example.org/fondo-1920",
            Publisher = "Archive Trust",
            TrustTier = "PRIMARY",
            RobotsAllowed = true,
            ContentHash = new string('a', 64)
        };
        await _researchService.InsertSourceAsync(source);

        var claim = new Claim
        {
            Id = UlidGenerator.NewUlid(),
            ProductionId = productionId,
            Text = "El archivo histórico confirma el evento de 1920.",
            Status = "VERIFIED",
            Materiality = "MATERIAL",
            SubjectClass = "GENERAL",
            ContainsPersonalData = false
        };
        await _researchService.InsertClaimWithSourceAsync(claim, source.Id, "SUPPORTS");
        (await _researchService.GetClaimAsync(claim.Id))!.Status.Should().Be("VERIFIED");

        // ------------------------------------------------------------- 4. Budget for the run (SPEC/20)
        await _budgetManager.CreateBudgetAsync("journey-budget", "MONTHLY", productionId, 100.00m, "EUR");
        (await _budgetManager.TryReserveBudgetAsync("journey-budget", 15.50m, "corr-journey-budget"))
            .Should().BeTrue();

        // ------------------------------------------ 5. Pipeline advances through the state machine (SPEC/13)
        // The orchestrator is the sole state committer (DEF-008), so these go through ProductionService,
        // which validates every hop against the canonical transition matrix.
        foreach (var state in new[] { "RESEARCHING", "RESEARCH_VERIFIED", "CONCEPT_SELECTED", "SCRIPTING", "SCRIPT_VERIFIED" })
        {
            await _productionService.TransitionAsync(productionId, state, "ORCHESTRATOR", "corr-journey-pipeline");
        }
        (await _productionService.GetProductionAsync(productionId))!.State.Should().Be("SCRIPT_VERIFIED");

        // ------------------------------- 6. Work runs as a job, dies, and the operator rescues it (SPEC/14)
        var job = await _jobManager.EnqueueJobAsync(
            type: "generate_script",
            idempotencyKey: "idem-journey-script",
            correlationId: "corr-journey-job",
            payloadJson: "{}",
            priority: 2,
            maxAttempts: 1,
            productionId: productionId);

        var lease = await _jobManager.AcquireLeaseAsync(job.Id, "render-worker-1", TimeSpan.FromMinutes(2));
        await _jobManager.FailJobAsync(job.Id, "render-worker-1", lease!.FenceToken, "provider timeout");
        (await _jobManager.GetJobAsync(job.Id))!.State.Should().Be("DEAD_LETTER");

        // The operator finds it on the Job Queue screen and requeues it from there.
        var jobQueueVm = new JobQueueViewModel(_operatorControl, _dialogs, _notifications);
        await jobQueueVm.RefreshAsync();

        jobQueueVm.SelectedState = "DEAD_LETTER";
        await jobQueueVm.LoadJobsAsync();
        jobQueueVm.Jobs.Should().ContainSingle(j => j.Id == job.Id);

        jobQueueVm.SelectedJob = jobQueueVm.Jobs.Single(j => j.Id == job.Id);
        jobQueueVm.CanRequeueSelectedJob.Should().BeTrue();
        await jobQueueVm.RequeueSelectedJobAsync();

        var requeued = await _jobManager.GetJobAsync(job.Id);
        requeued!.State.Should().Be("QUEUED");
        requeued.ProductionId.Should().Be(productionId, "a job must be traceable to the production it serves");

        // Then a worker picks it up again and finishes it.
        var retryLease = await _jobManager.AcquireLeaseAsync(job.Id, "render-worker-2", TimeSpan.FromMinutes(2));
        await _jobManager.CompleteJobOrThrowAsync(job.Id, "render-worker-2", retryLease!.FenceToken);
        (await _jobManager.GetJobAsync(job.Id))!.State.Should().Be("SUCCEEDED"); // job.schema.json's terminal success state (migration 006)

        // ------------------------------------- 7. The protected action needs a human approval (SPEC/09)
        // Policy refuses to let a protected action through on its own.
        _policyEngine.EvaluateAction(productionId, "publication.dispatch")
            .Decision.Should().Be("REQUIRE_APPROVAL");

        var scopeJson = System.Text.Json.JsonSerializer.Serialize(
            new ApprovalScope(Target: "youtube", Subject: "documental-1920", CostCeiling: 50.00m));
        var approvalId = await _approvalManager.CreateApprovalRequestAsync(
            productionId, "publication.dispatch", scopeJson, TimeSpan.FromHours(1));

        // The operator approves it on the Approval Queue screen, not by writing to the table.
        var approvalVm = new ApprovalQueueViewModel(_operatorControl, _dialogs, _notifications);
        await approvalVm.LoadApprovalsAsync();
        approvalVm.Approvals.Should().ContainSingle(a => a.Id == approvalId);

        approvalVm.SelectedApproval = approvalVm.Approvals.Single(a => a.Id == approvalId);
        approvalVm.ApprovalReason = "Claims verified against a primary source";
        await approvalVm.ApproveAsync();

        await approvalVm.LoadApprovalsAsync();
        approvalVm.Approvals.Should().BeEmpty("the approval is no longer pending once decided");

        // The approval is single-use and is consumed atomically by the action it authorises.
        var dispatched = false;
        await _approvalManager.ExecuteWithApprovalAsync(
            productionId, "publication.dispatch", "youtube", "documental-1920", 12.00m,
            protectedAction: () => { dispatched = true; return Task.CompletedTask; });
        dispatched.Should().BeTrue();

        var secondAttempt = async () => await _approvalManager.ExecuteWithApprovalAsync(
            productionId, "publication.dispatch", "youtube", "documental-1920", 12.00m,
            protectedAction: () => Task.CompletedTask);
        (await secondAttempt.Should().ThrowAsync<AmccaException>())
            .Which.ErrorCode.Should().Be(AmccaErrors.Pol004, "a consumed approval cannot authorise a second dispatch");

        // -------------------------------------------------------- 8. Money: cost and revenue (SPEC/20-21)
        await _revenueService.RecordCostAsync(productionId, "SETTLEMENT", 12.00m, "EUR", "omnirouters", job.Id);
        await _revenueService.RecordRevenueAsync(productionId, "CONFIRMED", "OFFICIAL_API", 40.00m, 4.00m, 36.00m, "EUR");

        var profit = await _revenueService.ComputeProfitAsync(productionId);
        profit.ConfirmedRevenue.Should().Be(36.00m);
        profit.SettledCost.Should().Be(12.00m);
        profit.NetProfit.Should().Be(24.00m);

        // ------------------------------- 9. The operator inspects what actually happened (SPEC/60)
        var inspectorVm = new ProductionInspectorViewModel(_productionService, _factory, _notifications);
        await inspectorVm.LoadAvailableProductionsAsync();
        inspectorVm.SelectedProduction = inspectorVm.AvailableProductions.Single(p => p.Id == productionId);
        await inspectorVm.LoadInspectionAsync();

        inspectorVm.ProductionDetail!.State.Should().Be("SCRIPT_VERIFIED");
        inspectorVm.StateTransitions.Should().HaveCount(5);
        inspectorVm.Approvals.Should().ContainSingle(a => a.Id == approvalId);
        inspectorVm.Jobs.Should().ContainSingle(j => j.Id == job.Id);
        inspectorVm.CostEvents.Should().ContainSingle();

        // ------------------------------------------- 10. Safety: the kill switch really stops work (SPEC/53)
        var settingsVm = new SettingsViewModel(_operatorControl, _secretStore, _notifications);
        await settingsVm.LoadSettingsAsync();
        settingsVm.GlobalKillSwitch.Should().BeFalse();

        settingsVm.GlobalKillSwitch = true;
        await settingsVm.SaveSettingsAsync();

        _policyEngine.EvaluateAction(productionId, "publication.dispatch")
            .Decision.Should().Be("BLOCK", "an engaged kill switch blocks protected actions outright");

        // And it survives as persisted state, which is what preflight gate 10 reads on the next start.
        var haltedPreflight = await _preflightService.RunSystemStartupPreflightAsync(_config, _secretStore);
        haltedPreflight.Status.Should().Be(PreflightStatus.Halted);
        haltedPreflight.IsStartupPermitted.Should().BeFalse();

        settingsVm.GlobalKillSwitch = false;
        await settingsVm.SaveSettingsAsync();
        (await _preflightService.RunSystemStartupPreflightAsync(_config, _secretStore))
            .IsStartupPermitted.Should().BeTrue();

        // ------------------------------------------ 11. Every operator action left an audit trail (SPEC/55)
        var trail = await _operatorControl.QueryAuditTrailAsync();
        trail.Should().Contain(a => a.Action == "operator.approval_decided" && a.SubjectId == approvalId);
        trail.Should().Contain(a => a.Action == "operator.job_requeued" && a.SubjectId == job.Id);
        trail.Should().Contain(a => a.Action == "operator.global_kill_switch_toggled");
        trail.Where(a => a.Action.StartsWith("operator."))
            .Should().OnlyContain(a => a.ActorType == "OPERATOR",
                "an operator action is never recorded as anything else (DEF-008)");

        // Nothing in this journey reported a failure to the operator.
        _notifications.Notifications.Should().NotContain(n => n.Type == "Error");
    }

    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task ShowAlertAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> ShowPromptAsync(string title, string message, string defaultValue = "") =>
            Task.FromResult<string?>(defaultValue);
    }
}
