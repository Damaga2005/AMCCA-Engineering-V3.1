using System;
using System.Collections.Generic;

namespace AMCCA.Core.Genome;

public record ContentGenome(
    string HookPattern,
    string PacingProfile,
    string VoiceProfile,
    string VisualStyle,
    int DurationSeconds,
    string DisclosurePlacement,
    double CutFrequencyPerMinute,
    double EnergyScore,
    bool IsSynthetic,
    IReadOnlyDictionary<string, string>? ExtraTraits = null
);

public record ChannelInvariants(
    int MaxDurationSeconds = 60,
    int MinDurationSeconds = 15,
    double MinCutFrequency = 5.0,
    double MaxCutFrequency = 30.0,
    bool RequiresSyntheticDisclosure = true,
    IReadOnlyList<string>? AllowedPacingProfiles = null
);

public record GenomeMutationResult(
    bool Success,
    ContentGenome? MutatedGenome,
    string? MutatedDimension,
    double Drift,
    string? ErrorMessage
);
