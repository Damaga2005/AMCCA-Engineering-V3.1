using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly JobManager _jobManager;
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
        _jobManager = new JobManager(_factory);
        _operatorControlService = new OperatorControlService(_factory, _auditStore, _policyEngine, _approvalManager, _jobManager);
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
        dash = new DashboardViewModel(_factory, _operatorControlService, null!);
        prod = new ProductionsViewModel(_productionService, _dialogService, _notificationService);
        apprv = new ApprovalQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        sett = new SettingsViewModel(_operatorControlService, _secretStore, _notificationService);
        audit = new AuditLogViewModel(_factory, _notificationService);

        var d = dash;
        var p = prod;
        var a = apprv;
        var s = sett;
        var au = audit;
        var inspector = new ProductionInspectorViewModel(_productionService, _factory, _notificationService);
        var jobQueue = new JobQueueViewModel(_operatorControlService, _dialogService, _notificationService);

        return new NavigationService(type =>
        {
            if (type == typeof(DashboardViewModel)) return d;
            if (type == typeof(ProductionsViewModel)) return p;
            if (type == typeof(ProductionInspectorViewModel)) return inspector;
            if (type == typeof(JobQueueViewModel)) return jobQueue;
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
        var config = new AmccaConfig { PublishingEnabled = false };
        var mainVm = new MainViewModel(nav, _operatorControlService, config, _dialogService, _notificationService);

        // Initially navigate to dashboard
        mainVm.NavigateDashboardCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<DashboardViewModel>();

        // Navigate to Productions
        mainVm.NavigateProductionsCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<ProductionsViewModel>();

        // Navigate to Production Inspector (SPEC/60)
        mainVm.NavigateProductionInspectorCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<ProductionInspectorViewModel>();

        // Navigate to Job Queue (SPEC/14)
        mainVm.NavigateJobQueueCommand.Execute(null);
        mainVm.CurrentView.Should().BeOfType<JobQueueViewModel>();

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

    /// <summary>
    /// SPEC/60 obligations 1-2: the kill switch is reachable in one action from every screen, and
    /// autonomy mode / publishing state are visible on every screen. MainViewModel hosts the sidebar
    /// chrome shared by all six screens, so this is where those obligations are actually satisfied --
    /// this test exercises the toggle end to end (through OperatorControlService, so it persists and
    /// is audited exactly like the Settings screen's own toggle) rather than just the presentation hint.
    /// </summary>
    [Fact]
    public async Task MainViewModel_TogglesGlobalKillSwitch_PersistsAndSurfacesAutonomyAndPublishingState()
    {
        var nav = CreateNavigationService(out _, out _, out _, out _, out _);
        var config = new AmccaConfig { PublishingEnabled = true };
        var mainVm = new MainViewModel(nav, _operatorControlService, config, _dialogService, _notificationService);

        // RefreshStatusAsync from the constructor is fire-and-forget; await one explicitly before asserting.
        await mainVm.RefreshStatusAsync();
        mainVm.IsKillSwitchActive.Should().BeFalse();
        mainVm.PublishingEnabled.Should().BeTrue();
        mainVm.AutonomyMode.Should().NotBeNullOrWhiteSpace();

        await mainVm.ToggleKillSwitchAsync();
        mainVm.IsKillSwitchActive.Should().BeTrue("the toggle re-asks the current state and flips it");

        // The write went through OperatorControlService, so it is the same persisted state Settings and
        // SPEC/49 preflight gate 10 read -- not a value local to this one view model.
        var status = await _operatorControlService.GetSystemStatusAsync();
        status.GlobalKillSwitchActive.Should().BeTrue();
        var auditTrail = await _operatorControlService.QueryAuditTrailAsync(action: "operator.global_kill_switch_toggled");
        auditTrail.Should().Contain(a => a.ActorType == "OPERATOR");

        // A refresh must re-ask rather than trust a cached value (SPEC/62) -- it has to reflect a change
        // made by a completely different actor since the last read, not just its own prior toggle.
        await _operatorControlService.ToggleGlobalKillSwitchAsync("someone-else", active: false, "cleared elsewhere", Guid.NewGuid().ToString("N"));
        await mainVm.RefreshStatusAsync();
        mainVm.IsKillSwitchActive.Should().BeFalse("a refresh must re-read state changed elsewhere, not trust what it last saw");
    }

    [Fact]
    public async Task DashboardViewModel_AggregatesCounts_FromProductionDatabase()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            // Insert active production
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-d1', 'SCRIPTING', 'Tech Trends', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
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

            // A production in a terminal state is not active (SPEC/13). ARCHIVED used to be counted here
            // because the filter excluded 'PUBLISHED', which is not a state in the canonical machine.
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-d2', 'ARCHIVED', 'Done and filed', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
            ");
        }

        var nav = CreateNavigationService(out var dash, out _, out _, out _, out _);
        await dash.RefreshAsync();

        dash.ActiveProductionsCount.Should().Be(1, "the ARCHIVED production is terminal and must not count as active");
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
                VALUES ('prod-app-1', 'BLOCKED', 'Topic 1', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
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

    /// <summary>
    /// SPEC/60 obligation 5: "Every approval request shows the exact action, subject, cost ceiling and
    /// expiry being approved." Before this, the queue showed only id/production/action/state/created --
    /// an operator approved a scoped, cost-ceilinged, time-bounded request without ever seeing the scope,
    /// ceiling or expiry that made it safe to approve in the first place.
    /// </summary>
    [Fact]
    public async Task ApprovalQueueViewModel_ExposesScopeSubjectCostCeilingAndExpiry()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO productions (id, state, title, language, niche_id, autonomy_mode, schema_version, created_at, updated_at)
                VALUES ('prod-scope-1', 'BLOCKED', 'Topic', 'en', 'tech', 'ASSISTED', '3.1.0', datetime('now'), datetime('now'));
            ");

            var scopeJson = System.Text.Json.JsonSerializer.Serialize(
                new AMCCA.Core.Policy.ApprovalScope(Target: "youtube", Subject: "documental-1920", CostCeiling: 42.50m));

            await conn.ExecuteAsync(@"
                INSERT INTO approvals (id, production_id, action, scope_json, state, expires_at, created_at)
                VALUES ('app-scoped-1', 'prod-scope-1', 'PUBLISH', @ScopeJson, 'PENDING', '2026-12-31T00:00:00Z', datetime('now'));
            ", new { ScopeJson = scopeJson });

            // A legacy/scope-less approval must still surface in the queue -- just with an explicit
            // "no scope recorded" rather than a silently blank cell.
            await conn.ExecuteAsync(@"
                INSERT INTO approvals (id, production_id, action, scope_json, state, expires_at, created_at)
                VALUES ('app-noscope-1', 'prod-scope-1', 'PUBLISH', '{}', 'PENDING', '2026-12-31T00:00:00Z', datetime('now'));
            ");
        }

        var queueVm = new ApprovalQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        await queueVm.LoadApprovalsAsync();

        var scoped = queueVm.Approvals.Should().ContainSingle(a => a.Id == "app-scoped-1").Subject;
        scoped.Subject.Should().Be("documental-1920");
        scoped.CostCeiling.Should().Be(42.50m);
        scoped.ExpiresAt.Should().Be("2026-12-31T00:00:00Z");
        scoped.SubjectDisplay.Should().Be("documental-1920");
        scoped.CostCeilingDisplay.Should().Be("42.50");

        var noScope = queueVm.Approvals.Should().ContainSingle(a => a.Id == "app-noscope-1").Subject;
        noScope.Subject.Should().BeNull();
        noScope.CostCeiling.Should().BeNull();
        noScope.SubjectDisplay.Should().Be("(no scope recorded)");
        noScope.CostCeilingDisplay.Should().Be("(no scope recorded)");
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
        var prod = await _productionService.CreateProductionAsync("Inspector Target", "en", "ASSISTED", correlationId, nicheId: "tech");
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
                VALUES ('qa-1', @ProductionId, 'artv-1', 'CONTENT_QA', 0.95, '{}', 'PASS', 'default', '3.1.0', datetime('now'));
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
                INSERT INTO publications (id, production_id, platform, account_id, content_version_id, state, idempotency_key, external_id, evidence_source, evidence_retrieved_at, schema_version, created_at, updated_at)
                VALUES ('pub-insp-1', @ProductionId, 'youtube', 'acc-1', 'artv-1', 'VERIFIED', 'idem-insp-1', 'yt-video-1', 'OFFICIAL_API', datetime('now'), '3.1.0', datetime('now'), datetime('now'));
            ", new { ProductionId = prod.Id });

            // SPEC/60: claims with sources and retrieval timestamps.
            await conn.ExecuteAsync(@"
                INSERT INTO sources (id, url, publisher, retrieved_at, content_hash, trust_tier, robots_allowed, created_at)
                VALUES ('src-1', 'https://example.com/article', 'Example Wire', datetime('now'), 'hash-1', 'SECONDARY', 1, datetime('now'));
                INSERT INTO claims (id, production_id, text, status, materiality, subject_class, schema_version, created_at)
                VALUES ('claim-1', @ProductionId, 'Example claim under review', 'VERIFIED', 'MATERIAL', 'GENERAL', '3.1.0', datetime('now'));
                INSERT INTO claim_sources (claim_id, source_id, relation)
                VALUES ('claim-1', 'src-1', 'SUPPORTS');
            ", new { ProductionId = prod.Id });

            // SPEC/60: every policy decision.
            await conn.ExecuteAsync(@"
                INSERT INTO policy_decisions (id, production_id, action, decision, rule_key, policy_version_id, inputs_hash, correlation_id, decided_at)
                VALUES ('pd-1', @ProductionId, 'PUBLISH', 'REQUIRE_APPROVAL', 'publish_requires_approval', 'polver-1', 'inhash-1', 'corr-pd-1', datetime('now'));
            ", new { ProductionId = prod.Id });

            // SPEC/60: every QA finding with its responsible node.
            await conn.ExecuteAsync(@"
                INSERT INTO qa_findings (id, report_id, check_id, check_kind, status, severity, responsible_artifact_version_id, remediation_code, message)
                VALUES ('finding-1', 'qa-1', 'audio_levels', 'DETERMINISTIC', 'FAIL', 'HIGH', 'artv-1', 'REMEDIATE_AUDIO', 'Audio peaks exceed threshold');
            ");

            // SPEC/60: the artifact DAG's edges, not just its nodes.
            await conn.ExecuteAsync(@"
                INSERT INTO artifact_versions (id, artifact_id, version_no, sha256, bytes, rel_path, state, created_at)
                VALUES ('artv-2', 'art-1', 2, @Sha256, 20, 'script_v2.txt', 'CURRENT', datetime('now'));
                INSERT INTO artifact_edges (parent_version_id, child_version_id, edge_kind, created_at)
                VALUES ('artv-1', 'artv-2', 'DERIVED_FROM', datetime('now'));
            ", new { Sha256 = new string('b', 64) });
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

        var publication = inspectorVm.Publications.Should().ContainSingle(p => p.Id == "pub-insp-1").Subject;
        publication.EvidenceSource.Should().Be("OFFICIAL_API", "SPEC/60 requires every publication to show its evidence, not just its URL");
        publication.EvidenceRetrievedAt.Should().NotBeNullOrEmpty();

        var claim = inspectorVm.Claims.Should().ContainSingle(c => c.ClaimId == "claim-1").Subject;
        claim.SourceUrl.Should().Be("https://example.com/article");
        claim.Publisher.Should().Be("Example Wire");
        claim.Relation.Should().Be("SUPPORTS");
        claim.RetrievedAt.Should().NotBeNullOrEmpty();

        inspectorVm.PolicyDecisions.Should().ContainSingle(pd => pd.Id == "pd-1" && pd.Decision == "REQUIRE_APPROVAL");

        var finding = inspectorVm.QaFindings.Should().ContainSingle(f => f.Id == "finding-1").Subject;
        finding.Stage.Should().Be("CONTENT_QA", "a QA finding must be traceable to the report/stage that raised it");
        finding.ResponsibleArtifactVersionId.Should().Be("artv-1");

        inspectorVm.ArtifactEdges.Should().ContainSingle(e => e.ParentVersionId == "artv-1" && e.ChildVersionId == "artv-2" && e.EdgeKind == "DERIVED_FROM");

        var costEvent = inspectorVm.CostEvents.Should().ContainSingle(c => c.Id == "cost-1").Subject;
        costEvent.ReconciliationState.Should().Be("ESTIMATED", "SPEC/60 obligation 3: a cost figure's provenance must be visible, not just its amount");

        // SPEC/60 obligation 4 is scoped to a *blocked* production; RESEARCHING is not one.
        inspectorVm.HasBlockInfo.Should().BeFalse();
        inspectorVm.BlockInfo.Should().BeNull();
    }

    /// <summary>
    /// SPEC/60 obligation 4: "Every blocked element indicates which rule blocked it, which policy
    /// version, and what would unblock it." The rule/policy half comes from whatever audit_log row
    /// recorded the block (subject_id = the production); the unblock half is not a guess -- it is the
    /// literal state StateMachineRegistry enforces resuming is only ever legal back to (AMCCA-STM-002).
    /// </summary>
    [Fact]
    public async Task ProductionInspectorViewModel_BlockedProduction_ShowsRuleAndUnblockPath()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var prod = await _productionService.CreateProductionAsync("Blocked Target", "en", "ASSISTED", correlationId, nicheId: "tech");
        await _productionService.TransitionAsync(prod.Id, "BLOCKED", "ORCHESTRATOR", correlationId);

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
                VALUES ('aud-block-1', 'production.blocked', 'ORCHESTRATOR', 'orchestrator', 'PRODUCTION', @ProductionId, 'BLOCKED', 'AMCCA-BUD-002', @CorrId, '3.1.0', datetime('now'));
            ", new { ProductionId = prod.Id, CorrId = correlationId });
        }

        var inspectorVm = new ProductionInspectorViewModel(_productionService, _factory, _notificationService);
        await inspectorVm.LoadAvailableProductionsAsync();
        inspectorVm.SelectedProduction = inspectorVm.AvailableProductions.First(p => p.Id == prod.Id);
        await inspectorVm.LoadInspectionAsync();

        inspectorVm.ProductionDetail!.State.Should().Be("BLOCKED");
        inspectorVm.HasBlockInfo.Should().BeTrue();
        var blockInfo = inspectorVm.BlockInfo!;
        blockInfo.ReasonCode.Should().Be("AMCCA-BUD-002");
        blockInfo.PolicyDecisionId.Should().BeNull("nothing in this codebase writes policy_decisions today -- disclosed, not fabricated");
        blockInfo.ResumesTo.Should().Be("INIT", "AMCCA-STM-002: resuming from BLOCKED is only ever legal back to blocked_from");
        blockInfo.UnblockHint.Should().Contain("INIT");
    }

    private async Task<string> CreateDeadLetteredJobAsync(string idempotencyKey)
    {
        var job = await _jobManager.EnqueueJobAsync(
            type: "RENDER",
            idempotencyKey: idempotencyKey,
            correlationId: "corr-vm-dl",
            payloadJson: "{}",
            priority: 3,
            maxAttempts: 1);

        var lease = await _jobManager.AcquireLeaseAsync(job.Id, "worker-vm", TimeSpan.FromMinutes(5));
        await _jobManager.FailJobAsync(job.Id, "worker-vm", lease!.FenceToken, "render failed");

        return job.Id;
    }

    [Fact]
    public async Task JobQueueViewModel_PagesJobsAndFiltersByState()
    {
        for (int i = 0; i < 3; i++)
        {
            await _jobManager.EnqueueJobAsync("RENDER", $"idem-vm-{i}", "corr-vm", "{}");
        }
        var deadLetteredId = await CreateDeadLetteredJobAsync("idem-vm-dl");

        var queueVm = new JobQueueViewModel(_operatorControlService, _dialogService, _notificationService)
        {
            PageSize = 2
        };
        await queueVm.RefreshAsync();

        queueVm.TotalCount.Should().Be(4);
        queueVm.TotalPages.Should().Be(2);
        queueVm.Jobs.Should().HaveCount(2);
        queueVm.CanGoToPreviousPage.Should().BeFalse();
        queueVm.CanGoToNextPage.Should().BeTrue();

        await queueVm.GoToNextPageAsync();
        queueVm.PageIndex.Should().Be(1);
        queueVm.Jobs.Should().HaveCount(2);
        queueVm.CanGoToNextPage.Should().BeFalse();

        queueVm.AvailableStates.Should().Contain(JobQueueViewModel.AllStatesLabel).And.Contain("DEAD_LETTER");

        queueVm.SelectedState = "DEAD_LETTER";
        await queueVm.LoadJobsAsync();

        queueVm.TotalCount.Should().Be(1);
        queueVm.Jobs.Should().ContainSingle(j => j.Id == deadLetteredId);
        queueVm.PageIndex.Should().Be(0, "changing the filter returns to the first page");
    }

    [Fact]
    public async Task JobQueueViewModel_RequeuesDeadLetteredJobThroughOperatorControlService()
    {
        var deadLetteredId = await CreateDeadLetteredJobAsync("idem-vm-requeue");

        var queueVm = new JobQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        await queueVm.RefreshAsync();

        queueVm.SelectedJob = queueVm.Jobs.First(j => j.Id == deadLetteredId);
        queueVm.CanRequeueSelectedJob.Should().BeTrue();

        await queueVm.RequeueSelectedJobAsync();

        var requeued = await _jobManager.GetJobAsync(deadLetteredId);
        requeued!.State.Should().Be("QUEUED");
        _notificationService.Notifications.Should().Contain(n => n.Type == "Success");

        // The mutation must have gone through the domain, so it carries an audit record (DEF-001/DEF-002).
        var audit = await _operatorControlService.QueryAuditTrailAsync(action: "operator.job_requeued");
        audit.Should().ContainSingle(a => a.SubjectId == deadLetteredId);
    }

    [Fact]
    public async Task JobQueueViewModel_RequeueOfNonDeadLetteredJob_SurfacesErrorCodeAndDoesNotMutate()
    {
        var job = await _jobManager.EnqueueJobAsync("RENDER", "idem-vm-queued", "corr-vm", "{}");

        var queueVm = new JobQueueViewModel(_operatorControlService, _dialogService, _notificationService);
        await queueVm.RefreshAsync();

        // Simulates SPEC/62's "never cache a decision": the row was QUEUED all along, and the domain
        // refuses at the moment of the action rather than the UI assuming it may proceed.
        queueVm.SelectedJob = queueVm.Jobs.First(j => j.Id == job.Id);
        await queueVm.RequeueSelectedJobAsync();

        var unchanged = await _jobManager.GetJobAsync(job.Id);
        unchanged!.State.Should().Be("QUEUED");

        _notificationService.Notifications.Should().Contain(
            n => n.Type == "Error" && n.Message.Contains(AmccaErrors.Job003));
        _notificationService.Notifications.Should().NotContain(n => n.Type == "Success");
    }

    [Fact]
    public async Task AuditLogViewModel_LoadsAndFiltersEntries()
    {
        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (audit_id, action, actor_type, actor_id, subject_type, subject_id, outcome, reason_code, correlation_id, schema_version, occurred_at)
                VALUES ('aud-wpf-1', 'POLICY_CHECK_PASSED', 'SYSTEM', 'policy_engine', 'PRODUCTION', 'prod-100', 'ALLOWED', 'POL_OK', 'corr-1', '3.1.0', datetime('now')),
                       ('aud-wpf-2', 'KILL_SWITCH_ENGAGED', 'OPERATOR', 'operator_admin', 'GLOBAL', 'kill_switch', 'BLOCKED', 'KILL_ALL', 'corr-2', '3.1.0', datetime('now'));
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
