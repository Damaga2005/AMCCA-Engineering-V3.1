using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Jobs;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class MediaStageHandlerContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ArtifactStore _artifacts;
    private readonly JobManager _jobs;

    public MediaStageHandlerContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_MEDIA_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "media.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _artifacts = new ArtifactStore(_factory, Path.Combine(_testDir, "data"));
        _jobs = new JobManager(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private async Task<string> NewProductionAsync(string state)
    {
        var id = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
              VALUES (@Id, @S, 0, 0, 'AUTONOMOUS', 'en', '3.1.0', @Now, @Now);",
            new { Id = id, S = state, Now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private static StageContext Ctx(string pid, string state)
        => new(new Production { Id = pid, State = state, AutonomyMode = "AUTONOMOUS" }, "corr-m");

    private sealed class FnMediaAgent : IMediaStageAgent
    {
        private readonly Func<string, Task> _fn;
        public string ProducesArtifactKind { get; }
        public FnMediaAgent(string kind, Func<string, Task> fn) { ProducesArtifactKind = kind; _fn = fn; }
        public Task ProduceAsync(string pid, string corr, CancellationToken ct = default) => _fn(pid);
    }

    private sealed class FnEditAgent : IEditAgent
    {
        private readonly string _path;
        public bool Called { get; private set; }
        public FnEditAgent(string path) => _path = path;
        public Task<string> AssembleAsync(string pid, string corr, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(_path);
        }
    }

    // ---- MediaProducingStageHandler --------------------------------

    [Fact]
    public async Task MediaProducing_NoAgent_BlocksWithMed001()
    {
        var pid = await NewProductionAsync("STORYBOARDING");
        var r = await new MediaProducingStageHandler(_factory, "STORYBOARDING", "STORYBOARD", agent: null)
            .HandleAsync(Ctx(pid, "STORYBOARDING"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Med001);
    }

    [Fact]
    public async Task MediaProducing_AgentThatSeedsTheArtifact_Advances()
    {
        var pid = await NewProductionAsync("STORYBOARDING");
        var agent = new FnMediaAgent("STORYBOARD", p => _artifacts.PutTextVersionAsync(p, "STORYBOARD", "{\"scenes\":[]}"));

        var r = await new MediaProducingStageHandler(_factory, "STORYBOARDING", "STORYBOARD", agent)
            .HandleAsync(Ctx(pid, "STORYBOARDING"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task MediaProducing_AgentThatProducesNothing_Blocks()
    {
        var pid = await NewProductionAsync("ASSET_GENERATION");
        var agent = new FnMediaAgent("ASSET_MANIFEST", _ => Task.CompletedTask);

        var r = await new MediaProducingStageHandler(_factory, "ASSET_GENERATION", "ASSET_MANIFEST", agent)
            .HandleAsync(Ctx(pid, "ASSET_GENERATION"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
    }

    // ---- EditingStageHandler -------------------------------------

    [Fact]
    public async Task Editing_NoRender_NoJob_NoAgent_Blocks()
    {
        var pid = await NewProductionAsync("EDITING");
        var r = await new EditingStageHandler(_factory, _jobs, agent: null).HandleAsync(Ctx(pid, "EDITING"));

        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Med001);
    }

    [Fact]
    public async Task Editing_WithEditAgent_EnqueuesARenderJob_AndNoops()
    {
        var pid = await NewProductionAsync("EDITING");
        var agent = new FnEditAgent("renders/prod/input.mov");

        var r = await new EditingStageHandler(_factory, _jobs, agent).HandleAsync(Ctx(pid, "EDITING"));

        r.Kind.Should().Be(StageOutcomeKind.Noop);
        agent.Called.Should().BeTrue();
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM jobs WHERE production_id=@P AND type='RENDER' AND state='QUEUED';", new { P = pid }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Editing_RenderJobAlreadyInFlight_Noops_WithoutEnqueuingAnother()
    {
        var pid = await NewProductionAsync("EDITING");
        await _jobs.EnqueueJobAsync("RENDER", $"render:{pid}:x", "corr", "{}", productionId: pid);

        var r = await new EditingStageHandler(_factory, _jobs, new FnEditAgent("p")).HandleAsync(Ctx(pid, "EDITING"));

        r.Kind.Should().Be(StageOutcomeKind.Noop);
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM jobs WHERE production_id=@P AND type='RENDER';", new { P = pid }))
            .Should().Be(1, "the in-flight render is left alone");
    }

    [Fact]
    public async Task Editing_WhenARenderArtifactExists_Advances()
    {
        var pid = await NewProductionAsync("EDITING");
        var f = Path.Combine(_testDir, "out.mp4");
        await File.WriteAllBytesAsync(f, new byte[8]);
        await _artifacts.PutExistingFileVersionAsync(pid, "RENDER", f, "mp4");

        var r = await new EditingStageHandler(_factory, _jobs, agent: null).HandleAsync(Ctx(pid, "EDITING"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }
}
