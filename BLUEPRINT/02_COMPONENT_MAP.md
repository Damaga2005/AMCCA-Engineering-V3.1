# Component Map

## Control plane

| Component | Responsibility | Must not |
|---|---|---|
| PolicyEngine | Evaluate a protected action against ordered rules; emit a `policy_decisions` row | Perform side effects; be consulted after the fact |
| ApprovalService | Request, record, expire and consume scoped approvals | Grant an approval that outlives its scope |
| AutonomyService | Resolve the effective autonomy for an action from mode, matrix and agent ceiling | Let an agent influence the result |
| BudgetService | Reserve, settle, release; enforce window caps | Reserve without a single conditional statement |
| SecretStore | Store and retrieve credentials via DPAPI/Credential Manager | Return a secret into a loggable structure |
| KillSwitchService | Persist and enforce operational halt modes | Allow a non-operator to clear `EMERGENCY_STOP` |
| ConfigService | Load and schema-validate configuration | Start with an invalid or secret-bearing configuration |

## Execution plane

| Component | Responsibility | Must not |
|---|---|---|
| Scheduler | Select and enqueue eligible work under limits | Dispatch beyond reserved budget or disk headroom |
| LeaseManager | Atomic claim, heartbeat, expiry, fence tokens | Use read-then-write claiming |
| WorkerSupervisor | Lifecycle of worker pool; per-provider concurrency caps | Exceed configured per-provider limits |
| Orchestrator | Commit production state, events, transitions | Commit a transition absent from `SPEC/13` |
| AgentRuntime | Execute agents within contract limits | Permit a tool outside `allowed_tools` |
| ToolRegistry | Type, permission and side-effect class per tool | Execute `EXTERNAL_UNSAFE` without a committed intent |
| MediaWorker | FFmpeg invocation, probing, rendering | Concatenate strings into a shell |
| ReconciliationService | Resolve `UNKNOWN` intents against authoritative sources | Guess; mark `CONFIRMED` without evidence |

## Domain engines

ResearchEngine, TrendEngine, NicheEngine, OpportunityScorer, StrategyEngine, HookEngine,
ScriptEngine, StoryboardEngine, AssetEngine, VoiceEngine, RenderEngine, QaEngine, RightsEngine,
DuplicateEngine, DisclosureEngine, ComplianceEngine, PublicationHub, AttributionEngine, RevenueEngine,
MemoryEngine, ExperimentEngine.

Every engine is deterministic code that *may call* agents. No engine is an agent.

## Persistence and evidence

DatabaseLayer (Dapper, migrations, transactions), EventStore (append-only), AuditStore (separate),
ArtifactStore (hash-addressed), ManifestService, DagService (lineage, invalidation, cycle rejection),
BackupService, ExportImportService, RetentionService.

## Ports

`IProviderGateway`, `IPlatformAdapter`, `IResearchSource`, `IAffiliateProvider`, `ISecretStore`,
`IClock`, `IFileSystem`.

`IClock` and `IFileSystem` are ports for a practical reason: without them, the crash-recovery,
lease-expiry and retention tests cannot be written deterministically, and those are precisely the tests
that matter most.
