using System;
using System.Collections.Generic;
using System.Linq;

namespace AMCCA.Core.Genome;

public class GenomeMutationService
{
    private static readonly HashSet<string> ValidPacingProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAST", "BALANCED", "DOCUMENTARY", "DYNAMIC"
    };

    public GenomeMutationResult MutateSingleDimension(
        ContentGenome baseline,
        string dimension,
        object newValue,
        ChannelInvariants invariants)
    {
        // 1. Single dimension mutation enforcement (SPEC/48)
        ContentGenome candidate;
        try
        {
            candidate = ApplyMutation(baseline, dimension, newValue);
        }
        catch (Exception ex)
        {
            return new GenomeMutationResult(false, null, dimension, 0.0, $"Invalid mutation on {dimension}: {ex.Message}");
        }

        // 2. Validate dimensional compatibility & channel invariants
        var invariantCheck = ValidateInvariants(candidate, invariants);
        if (!invariantCheck.IsValid)
        {
            return new GenomeMutationResult(false, null, dimension, 0.0, invariantCheck.Error);
        }

        // 3. Compute deterministic drift
        var drift = ComputeDrift(baseline, candidate);

        return new GenomeMutationResult(true, candidate, dimension, drift, null);
    }

    public GenomeMutationResult ValidateVariantMutation(
        ContentGenome baseline,
        ContentGenome candidate,
        ChannelInvariants invariants)
    {
        // SPEC/48: "Variants differ in one genome dimension at a time; a multi-dimensional variant produces a result nobody can attribute"
        var changedDimensions = GetChangedDimensions(baseline, candidate);

        if (changedDimensions.Count == 0)
        {
            return new GenomeMutationResult(false, null, null, 0.0, "Mutation must change at least one dimension");
        }

        if (changedDimensions.Count > 1)
        {
            return new GenomeMutationResult(false, null, string.Join(", ", changedDimensions), 0.0,
                $"SPEC/48 violation: multi-dimensional variant ({string.Join(", ", changedDimensions)}) produces an unattributable result");
        }

        var dimension = changedDimensions[0];
        var invariantCheck = ValidateInvariants(candidate, invariants);
        if (!invariantCheck.IsValid)
        {
            return new GenomeMutationResult(false, null, dimension, 0.0, invariantCheck.Error);
        }

        var drift = ComputeDrift(baseline, candidate);
        return new GenomeMutationResult(true, candidate, dimension, drift, null);
    }

    public double ComputeDrift(ContentGenome a, ContentGenome b)
    {
        double sumSquares = 0.0;

        // Categorical traits (distance 0 if equal, 1 if different)
        sumSquares += (a.HookPattern == b.HookPattern ? 0.0 : 1.0);
        sumSquares += (a.PacingProfile == b.PacingProfile ? 0.0 : 1.0);
        sumSquares += (a.VoiceProfile == b.VoiceProfile ? 0.0 : 1.0);
        sumSquares += (a.VisualStyle == b.VisualStyle ? 0.0 : 1.0);
        sumSquares += (a.DisclosurePlacement == b.DisclosurePlacement ? 0.0 : 1.0);

        // Continuous traits (normalized differences)
        double durationDiff = Math.Abs(a.DurationSeconds - b.DurationSeconds) / 60.0;
        sumSquares += Math.Pow(Math.Min(1.0, durationDiff), 2);

        double cutDiff = Math.Abs(a.CutFrequencyPerMinute - b.CutFrequencyPerMinute) / 30.0;
        sumSquares += Math.Pow(Math.Min(1.0, cutDiff), 2);

        double energyDiff = Math.Abs(a.EnergyScore - b.EnergyScore);
        sumSquares += Math.Pow(Math.Min(1.0, energyDiff), 2);

        // Normalize to [0.0, 1.0]
        return Math.Round(Math.Sqrt(sumSquares / 8.0), 4);
    }

    private static (bool IsValid, string? Error) ValidateInvariants(ContentGenome genome, ChannelInvariants invariants)
    {
        if (genome.DurationSeconds > invariants.MaxDurationSeconds)
        {
            return (false, $"Duration {genome.DurationSeconds}s exceeds channel maximum {invariants.MaxDurationSeconds}s");
        }

        if (genome.DurationSeconds < invariants.MinDurationSeconds)
        {
            return (false, $"Duration {genome.DurationSeconds}s below channel minimum {invariants.MinDurationSeconds}s");
        }

        if (genome.CutFrequencyPerMinute < invariants.MinCutFrequency || genome.CutFrequencyPerMinute > invariants.MaxCutFrequency)
        {
            return (false, $"Cut frequency {genome.CutFrequencyPerMinute} out of channel bounds [{invariants.MinCutFrequency}, {invariants.MaxCutFrequency}]");
        }

        if (genome.IsSynthetic && invariants.RequiresSyntheticDisclosure &&
            (string.IsNullOrWhiteSpace(genome.DisclosurePlacement) || genome.DisclosurePlacement.Equals("NONE", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, "SPEC/45 & channel invariant violation: synthetic content requires explicit disclosure placement");
        }

        if (invariants.AllowedPacingProfiles != null && invariants.AllowedPacingProfiles.Count > 0)
        {
            if (!invariants.AllowedPacingProfiles.Contains(genome.PacingProfile, StringComparer.OrdinalIgnoreCase))
            {
                return (false, $"Pacing profile {genome.PacingProfile} not allowed for channel/niche");
            }
        }

        return (true, null);
    }

    private static List<string> GetChangedDimensions(ContentGenome a, ContentGenome b)
    {
        var changes = new List<string>();

        if (a.HookPattern != b.HookPattern) changes.Add(nameof(ContentGenome.HookPattern));
        if (a.PacingProfile != b.PacingProfile) changes.Add(nameof(ContentGenome.PacingProfile));
        if (a.VoiceProfile != b.VoiceProfile) changes.Add(nameof(ContentGenome.VoiceProfile));
        if (a.VisualStyle != b.VisualStyle) changes.Add(nameof(ContentGenome.VisualStyle));
        if (a.DurationSeconds != b.DurationSeconds) changes.Add(nameof(ContentGenome.DurationSeconds));
        if (a.DisclosurePlacement != b.DisclosurePlacement) changes.Add(nameof(ContentGenome.DisclosurePlacement));
        if (Math.Abs(a.CutFrequencyPerMinute - b.CutFrequencyPerMinute) > 0.001) changes.Add(nameof(ContentGenome.CutFrequencyPerMinute));
        if (Math.Abs(a.EnergyScore - b.EnergyScore) > 0.001) changes.Add(nameof(ContentGenome.EnergyScore));
        if (a.IsSynthetic != b.IsSynthetic) changes.Add(nameof(ContentGenome.IsSynthetic));

        return changes;
    }

    private static ContentGenome ApplyMutation(ContentGenome b, string dimension, object newValue)
    {
        return dimension.ToLowerInvariant() switch
        {
            "hookpattern" => b with { HookPattern = Convert.ToString(newValue)! },
            "pacingprofile" => b with { PacingProfile = Convert.ToString(newValue)! },
            "voiceprofile" => b with { VoiceProfile = Convert.ToString(newValue)! },
            "visualstyle" => b with { VisualStyle = Convert.ToString(newValue)! },
            "durationseconds" => b with { DurationSeconds = Convert.ToInt32(newValue) },
            "disclosureplacement" => b with { DisclosurePlacement = Convert.ToString(newValue)! },
            "cutfrequencyperminute" => b with { CutFrequencyPerMinute = Convert.ToDouble(newValue) },
            "energyscore" => b with { EnergyScore = Convert.ToDouble(newValue) },
            _ => throw new ArgumentException($"Unknown genome dimension '{dimension}'")
        };
    }
}
