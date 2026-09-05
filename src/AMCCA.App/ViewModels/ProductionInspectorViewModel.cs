using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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

// SPEC/60 obligation 3: "Every number carries its provenance; measured and estimated are visually
// distinct." reconciliation_state is that provenance for a cost amount -- ESTIMATED until a provider
// statement reconciles it -- and DataGridStyle in the view colors this column by value.
public record InspectorCostEventItem(string Id, string Kind, string Amount, string Currency, string ReconciliationState, string OccurredAt);

public record InspectorPublicationItem(
    string Id,
    string Platform,
    string State,
    string? ExternalUrl,
    string? EvidenceSource,
    string? EvidenceRetrievedAt,
    string UpdatedAt);

// SPEC/60: "Claims with sources and retrieval timestamps." One row per claim-source pair (a claim with
// no source yet still gets one row, with null source fields) rather than a nested grid, matching the
// flat DataGrid style already used by every other tab.
public record InspectorClaimItem(
    string ClaimId,
    string Text,
    string Status,
    string Materiality,
    string SubjectClass,
    string? SourceUrl,
    string? Publisher,
    string? RetrievedAt,
    string? Relation);

public record InspectorPolicyDecisionItem(
    string Id,
    string Action,
    string Decision,
    string RuleKey,
    string PolicyVersionId,
    string CorrelationId,
    string DecidedAt);

public record InspectorQaFindingItem(
    string Id,
    string Stage,
    string CheckId,
    string CheckKind,
    string Status,
    string Severity,
    string ResponsibleArtifactVersionId,
    string? RemediationCode,
    string? Message);

public record InspectorArtifactEdgeItem(
    string ParentVersionId,
    string ChildVersionId,
    string EdgeKind,
    string CreatedAt);

/// <summary>
/// SPEC/60 obligation 4: "Every blocked element indicates which rule blocked it, which policy version,
/// and what would unblock it." The reason/policy-version half comes from the one place this codebase
/// records why an action was blocked -- an audit_log row with a reason_code and (if a policy engine ever
/// persists one) a policy_decision_id -- looked up by subject_id against this production. Nothing writes
/// policy_decisions today (verified: no INSERT anywhere in src/AMCCA.Core), so PolicyDecisionId is always
/// null in practice; this is disclosed via UnblockHint rather than invented. The "what would unblock it"
/// half is not a guess: StateMachineRegistry enforces (AMCCA-STM-002) that resuming from BLOCKED is only
/// ever legal back to the recorded blocked_from state, so that is the literal, code-enforced answer.
/// </summary>
public record InspectorBlockInfo(string? ReasonCode, string? PolicyDecisionId, string? OccurredAt, string? ResumesTo)
{
    public string ReasonDisplay => ReasonCode ?? "(no reason code recorded for this block)";
    public string PolicyVersionDisplay => PolicyDecisionId ?? "(no policy decision recorded for this block)";
    public string UnblockHint => ResumesTo is { Length: > 0 }
        ? $"Resolve the blocking condition, then resume to '{ResumesTo}' (the only legal target per AMCCA-STM-002)."
        : "No prior state was recorded to resume to.";
}

/// <summary>
/// SPEC/60 Production Inspector: a read-only aggregate view across production state, versions,
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

    // The picker list has its own token: it is refilled by a different pair of callers (the constructor
    // and Refresh) and must not invalidate an inspection load, nor be invalidated by one.
    private int _pickerRequestToken;

    // SPEC/60 obligation 7: cancelling this actually stops the in-flight queries via the
    // CancellationToken threaded through every call below, instead of merely discarding results the
    // queries would still finish computing.
    private CancellationTokenSource? _loadCts;

    private InspectorBlockInfo? _blockInfo;

    public ObservableCollection<InspectorProductionSummary> AvailableProductions { get; } = new();
    public ObservableCollection<InspectorTransitionItem> StateTransitions { get; } = new();
    public ObservableCollection<InspectorArtifactItem> Artifacts { get; } = new();
    public ObservableCollection<InspectorArtifactVersionItem> ArtifactVersions { get; } = new();
    public ObservableCollection<InspectorQaReportItem> QaReports { get; } = new();
    public ObservableCollection<InspectorApprovalItem> Approvals { get; } = new();
    public ObservableCollection<InspectorJobItem> Jobs { get; } = new();
    public ObservableCollection<InspectorCostEventItem> CostEvents { get; } = new();
    public ObservableCollection<InspectorPublicationItem> Publications { get; } = new();
    public ObservableCollection<InspectorClaimItem> Claims { get; } = new();
    public ObservableCollection<InspectorPolicyDecisionItem> PolicyDecisions { get; } = new();
    public ObservableCollection<InspectorQaFindingItem> QaFindings { get; } = new();
    public ObservableCollection<InspectorArtifactEdgeItem> ArtifactEdges { get; } = new();

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

    public InspectorBlockInfo? BlockInfo
    {
        get => _blockInfo;
        private set
        {
            if (SetProperty(ref _blockInfo, value))
            {
                OnPropertyChanged(nameof(HasBlockInfo));
            }
        }
    }

    public bool HasBlockInfo => BlockInfo != null;

    public ICommand RefreshCommand { get; }
    public ICommand CancelLoadCommand { get; }

    public ProductionInspectorViewModel(
        ProductionService productionService,
        DatabaseConnectionFactory connectionFactory,
        INotificationService notificationService)
    {
        _productionService = productionService;
        _connectionFactory = connectionFactory;
        _notificationService = notificationService;

        RefreshCommand = new AsyncRelayCommand(LoadAvailableProductionsAsync);
        CancelLoadCommand = new RelayCommand(() => _loadCts?.Cancel());

        _ = LoadAvailableProductionsAsync();
    }

    public async Task LoadAvailableProductionsAsync()
    {
        var token = ++_pickerRequestToken;
        try
        {
            var previouslySelectedId = SelectedProduction?.Id;

            var recent = await _productionService.ListRecentAsync(50);

            if (token != _pickerRequestToken)
            {
                return; // a newer picker load has started; its results are the ones that count
            }

            AvailableProductions.Clear();
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
        Claims.Clear();
        PolicyDecisions.Clear();
        QaFindings.Clear();
        ArtifactEdges.Clear();
        BlockInfo = null;
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
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;
        IsLoading = true;
        var productionId = SelectedProduction.Id;
        try
        {
            // Everything is fetched first and applied in one guarded block, so a load that is superseded
            // mid-flight can never leave the tabs holding a mixture of two productions.
            var detail = await _productionService.GetProductionAsync(productionId, ct);
            var transitions = await _productionService.GetStateTransitionsAsync(productionId, ct);

            using var conn = await _connectionFactory.CreateOpenConnectionAsync(ct);
            var parameters = new { ProductionId = productionId };
            CommandDefinition Cmd(string sql) => new(sql, parameters, cancellationToken: ct);

            var artifacts = await conn.QueryAsync<InspectorArtifactItem>(Cmd(
                "SELECT id AS Id, kind AS Kind, current_version_id AS CurrentVersionId, created_at AS CreatedAt FROM artifacts WHERE production_id = @ProductionId ORDER BY created_at ASC;"));

            var versions = await conn.QueryAsync<InspectorArtifactVersionItem>(Cmd(@"
                SELECT av.id AS Id, av.artifact_id AS ArtifactId, av.version_no AS VersionNo, av.sha256 AS Sha256, av.state AS State, av.created_at AS CreatedAt
                FROM artifact_versions av
                INNER JOIN artifacts a ON a.id = av.artifact_id
                WHERE a.production_id = @ProductionId
                ORDER BY av.created_at ASC;"));

            var qaReports = await conn.QueryAsync<InspectorQaReportItem>(Cmd(
                "SELECT report_id AS ReportId, stage AS Stage, overall_score AS OverallScore, verdict AS Verdict, evaluated_at AS EvaluatedAt FROM qa_reports WHERE production_id = @ProductionId ORDER BY evaluated_at ASC;"));

            var approvals = await conn.QueryAsync<InspectorApprovalItem>(Cmd(
                "SELECT id AS Id, action AS Action, state AS State, expires_at AS ExpiresAt, created_at AS CreatedAt FROM approvals WHERE production_id = @ProductionId ORDER BY created_at ASC;"));

            var jobs = await conn.QueryAsync<InspectorJobItem>(Cmd(
                "SELECT id AS Id, type AS Type, state AS State, attempt AS Attempt, max_attempts AS MaxAttempts, updated_at AS UpdatedAt FROM jobs WHERE production_id = @ProductionId ORDER BY updated_at DESC;"));

            var costs = await conn.QueryAsync<InspectorCostEventItem>(Cmd(
                "SELECT id AS Id, kind AS Kind, amount AS Amount, currency AS Currency, reconciliation_state AS ReconciliationState, occurred_at AS OccurredAt FROM cost_events WHERE production_id = @ProductionId ORDER BY occurred_at ASC;"));

            var publications = await conn.QueryAsync<InspectorPublicationItem>(Cmd(
                "SELECT id AS Id, platform AS Platform, state AS State, external_url AS ExternalUrl, evidence_source AS EvidenceSource, evidence_retrieved_at AS EvidenceRetrievedAt, updated_at AS UpdatedAt FROM publications WHERE production_id = @ProductionId ORDER BY updated_at DESC;"));

            // SPEC/60: claims with their sources and retrieval timestamps. LEFT JOINed so a claim with
            // no source recorded yet still shows a row instead of vanishing from the inspector.
            var claims = await conn.QueryAsync<InspectorClaimItem>(Cmd(@"
                SELECT c.id AS ClaimId, c.text AS Text, c.status AS Status, c.materiality AS Materiality, c.subject_class AS SubjectClass,
                       s.url AS SourceUrl, s.publisher AS Publisher, s.retrieved_at AS RetrievedAt, cs.relation AS Relation
                FROM claims c
                LEFT JOIN claim_sources cs ON cs.claim_id = c.id
                LEFT JOIN sources s ON s.id = cs.source_id
                WHERE c.production_id = @ProductionId
                ORDER BY c.created_at ASC;"));

            var policyDecisions = await conn.QueryAsync<InspectorPolicyDecisionItem>(Cmd(
                "SELECT id AS Id, action AS Action, decision AS Decision, rule_key AS RuleKey, policy_version_id AS PolicyVersionId, correlation_id AS CorrelationId, decided_at AS DecidedAt FROM policy_decisions WHERE production_id = @ProductionId ORDER BY decided_at ASC;"));

            // qa_findings has no production_id of its own; it is scoped to a production through the
            // qa_reports row it belongs to.
            var qaFindings = await conn.QueryAsync<InspectorQaFindingItem>(Cmd(@"
                SELECT qf.id AS Id, qr.stage AS Stage, qf.check_id AS CheckId, qf.check_kind AS CheckKind, qf.status AS Status,
                       qf.severity AS Severity, qf.responsible_artifact_version_id AS ResponsibleArtifactVersionId,
                       qf.remediation_code AS RemediationCode, qf.message AS Message
                FROM qa_findings qf
                INNER JOIN qa_reports qr ON qr.report_id = qf.report_id
                WHERE qr.production_id = @ProductionId
                ORDER BY qr.evaluated_at ASC;"));

            // Same scoping problem as qa_findings: artifact_edges has no production_id, so it is joined
            // through its parent version's artifact.
            var artifactEdges = await conn.QueryAsync<InspectorArtifactEdgeItem>(Cmd(@"
                SELECT ae.parent_version_id AS ParentVersionId, ae.child_version_id AS ChildVersionId, ae.edge_kind AS EdgeKind, ae.created_at AS CreatedAt
                FROM artifact_edges ae
                INNER JOIN artifact_versions pv ON pv.id = ae.parent_version_id
                INNER JOIN artifacts pa ON pa.id = pv.artifact_id
                WHERE pa.production_id = @ProductionId
                ORDER BY ae.created_at ASC;"));

            // SPEC/60 obligation 4: the most recent audit_log row explaining a block against this
            // production, if any writer ever recorded one (see InspectorBlockInfo's own remarks on why
            // this is often absent today rather than always populated).
            InspectorBlockInfo? blockInfo = null;
            if (string.Equals(detail?.State, "BLOCKED", StringComparison.OrdinalIgnoreCase))
            {
                var blockRow = await conn.QuerySingleOrDefaultAsync<(string? ReasonCode, string? PolicyDecisionId, string? OccurredAt)>(Cmd(
                    @"SELECT reason_code AS ReasonCode, policy_decision_id AS PolicyDecisionId, occurred_at AS OccurredAt
                      FROM audit_log
                      WHERE subject_id = @ProductionId AND outcome IN ('BLOCKED','DENIED','REJECTED','ERROR')
                      ORDER BY occurred_at DESC
                      LIMIT 1;"));
                blockInfo = new InspectorBlockInfo(blockRow.ReasonCode, blockRow.PolicyDecisionId, blockRow.OccurredAt, detail!.BlockedFrom);
            }

            if (token != _loadRequestToken)
            {
                return; // a newer load has started; its results are the ones that count
            }

            ProductionDetail = detail;
            BlockInfo = blockInfo;

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

            Claims.Clear();
            foreach (var c in claims) Claims.Add(c);

            PolicyDecisions.Clear();
            foreach (var pd in policyDecisions) PolicyDecisions.Add(pd);

            QaFindings.Clear();
            foreach (var f in qaFindings) QaFindings.Add(f);

            ArtifactEdges.Clear();
            foreach (var e in artifactEdges) ArtifactEdges.Add(e);
        }
        catch (OperationCanceledException)
        {
            // The operator cancelled this load deliberately (SPEC/60 obligation 7); a newer load already
            // took over, or nothing has -- either way this is not an error.
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
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }
            cts.Dispose();
        }
    }
}
