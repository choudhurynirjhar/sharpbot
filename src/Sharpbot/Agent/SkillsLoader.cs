using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sharpbot.Config;

namespace Sharpbot.Agent;

/// <summary>
/// Represents a loaded skill with its metadata, availability, and gating status.
/// </summary>
public sealed record SkillInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Source { get; init; }
    public string Description { get; init; } = "";
    public bool Available { get; init; } = true;
    public string? UnavailableReason { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public SkillRequirements? Requirements { get; init; }
}

/// <summary>
/// Parsed requirements from skill metadata.
/// </summary>
public sealed record SkillRequirements
{
    public List<string> Bins { get; init; } = [];
    public List<string> AnyBins { get; init; } = [];
    public List<string> Env { get; init; } = [];
    public List<string> Config { get; init; } = [];
}

/// <summary>
/// Loader for agent skills with support for:
/// - Three-tier precedence: workspace > managed ({app}/data/skills) > builtin
/// - Requirements gating (bins, env, config)
/// - Per-skill environment injection (apiKey, env)
/// - Extra skill directories from config
/// </summary>
public sealed class SkillsLoader
{
    private readonly string _workspace;
    private readonly string _workspaceSkills;
    private readonly string _managedSkills;
    private readonly string? _builtinSkills;
    private readonly SkillsConfig? _skillsConfig;
    private readonly SharpbotConfig? _appConfig;
    private readonly List<string> _extraDirs;

    // Cache for loaded skills to avoid re-parsing
    private List<SkillInfo>? _cachedSkills;

    /// <summary>Regex for YAML frontmatter, compiled once.</summary>
    private static readonly Regex FrontmatterRegex = new(
        @"^---\r?\n(.*?)\r?\n---",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public SkillsLoader(
        string workspace,
        string? builtinSkillsDir = null,
        SkillsConfig? skillsConfig = null,
        SharpbotConfig? appConfig = null)
    {
        _workspace = workspace;
        _workspaceSkills = Path.Combine(workspace, "skills");
        _managedSkills = Path.Combine(Utils.Helpers.GetDataPath(), "skills");
        _builtinSkills = builtinSkillsDir;
        _skillsConfig = skillsConfig;
        _appConfig = appConfig;
        _extraDirs = skillsConfig?.Load.ExtraDirs ?? [];
    }

    /// <summary>Invalidate cached skills to force re-scan.</summary>
    public void InvalidateCache() => _cachedSkills = null;

    /// <summary>List all available skills with full metadata and gating status.</summary>
    public List<SkillInfo> ListAllSkills()
    {
        if (_cachedSkills != null) return _cachedSkills;

        var skills = new List<SkillInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Workspace skills (highest priority)
        ScanDirectory(_workspaceSkills, "workspace", skills, seenNames);

        // 2. Managed/local skills ({app}/data/skills)
        ScanDirectory(_managedSkills, "managed", skills, seenNames);

        // 3. Built-in skills
        if (_builtinSkills != null)
            ScanDirectory(_builtinSkills, "builtin", skills, seenNames);

        // 4. Extra directories (lowest priority)
        foreach (var extraDir in _extraDirs)
        {
            var expandedDir = ExpandPath(extraDir);
            ScanDirectory(expandedDir, "extra", skills, seenNames);
        }

        // Apply config overrides (enable/disable)
        if (_skillsConfig?.Entries is { Count: > 0 } entries)
        {
            skills = skills.Where(s =>
            {
                if (entries.TryGetValue(s.Name, out var entry))
                    return entry.Enabled;
                return true; // enabled by default if not in config
            }).ToList();
        }

        _cachedSkills = skills;
        return skills;
    }

    /// <summary>List skills as simple dictionaries (backward compatible).</summary>
    public List<Dictionary<string, string>> ListSkills(bool filterUnavailable = true)
    {
        var allSkills = ListAllSkills();
        if (filterUnavailable)
            allSkills = allSkills.Where(s => s.Available).ToList();

        return allSkills.Select(s => new Dictionary<string, string>
        {
            ["name"] = s.Name,
            ["path"] = s.Path,
            ["source"] = s.Source,
        }).ToList();
    }

    /// <summary>Load a skill by name.</summary>
    public string? LoadSkill(string name)
    {
        // Check workspace first
        var workspaceSkill = Path.Combine(_workspaceSkills, name, "SKILL.md");
        if (File.Exists(workspaceSkill))
            return File.ReadAllText(workspaceSkill);

        // Check managed
        var managedSkill = Path.Combine(_managedSkills, name, "SKILL.md");
        if (File.Exists(managedSkill))
            return File.ReadAllText(managedSkill);

        // Check builtin
        if (_builtinSkills != null)
        {
            var builtinSkill = Path.Combine(_builtinSkills, name, "SKILL.md");
            if (File.Exists(builtinSkill))
                return File.ReadAllText(builtinSkill);
        }

        // Check extra dirs
        foreach (var extraDir in _extraDirs)
        {
            var expandedDir = ExpandPath(extraDir);
            var extraSkill = Path.Combine(expandedDir, name, "SKILL.md");
            if (File.Exists(extraSkill))
                return File.ReadAllText(extraSkill);
        }

        return null;
    }

    /// <summary>Load specific skills for inclusion in agent context.</summary>
    public string LoadSkillsForContext(List<string> skillNames)
    {
        var parts = new List<string>();
        foreach (var name in skillNames)
        {
            var content = LoadSkill(name);
            if (content != null)
            {
                content = StripFrontmatter(content);
                content = SubstituteEnvironmentVariables(content);
                parts.Add($"### Skill: {name}\n\n{content}");
            }
        }
        return string.Join("\n\n---\n\n", parts);
    }

    /// <summary>Build a summary of all skills (name, description, path, availability).</summary>
    public string BuildSkillsSummary()
    {
        var allSkills = ListAllSkills();
        if (allSkills.Count == 0) return "";

        var lines = new List<string> { "<skills>" };
        foreach (var s in allSkills)
        {
            var name = EscapeXml(s.Name);
            var desc = EscapeXml(s.Description);
            var available = s.Available ? "true" : "false";

            lines.Add($"  <skill available=\"{available}\">");
            lines.Add($"    <name>{name}</name>");
            lines.Add($"    <description>{desc}</description>");
            lines.Add($"    <location>{s.Path}</location>");
            if (!s.Available && s.UnavailableReason != null)
                lines.Add($"    <reason>{EscapeXml(s.UnavailableReason)}</reason>");
            lines.Add($"  </skill>");
        }
        lines.Add("</skills>");
        return string.Join("\n", lines);
    }

    /// <summary>Get skills marked as always=true (and available).</summary>
    public List<string> GetAlwaysSkills()
    {
        return ListAllSkills()
            .Where(s => s.Available && s.Metadata.TryGetValue("always", out var v) && v == "true")
            .Select(s => s.Name)
            .ToList();
    }

    /// <summary>
    /// Inject environment variables for all active skills.
    /// Call this at the start of an agent run. Returns a restore action.
    /// </summary>
    public Action InjectSkillEnvironment()
    {
        var originalValues = new Dictionary<string, string?>();

        foreach (var skill in ListAllSkills().Where(s => s.Available))
        {
            // Check config entries for this skill
            if (_skillsConfig?.Entries.TryGetValue(skill.Name, out var entry) != true || entry is null)
                continue;

            // Inject apiKey as the primaryEnv variable
            if (!string.IsNullOrEmpty(entry.ApiKey))
            {
                var primaryEnv = GetPrimaryEnv(skill);
                if (primaryEnv != null)
                {
                    var existing = Environment.GetEnvironmentVariable(primaryEnv);
                    if (existing == null) // only inject if not already set
                    {
                        originalValues[primaryEnv] = null;
                        Environment.SetEnvironmentVariable(primaryEnv, entry.ApiKey);
                    }
                }
            }

            // Inject env variables
            foreach (var envKvp in entry.Env)
            {
                var existing = Environment.GetEnvironmentVariable(envKvp.Key);
                if (existing == null) // only inject if not already set
                {
                    originalValues[envKvp.Key] = null;
                    Environment.SetEnvironmentVariable(envKvp.Key, envKvp.Value);
                }
            }
        }

        // Return a restore action that undoes the injection
        return () =>
        {
            foreach (var kvp in originalValues)
                Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        };
    }

    /// <summary>Get metadata from a skill's frontmatter.</summary>
    public Dictionary<string, string>? GetSkillMetadata(string name)
    {
        var content = LoadSkill(name);
        if (content == null || !content.StartsWith("---")) return null;

        var match = FrontmatterRegex.Match(content);
        if (!match.Success) return null;

        var metadata = new Dictionary<string, string>();
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
                metadata[key] = value;
            }
        }
        return metadata;
    }

    // ─── Private helpers ──────────────────────────────────────────────

    private void ScanDirectory(string directory, string source,
        List<SkillInfo> skills, HashSet<string> seenNames)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var skillDir in Directory.GetDirectories(directory))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            var name = Path.GetFileName(skillDir);
            if (!seenNames.Add(name)) continue; // already seen at higher priority

            var metadata = ParseFrontmatter(skillFile);
            var description = metadata.TryGetValue("description", out var desc) ? desc : name;
            var requirements = ParseRequirements(metadata);
            var (available, reason) = CheckRequirements(name, requirements, metadata);

            skills.Add(new SkillInfo
            {
                Name = name,
                Path = skillFile,
                Source = source,
                Description = description,
                Available = available,
                UnavailableReason = reason,
                Metadata = metadata,
                Requirements = requirements,
            });
        }
    }

    private static Dictionary<string, string> ParseFrontmatter(string skillFilePath)
    {
        var content = File.ReadAllText(skillFilePath);
        if (!content.StartsWith("---")) return [];

        var match = FrontmatterRegex.Match(content);
        if (!match.Success) return [];

        var metadata = new Dictionary<string, string>();
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim().Trim('"', '\'');
                metadata[key] = value;
            }
        }
        return metadata;
    }

    /// <summary>
    /// Parse requirements from the 'metadata' field in frontmatter.
    /// The metadata field should be a single-line JSON object.
    /// </summary>
    private static SkillRequirements? ParseRequirements(Dictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("metadata", out var metaJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;

            // Look for sharpbot or openclaw requires block
            JsonElement requiresElement = default;
            if (root.TryGetProperty("sharpbot", out var sharpbotEl) &&
                sharpbotEl.TryGetProperty("requires", out var sbReq))
            {
                requiresElement = sbReq;
            }
            else if (root.TryGetProperty("openclaw", out var openclawEl) &&
                     openclawEl.TryGetProperty("requires", out var ocReq))
            {
                requiresElement = ocReq;
            }
            else
            {
                return null;
            }

            return new SkillRequirements
            {
                Bins = GetStringList(requiresElement, "bins"),
                AnyBins = GetStringList(requiresElement, "anyBins"),
                Env = GetStringList(requiresElement, "env"),
                Config = GetStringList(requiresElement, "config"),
            };
        }
        catch
        {
            return null; // Invalid JSON, skip
        }
    }

    /// <summary>Check if a skill's requirements are met.</summary>
    private (bool Available, string? Reason) CheckRequirements(
        string name, SkillRequirements? requirements, Dictionary<string, string> metadata)
    {
        // Skills with always: true skip gating
        if (metadata.TryGetValue("always", out var alwaysVal) && alwaysVal == "true")
            return (true, null);

        // Check OS gating from metadata JSON
        var osGate = CheckOsGating(metadata);
        if (osGate != null)
            return (false, osGate);

        if (requirements == null)
            return (true, null);

        // Check required binaries (all must exist)
        foreach (var bin in requirements.Bins)
        {
            if (!IsBinaryOnPath(bin))
                return (false, $"Required binary not found: {bin}");
        }

        // Check anyBins (at least one must exist)
        if (requirements.AnyBins.Count > 0 && !requirements.AnyBins.Any(IsBinaryOnPath))
            return (false, $"None of required binaries found: {string.Join(", ", requirements.AnyBins)}");

        // Check environment variables
        foreach (var envVar in requirements.Env)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            {
                // Also check if provided in skill config
                if (_skillsConfig?.Entries.TryGetValue(name, out var entry) == true)
                {
                    if (entry.Env.ContainsKey(envVar) || !string.IsNullOrEmpty(entry.ApiKey))
                        continue; // provided via config
                }
                return (false, $"Required environment variable not set: {envVar}");
            }
        }

        // Check config keys
        foreach (var configKey in requirements.Config)
        {
            if (!IsConfigKeyTruthy(configKey))
                return (false, $"Required config key not set: {configKey}");
        }

        return (true, null);
    }

    /// <summary>Check OS gating from metadata JSON.</summary>
    private static string? CheckOsGating(Dictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("metadata", out var metaJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;

            JsonElement? agentConfig = null;
            if (root.TryGetProperty("sharpbot", out var sb))
                agentConfig = sb;
            else if (root.TryGetProperty("openclaw", out var oc))
                agentConfig = oc;

            if (agentConfig?.TryGetProperty("os", out var osEl) == true &&
                osEl.ValueKind == JsonValueKind.Array)
            {
                var currentOs = OperatingSystem.IsWindows() ? "win32" :
                                OperatingSystem.IsMacOS() ? "darwin" : "linux";

                var allowed = new List<string>();
                foreach (var item in osEl.EnumerateArray())
                {
                    if (item.GetString() is { } os)
                        allowed.Add(os);
                }

                if (allowed.Count > 0 && !allowed.Contains(currentOs))
                    return $"Skill requires OS: {string.Join(", ", allowed)} (current: {currentOs})";
            }
        }
        catch
        {
            // Ignore JSON errors
        }

        return null;
    }

    /// <summary>Get the primaryEnv variable name for a skill.</summary>
    private static string? GetPrimaryEnv(SkillInfo skill)
    {
        if (!skill.Metadata.TryGetValue("metadata", out var metaJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("sharpbot", out var sb) &&
                sb.TryGetProperty("primaryEnv", out var pe))
                return pe.GetString();

            if (root.TryGetProperty("openclaw", out var oc) &&
                oc.TryGetProperty("primaryEnv", out var ocPe))
                return ocPe.GetString();
        }
        catch { }

        return null;
    }

    /// <summary>Check if a binary exists on PATH.</summary>
    private static bool IsBinaryOnPath(string binaryName)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            var separator = OperatingSystem.IsWindows() ? ';' : ':';
            var extensions = OperatingSystem.IsWindows()
                ? new[] { ".exe", ".cmd", ".bat", ".com", "" }
                : new[] { "" };

            foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(dir, binaryName + ext);
                    if (File.Exists(fullPath))
                        return true;
                }
            }
        }
        catch
        {
            // Ignore errors in path scanning
        }

        return false;
    }

    /// <summary>Check if a config key is truthy.</summary>
    private bool IsConfigKeyTruthy(string configKey)
    {
        if (_appConfig == null) return false;

        // Navigate the config object using dot-separated keys
        // e.g. "tools.web.search.apiKey" → config.Tools.Web.Search.ApiKey
        try
        {
            var parts = configKey.Split('.');
            object? current = _appConfig;

            foreach (var part in parts)
            {
                if (current == null) return false;
                var type = current.GetType();
                var prop = type.GetProperty(part,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.IgnoreCase);

                if (prop == null) return false;
                current = prop.GetValue(current);
            }

            return current switch
            {
                null => false,
                bool b => b,
                string s => !string.IsNullOrEmpty(s),
                int i => i != 0,
                _ => true,
            };
        }
        catch
        {
            return false;
        }
    }

    private static List<string> GetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.GetString() is { } s)
                result.Add(s);
        }
        return result;
    }

    /// <summary>Regex for {env:VAR_NAME} substitution, compiled once.</summary>
    private static readonly Regex EnvVarRegex = new(
        @"\{env:([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Substitute {env:VAR_NAME} placeholders with actual environment variable values.
    /// Also substitutes $VAR_NAME references (common in bash/curl examples).
    /// Unresolved placeholders are replaced with "[NOT SET]" so the agent knows.
    /// </summary>
    private static string SubstituteEnvironmentVariables(string content)
    {
        // Handle {env:VAR_NAME} syntax
        content = EnvVarRegex.Replace(content, match =>
        {
            var varName = match.Groups[1].Value;
            return Environment.GetEnvironmentVariable(varName) ?? $"[{varName} NOT SET]";
        });

        return content;
    }

    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith("---")) return content;
        var match = FrontmatterRegex.Match(content);
        if (!match.Success) return content;
        return content[(match.Length)..].TrimStart('\r', '\n').Trim();
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(Utils.Helpers.GetDataPath(), path[2..]);
        if (!Path.IsPathRooted(path))
            return Path.Combine(AppContext.BaseDirectory, path);
        return path;
    }
}
