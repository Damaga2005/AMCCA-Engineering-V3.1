using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AMCCA.Core.Configuration;

public class AmccaConfig
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "3.1.0";

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "DEVELOPMENT";

    [JsonPropertyName("autonomy_mode")]
    public string AutonomyMode { get; set; } = "MANUAL";

    [JsonPropertyName("publishing_enabled")]
    public bool PublishingEnabled { get; set; }

    [JsonPropertyName("dry_run")]
    public bool DryRun { get; set; } = true;

    [JsonPropertyName("data_root")]
    public string DataRoot { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "EUR";

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();

    [JsonPropertyName("budgets")]
    public BudgetsConfig Budgets { get; set; } = new();

    [JsonPropertyName("storage")]
    public StorageConfig Storage { get; set; } = new();

    [JsonPropertyName("providers")]
    public ProvidersConfig Providers { get; set; } = new();

    [JsonPropertyName("platforms")]
    public Dictionary<string, PlatformConfig> Platforms { get; set; } = new();

    [JsonPropertyName("policy")]
    public PolicyConfig? Policy { get; set; }
}

public class LoggingConfig
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "Information";

    [JsonPropertyName("retention_days")]
    public int? RetentionDays { get; set; }
}

public class BudgetsConfig
{
    [JsonPropertyName("per_production")]
    public string PerProduction { get; set; } = "5.000000";

    [JsonPropertyName("per_rework")]
    public string PerRework { get; set; } = "2.000000";

    [JsonPropertyName("per_recovery")]
    public string PerRecovery { get; set; } = "1.000000";

    [JsonPropertyName("daily")]
    public string Daily { get; set; } = "25.000000";

    [JsonPropertyName("monthly")]
    public string Monthly { get; set; } = "300.000000";

    [JsonPropertyName("warn_percent")]
    public int WarnPercent { get; set; } = 70;

    [JsonPropertyName("pause_percent")]
    public int PausePercent { get; set; } = 85;

    [JsonPropertyName("block_percent")]
    public int BlockPercent { get; set; } = 100;
}

public class StorageConfig
{
    [JsonPropertyName("minimum_free_gb")]
    public int MinimumFreeGb { get; set; } = 20;

    [JsonPropertyName("temp_retention_hours")]
    public int TempRetentionHours { get; set; } = 24;

    [JsonPropertyName("cache_retention_days")]
    public int CacheRetentionDays { get; set; } = 7;
}

public class ProvidersConfig
{
    [JsonPropertyName("gateway")]
    public GatewayConfig Gateway { get; set; } = new();
}

public class GatewayConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "omnirouters";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("api_key_secret_ref")]
    public string? ApiKeySecretRef { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("capabilities_verified")]
    public bool CapabilitiesVerified { get; set; }
}

public class PlatformConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("synthetic_label_required")]
    public bool SyntheticLabelRequired { get; set; } = true;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();
}

public class PolicyConfig
{
    [JsonPropertyName("research")]
    public Dictionary<string, object>? Research { get; set; }

    [JsonPropertyName("qa")]
    public Dictionary<string, object>? Qa { get; set; }

    [JsonPropertyName("rework")]
    public Dictionary<string, object>? Rework { get; set; }

    [JsonPropertyName("reconcile")]
    public Dictionary<string, object>? Reconcile { get; set; }
}
