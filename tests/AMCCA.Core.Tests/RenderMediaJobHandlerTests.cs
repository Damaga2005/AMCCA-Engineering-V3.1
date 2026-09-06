using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Artifacts;
using AMCCA.Core.Database;
using AMCCA.Core.Jobs;
using AMCCA.Core.Media;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AMCCA.Core.Tests;

public class RenderMediaJobHandlerTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataRoot;
    private readonly DatabaseConnectionFactory _factory;
    private readonly ArtifactStore _artifacts;

    public RenderMediaJobHandlerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AMCCA_RENDER_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dataRoot = Path.Combine(_testDir, "data");
        Directory.CreateDirectory(_dataRoot);
        _factory = new DatabaseConnectionFactory(Path.Combine(_testDir, "render.db"));
        new MigrationService(_factory, _testDir).UpgradeAsync().GetAwaiter().GetResult();
        _artifacts = new ArtifactStore(_factory, _dataRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    private static readonly MediaProfile Profile = new(
        "yt-vertical", "1.0", "mp4", "libx264", "aac", 1080, 1920, 30, 8000, -14.0, "ref://profiles/yt", "2026-01-01T00:00:00Z");

    // ---- arg builder --------------------------------------------------

    [Fact]
    public void BuildFfmpegArguments_HasScalePad_Loudnorm_Codecs_AndClosesStdin()
    {
        var args = MediaRenderer.BuildFfmpegArguments("in.mov", "out.mp4", Profile);
        var joined = string.Join(" ", args);

        args.Should().StartWith(new[] { "-nostdin", "-y", "-i", "in.mov" });
        joined.Should().Contain("scale=1080:1920").And.Contain("pad=1080:1920");
        joined.Should().Contain("loudnorm=I=-14:TP=-1.5:LRA=11");
        joined.Should().Contain("-c:v libx264").And.Contain("-b:v 8000k").And.Contain("-r 30").And.Contain("-c:a aac");
        args.Last().Should().Be("out.mp4");
    }

    [Fact]
    public void BuildFfmpegArguments_WithDisclosure_BurnsInACaption()
    {
        var d = new SyntheticDisclosure(true, true, "model-x", "AI-generated video");
        var joined = string.Join(" ", MediaRenderer.BuildFfmpegArguments("in.mov", "out.mp4", Profile, d));

        joined.Should().Contain("drawtext=text='AI-generated video'");
    }

    [Fact]
    public void BuildFfmpegArguments_WithMaxDuration_AddsHardCap()
    {
        var args = MediaRenderer.BuildFfmpegArguments("in.mov", "out.mp4", Profile, null, maxDurationMs: 45_000);
        var i = args.ToList().IndexOf("-t");
        i.Should().BeGreaterThan(-1);
        args.ToList()[i + 1].Should().Be("45");
    }

    // ---- job handler ------------------------------------------------

    private sealed class FakeFfmpeg : IFfmpegRunner
    {
        private readonly int _exitCode;
        private readonly bool _timedOut;
        private readonly bool _writeOutput;
        public IReadOnlyList<string>? LastArgs { get; private set; }

        public FakeFfmpeg(int exitCode = 0, bool timedOut = false, bool writeOutput = true)
        { _exitCode = exitCode; _timedOut = timedOut; _writeOutput = writeOutput; }

        public Task<FfmpegResult> RunAsync(IReadOnlyList<string> args, string wd, TimeSpan timeout, CancellationToken ct)
        {
            LastArgs = args;
            if (_writeOutput && !_timedOut && _exitCode == 0)
            {
                File.WriteAllBytes(args[^1], new byte[] { 0x00, 0x01, 0x02, 0x03 });
            }
            return Task.FromResult(new FfmpegResult(_timedOut ? -1 : _exitCode, "stderr tail", _timedOut));
        }
    }

    private async Task<string> NewProductionAsync()
    {
        var id = UlidGenerator.NewUlid();
        using var conn = await _factory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO productions (id, state, rework_attempts, aggregate_version, autonomy_mode, language, schema_version, created_at, updated_at)
              VALUES (@Id, 'EDITING', 0, 0, 'AUTONOMOUS', 'en', '3.1.0', @Now, @Now);",
            new { Id = id, Now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private JobExecutionContext Job(string productionId, string inputPath)
    {
        var payload = JsonSerializer.Serialize(new
        {
            input_path = inputPath,
            profile = new
            {
                profile_id = "yt-vertical", version = "1.0", container = "mp4",
                video_codec = "libx264", audio_codec = "aac", width = 1080, height = 1920,
                fps = 30, bitrate_kbps = 8000, loudness_target_lufs = -14.0,
                source_ref = "ref://p", retrieved_at = "2026-01-01T00:00:00Z",
            },
            max_duration_ms = 60000,
        });
        var job = new JobRecord { Id = UlidGenerator.NewUlid(), ProductionId = productionId, Type = "RENDER", PayloadJson = payload };
        return new JobExecutionContext(job, FenceToken: 1, WorkerId: "w1");
    }

    private RenderMediaJobHandler Handler(IFfmpegRunner ff) => new(_artifacts, ff, _dataRoot);

    [Fact]
    public async Task OnFfmpegSuccess_StoresTheRenderArtifact_AndSucceeds()
    {
        var pid = await NewProductionAsync();
        var input = Path.Combine(_dataRoot, "in.mov");
        await File.WriteAllTextAsync(input, "fake source");

        var result = await Handler(new FakeFfmpeg()).HandleAsync(Job(pid, input));

        result.Kind.Should().Be(JobResultKind.Success);
        (await _artifacts.GetCurrentTextAsync(pid, "RENDER")).Should().NotBeNull("the render output was stored as an artifact");
        using var conn = await _factory.CreateOpenConnectionAsync();
        (await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM artifact_versions av JOIN artifacts a ON a.id=av.artifact_id WHERE a.production_id=@P AND a.kind='RENDER' AND av.state='CURRENT';",
            new { P = pid })).Should().Be(1);
    }

    [Fact]
    public async Task OnFfmpegNonZeroExit_Fails()
    {
        var pid = await NewProductionAsync();
        var input = Path.Combine(_dataRoot, "in2.mov");
        await File.WriteAllTextAsync(input, "src");

        var result = await Handler(new FakeFfmpeg(exitCode: 1, writeOutput: false)).HandleAsync(Job(pid, input));

        result.Kind.Should().Be(JobResultKind.Failure);
        result.Detail.Should().Contain("exited 1");
    }

    [Fact]
    public async Task OnTimeout_Fails()
    {
        var pid = await NewProductionAsync();
        var input = Path.Combine(_dataRoot, "in3.mov");
        await File.WriteAllTextAsync(input, "src");

        var result = await Handler(new FakeFfmpeg(timedOut: true)).HandleAsync(Job(pid, input));

        result.Kind.Should().Be(JobResultKind.Failure);
        result.Detail.Should().Contain("exceeded");
    }

    [Fact]
    public async Task MissingInput_Fails_WithoutInvokingFfmpeg()
    {
        var pid = await NewProductionAsync();
        var ff = new FakeFfmpeg();

        var result = await Handler(ff).HandleAsync(Job(pid, Path.Combine(_dataRoot, "nope.mov")));

        result.Kind.Should().Be(JobResultKind.Failure);
        ff.LastArgs.Should().BeNull();
    }
}
