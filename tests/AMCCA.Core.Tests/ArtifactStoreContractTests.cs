using System;
using System.IO;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Database;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class ArtifactStoreContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ArtifactStore _store;

    public ArtifactStoreContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_ARTS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "arts.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _store = new ArtifactStore(_factory, Path.Combine(_testDir, "data"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private async Task<string> NewProductionAsync()
    {
        var id = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
              VALUES (@Id, 'SCRIPTING', 0, 0, 'AUTONOMOUS', 'en', '3.1.0', @Now, @Now);",
            new { Id = id, Now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    [Fact]
    public async Task PutTextVersion_CreatesTheArtifactAndVersion_AndWritesTheFile()
    {
        var pid = await NewProductionAsync();

        var versionId = await _store.PutTextVersionAsync(pid, "SCRIPT", "{\"hello\":1}");

        using var conn = await _factory.CreateOpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string State, long Bytes, string Sha, string Rel, long No)>(
            "SELECT state AS State, bytes AS Bytes, sha256 AS Sha, rel_path AS Rel, version_no AS No FROM artifact_versions WHERE id = @Id;",
            new { Id = versionId });
        row.State.Should().Be("CURRENT");
        row.No.Should().Be(1);
        row.Sha.Should().MatchRegex("^[0-9a-f]{64}$");
        File.Exists(Path.Combine(_testDir, "data", row.Rel.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();

        (await _store.GetCurrentTextAsync(pid, "SCRIPT")).Should().Be("{\"hello\":1}");
        (await conn.ExecuteScalarAsync<string>("SELECT current_version_id FROM artifacts WHERE production_id=@P AND kind='SCRIPT';", new { P = pid }))
            .Should().Be(versionId);
    }

    [Fact]
    public async Task PutTextVersion_Twice_BumpsVersion_AndSupersedesThePrevious()
    {
        var pid = await NewProductionAsync();
        var v1 = await _store.PutTextVersionAsync(pid, "SCRIPT", "v1");
        var v2 = await _store.PutTextVersionAsync(pid, "SCRIPT", "v2");

        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<string>("SELECT state FROM artifact_versions WHERE id=@Id;", new { Id = v1 })).Should().Be("SUPERSEDED");
        (await conn.ExecuteScalarAsync<string>("SELECT state FROM artifact_versions WHERE id=@Id;", new { Id = v2 })).Should().Be("CURRENT");
        (await conn.ExecuteScalarAsync<long>("SELECT version_no FROM artifact_versions WHERE id=@Id;", new { Id = v2 })).Should().Be(2);
        (await _store.GetCurrentTextAsync(pid, "SCRIPT")).Should().Be("v2");
    }
}
