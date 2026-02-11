namespace Sharpbot.Utils;

/// <summary>
/// Utility functions for Sharpbot.
/// All data paths are relative to the application runtime directory,
/// making deployment via Docker or other containers straightforward.
/// </summary>
public static class Helpers
{
    private const string UnsafeFilenameCharacters = "<>:\"/\\|?*";
    private const string DataDirName = "data";

    /// <summary>Ensure a directory exists, creating it if necessary.</summary>
    public static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Get the sharpbot data directory ({app}/data).
    /// All runtime data (workspace, sessions, cron, media, config overrides)
    /// lives under this directory so it can be volume-mounted in Docker.
    /// </summary>
    public static string GetDataPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DataDirName);
        return EnsureDir(path);
    }

    /// <summary>
    /// Get a persistent database directory that survives app rebuilds.
    /// Uses SHARPBOT_DB env var if set, otherwise {LocalApplicationData}/sharpbot.
    /// </summary>
    public static string GetPersistentDbPath()
    {
        var envPath = Environment.GetEnvironmentVariable("SHARPBOT_DB");
        if (!string.IsNullOrEmpty(envPath))
            return envPath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = EnsureDir(Path.Combine(appData, "sharpbot"));
        return Path.Combine(dir, "sharpbot.db");
    }

    /// <summary>Get the workspace path ({app}/data/workspace by default).</summary>
    public static string GetWorkspacePath(string? workspace = null)
    {
        if (workspace is not null)
        {
            // Expand ~/  to app-relative data dir (not home dir)
            if (workspace.StartsWith("~/") || workspace.StartsWith("~\\"))
                workspace = Path.Combine(GetDataPath(), workspace[2..]);
            return EnsureDir(workspace);
        }
        return EnsureDir(Path.Combine(GetDataPath(), "workspace"));
    }

    /// <summary>Get the media download directory ({app}/data/media).</summary>
    public static string GetMediaPath() => EnsureDir(Path.Combine(GetDataPath(), "media"));

    /// <summary>Get the memory directory within the workspace.</summary>
    public static string GetMemoryPath(string? workspace = null)
    {
        var ws = workspace ?? GetWorkspacePath();
        return EnsureDir(Path.Combine(ws, "memory"));
    }

    /// <summary>Get the skills directory within the workspace.</summary>
    public static string GetSkillsPath(string? workspace = null)
    {
        var ws = workspace ?? GetWorkspacePath();
        return EnsureDir(Path.Combine(ws, "skills"));
    }

    /// <summary>Get today's date in YYYY-MM-DD format.</summary>
    public static string TodayDate() => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>Get current timestamp in ISO format.</summary>
    public static string Timestamp() => DateTime.Now.ToString("o");

    /// <summary>Truncate a string to max length, adding suffix if truncated.</summary>
    public static string TruncateString(string s, int maxLen = 100, string suffix = "...")
    {
        if (s.Length <= maxLen) return s;
        return string.Concat(s.AsSpan(0, maxLen - suffix.Length), suffix);
    }

    /// <summary>Convert a string to a safe filename.</summary>
    public static string SafeFilename(string name)
    {
        foreach (var c in UnsafeFilenameCharacters)
            name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>Parse a session key into channel and chat_id.</summary>
    public static (string Channel, string ChatId) ParseSessionKey(string key)
    {
        var idx = key.IndexOf(':');
        return idx < 0
            ? throw new ArgumentException($"Invalid session key: {key}")
            : (key[..idx], key[(idx + 1)..]);
    }
}
