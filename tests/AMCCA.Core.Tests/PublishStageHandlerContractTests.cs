using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Orchestration;
using AMCCA.Core.Orchestration.Handlers;
using AMCCA.Core.Policy;
using AMCCA.Core.StateMachine;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class PublishStageHandlerContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;
    private readonly ApprovalManager _approvals;

    public PublishStageHandlerContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_PUB_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "pub.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        var reg = new StateMachineRegistry(File.ReadAllText(Path.Combine(FindRepoRoot(), "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, reg, new EventStore(_factory));
        _approvals = new ApprovalManager(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private static string FindRepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d) && !File.Exists(Path.Combine(d, "BUILD_ORDER.md"))) d = Directory.GetParent(d)?.FullName;
        return d!;
    }

    private async Task<string> NewProductionAsync()
        => (await _productions.CreateProductionAsync("t", "en", "AUTONOMOUS", "corr")).Id;

    private async Task SeedApprovalAsync(string pid)
    {
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO approvals (id, production_id, action, scope_json, state, single_use, expires_at, created_at)
              VALUES (@Id, @Pid, 'publication.dispatch', '{}', 'APPROVED', 1, @Exp, @Now);",
            new { Id = UlidGenerator.NewUlid(), Pid = pid,
                  Exp = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), Now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private static StageContext Ctx(string pid, string state)
        => new(new Production { Id = pid, State = state, AutonomyMode = "AUTONOMOUS" }, "corr-p");

    private sealed class FnPublisher : IPublisher
    {
        private readonly PublishDispatchResult _dispatch;
        private readonly PublishTrackResult _track;
        public FnPublisher(PublishDispatchResult? d = null, PublishTrackResult? t = null)
        {
            _dispatch = d ?? new PublishDispatchResult(PublishDispatchStatus.Accepted, "ok");
            _track = t ?? new PublishTrackResult(PublishTrackStatus.Verified, "ok");
        }
        public Task<PublishDispatchResult> DispatchAsync(string p, string c, CancellationToken ct = default) => Task.FromResult(_dispatch);
        public Task<PublishTrackResult> PollStatusAsync(string p, string c, CancellationToken ct = default) => Task.FromResult(_track);
    }

    // ---- PublishStageHandler ---------------------------------------

    [Fact]
    public async Task Publish_NoPublisher_Blocks()
    {
        var pid = await NewProductionAsync();
        var r = await new PublishStageHandler(_approvals, publisher: null).HandleAsync(Ctx(pid, "READY_TO_PUBLISH"));
        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Plt001);
    }

    [Fact]
    public async Task Publish_NoApprovalToConsume_BlocksWithPol004()
    {
        var pid = await NewProductionAsync();
        var r = await new PublishStageHandler(_approvals, new FnPublisher()).HandleAsync(Ctx(pid, "READY_TO_PUBLISH"));
        r.Kind.Should().Be(StageOutcomeKind.Blocked);
        r.ReasonCode.Should().Be(AmccaErrors.Pol004);
    }

    [Fact]
    public async Task Publish_ConsumesTheApproval_AndDispatches_Advances()
    {
        var pid = await NewProductionAsync();
        await SeedApprovalAsync(pid);

        var r = await new PublishStageHandler(_approvals, new FnPublisher()).HandleAsync(Ctx(pid, "READY_TO_PUBLISH"));

        r.Kind.Should().Be(StageOutcomeKind.Advance);
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM approvals WHERE production_id=@P AND state='APPROVED' AND expires_at > @Now;",
            new { P = pid, Now = DateTimeOffset.UtcNow.ToString("O") }))
            .Should().Be(0, "the single-use approval was consumed");
    }

    [Fact]
    public async Task Publish_DispatchAmbiguous_RoutesToUnknownExternalState()
    {
        var pid = await NewProductionAsync();
        await SeedApprovalAsync(pid);
        var pub = new FnPublisher(new PublishDispatchResult(PublishDispatchStatus.Ambiguous, "timeout after send"));

        var r = await new PublishStageHandler(_approvals, pub).HandleAsync(Ctx(pid, "READY_TO_PUBLISH"));

        r.Kind.Should().Be(StageOutcomeKind.Ambiguous);
    }

    [Fact]
    public async Task Publish_DispatchRejected_Fails()
    {
        var pid = await NewProductionAsync();
        await SeedApprovalAsync(pid);
        var pub = new FnPublisher(new PublishDispatchResult(PublishDispatchStatus.Rejected, "policy violation on platform"));

        var r = await new PublishStageHandler(_approvals, pub).HandleAsync(Ctx(pid, "READY_TO_PUBLISH"));

        r.Kind.Should().Be(StageOutcomeKind.Failed);
    }

    // ---- PublishTrackingStageHandler -----------------------------

    [Fact]
    public async Task PublishTracking_NoPublisher_Blocks()
    {
        var r = await new PublishTrackingStageHandler(publisher: null).HandleAsync(Ctx("p", "PUBLISHING"));
        r.Kind.Should().Be(StageOutcomeKind.Blocked);
    }

    [Fact]
    public async Task PublishTracking_InPublishing_ProcessingStatus_Advances()
    {
        var pub = new FnPublisher(t: new PublishTrackResult(PublishTrackStatus.Processing, "targets accepted"));
        var r = await new PublishTrackingStageHandler(pub).HandleAsync(Ctx("p", "PUBLISHING"));
        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task PublishTracking_InPublicationProcessing_StillProcessing_Noops()
    {
        var pub = new FnPublisher(t: new PublishTrackResult(PublishTrackStatus.Processing, "encoding"));
        var r = await new PublishTrackingStageHandler(pub).HandleAsync(Ctx("p", "PUBLICATION_PROCESSING"));
        r.Kind.Should().Be(StageOutcomeKind.Noop);
    }

    [Fact]
    public async Task PublishTracking_Verified_Advances()
    {
        var pub = new FnPublisher(t: new PublishTrackResult(PublishTrackStatus.Verified, "live url confirmed"));
        var r = await new PublishTrackingStageHandler(pub).HandleAsync(Ctx("p", "PUBLICATION_PROCESSING"));
        r.Kind.Should().Be(StageOutcomeKind.Advance);
    }

    [Fact]
    public async Task PublishTracking_Rejected_Fails()
    {
        var pub = new FnPublisher(t: new PublishTrackResult(PublishTrackStatus.Rejected, "taken down"));
        var r = await new PublishTrackingStageHandler(pub).HandleAsync(Ctx("p", "PUBLISHING"));
        r.Kind.Should().Be(StageOutcomeKind.Failed);
    }
}
