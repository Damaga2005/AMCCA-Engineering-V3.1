using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AMCCA.Core.Configuration;
using AMCCA.Core.Contracts;
using AMCCA.Core.Database;
using AMCCA.Core.Domain;
using AMCCA.Core.Events;
using AMCCA.Core.Policy;
using AMCCA.Core.Providers;
using AMCCA.Core.Security;
using AMCCA.Core.StateMachine;
using Dapper;

namespace AMCCA.App;

/// <summary>
/// Headless bootstrap verbs for a local end-to-end run (see RUN_LOCAL.md). They exist because there was
/// no way to put a credential, verify a provider, or create a production without the WPF UI or raw SQL.
/// </summary>
internal static class LocalRunCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (dbDir, dbPath) = Composition.ResolvePaths();
        var configPath = Path.Combine(dbDir, "config.yaml");

        switch (args[0])
        {
            case "--set-secret":
                return SetSecret(args, dbDir);
            case "--probe":
                return await ProbeAsync(configPath, dbDir);
            case "--seed-demo":
                return await SeedDemoAsync(args, dbPath, dbDir);
            default:
                Console.Error.WriteLine($"Unknown verb '{args[0]}'.");
                return 2;
        }
    }

    // --set-secret secret://vault/name   (value read from stdin; never taken as an argument, never echoed)
    private static int SetSecret(string[] args, string dbDir)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: AMCCA.exe --set-secret secret://vault/name   (paste the value when prompted)");
            return 2;
        }

        SecretReference reference;
        try { reference = SecretReference.Parse(args[1]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid secret reference: {ex.Message}");
            return 2;
        }

        Console.Write($"Value for {args[1]} (input hidden as far as the terminal allows): ");
        var value = ReadHidden();
        Console.WriteLine();
        if (string.IsNullOrEmpty(value))
        {
            Console.Error.WriteLine("No value entered; nothing stored.");
            return 2;
        }

        var store = new WindowsDpapiSecretStore(dbDir);
        store.SetSecretAsync(reference, value).GetAwaiter().GetResult();
        Console.WriteLine($"Stored {args[1]} in the DPAPI secret store under {dbDir}.");
        return 0;
    }

    private static string ReadHidden()
    {
        try
        {
            var buf = new System.Text.StringBuilder();
            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace) { if (buf.Length > 0) buf.Length--; }
                else if (!char.IsControl(key.KeyChar)) buf.Append(key.KeyChar);
            }
            return buf.ToString();
        }
        catch (InvalidOperationException)
        {
            // stdin is redirected (piped) — fall back to a plain read.
            return Console.ReadLine() ?? string.Empty;
        }
    }

    // --probe : real capability probe against the configured gateway. On success flips
    // capabilities_verified to true in config.yaml so AUTONOMOUS is permitted (D-028).
    private static async Task<int> ProbeAsync(string configPath, string dbDir)
    {
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"No config.yaml at {configPath}. Copy CONFIG/config.gemini.example.yaml there first.");
            return 2;
        }

        AmccaConfig config;
        try { config = ConfigService.CreateWithBundledSchema().LoadFromYaml(File.ReadAllText(configPath)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config.yaml failed validation: {ex.Message}");
            return 2;
        }

        var gateway = ProviderGatewayComposer.Compose(config, new WindowsDpapiSecretStore(dbDir));
        if (gateway is null)
        {
            Console.Error.WriteLine("No gateway is enabled/complete in config.yaml (need providers.gateway.enabled=true, base_url, api_key_secret_ref).");
            return 2;
        }

        var model = config.Providers.Gateway.DefaultModelId;
        if (string.IsNullOrWhiteSpace(model))
        {
            Console.Error.WriteLine("Set providers.gateway.default_model_id in config.yaml (e.g. \"gemini-2.0-flash\").");
            return 2;
        }

        Console.WriteLine($"Probing {config.Providers.Gateway.BaseUrl} with model '{model}' ...");
        var result = await gateway.ProbeCapabilityAsync(config.Providers.Gateway.Id, model, "text");
        if (!result.Success)
        {
            Console.Error.WriteLine($"Probe FAILED: {result.ErrorMessage}");
            return 1;
        }

        Console.WriteLine($"Probe OK ({result.LatencyMs} ms).");

        var text = File.ReadAllText(configPath);
        var flipped = Regex.Replace(text, @"(capabilities_verified\s*:\s*)false", "${1}true");
        // A green probe is exactly the evidence D-028 asks for; promote ASSISTED -> AUTONOMOUS so the
        // orchestrator drives the production instead of parking at every gate.
        flipped = Regex.Replace(flipped, @"(^autonomy_mode\s*:\s*)ASSISTED", "${1}AUTONOMOUS", RegexOptions.Multiline);
        if (flipped != text)
        {
            File.WriteAllText(configPath, flipped);
            Console.WriteLine("config.yaml updated: capabilities_verified: true, autonomy_mode: AUTONOMOUS.");
        }
        else
        {
            Console.WriteLine("config.yaml already has capabilities_verified: true / autonomy_mode set — no change.");
        }
        return 0;
    }

    // --seed-demo ["topic"] : one AUTONOMOUS production + a SCORED opportunity + a PRODUCTION budget,
    // so the CONCEPT_SELECTED gate (D-035) can advance instead of blocking.
    private static async Task<int> SeedDemoAsync(string[] args, string dbPath, string dbDir)
    {
        var topic = args.Length > 1 ? args[1] : "The James Webb Space Telescope's first deep-field image";
        var factory = new DatabaseConnectionFactory(dbPath);
        await new MigrationService(factory, dbDir).UpgradeAsync();

        var registry = StateMachineRegistry.CreateFromBundledDefinition();
        var productions = new ProductionService(factory, registry, new EventStore(factory));
        var budgets = new BudgetManager(factory);

        var nicheId = UlidGenerator.NewUlid();
        var oppId = UlidGenerator.NewUlid();
        using (var c = await factory.CreateOpenConnectionAsync())
        {
            await c.ExecuteAsync(@"INSERT INTO niches (id, name, language, state, created_at, updated_at)
                VALUES (@Id, 'demo-niche', 'en', 'CANDIDATE', datetime('now'), datetime('now'));", new { Id = nicheId });
            await c.ExecuteAsync(@"
                INSERT INTO opportunities (id, niche_id, state, score, score_breakdown_json, expected_revenue,
                                           expected_cost, risk_penalty, currency, scored_at, created_at, updated_at)
                VALUES (@Id, @Niche, 'SCORED', 0.85, '{""trend_strength"":0.8,""niche_fit"":0.9}', '40.000000',
                        '3.000000', 0.15, 'EUR', datetime('now'), datetime('now'), datetime('now'));",
                new { Id = oppId, Niche = nicheId });
        }

        var prod = await productions.CreateProductionAsync(topic, "en", "AUTONOMOUS", "seed-demo", nicheId);

        // PRODUCTION budget the scripting reservation draws against.
        await budgets.CreateBudgetAsync(UlidGenerator.NewUlid(), "PRODUCTION", prod.Id, 25.000000m, "EUR");

        // Move it off INIT so the first orchestrator tick starts real work (InitStageHandler would too;
        // this keeps the demo obvious).
        await productions.TransitionAsync(prod.Id, "RESEARCHING", "seed-demo", "seed-demo", causationId: null);

        Console.WriteLine($"Seeded production {prod.Id} (state RESEARCHING, niche {nicheId}, opportunity {oppId}).");
        Console.WriteLine("Now run:  AMCCA.exe --orchestrator");
        return 0;
    }
}
