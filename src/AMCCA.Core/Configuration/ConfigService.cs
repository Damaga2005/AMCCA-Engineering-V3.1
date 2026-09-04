using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;
using Json.Schema;
using YamlDotNet.Serialization;

namespace AMCCA.Core.Configuration;

public class ConfigService
{
    private readonly JsonSchema _schema;

    public ConfigService(string schemaJson)
    {
        _schema = JsonSchema.FromText(schemaJson);
    }

    public AmccaConfig LoadFromYaml(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithAttemptingUnquotedStringTypeDeserialization()
            .Build();
        var yamlObject = deserializer.Deserialize<object>(new StringReader(yamlContent));

        var jsonString = JsonSerializer.Serialize(yamlObject);
        return LoadFromJson(jsonString);
    }

    public AmccaConfig LoadFromJson(string jsonContent)
    {
        JsonNode? jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(jsonContent);
        }
        catch (JsonException ex)
        {
            throw new AmccaException(
                AmccaErrors.Cfg001,
                ErrorCategory.Configuration,
                $"Invalid JSON format: {ex.Message}",
                innerException: ex);
        }

        if (jsonNode == null)
        {
            throw new AmccaException(
                AmccaErrors.Cfg001,
                ErrorCategory.Configuration,
                "Configuration document is empty.");
        }

        // 1. Scan for literal secrets in any field named *_secret_ref (AMCCA-SEC-002, SPEC/02, SPEC/03, SPEC/49)
        ScanForLiteralSecrets(jsonNode);

        // 2. Validate against JSON Schema Draft 2020-12 (D-004, SPEC/03, AMCCA-CFG-001)
        var evaluationResults = _schema.Evaluate(jsonNode, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });

        if (!evaluationResults.IsValid)
        {
            var errorList = new System.Collections.Generic.List<string>();
            foreach (var detail in evaluationResults.Details)
            {
                if (!detail.IsValid && detail.Errors != null)
                {
                    foreach (var (k, v) in detail.Errors)
                    {
                        errorList.Add($"{detail.InstanceLocation} [{k}]: {v}");
                    }
                }
            }
            var errors = errorList.Count > 0 ? string.Join("; ", errorList) : "Schema validation failed";
            throw new AmccaException(
                AmccaErrors.Cfg001,
                ErrorCategory.Configuration,
                $"Configuration failed schema validation: {errors}");
        }

        // 3. Deserialize to strongly typed POCO
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false
        };
        var config = jsonNode.Deserialize<AmccaConfig>(options)
            ?? throw new AmccaException(
                AmccaErrors.Cfg001,
                ErrorCategory.Configuration,
                "Failed to deserialize configuration to domain model.");

        // 4. Cross-field consistency rules (SPEC/03, SPEC/49)
        ValidateCrossFieldConsistency(config);

        return config;
    }

    private static void ScanForLiteralSecrets(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, val) in obj)
            {
                if (key.EndsWith("_secret_ref", StringComparison.OrdinalIgnoreCase) && val != null)
                {
                    var refValue = val.GetValue<string?>();
                    if (!string.IsNullOrEmpty(refValue) && !SecretReference.TryParse(refValue, out _))
                    {
                        throw new AmccaException(
                            AmccaErrors.Sec002,
                            ErrorCategory.Security,
                            $"Literal credential or malformed secret reference found in '{key}': '{refValue}'. Configuration must use 'secret://<vault>/<name>'.");
                    }
                }
                else if (val != null)
                {
                    ScanForLiteralSecrets(val);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null) ScanForLiteralSecrets(item);
            }
        }
    }

    private static void ValidateCrossFieldConsistency(AmccaConfig config)
    {
        // Rule 1: budgets.daily <= budgets.monthly
        if (decimal.TryParse(config.Budgets.Daily, NumberStyles.Number, CultureInfo.InvariantCulture, out var daily) &&
            decimal.TryParse(config.Budgets.Monthly, NumberStyles.Number, CultureInfo.InvariantCulture, out var monthly))
        {
            if (daily > monthly)
            {
                throw new AmccaException(
                    AmccaErrors.Cfg004,
                    ErrorCategory.Configuration,
                    $"Budget consistency violation: daily cap ({daily}) cannot exceed monthly cap ({monthly}).");
            }
        }

        // Rule 2: warn_percent < pause_percent < block_percent <= 100
        var warn = config.Budgets.WarnPercent;
        var pause = config.Budgets.PausePercent;
        var block = config.Budgets.BlockPercent;
        if (!(warn < pause && pause < block && block <= 100))
        {
            throw new AmccaException(
                AmccaErrors.Cfg004,
                ErrorCategory.Configuration,
                $"Budget threshold ordering violation: must satisfy warn ({warn}) < pause ({pause}) < block ({block}) <= 100.");
        }

        // Rule 3: budgets.per_production <= budgets.daily
        if (decimal.TryParse(config.Budgets.PerProduction, NumberStyles.Number, CultureInfo.InvariantCulture, out var perProd))
        {
            if (perProd > daily)
            {
                throw new AmccaException(
                    AmccaErrors.Cfg004,
                    ErrorCategory.Configuration,
                    $"Budget consistency violation: per_production limit ({perProd}) cannot exceed daily cap ({daily}).");
            }
        }

        // Rule 4: autonomy_mode = AUTONOMOUS requires providers.gateway.capabilities_verified = true
        if (string.Equals(config.AutonomyMode, "AUTONOMOUS", StringComparison.OrdinalIgnoreCase))
        {
            if (!config.Providers.Gateway.CapabilitiesVerified)
            {
                throw new AmccaException(
                    AmccaErrors.Cfg001,
                    ErrorCategory.Configuration,
                    "Autonomy mode AUTONOMOUS is forbidden while providers.gateway.capabilities_verified is false (D-028).");
            }
        }

        // Rule 5: publishing_enabled = true with environment = DEVELOPMENT is rejected
        if (config.PublishingEnabled && string.Equals(config.Environment, "DEVELOPMENT", StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Cfg001,
                ErrorCategory.Configuration,
                "Autonomous publishing cannot be enabled in DEVELOPMENT environment (D-020).");
        }
    }
}
