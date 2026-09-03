using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.App.ViewModels;
using AMCCA.Core.Database;
using AMCCA.Core.Security;
using Dapper;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class WpfMvvmContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly DatabaseConnectionFactory _factory;
    private readonly MigrationService _migrationService;
    private readonly INotificationService _notificationService;
    private readonly FakeDialogService _dialogService;
    private readonly ISecretStore _secretStore;

    public WpfMvvmContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_WPF_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "wpf_test.db");
        _factory = new DatabaseConnectionFactory(_dbPath);
        _migrationService = new MigrationService(_factory, _testDir);
        _migrationService.UpgradeAsync().GetAwaiter().GetResult();

        _notificationService = new NotificationService();
        _dialogService = new FakeDialogService();
        _secretStore = new InMemorySecretStore();
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
        prod = new ProductionsViewModel(_factory, _dialogService, _notificationService, null);
        apprv = new ApprovalQueueViewModel(_factory, _dialogService, _notificationService);
        sett = new SettingsViewModel(_factory, _secretStore, _notificationService);
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
        var prodVm = new ProductionsViewModel(_factory, _dialogService, _notificationService, null);

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

        var queueVm = new ApprovalQueueViewModel(_factory, _dialogService, _notificationService);
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
        var settingsVm = new SettingsViewModel(_factory, _secretStore, _notificationService);
        await settingsVm.LoadSettingsAsync();

        settingsVm.GlobalKillSwitch = true;
        await settingsVm.SaveSettingsAsync();

        using (var conn = await _factory.CreateOpenConnectionAsync())
        {
            var json = await conn.ExecuteScalarAsync<string>("SELECT value_json FROM settings WHERE key = 'kill_switch.global'");
            json.Should().Contain("\"active\":true");
        }

        _notificationService.Notifications.Should().Contain(n => n.Type == "Success");
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
