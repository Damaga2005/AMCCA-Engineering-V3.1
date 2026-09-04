namespace AMCCA.Core.Database;

public class MigrationRecord
{
    public long Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public string AppliedAt { get; set; } = string.Empty;
    public string AppliedBy { get; set; } = string.Empty;
    public string? RollbackSqlRef { get; set; }
}

public record MigrationReport(
    int AppliedCount,
    string Message);

public record RollbackReport(
    int RolledBackCount,
    string Message);
