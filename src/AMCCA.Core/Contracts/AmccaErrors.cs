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
