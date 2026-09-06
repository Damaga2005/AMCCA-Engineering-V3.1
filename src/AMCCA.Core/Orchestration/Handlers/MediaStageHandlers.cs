using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using Dapper;

namespace AMCCA.Core.Orchestration.Handlers;

/// <summary>
/// The generative half of a media producing stage (STORYBOARDING / ASSET_GENERATION / AUDIO_GENERATION):
/// runs a model + an image/audio provider and stores the stage's artifact. No such provider exists yet,
/// so the handler has no agent and blocks the production for an operator — the same honest pattern as
/// IResearchAgent / IScriptAgent before their agents were wired.
/// </summary>
public interface IMediaStageAgent
{
    /// <summary>The artifact kind this agent produces (e.g. STORYBOARD, ASSET_MANIFEST, AUDIO).</summary>
    string ProducesArtifactKind { get; }

    Task ProduceAsync(string productionId, string correlationId, CancellationToken ct = default);
}

public sealed class MediaProducingStageHandler : IStageHandler
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _stage;
    private readonly string _artifactKind;
    private readonly IMediaStageAgent? _agent;

    public MediaProducingStageHandler(
        DatabaseConnectionFactory connectionFactory, string stage, string artifactKind, IMediaStageAgent? agent)
    {
        _connectionFactory = connectionFactory;
        _stage = stage;
        _artifactKind = artifactKind;
        _agent = agent;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        if (_agent is null)
        {
            return StageResult.Blocked(AmccaErrors.Med001,
                $"{_stage} needs a {_artifactKind} generation agent (a model + an image/audio provider); none is configured.");
        }

        await _agent.ProduceAsync(context.Production.Id, context.CorrelationId, ct);

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var produced = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*) FROM artifact_versions av JOIN artifacts a ON a.id = av.artifact_id
              WHERE a.production_id = @P AND a.kind = @K AND av.state = 'CURRENT';",
            new { P = context.Production.Id, K = _artifactKind }, cancellationToken: ct));

        return produced > 0
            ? StageResult.Advance($"{_stage}: {_artifactKind} artifact produced.")
            : StageResult.Blocked(AmccaErrors.Med001, $"{_stage}: the agent produced no {_artifactKind} artifact.");
    }
}

/// <summary>
/// Assembles the pre-render input from the production's assets + audio and stores it as the CURRENT
/// EDIT artifact, returning its path under the data root. A real editor does not exist yet.
/// </summary>
public interface IEditAgent
{
    /// <summary>Data-root-relative path of the assembled pre-render file.</summary>
    Task<string> AssembleAsync(string productionId, string correlationId, CancellationToken ct = default);
}

/// <summary>
/// EDITING: assemble the pre-render input (via <see cref="IEditAgent"/>), enqueue a RENDER job for it,
/// and wait for the render. The RENDER job handler (A7) is real; the missing piece is the editor that
/// produces its input, so without one the stage blocks.
/// </summary>
public sealed class EditingStageHandler : IStageHandler
{
    // ponytail: a single hardcoded vertical render profile until AmccaConfig carries media profiles.
    private static readonly object DefaultProfile = new
    {
        profile_id = "default-vertical", version = "1.0", container = "mp4",
        video_codec = "libx264", audio_codec = "aac", width = 1080, height = 1920,
        fps = 30, bitrate_kbps = 8000, loudness_target_lufs = -14.0,
        source_ref = "builtin://profiles/default-vertical", retrieved_at = "",
    };

    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly JobManager _jobs;
    private readonly IEditAgent? _agent;

    public EditingStageHandler(DatabaseConnectionFactory connectionFactory, JobManager jobs, IEditAgent? agent)
    {
        _connectionFactory = connectionFactory;
        _jobs = jobs;
        _agent = agent;
    }

    public async Task<StageResult> HandleAsync(StageContext context, CancellationToken ct = default)
    {
        var productionId = context.Production.Id;
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);

        // 1. RENDER already finished?
        var hasRender = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            @"SELECT COUNT(*) FROM artifact_versions av JOIN artifacts a ON a.id = av.artifact_id
              WHERE a.production_id = @P AND a.kind = 'RENDER' AND av.state = 'CURRENT';",
            new { P = productionId }, cancellationToken: ct));
        if (hasRender > 0)
        {
            return StageResult.Advance("EDITING: a candidate render is ready.");
        }

        // 2. RENDER job still in flight?
        var renderInFlight = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM jobs WHERE production_id = @P AND type = 'RENDER' AND state IN ('QUEUED','LEASED');",
            new { P = productionId }, cancellationToken: ct));
        if (renderInFlight > 0)
        {
            return StageResult.Noop("EDITING: render job is running.");
        }

        // 3. Assemble and enqueue.
        if (_agent is null)
        {
            return StageResult.Blocked(AmccaErrors.Med001,
                "EDITING needs an editor to assemble assets + audio into a render input; none is configured.");
        }

        var inputRelPath = await _agent.AssembleAsync(productionId, context.CorrelationId, ct);

        var payload = JsonSerializer.Serialize(new
        {
            input_path = inputRelPath,
            profile = DefaultProfile,
            max_duration_ms = 90_000,
        });
        var idempotencyKey = $"render:{productionId}:{inputRelPath}";

        try
        {
            await _jobs.EnqueueJobAsync("RENDER", idempotencyKey, context.CorrelationId, payload,
                priority: 1, maxAttempts: 2, productionId: productionId, ct: ct);
        }
        catch (AmccaException ex) when (ex.ErrorCode == AmccaErrors.Job002)
        {
            // A RENDER for this exact input is already enqueued — fine, just wait for it.
        }

        return StageResult.Noop("EDITING: assembled the render input and enqueued a RENDER job.");
    }
}
