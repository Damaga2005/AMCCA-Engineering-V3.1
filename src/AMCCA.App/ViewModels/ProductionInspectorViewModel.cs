using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AMCCA.App.Common;
using AMCCA.App.Services;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using Dapper;

namespace AMCCA.App.ViewModels;

public record InspectorProductionSummary(string Id, string Title, string State);

public record InspectorTransitionItem(string TransitionId, string FromState, string ToState, string ActorType, string OccurredAt);

public record InspectorArtifactItem(string Id, string Kind, string CurrentVersionId, string CreatedAt);

public record InspectorArtifactVersionItem(string Id, string ArtifactId, long VersionNo, string Sha256, string State, string CreatedAt);

public record InspectorQaReportItem(string ReportId, string Stage, double OverallScore, string Verdict, string EvaluatedAt);

public record InspectorApprovalItem(string Id, string Action, string State, string ExpiresAt, string CreatedAt);

public record InspectorJobItem(string Id, string Type, string State, long Attempt, long MaxAttempts, string UpdatedAt);

public record InspectorCostEventItem(string Id, string Kind, string Amount, string Currency, string OccurredAt);

public record InspectorPublicationItem(string Id, string Platform, string State, string? ExternalUrl, string UpdatedAt);

/// <summary>
/// SPEC/61 Production Inspector: a read-only aggregate view across production state, versions,
/// artifacts, QA, approvals, jobs, costs and publications for one production. Reads only -- it never
/// mutates, so unlike the DEF-001 fix in Productions/Settings there is no domain service to bypass;
/// this follows the same read-only-SQL precedent already used by Dashboard and Audit Log.
/// </summary>
public class ProductionInspectorViewModel : ViewModelBase
{
    private readonly ProductionService _productionService;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly INotificationService _notificationService;

    private InspectorProductionSummary? _selectedProduction;
    private Production? _production;
    private bool _isLoading;

    // Selecting a production starts a load, and so do the constructor and Refresh. Each load takes a
    // token and only applies its results if no newer load has started since, so switching production
    // twice in quick succession cannot leave the tabs showing a mixture of both.
    private int _loadRequestToken;

    public ObservableCollection<InspectorProductionSummary> AvailableProductions { get; } = new();
    public ObservableCollection<InspectorTransitionItem> StateTransitions { get; } = new();
    public ObservableCollection<InspectorArtifactItem> Artifacts { get; } = new();
    public ObservableCollection<InspectorArtifactVersionItem> ArtifactVersions { get; } = new();
    public ObservableCollection<InspectorQaReportItem> QaReports { get; } = new();
    public ObservableCollection<InspectorApprovalItem> Approvals { get; } = new();
    public ObservableCollection<InspectorJobItem> Jobs { get; } = new();
    public ObservableCollection<InspectorCostEventItem> CostEvents { get; } = new();
    public ObservableCollection<InspectorPublicationItem> Publications { get; } = new();

    public InspectorProductionSummary? SelectedProduction
    {
        get => _selectedProduction;
        set
        {
            if (SetProperty(ref _selectedProduction, value))
            {
                _ = LoadInspectionAsync();
            }
        }
    }

    public Production? ProductionDetail
    {
        get => _production;
        private set => SetProperty(ref _production, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public ICommand RefreshCommand { get; }

    public ProductionInspectorViewModel(
        ProductionService productionService,
        DatabaseConnectionFactory connectionFactory,
        INotificationService notificationService)
    {
        _productionService = productionService;
        _connectionFactory = connectionFactory;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadAvailableProductionsAsync);

        _ = LoadAvailableProductionsAsync();
    }

    public async Task LoadAvailableProductionsAsync()
    {
        try
        {
            var previouslySelectedId = SelectedProduction?.Id;
            AvailableProductions.Clear();

            var recent = await _productionService.ListRecentAsync(50);
            foreach (var p in recent)
            {
                AvailableProductions.Add(new InspectorProductionSummary(p.Id, p.Title ?? p.Id, p.State));
            }

            if (previouslySelectedId != null)
            {
                var match = AvailableProductions.FirstOrDefault(p => p.Id == previouslySelectedId);
                if (match != null)
                {
                    SelectedProduction = match;
                    return;
                }
            }

            if (AvailableProductions.Count > 0)
            {
                SelectedProduction = AvailableProductions[0];
            }
            else
            {
                await ClearInspectionAsync();
            }
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load productions for inspection: {ex.Message}", "Error");
        }
    }

    private Task ClearInspectionAsync()
    {
        ProductionDetail = null;
        StateTransitions.Clear();
        Artifacts.Clear();
        ArtifactVersions.Clear();
        QaReports.Clear();
        Approvals.Clear();
        Jobs.Clear();
        CostEvents.Clear();
        Publications.Clear();
        return Task.CompletedTask;
    }

    public async Task LoadInspectionAsync()
    {
        if (SelectedProduction == null)
        {
            await ClearInspectionAsync();
            return;
        }

        var token = ++_loadRequestToken;
        IsLoading = true;
        var productionId = SelectedProduction.Id;
        try
        {
            // Everything is fetched first and applied in one guarded block, so a load that is superseded
            // mid-flight can never leave the tabs holding a mixture of two productions.
            var detail = await _productionService.GetProductionAsync(productionId);
            var transitions = await _productionService.GetStateTransitionsAsync(productionId);

            using var conn = await _connectionFactory.CreateOpenConnectionAsync();
            var parameters = new { ProductionId = productionId };

            var artifacts = await conn.QueryAsync<InspectorArtifactItem>(
                "SELECT id AS Id, kind AS Kind, current_version_id AS CurrentVersionId, created_at AS CreatedAt FROM artifacts WHERE production_id = @ProductionId ORDER BY created_at ASC;",
                parameters);

            var versions = await conn.QueryAsync<InspectorArtifactVersionItem>(@"
                SELECT av.id AS Id, av.artifact_id AS ArtifactId, av.version_no AS VersionNo, av.sha256 AS Sha256, av.state AS State, av.created_at AS CreatedAt
                FROM artifact_versions av
                INNER JOIN artifacts a ON a.id = av.artifact_id
                WHERE a.production_id = @ProductionId
                ORDER BY av.created_at ASC;",
                parameters);

            var qaReports = await conn.QueryAsync<InspectorQaReportItem>(
                "SELECT report_id AS ReportId, stage AS Stage, overall_score AS OverallScore, verdict AS Verdict, evaluated_at AS EvaluatedAt FROM qa_reports WHERE production_id = @ProductionId ORDER BY evaluated_at ASC;",
                parameters);

            var approvals = await conn.QueryAsync<InspectorApprovalItem>(
                "SELECT id AS Id, action AS Action, state AS State, expires_at AS ExpiresAt, created_at AS CreatedAt FROM approvals WHERE production_id = @ProductionId ORDER BY created_at ASC;",
                parameters);

            var jobs = await conn.QueryAsync<InspectorJobItem>(
                "SELECT id AS Id, type AS Type, state AS State, attempt AS Attempt, max_attempts AS MaxAttempts, updated_at AS UpdatedAt FROM jobs WHERE production_id = @ProductionId ORDER BY updated_at DESC;",
                parameters);

            var costs = await conn.QueryAsync<InspectorCostEventItem>(
                "SELECT id AS Id, kind AS Kind, amount AS Amount, currency AS Currency, occurred_at AS OccurredAt FROM cost_events WHERE production_id = @ProductionId ORDER BY occurred_at ASC;",
                parameters);

            var publications = await conn.QueryAsync<InspectorPublicationItem>(
                "SELECT id AS Id, platform AS Platform, state AS State, external_url AS ExternalUrl, updated_at AS UpdatedAt FROM publications WHERE production_id = @ProductionId ORDER BY updated_at DESC;",
                parameters);

            if (token != _loadRequestToken)
            {
                return; // a newer load has started; its results are the ones that count
            }

            ProductionDetail = detail;

            StateTransitions.Clear();
            foreach (var t in transitions)
            {
                StateTransitions.Add(new InspectorTransitionItem(t.TransitionId, t.FromState, t.ToState, t.ActorType, t.OccurredAt));
            }

            Artifacts.Clear();
            foreach (var a in artifacts) Artifacts.Add(a);

            ArtifactVersions.Clear();
            foreach (var v in versions) ArtifactVersions.Add(v);

            QaReports.Clear();
            foreach (var q in qaReports) QaReports.Add(q);

            Approvals.Clear();
            foreach (var ap in approvals) Approvals.Add(ap);

            Jobs.Clear();
            foreach (var j in jobs) Jobs.Add(j);

            CostEvents.Clear();
            foreach (var c in costs) CostEvents.Add(c);

            Publications.Clear();
            foreach (var p in publications) Publications.Add(p);
        }
        catch (Exception ex)
        {
            _notificationService.AddNotification($"Failed to load inspection for {productionId}: {ex.Message}", "Error");
        }
        finally
        {
            if (token == _loadRequestToken)
            {
                IsLoading = false;
            }
        }
    }
}
