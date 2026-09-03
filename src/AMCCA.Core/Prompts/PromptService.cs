using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using Dapper;

namespace AMCCA.Core.Prompts;

public class PromptService
{
    private readonly DatabaseConnectionFactory _connectionFactory;

    public PromptService(DatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<PromptTemplate> CreateTemplateAsync(string key, string purpose, CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var template = new PromptTemplate
        {
            Id = id,
            Key = key,
            Purpose = purpose,
            CurrentVersionId = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO prompt_templates (id, key, purpose, current_version_id, created_at, updated_at)
            VALUES (@Id, @Key, @Purpose, @CurrentVersionId, @CreatedAt, @UpdatedAt);
        ";
        await connection.ExecuteAsync(sql, template);
        return template;
    }

    public async Task<PromptVersion> CreateVersionAsync(
        string templateId,
        int versionNo,
        string bodyText,
        string? notes = null,
        CancellationToken ct = default)
    {
        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyText))).ToLowerInvariant();
        var bodyRef = $"prompt://versions/{id}";

        var version = new PromptVersion
        {
            Id = id,
            TemplateId = templateId,
            VersionNo = versionNo,
            BodySha256 = hash,
            BodyRef = bodyRef,
            Notes = notes,
            CreatedAt = now
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        using var tx = connection.BeginTransaction();

        const string sql = @"
            INSERT INTO prompt_versions (id, template_id, version_no, body_sha256, body_ref, notes, created_at)
            VALUES (@Id, @TemplateId, @VersionNo, @BodySha256, @BodyRef, @Notes, @CreatedAt);
        ";
        await connection.ExecuteAsync(sql, version, transaction: tx);

        const string updateTemplateSql = @"
            UPDATE prompt_templates
            SET current_version_id = @VersionId, updated_at = @Now
            WHERE id = @TemplateId;
        ";
        await connection.ExecuteAsync(updateTemplateSql, new { VersionId = id, Now = now, TemplateId = templateId }, transaction: tx);

        tx.Commit();
        return version;
    }

    public async Task<AgentRunRecord> RecordAgentRunAsync(
        string agentId,
        string agentVersion,
        string? promptVersionId,
        string modelId,
        string modelParamsHash,
        string inputHash,
        CancellationToken ct = default)
    {
        // SPEC/38, D-004: "An unprompted run — one with no pinned version — cannot start."
        if (string.IsNullOrWhiteSpace(promptVersionId))
        {
            throw new AmccaException(
                AmccaErrors.Ai004,
                ErrorCategory.Validation,
                $"Unprompted agent run rejected. Every agent run MUST pin an immutable prompt_version_id (SPEC/38, D-004).");
        }

        var id = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        var run = new AgentRunRecord
        {
            Id = id,
            AgentId = agentId,
            AgentVersion = agentVersion,
            PromptVersionId = promptVersionId,
            ModelId = modelId,
            ModelParamsHash = modelParamsHash,
            InputHash = inputHash,
            OutputValid = false,
            State = "RUNNING",
            Cost = "0.00",
            StartedAt = now,
            CompletedAt = null
        };

        using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
        const string sql = @"
            INSERT INTO agent_runs (
                id, agent_id, agent_version, prompt_version_id, model_id,
                model_params_hash, input_hash, output_valid, state, cost,
                started_at, completed_at
            ) VALUES (
                @Id, @AgentId, @AgentVersion, @PromptVersionId, @ModelId,
                @ModelParamsHash, @InputHash, @OutputValid, @State, @Cost,
                @StartedAt, @CompletedAt
            );
        ";
        await connection.ExecuteAsync(sql, new
        {
            run.Id,
            run.AgentId,
            run.AgentVersion,
            run.PromptVersionId,
            run.ModelId,
            run.ModelParamsHash,
            run.InputHash,
            OutputValid = run.OutputValid ? 1 : 0,
            run.State,
            run.Cost,
            run.StartedAt,
            run.CompletedAt
        });

        return run;
    }
}
