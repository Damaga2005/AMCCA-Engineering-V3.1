using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// RESEARCHING (SPEC/26): runs the research agent if one is wired, then checks the outcome against the
/// exit criterion — every MATERIAL claim for the production is VERIFIED (status set by ClaimValidator
/// per SPEC/26 when the claim was recorded).
///
/// - no material claims           → BLOCKED (AMCCA-RES-002): research has not been performed and no
///                                  agent is available to perform it
/// - some material claim unverified → REWORK (AMCCA-RES-001): regenerate research for this production
/// - all material claims verified   → advance to RESEARCH_VERIFIED
/// </summary>
public sealed class ResearchStageHandler : IStageHandler
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IResearchAgent? _agent;

    public ResearchStageHandler(DatabaseConnectionFactory connectionFactory, IResearchAgent? agent = null)
    {
        _connectionFactory = connectionFactory;
        _agent = agent;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        if (_agent is not null)
        {
            await _agent.PerformResearchAsync(context.Production.Id, context.CorrelationId, ct);
        }

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var counts = await connection.QuerySingleAsync<(int Total, int Verified)>(new CommandDefinition(
            @"SELECT
                COUNT(*) AS Total,
                COALESCE(SUM(CASE WHEN status = 'VERIFIED' THEN 1 ELSE 0 END), 0) AS Verified
              FROM claims
              WHERE production_id = @Id AND materiality = 'MATERIAL';",
            new { Id = context.Production.Id }, cancellationToken: ct));

        if (counts.Total == 0)
        {
            return _agent is null
                ? StageResult.Blocked(AmccaErrors.Res002,
                    "No research has been performed for this production and no research agent is configured for RESEARCHING.")
                : StageResult.Blocked(AmccaErrors.Res002,
                    "The research agent produced no material claims for this production.");
        }

        if (counts.Verified < counts.Total)
        {
            return StageResult.Defect(AmccaErrors.Res001,
                $"{counts.Total - counts.Verified} of {counts.Total} material claim(s) are not VERIFIED; regenerating research (SPEC/26).");
        }

        return StageResult.Advance($"All {counts.Total} material claim(s) are verified.");
    }
}
