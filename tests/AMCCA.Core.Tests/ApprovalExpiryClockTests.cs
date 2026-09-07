using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Policy;
using AMCCA.Core.StateMachine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// M1: <see cref="ApprovalManager"/> takes an injectable clock, so approval expiry
/// (<c>expires_at &gt; now</c>) is testable with <see cref="FakeTimeProvider"/> instead of a real
/// <c>Task.Delay</c>.
/// </summary>
public class ApprovalExpiryClockTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ProductionService _productions;

    public ApprovalExpiryClockTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_APEXP_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "apexp.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        var d = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(d) && !File.Exists(Path.Combine(d, "BUILD_ORDER.md"))) d = Directory.GetParent(d)?.FullName;
        var reg = new StateMachineRegistry(File.ReadAllText(Path.Combine(d!, "SCHEMAS", "state-machine.json")));
        _productions = new ProductionService(_factory, reg, new EventStore(_factory));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ApprovedGate_IsValidBeforeExpiry_AndGoneAfter_WithoutASleep()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var approvals = new ApprovalManager(_factory, clock);
        var pid = (await _productions.CreateProductionAsync("t", "en", "AUTONOMOUS", "corr")).Id;

        var id = await approvals.CreateApprovalRequestAsync(pid, "publication.dispatch", "{}", TimeSpan.FromMinutes(30));
        await approvals.ApproveRequestAsync(id, "operator");

        (await approvals.HasApprovedGateAsync(pid, "publication.dispatch"))
            .Should().BeTrue("29 minutes before the 30-minute expiry");

        clock.Advance(TimeSpan.FromMinutes(31));

        (await approvals.HasApprovedGateAsync(pid, "publication.dispatch"))
            .Should().BeFalse("the approval expired one minute ago — the injected clock, not a real wait, moved past expires_at");
    }
}
