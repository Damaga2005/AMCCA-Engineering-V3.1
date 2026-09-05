using System.Collections.Generic;
using System.Linq;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.QA;

/// <summary>
/// A named set of QA thresholds. <c>default</c> is the base from <c>policy.qa</c>; any other profile is
/// a stricter platform profile that raises one or both thresholds (SPEC/35: "A stricter platform profile
/// may raise thresholds; nothing may lower them").
/// </summary>
public sealed record QaThresholdProfile(string Id, double OverallMin, double CriticalMin);

/// <summary>
/// Resolves a <c>qa_reports.threshold_profile_id</c> to the thresholds <see cref="QaVerdictEvaluator"/>
/// must apply. An unknown id, or a configured profile that lowers a threshold below the base, is a
/// <c>AMCCA-QA-003</c> — the "named QA threshold-profile lookup" failure reserved in SPEC/05.
/// </summary>
public sealed class QaThresholdProfileRegistry
{
    public const string DefaultProfileId = "default";

    private readonly QaThresholdProfile _baseProfile;
    private readonly IReadOnlyDictionary<string, QaThresholdProfile> _stricter;

    public QaThresholdProfileRegistry(
        double baseOverallMin,
        double baseCriticalMin,
        IEnumerable<QaThresholdProfile>? stricterProfiles = null)
    {
        _baseProfile = new QaThresholdProfile(DefaultProfileId, baseOverallMin, baseCriticalMin);

        var map = new Dictionary<string, QaThresholdProfile>();
        foreach (var p in stricterProfiles ?? Enumerable.Empty<QaThresholdProfile>())
        {
            if (string.Equals(p.Id, DefaultProfileId, System.StringComparison.Ordinal))
            {
                throw new AmccaException(
                    AmccaErrors.Qa003,
                    ErrorCategory.Internal,
                    "A QA threshold profile cannot be named 'default'; that id is reserved for the base thresholds from policy.qa.");
            }

            // SPEC/35: a profile may only raise thresholds. One that lowers either is invalid.
            if (p.OverallMin < baseOverallMin || p.CriticalMin < baseCriticalMin)
            {
                throw new AmccaException(
                    AmccaErrors.Qa003,
                    ErrorCategory.Internal,
                    $"QA threshold profile '{p.Id}' lowers a threshold below the base " +
                    $"(overall {p.OverallMin} < {baseOverallMin} or critical {p.CriticalMin} < {baseCriticalMin}). " +
                    "SPEC/35: a stricter platform profile may raise thresholds; nothing may lower them.");
            }

            map[p.Id] = p;
        }
        _stricter = map;
    }

    /// <summary>Base thresholds only, no stricter profiles.</summary>
    public static QaThresholdProfileRegistry Base(double overallMin, double criticalMin)
        => new(overallMin, criticalMin);

    /// <summary>
    /// The thresholds for <paramref name="profileId"/>. Null/empty or <c>default</c> yields the base;
    /// any other id must have been registered or this throws <c>AMCCA-QA-003</c>.
    /// </summary>
    public QaThresholdProfile Resolve(string? profileId)
    {
        if (string.IsNullOrEmpty(profileId) || string.Equals(profileId, DefaultProfileId, System.StringComparison.Ordinal))
        {
            return _baseProfile;
        }

        if (_stricter.TryGetValue(profileId, out var profile))
        {
            return profile;
        }

        var known = string.Join(", ", new[] { DefaultProfileId }.Concat(_stricter.Keys));
        throw new AmccaException(
            AmccaErrors.Qa003,
            ErrorCategory.Internal,
            $"Unknown QA threshold profile '{profileId}'. Known profiles: {known}.");
    }
}
