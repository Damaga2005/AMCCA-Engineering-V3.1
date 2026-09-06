using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Research;
using AMCCA.Core.Scripts;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// SCRIPTING (SPEC/32): runs the script agent if one is wired to produce a <see cref="ScriptDocument"/>,
/// then validates it with <see cref="ScriptValidator"/> against the production's claims.
///
/// - no script agent           → BLOCKED (AMCCA-RES-001): scripting cannot proceed without one
/// - script fails validation   → REWORK: it asserts an unbacked / UNKNOWN / uncertain-without-wording fact
/// - script validates          → advance to SCRIPT_VERIFIED
/// </summary>
public sealed class ScriptStageHandler : IStageHandler
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly IScriptAgent? _agent;

    public ScriptStageHandler(DatabaseConnectionFactory connectionFactory, IScriptAgent? agent = null)
    {
        _connectionFactory = connectionFactory;
        _agent = agent;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        if (_agent is null)
        {
            return StageResult.Blocked(AmccaErrors.Res001,
                "No script agent is configured for SCRIPTING; a script cannot be generated.");
        }

        var script = await _agent.GenerateScriptAsync(context.Production.Id, context.CorrelationId, ct);
        var claims = await LoadClaimsAsync(context.Production.Id, ct);

        try
        {
            ScriptValidator.ValidateScriptAssertions(script, claims);
        }
        catch (AmccaException ex)
        {
            return StageResult.Defect(ex.ErrorCode,
                $"Generated script failed SPEC/32 validation: {ex.Message}");
        }

        var material = 0;
        foreach (var line in script.Lines)
        {
            if (line.IsMaterialFact) material++;
        }
        return StageResult.Advance($"Script validated: {script.Lines.Count} line(s), {material} material fact(s).");
    }

    private async Task<IDictionary<string, Claim>> LoadClaimsAsync(string productionId, CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Claim>(new CommandDefinition(
            @"SELECT id AS Id, production_id AS ProductionId, text AS Text, status AS Status,
                     materiality AS Materiality, subject_class AS SubjectClass,
                     contains_personal_data AS ContainsPersonalData, schema_version AS SchemaVersion,
                     created_at AS CreatedAt
              FROM claims WHERE production_id = @Id;",
            new { Id = productionId }, cancellationToken: ct));

        var map = new Dictionary<string, Claim>();
        foreach (var c in rows)
        {
            map[c.Id] = c;
        }
        return map;
    }
}
