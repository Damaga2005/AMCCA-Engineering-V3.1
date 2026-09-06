using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Artifacts;

/// <summary>
/// Writes production artifacts (SCRIPT, STORYBOARD, …) as real files under the data root and records
/// each write as a new <c>artifact_versions</c> row (SPEC/13, SPEC/37): version numbers increase, the
/// previous CURRENT becomes SUPERSEDED, and <c>artifacts.current_version_id</c> points at the newest.
/// The body is content-hashed; nothing is stored that was not written to disk.
/// </summary>
public sealed class ArtifactStore
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly string _dataRoot;

    public ArtifactStore(DatabaseConnectionFactory connectionFactory, string dataRoot)
    {
        _connectionFactory = connectionFactory;
        _dataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Path.GetTempPath(), "amcca-artifacts")
            : Path.GetFullPath(dataRoot);
    }

    /// <summary>Stores <paramref name="body"/> as the new CURRENT version of the (production, kind) artifact.</summary>
    public async Task<string> PutTextVersionAsync(
        string productionId, string kind, string body, string extension = "json",
        string? generatorModelId = null, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        var artifactId = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT id FROM artifacts WHERE production_id = @P AND kind = @K;",
            new { P = productionId, K = kind }, tx);

        if (artifactId is null)
        {
            artifactId = UlidGenerator.NewUlid();
            await connection.ExecuteAsync(
                "INSERT INTO artifacts (id, production_id, kind, created_at, updated_at) VALUES (@Id, @P, @K, @Now, @Now);",
                new { Id = artifactId, P = productionId, K = kind, Now = now }, tx);
        }

        var nextNo = (await connection.ExecuteScalarAsync<long?>(
            "SELECT MAX(version_no) FROM artifact_versions WHERE artifact_id = @A;", new { A = artifactId }, tx) ?? 0) + 1;

        var relPath = $"artifacts/{productionId}/{artifactId}/v{nextNo}.{extension}";
        var absPath = Path.Combine(_dataRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        await File.WriteAllBytesAsync(absPath, bytes, ct);

        await connection.ExecuteAsync(
            "UPDATE artifact_versions SET state = 'SUPERSEDED' WHERE artifact_id = @A AND state = 'CURRENT';",
            new { A = artifactId }, tx);

        var versionId = UlidGenerator.NewUlid();
        await connection.ExecuteAsync(
            @"INSERT INTO artifact_versions
                (id, artifact_id, version_no, sha256, bytes, rel_path, state, generator_model_id, created_at)
              VALUES (@Id, @A, @No, @Sha, @Bytes, @Rel, 'CURRENT', @Gen, @Now);",
            new { Id = versionId, A = artifactId, No = nextNo, Sha = sha, Bytes = bytes.Length, Rel = relPath, Gen = generatorModelId, Now = now }, tx);

        await connection.ExecuteAsync(
            "UPDATE artifacts SET current_version_id = @V, updated_at = @Now WHERE id = @A;",
            new { V = versionId, Now = now, A = artifactId }, tx);

        tx.Commit();
        return versionId;
    }

    /// <summary>
    /// Moves an already-produced file (e.g. an ffmpeg render output) into the artifact tree as the new
    /// CURRENT version of the (production, kind) artifact. Returns the artifact_version id.
    /// </summary>
    public async Task<string> PutExistingFileVersionAsync(
        string productionId, string kind, string sourceAbsolutePath, string extension,
        string? generatorModelId = null, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourceAbsolutePath, ct);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        var artifactId = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT id FROM artifacts WHERE production_id = @P AND kind = @K;", new { P = productionId, K = kind }, tx);
        if (artifactId is null)
        {
            artifactId = UlidGenerator.NewUlid();
            await connection.ExecuteAsync(
                "INSERT INTO artifacts (id, production_id, kind, created_at, updated_at) VALUES (@Id, @P, @K, @Now, @Now);",
                new { Id = artifactId, P = productionId, K = kind, Now = now }, tx);
        }

        var nextNo = (await connection.ExecuteScalarAsync<long?>(
            "SELECT MAX(version_no) FROM artifact_versions WHERE artifact_id = @A;", new { A = artifactId }, tx) ?? 0) + 1;

        var relPath = $"artifacts/{productionId}/{artifactId}/v{nextNo}.{extension}";
        var absPath = Path.Combine(_dataRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        File.Copy(sourceAbsolutePath, absPath, overwrite: true);

        await connection.ExecuteAsync(
            "UPDATE artifact_versions SET state = 'SUPERSEDED' WHERE artifact_id = @A AND state = 'CURRENT';",
            new { A = artifactId }, tx);

        var versionId = UlidGenerator.NewUlid();
        await connection.ExecuteAsync(
            @"INSERT INTO artifact_versions
                (id, artifact_id, version_no, sha256, bytes, rel_path, state, generator_model_id, created_at)
              VALUES (@Id, @A, @No, @Sha, @Bytes, @Rel, 'CURRENT', @Gen, @Now);",
            new { Id = versionId, A = artifactId, No = nextNo, Sha = sha, Bytes = bytes.Length, Rel = relPath, Gen = generatorModelId, Now = now }, tx);

        await connection.ExecuteAsync(
            "UPDATE artifacts SET current_version_id = @V, updated_at = @Now WHERE id = @A;",
            new { V = versionId, Now = now, A = artifactId }, tx);

        tx.Commit();
        return versionId;
    }

    /// <summary>The body of the CURRENT version of the (production, kind) artifact, or null if none / file missing.</summary>
    public async Task<string?> GetCurrentTextAsync(string productionId, string kind, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        var relPath = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            @"SELECT av.rel_path
              FROM artifact_versions av
              JOIN artifacts a ON a.id = av.artifact_id
              WHERE a.production_id = @P AND a.kind = @K AND av.state = 'CURRENT';",
            new { P = productionId, K = kind }, cancellationToken: ct));

        if (relPath is null) return null;
        var absPath = Path.Combine(_dataRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(absPath) ? await File.ReadAllTextAsync(absPath, ct) : null;
    }
}
