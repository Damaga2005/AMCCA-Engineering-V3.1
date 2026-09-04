using System.Collections.Generic;

namespace AMCCA.Core.Preflight;

public enum PreflightStatus
{
    Pass,
    Degraded,
    Abort,
    Halted
}

public class PreflightReport
{
    public PreflightStatus Status { get; set; } = PreflightStatus.Pass;
    public bool IsStartupPermitted => Status is PreflightStatus.Pass or PreflightStatus.Degraded;
    public List<string> FailureDetails { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
