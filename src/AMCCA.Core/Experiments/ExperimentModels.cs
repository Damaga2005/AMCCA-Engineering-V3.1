using System;
using System.Collections.Generic;

namespace AMCCA.Core.Experiments;

public record Experiment(
    string Id,
    string Hypothesis,
    string State,
    string Metric,
    int MinSample,
    DateTime? StartedAt,
    DateTime? ConcludedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record ExperimentVariant(
    string Id,
    string ExperimentId,
    string Label,
    string ParametersJson,
    string? ProductionId,
    string? ResultJson
);

public record ExperimentAnalysis(
    string ExperimentId,
    int TotalSampleSize,
    bool MeetsMinSample,
    double PValue,
    double EffectSize,
    bool IsStatisticallySignificant,
    string? WinningVariantLabel,
    string Recommendation,
    double? EmittedMemoryConfidence
);
