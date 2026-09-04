using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.App.ViewModels;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Operator;
using AMCCA.Core.Policy;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class WpfMvvmContractTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrationService;
    private readonly INotificationService _notificationService;
    private readonly FakeDialogService _dialogService;
    private readonly ISecretStore _secretStore;
    private readonly StateMachineRegistry _stateMachine;
    private readonly IEventStore _eventStore;
    private readonly ProductionService _productionService;
    private readonly IAuditStore _auditStore;
    private readonly BudgetManager _budgetManager;
    private readonly ApprovalManager _approvalManager;
    private readonly PolicyEngine _policyEngine;
    private readonly OperatorControlService _operatorControlService;

    public WpfMvvmContractTests()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && !File.Exists(Path.Combine(dir, "BUILD_ORDER.md")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        _repoRoot = dir ?? throw new InvalidOperationException("Could not locate repo root");

        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_WPF_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "wpf_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
        _migrationService = new MigrationService(_factory, _testDir);
        _migrationService.UpgradeAsync().GetAwaiter().GetResult();

        _notificationService = new NotificationService();
        _dialogService = new FakeDialogService();
        _secretStore = new InMemorySecretStore();

        var stateMachineJson = File.ReadAllText(Path.Combine(_repoRoot, "SCHEMAS", "state-machine.json"));
        _stateMachine = new StateMachineRegistry(stateMachineJson);
        _eventStore = new EventStore(_factory);
        _productionService = new ProductionService(_factory, _stateMachine, _eventStore);

        _auditStore = new AuditStore(_factory);
        _budgetManager = new BudgetManager(_factory);
        _approvalManager = new ApprovalManager(_factory);
        _policyEngine = new PolicyEngine(_factory, _budgetManager, _approvalManager);
        _operatorControlService = new OperatorControlService(_factory, _auditStore, _policyEngine, _approvalManager);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    private NavigationService CreateNavigationService(out DashboardViewModel dash, out ProductionsViewModel prod, out ApprovalQueueViewModel apprv, out SettingsViewModel sett, out AuditLogViewModel audit)
    {
        dash = new DashboardViewModel(_factory, null!);
        prod = new ProductionsViewModel(_productionService, _dialogService, _notificationService);
        apprv = new ApprovalQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        sett = new SettingsViewModel(_operatorControlService, _secretStore, _notificationService);
        audit = new AuditLogViewModel(_factory, _notificationService);

        var d = dash;
        var p = prod;
        var a = apprv;
        var s = sett;
        var au = audit;

        return new NavigationService(type =>
        {
            if (type == typeof(DashboardViewModel)) return d;
            if (type == typeof(ProductionsViewModel)) return p;
            if (type == typeof(ApprovalQueueViewModel)) return a;
            if (type == typeof(SettingsViewModel)) return s;
            if (type == typeof(AuditLogViewModel)) return au;
            throw new ArgumentException();
        });
    }

    [Fact]
    public void MainViewModel_NavigationCommands_SwitchCurrentViewProperly()
    {
        var nav = CreateNavigationService(out var dash, out var prod, out var apprv, out var sett, out var audit);
        var mainVm = new MainViewModel(nav);

        // Initially navigate to dashboard
        mainVm.NavigateDashboardCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<DashboardViewModel>();

        // Navigate to Productions
        mainVm.NavigateProductionsCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<ProductionsViewModel>();

        // Navigate to Approval Queue
        mainVm.NavigateApprovalQueueCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<ApprovalQueueViewModel>();

        // Navigate to Audit Log
        mainVm.NavigateAuditLogCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<AuditLogViewModel>();

        // Navigate to Settings
        mainVm.NavigateSettingsCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<SettingsViewModel>();
    }

    [Fact]
    public async Task DashboardViewModel_AggregatesCounts_FromProductionDatabase()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            // Insert active production
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-d1', 'SCRIPT_GEN', 'Tech Trends', 'en', 'tech', 'COLLABORATIVE', '3.1.0', datetime('now'), datetime('now'));
            ");

            // Insert pending approval
            await conn.ExecuteAsync(@"
                INSERT INTO approvals (id, production_id, action, scope_json, state, expires_at, created_at)
                VALUES ('app-d1', 'prod-d1', 'PUBLISH', '{}', 'PENDING', datetime('now', '+1 day'), datetime('now'));
            ");

            // Insert verified publication
            await conn.ExecuteAsync(@"
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, external_id, evidence_source, evidence_retrieved_at, schema_version, created_at, updated_at)
                VALUES ('pub-d1', 'prod-d1', 'youtube', 'acc-1', 'cv-1', 'VERIFIED', 'idem-d1', 'ext-123', 'OFFICIAL_API', datetime('now'), '3.1.0', datetime('now'), datetime('now'));
            ");
        }

        var nav = CreateNavigationService(out var dash, out _, out _, out _, out _);
        await dash.RefreshAsync();

        dash.ActiveProductionsCount.Should().Be(1);
        dash.PendingApprovalsCount.Should().Be(1);
        dash.VerifiedPublicationsCount.Should().Be(1);
    }

    [Fact]
    public async Task ProductionsViewModel_CreateAndCancelProduction_UpdatesDatabaseAndList()
    {
        var prodVm = new ProductionsViewModel(_productionService, _dialogService, _notificationService);

        prodVm.NewTopic = "Autonomous Video Automation";
        prodVm.NewNiche = "tech";
        await prodVm.CreateProductionAsync();

        prodVm.Productions.Should().Contain(p => p.Topic == "Autonomous Video Automation" && p.State == "INIT");
        _notificationService.Notifications.Should().Contain(n => n.Type == "Success");

        // Now cancel it
        var created = prodVm.Productions.First(p => p.Topic == "Autonomous Video Automation");
        prodVm.SelectedProduction = created;
        await prodVm.CancelProductionAsync();

        prodVm.Productions.Should().Contain(p => p.Id == created.Id && p.State == "CANCELLED");
        _notificationService.Notifications.Should().Contain(n => n.Type == "Info");
    }

    [Fact]
    public async Task ApprovalQueueViewModel_ApproveAndReject_UpdatesApprovalState()
    {
        // Seed 2 pending approvals
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-app-1', 'APPROVAL_PENDING', 'Topic 1', 'en', 'tech', 'COLLABORATIVE', '3.1.0', datetime('now'), datetime('now'));
            ");

            await conn.ExecuteAsync(@"
                INSERT INTO approvals (id, production_id, action, scope_json, state, expires_at, created_at)
                VALUES ('app-1', 'prod-app-1', 'PUBLISH', '{}', 'PENDING', datetime('now', '+1 day'), datetime('now')),
                       ('app-2', 'prod-app-1', 'COST_OVERRUN', '{}', 'PENDING', datetime('now', '+1 day'), datetime('now'));
            ");
        }

        var queueVm = new ApprovalQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        await queueVm.LoadApprovalsAsync();
        queueVm.Approvals.Should().HaveCount(2);

        // Approve first
        queueVm.SelectedApproval = queueVm.Approvals.First(a => a.Id == "app-1");
        queueVm.ApprovalReason = "Looks compliant with SPEC/45";
        await queueVm.ApproveAsync();

        // Reject second
        queueVm.SelectedApproval = queueVm.Approvals.First(a => a.Id == "app-2");
        queueVm.ApprovalReason = "Budget exceeded threshold";
        await queueVm.RejectAsync();

        // Queue should now be empty of pending approvals
        queueVm.Approvals.Should().BeEmpty();

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            var app1State = await conn.ExecuteScalarAsync<string>("SELECT state FROM approvals WHERE id = 'app-1'");
            var app2State = await conn.ExecuteScalarAsync<string>("SELECT state FROM approvals WHERE id = 'app-2'");
            app1State.Should().Be("APPROVED");
            app2State.Should().Be("REJECTED");
        }
    }

    [Fact]
    public async Task SettingsViewModel_TogglesKillSwitch_PersistsToDatabase()
    {
        var settingsVm = new SettingsViewModel(_operatorControlService, _secretStore, _notificationService);
        await settingsVm.LoadSettingsAsync();

        settingsVm.GlobalKillSwitch = true;
        await settingsVm.SaveSettingsAsync();

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            var mode = await conn.ExecuteScalarAsync<string>("SELECT mode FROM kill_switch_state WHERE id = 1");
            mode.Should().Be("EMERGENCY_STOP");
        }

        _notificationService.Notifications.Should().Contain(n => n.Type == "Success");

        // Reloading a fresh view model must reflect the persisted state, not an in-memory default.
        var reloadedVm = new SettingsViewModel(_operatorControlService, _secretStore, _notificationService);
        await reloadedVm.LoadSettingsAsync();
        reloadedVm.GlobalKillSwitch.Should().BeTrue();
    }

    [Fact]
    public async Task ProductionInspectorViewModel_LoadsFullAggregateForSelectedProduction()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var prod = await _productionService.CreateProductionAsync("Inspector Target", "en", "COLLABORATIVE", correlationId, nicheId: "tech");
        await _productionService.TransitionAsync(prod.Id, "RESEARCHING", "ORCHESTRATOR", correlationId);

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO artifacts (id, production_id, kind, current_version_id, created_at, updated_at)
                VALUES ('art-1', @ProductionId, 'SCRIPT', 'artv-1', datetime('now'), datetime('now'));
            ", new { ProductionId = prod.Id });

            await conn.ExecuteAsync(@"
                INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
                VALUES ('artv-1', 'art-1', 1, @Sha256, 10, 'script.txt', 'CURRENT', datetime('now'));
            ", new { Sha256 = new string('a', 64) });

            await conn.ExecuteAsync(@"
                INSERT INTO qa_reports (report_id, production_id, artifact_version_id, stage, overall_score, critical_scores_json, verdict, threshold_profile_id, schema_version, evaluated_at)
                VALUES ('qa-1', @ProductionId, 'artv-1', 'SCRIPT_QA', 0.95, '{}', 'PASS', 'default', '3.1.0', datetime('now'));
            ", new { ProductionId = prod.Id });

            await conn.ExecuteAsync(@"
                INSERT INTO approvals (id, production_id, action, scope_json, state, expires_at, created_at)
                VALUES ('appr-1', @ProductionId, 'PUBLISH', '{}', 'PENDING', datetime('now', '+1 day'), datetime('now'));
            ", new { ProductionId = prod.Id });

            await conn.ExecuteAsync(@"
                INSERT INTO jobs (id, production_id, type, state, priority, attempt, max_attempts, payload_json, created_at, updated_at)
                VALUES ('job-1', @ProductionId, 'RENDER', 'RUNNING', 3, 1, 3, '{}', datetime('now'), datetime('now'));
            ", new { ProductionId = prod.Id });

            await conn.ExecuteAsync(@"
                INSERT INTO cost_events (id, production_id, kind, amount, currency, provider, occurred_at, created_at)
                VALUES ('cost-1', @ProductionId, 'RESERVATION', '1.500000', 'EUR', 'omnirouters', datetime('now'), datetime('now'));
            ", new { ProductionId = prod.Id });

            await conn.ExecuteAsync(@"
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, schema_version, created_at, updated_at)
                VALUES ('pub-insp-1', @ProductionId, 'youtube', 'acc-1', 'artv-1', 'QUEUED', 'idem-insp-1', '3.1.0', datetime('now'), datetime('now'));
            ", new { ProductionId = prod.Id });
        }

        var inspectorVm = new ProductionInspectorViewModel(_productionService, _factory, _notificationService);
        await inspectorVm.LoadAvailableProductionsAsync();
        inspectorVm.AvailableProductions.Should().Contain(p => p.Id == prod.Id);

        inspectorVm.SelectedProduction = inspectorVm.AvailableProductions.First(p => p.Id == prod.Id);
        await inspectorVm.LoadInspectionAsync();

        inspectorVm.ProductionDetail.Should().NotBeNull();
        inspectorVm.ProductionDetail!.State.Should().Be("RESEARCHING");
        inspectorVm.StateTransitions.Should().Contain(t => t.ToState == "RESEARCHING");
        inspectorVm.Artifacts.Should().Contain(a => a.Id == "art-1");
        inspectorVm.ArtifactVersions.Should().Contain(v => v.Id == "artv-1");
        inspectorVm.QaReports.Should().Contain(q => q.ReportId == "qa-1" && q.Verdict == "PASS");
        inspectorVm.Approvals.Should().Contain(a => a.Id == "appr-1");
        inspectorVm.Jobs.Should().Contain(j => j.Id == "job-1");
        inspectorVm.CostEvents.Should().Contain(c => c.Id == "cost-1");
        inspectorVm.Publications.Should().Contain(p => p.Id == "pub-insp-1");
    }

    [Fact]
    public async Task AuditLogViewModel_LoadsAndFiltersEntries()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
                VALUES ('aud-wpf-1', 'POLICY_CHECK_PASSED', 'SYSTEM', 'policy_engine', 'PRODUCTION', 'prod-100', 'SUCCESS', 'POL_OK', 'corr-1', '3.1.0', datetime('now')),
                       ('aud-wpf-2', 'KILL_SWITCH_ENGAGED', 'OPERATOR', 'operator_admin', 'GLOBAL', 'kill_switch', 'HALTED', 'KILL_ALL', 'corr-2', '3.1.0', datetime('now'));
            ");
        }

        var auditVm = new AuditLogViewModel(_factory, _notificationService);
        await auditVm.LoadAuditLogAsync();
        auditVm.Entries.Should().HaveCount(2);

        // Filter by KILL_SWITCH
        auditVm.FilterQuery = "KILL_SWITCH";
        await auditVm.LoadAuditLogAsync();
        auditVm.Entries.Should().HaveCount(1);
        auditVm.Entries.Single().Action.Should().Be("KILL_SWITCH_ENGAGED");
    }

    private class FakeDialogService : IDialogService
    {
        public Task ShowAlertAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<string?> ShowPromptAsync(string title, string message, string defaultValue = "") => Task.FromResult<string?>(defaultValue);
    }
}
