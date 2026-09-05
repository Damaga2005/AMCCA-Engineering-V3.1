namespace AMCCA.Core.Contracts;

public static class AmccaErrors
{
    // Configuration Domain
    public const string Cfg001 = "AMCCA-CFG-001"; // Configuration failed schema validation
    public const string Cfg004 = "AMCCA-CFG-004"; // Budget window consistency rule violated

    // Security Domain
    public const string Sec001 = "AMCCA-SEC-001"; // Security policy block
    public const string Sec002 = "AMCCA-SEC-002"; // Literal credential found in configuration
    public const string Sec003 = "AMCCA-SEC-003"; // SSRF guard rejected a research target
    public const string Sec004 = "AMCCA-SEC-004"; // Archive rejected: entry count, size or path validation failed

    // Database Domain
    public const string Db001 = "AMCCA-DB-001";   // Foreign keys or WAL not enabled
    public const string Db002 = "AMCCA-DB-002";   // Migration checksum mismatch
    public const string Db003 = "AMCCA-DB-003";   // SQLite busy beyond timeout

    // Storage Domain
    public const string Sto001 = "AMCCA-STO-001"; // Free storage below minimum threshold

    // State Machine Domain
    public const string Stm001 = "AMCCA-STM-001"; // Transition not listed in canonical matrix
    public const string Stm002 = "AMCCA-STM-002"; // Illegal resume from BLOCKED (must match blocked_from)
    public const string Stm003 = "AMCCA-STM-003"; // Transition from terminal state attempted

    // AI / Agent Domain
    public const string Ai001 = "AMCCA-AI-001";   // Model provider returned error
    public const string Ai002 = "AMCCA-AI-002";   // Rate limit exceeded on AI gateway
    public const string Ai003 = "AMCCA-AI-003";   // Agent output failed schema validation
    public const string Ai004 = "AMCCA-AI-004";   // Agent attempted to call a tool outside its allowed_tools set (blocked and audited)
    public const string Ai005 = "AMCCA-AI-005";   // Agent run exceeded timeout or max_cost ceiling

    // Research Domain
    public const string Res001 = "AMCCA-RES-001"; // Source count below policy minimum or missing backing claim
    public const string Res002 = "AMCCA-RES-002"; // Unsubstantiated material claim
    public const string Res003 = "AMCCA-RES-003"; // Reserved, unused: duplicate of Sec003 (SsrfValidator throws Sec003 for every SSRF/domain rejection)

    // QA Domain
    public const string Qa001 = "AMCCA-QA-001";   // QA check failed on critical dimension
    public const string Qa002 = "AMCCA-QA-002";   // Verdict set from AI-assisted check alone (prohibited)
    public const string Qa003 = "AMCCA-QA-003";   // Reserved, unused: no named threshold-profile lookup exists yet; QaVerdictEvaluator takes fixed caller-supplied minOverall/minCritical

    // Cost / Budget Domain
    public const string Bud002 = "AMCCA-BUD-002"; // Budget reservation refused; insufficient remaining budget
    public const string Cst002 = Bud002; // Alias for backward compatibility

    // Policy Domain
    public const string Pol001 = "AMCCA-POL-001"; // Policy evaluation rejected or failed
    public const string Pol003 = "AMCCA-POL-003"; // Operation refused: global or per-platform kill switch is active
    public const string Pol004 = "AMCCA-POL-004"; // Human approval required before entering protected state (ErrorCategory.UserActionRequired, not Security -- the operator, not an attacker, is who needs to act)

    // Job Domain
    public const string Job001 = "AMCCA-JOB-001"; // Lease expired mid-execution; fence token stale, work abandoned
    public const string Job002 = "AMCCA-JOB-002"; // Reserved, unused: duplicate idempotency key on enqueue. EnqueueJobAsync has no pre-check and a UNIQUE(idempotency_key) violation surfaces as a raw unwrapped SqliteException today, not this. (Previously misused for stale-lease heartbeat rejection, which is Job001's condition -- fixed.)
    public const string Job003 = "AMCCA-JOB-003"; // Job dead-lettered after max attempts: JobQueueEntry.ReasonCode when a job is DEAD_LETTER, and thrown by RequeueDeadLetterJobAsync when requeue is attempted on a job that is not (stale fence token is Job001, not this -- fixed)

    // Platform Domain
    public const string Plt001 = "AMCCA-PLT-001"; // Platform rejected the request
    public const string Plt002 = "AMCCA-PLT-002"; // Credential invalid or expired; re-authenticate
    public const string Plt003 = "AMCCA-PLT-003"; // Platform rate limit
}

public enum ErrorCategory
{
    Transient,
    RateLimited,
    Auth,
    Configuration,
    Validation,
    Provider,
    Platform,
    Media,
    Rights,
    Compliance,
    Policy,
    Budget,
    Storage,
    Security,
    UserActionRequired,
    UnknownExternalState,
    Internal
}
