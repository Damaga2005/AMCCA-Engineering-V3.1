using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace AMCCA.Core.Diagnostics;

/// <summary>
/// Runtime counters for the orchestrator and worker pool, on the <c>AMCCA.Orchestrator</c> meter. No
/// exporter is wired — <c>dotnet-counters</c>, an OpenTelemetry SDK, or any MeterListener can read them.
/// </summary>
public static class AmccaMetrics
{
    public const string MeterName = "AMCCA.Orchestrator";

    private static readonly Meter Meter = new(MeterName, "1.0");

    public static readonly Counter<long> ProductionTransitions =
        Meter.CreateCounter<long>("amcca.production.transitions", unit: "{transition}", description: "State transitions the orchestrator committed.");

    public static readonly Counter<long> JobsProcessed =
        Meter.CreateCounter<long>("amcca.jobs.processed", unit: "{job}", description: "Jobs the worker pool finished, tagged by outcome.");

    public static readonly Counter<long> OrchestratorErrors =
        Meter.CreateCounter<long>("amcca.orchestrator.errors", unit: "{error}", description: "Productions the orchestrator failed to advance in a tick.");

    public static void CountTransition(string toState)
        => ProductionTransitions.Add(1, new KeyValuePair<string, object?>("to_state", toState));

    public static void CountJob(string outcome)
        => JobsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
