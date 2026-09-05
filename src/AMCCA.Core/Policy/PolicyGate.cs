using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Database;
using AMCCA.Core.Events;
using Dapper;

namespace AMCCA.Core.Policy;

public sealed record PolicyDecisionRecord(string DecisionId, PolicyDecisionResult Result)
{
    public bool IsAllowed => string.Equals(Result.Decision, "ALLOW", StringComparison.Ordinal);
}

/// <summary>
/// SPEC/08: evaluates a protected action through <see cref="PolicyEngine"/> and persists the outcome to
/// <c>policy_decisions</c> (pointing at a <c>policy_versions</c> row for the compiled-in ruleset), plus
/// an <c>audit_log</c> row so the block is explainable in the Production Inspector (SPEC/60 obligation
/// 4). Until a policy-bundle loader exists, every decision references one built-in policy version whose
/// checksum is the signature of the compiled rules.
/// </summary>
public sealed class PolicyGate
{
    private const string BuiltInPolicyId = "policy_builtin_spec08";
    private const string BuiltInPolicyKey = "builtin.spec08";
    private const string BuiltInPolicyVersionId = "policyver_builtin_spec08_v1";
    private const string BuiltInPolicyBodyRef = "builtin://policy-engine/spec-08/v1";

    // Bump this string whenever PolicyEngine's rule set changes materially, so decisions recorded
    // under the old rules are distinguishable by checksum.
    private const string BuiltInRulesetSignature =
        "spec08:order=emergency,security,safety,rights,compliance,provider,budget,approval,allow,fail-closed;v=1";

    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly PolicyEngine _policyEngine;
    private readonly IAuditStore _auditStore;
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;

    public PolicyGate(DatabaseConnectionFactory connectionFactory, PolicyEngine policyEngine, IAuditStore auditStore)
    {
        _connectionFactory = connectionFactory;
        _policyEngine = policyEngine;
        _auditStore = auditStore;
    }

    public async Task<PolicyDecisionRecord> EvaluateAndRecordAsync(
        PolicyEvaluationContext context, string correlationId, string actorType = "ORCHESTRATOR", CancellationToken ct = default)
    {
        var result = _policyEngine.EvaluateAction(context);
        var versionId = await EnsureBuiltInPolicyVersionAsync(ct);

        var decisionId = UlidGenerator.NewUlid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var inputsHash = Sha256(JsonSerializer.Serialize(context));

        using (var connection = await _connectionFactory.CreateOpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO policy_decisions
                    (id, production_id, action, decision, rule_key, policy_version_id, inputs_hash, correlation_id, decided_at)
                  VALUES (@Id, @ProductionId, @Action, @Decision, @RuleKey, @VersionId, @InputsHash, @CorrelationId, @Now);",
                new
                {
                    Id = decisionId,
                    context.ProductionId,
                    context.Action,
                    result.Decision,
                    RuleKey = result.RuleKey,
                    VersionId = versionId,
                    InputsHash = inputsHash,
                    CorrelationId = correlationId,
                    Now = now,
                }, cancellationToken: ct));
        }

        // SPEC/55: every protected-action decision is audited. A non-ALLOW decision also carries the
        // reason code and this policy_decision_id so the Inspector's block panel can resolve them.
        var outcome = result.Decision switch
        {
            "ALLOW" => "ALLOWED",
            "REQUIRE_APPROVAL" => "DENIED",
            _ => "BLOCKED",
        };
        await _auditStore.AppendAuditAsync(new AuditRecord(
            AuditId: UlidGenerator.NewUlid(),
            Action: "policy.evaluate_protected_action",
            ActorType: actorType,
            ActorId: "AMCCA.PolicyGate",
            SubjectType: "production",
            SubjectId: context.ProductionId,
            ProductionId: context.ProductionId,
            Outcome: outcome,
            PolicyDecisionId: decisionId,
            ReasonCode: result.ReasonCode,
            CorrelationId: correlationId,
            SchemaVersion: "3.1.0",
            OccurredAt: now), ct);

        return new PolicyDecisionRecord(decisionId, result);
    }

    /// <summary>Idempotently seeds the built-in policy + version row and returns its id.</summary>
    public async Task<string> EnsureBuiltInPolicyVersionAsync(CancellationToken ct = default)
    {
        if (_seeded)
        {
            return BuiltInPolicyVersionId;
        }

        await _seedLock.WaitAsync(ct);
        try
        {
            if (_seeded)
            {
                return BuiltInPolicyVersionId;
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            var checksum = Sha256(BuiltInRulesetSignature);

            using var connection = await _connectionFactory.CreateOpenConnectionAsync(ct);
            using var tx = connection.BeginTransaction();

            await connection.ExecuteAsync(new CommandDefinition(
                @"INSERT OR IGNORE INTO policies (id, key, current_version_id, description, created_at, updated_at)
                  VALUES (@Id, @Key, @VersionId, @Desc, @Now, @Now);",
                new
                {
                    Id = BuiltInPolicyId, Key = BuiltInPolicyKey, VersionId = BuiltInPolicyVersionId,
                    Desc = "Compiled-in SPEC/08 evaluation order (PolicyEngine). Replaced by a policy-bundle loader later.",
                    Now = now,
                }, transaction: tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(
                @"INSERT OR IGNORE INTO policy_versions
                    (id, policy_id, version_no, body_sha256, body_ref, activated_at, activated_by, created_at)
                  VALUES (@Id, @PolicyId, 1, @Sha, @Ref, @Now, 'SYSTEM', @Now);",
                new { Id = BuiltInPolicyVersionId, PolicyId = BuiltInPolicyId, Sha = checksum, Ref = BuiltInPolicyBodyRef, Now = now },
                transaction: tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE policies SET current_version_id = @VersionId, updated_at = @Now WHERE id = @Id AND current_version_id IS NULL;",
                new { VersionId = BuiltInPolicyVersionId, Now = now, Id = BuiltInPolicyId },
                transaction: tx, cancellationToken: ct));

            tx.Commit();
            _seeded = true;
            return BuiltInPolicyVersionId;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private static string Sha256(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
