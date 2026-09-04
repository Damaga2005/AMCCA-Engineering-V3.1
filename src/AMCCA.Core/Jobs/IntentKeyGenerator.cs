using System;
using System.Security.Cryptography;
using System.Text;

namespace AMCCA.Core.Jobs;

public static class IntentKeyGenerator
{
    public static string GenerateKey(string operationType, string stableEntityId, int intentVersion)
    {
        // SPEC/15: "A key is derived deterministically from operation_type + stable_entity_id + intent_version.
        // It is a pure function of those inputs. It is never random, never time-based..."
        var raw = $"{operationType.Trim().ToLowerInvariant()}:{stableEntityId.Trim()}:{intentVersion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFingerprint(string target, string payload)
    {
        // SPEC/15: "The fingerprint is a hash of the exact request body and target."
        var raw = $"{target.Trim()}|{payload.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
