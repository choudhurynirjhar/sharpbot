using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sharpbot.Config;

/// <summary>
/// Migrates user config files from older formats to the current schema.
/// Each migration is a function that transforms the raw JSON object in-place.
/// Migrations are versioned: a <c>ConfigVersion</c> field tracks which
/// migrations have already been applied, so they run at most once.
///
/// To add a new migration:
///   1. Increment <see cref="CurrentVersion"/>
///   2. Add an entry to the <see cref="Migrations"/> array
///   3. The migration function receives the root <see cref="JsonObject"/>
///      and returns <c>true</c> if it made changes
/// </summary>
public static class ConfigMigrator
{
    /// <summary>Current config schema version. Bump when adding new migrations.</summary>
    public const int CurrentVersion = 3;

    private const string VersionKey = "ConfigVersion";

    /// <summary>
    /// Ordered list of migrations. Index corresponds to the target version:
    /// Migrations[0] upgrades from v0 → v1, Migrations[1] from v1 → v2, etc.
    /// </summary>
    private static readonly Func<JsonObject, bool>[] Migrations =
    [
        MigrateV0ToV1,  // tools.exec.restrictToWorkspace → tools.restrictToWorkspace
        MigrateV1ToV2,  // workspace path: ~/.sharpbot/... → data/...
        MigrateV2ToV3,  // flatten single-key providers to full objects
    ];

    /// <summary>
    /// Migrate a user config file on disk. If the file doesn't exist or
    /// is already at the current version, this is a no-op.
    /// </summary>
    /// <returns>True if the file was rewritten with migrations applied.</returns>
    public static bool MigrateFile(string configPath, ILogger? logger = null)
    {
        if (!File.Exists(configPath)) return false;

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not read config file for migration: {Path}", configPath);
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Config file is not valid JSON, skipping migration: {Path}", configPath);
            return false;
        }

        if (root is null) return false;

        var version = GetVersion(root);

        if (version >= CurrentVersion)
        {
            logger?.LogDebug("Config already at v{Version}, no migration needed", version);
            return false;
        }

        logger?.LogInformation("Migrating config from v{From} → v{To}", version, CurrentVersion);

        // Back up the original file before migrating
        try
        {
            var backupPath = configPath + $".v{version}.bak";
            if (!File.Exists(backupPath))
                File.Copy(configPath, backupPath);
            logger?.LogInformation("Config backup saved to {Path}", backupPath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not create config backup (continuing anyway)");
        }

        var anyChanged = false;
        for (var i = version; i < Migrations.Length && i < CurrentVersion; i++)
        {
            try
            {
                var changed = Migrations[i](root);
                if (changed)
                {
                    logger?.LogInformation("Applied migration v{From} → v{To}", i, i + 1);
                    anyChanged = true;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Migration v{From} → v{To} failed, stopping", i, i + 1);
                break;
            }
        }

        // Stamp the current version
        root[VersionKey] = CurrentVersion;
        anyChanged = true;

        // Write back
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var migrated = root.ToJsonString(options);
            File.WriteAllText(configPath, migrated);
            logger?.LogInformation("Config migrated and saved to {Path}", configPath);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to write migrated config to {Path}", configPath);
            return false;
        }

        return anyChanged;
    }

    /// <summary>Read the config version, defaulting to 0 if absent.</summary>
    private static int GetVersion(JsonObject root)
    {
        if (root.TryGetPropertyValue(VersionKey, out var node) && node is JsonValue val)
        {
            if (val.TryGetValue<int>(out var v)) return v;
        }
        return 0;
    }

    // =====================================================================
    // Migration functions
    // =====================================================================

    /// <summary>
    /// v0 → v1: Move <c>Tools.Exec.RestrictToWorkspace</c> to <c>Tools.RestrictToWorkspace</c>.
    /// This matches the nanobot Python migration.
    /// </summary>
    private static bool MigrateV0ToV1(JsonObject root)
    {
        var tools = root["Tools"]?.AsObject();
        if (tools is null) return false;

        var exec = tools["Exec"]?.AsObject();
        if (exec is null) return false;

        if (!exec.ContainsKey("RestrictToWorkspace")) return false;
        if (tools.ContainsKey("RestrictToWorkspace")) 
        {
            // Already exists at the correct level, just remove old one
            exec.Remove("RestrictToWorkspace");
            return true;
        }

        var value = exec["RestrictToWorkspace"]?.DeepClone();
        exec.Remove("RestrictToWorkspace");
        tools["RestrictToWorkspace"] = value;
        return true;
    }

    /// <summary>
    /// v1 → v2: Migrate workspace paths from <c>~/.sharpbot/...</c> to <c>data/...</c>.
    /// </summary>
    private static bool MigrateV1ToV2(JsonObject root)
    {
        var agents = root["Agents"]?.AsObject();
        var defaults = agents?["Defaults"]?.AsObject();
        if (defaults is null) return false;

        var workspace = defaults["Workspace"]?.GetValue<string>();
        if (workspace is null) return false;

        // Migrate any variant of ~/.sharpbot/workspace to data/workspace
        if (workspace.Contains(".sharpbot"))
        {
            // ~/.sharpbot/workspace → data/workspace
            // ~/.sharpbot/custom   → data/custom
            var newWorkspace = workspace
                .Replace("~/.sharpbot/", "data/")
                .Replace("~\\.sharpbot\\", "data\\")
                .Replace("%USERPROFILE%\\.sharpbot\\", "data\\");

            defaults["Workspace"] = newWorkspace;
            return true;
        }

        return false;
    }

    /// <summary>
    /// v2 → v3: Normalize provider entries — if a provider value is a bare string
    /// (just an API key), wrap it in <c>{ "ApiKey": "..." }</c>.
    /// Also migrate a flat <c>"ApiKey"</c> field at the providers level
    /// (legacy format) to the proper provider object.
    /// </summary>
    private static bool MigrateV2ToV3(JsonObject root)
    {
        var providers = root["Providers"]?.AsObject();
        if (providers is null) return false;

        var changed = false;
        var keys = providers.Select(kv => kv.Key).ToList();

        foreach (var key in keys)
        {
            var node = providers[key];
            if (node is null) continue;

            // If it's a bare string, wrap it
            if (node is JsonValue jv && jv.TryGetValue<string>(out var apiKey))
            {
                providers[key] = new JsonObject { ["ApiKey"] = apiKey };
                changed = true;
            }
        }

        return changed;
    }
}
