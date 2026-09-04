using System;

namespace AMCCA.Core.Memory;

public record MemoryRecord(
    string Id,
    string Scope,
    string Key,
    string ValueJson,
    string? EvidenceRef,
    double Confidence,
    string SchemaVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record MemoryQuery(
    string Scope,
    string QueryText,
    double MinConfidence = 0.5,
    bool AutonomousDecision = true,
    int MaxTokens = 500,
    double HalfLifeDays = 30.0,
    bool ExcludeFailedProductions = true
);

public record MemorySearchResult(
    MemoryRecord Record,
    double Similarity,
    double DecayedConfidence,
    double CompositeScore
);
