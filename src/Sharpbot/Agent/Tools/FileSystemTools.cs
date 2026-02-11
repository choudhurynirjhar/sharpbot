namespace Sharpbot.Agent.Tools;

/// <summary>Helper to resolve paths and enforce directory restrictions.</summary>
internal static class PathResolver
{
    public static string Resolve(string path, string? allowedDir = null)
    {
        var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            path.StartsWith("~/") || path.StartsWith("~\\")
                ? Path.Combine(Utils.Helpers.GetDataPath(), path[2..])
                : path));

        if (allowedDir != null)
        {
            var allowed = Path.GetFullPath(allowedDir);
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Path {path} is outside allowed directory {allowedDir}");
        }

        return resolved;
    }
}

/// <summary>Tool to read file contents.</summary>
public sealed class ReadFileTool : ToolBase
{
    private readonly string? _allowedDir;

    public ReadFileTool(string? allowedDir = null) => _allowedDir = allowedDir;

    public override string Name => "read_file";
    public override string Description => "Read the contents of a file at the given path.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The file path to read" }
        },
        ["required"] = new[] { "path" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var path = GetString(args, "path");
        try
        {
            var filePath = PathResolver.Resolve(path, _allowedDir);
            if (!File.Exists(filePath)) return $"Error: File not found: {path}";
            return await File.ReadAllTextAsync(filePath);
        }
        catch (UnauthorizedAccessException e) { return $"Error: {e.Message}"; }
        catch (Exception e) { return $"Error reading file: {e.Message}"; }
    }
}

/// <summary>Tool to write content to a file.</summary>
public sealed class WriteFileTool : ToolBase
{
    private readonly string? _allowedDir;

    public WriteFileTool(string? allowedDir = null) => _allowedDir = allowedDir;

    public override string Name => "write_file";
    public override string Description => "Write content to a file at the given path. Creates parent directories if needed.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The file path to write to" },
            ["content"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The content to write" },
        },
        ["required"] = new[] { "path", "content" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var path = GetString(args, "path");
        var content = GetString(args, "content");
        try
        {
            var filePath = PathResolver.Resolve(path, _allowedDir);
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(filePath, content);
            return $"Successfully wrote {content.Length} bytes to {path}";
        }
        catch (UnauthorizedAccessException e) { return $"Error: {e.Message}"; }
        catch (Exception e) { return $"Error writing file: {e.Message}"; }
    }
}

/// <summary>Tool to edit a file by replacing text.</summary>
public sealed class EditFileTool : ToolBase
{
    private readonly string? _allowedDir;

    public EditFileTool(string? allowedDir = null) => _allowedDir = allowedDir;

    public override string Name => "edit_file";
    public override string Description => "Edit a file by replacing old_text with new_text. The old_text must exist exactly in the file.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The file path to edit" },
            ["old_text"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The exact text to find and replace" },
            ["new_text"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The text to replace with" },
        },
        ["required"] = new[] { "path", "old_text", "new_text" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var path = GetString(args, "path");
        var oldText = GetString(args, "old_text");
        var newText = GetString(args, "new_text");
        try
        {
            var filePath = PathResolver.Resolve(path, _allowedDir);
            if (!File.Exists(filePath)) return $"Error: File not found: {path}";

            var content = await File.ReadAllTextAsync(filePath);
            if (!content.Contains(oldText))
                return "Error: old_text not found in file. Make sure it matches exactly.";

            var count = CountOccurrences(content, oldText);
            if (count > 1)
                return $"Warning: old_text appears {count} times. Please provide more context to make it unique.";

            var idx = content.IndexOf(oldText, StringComparison.Ordinal);
            var newContent = content[..idx] + newText + content[(idx + oldText.Length)..];
            await File.WriteAllTextAsync(filePath, newContent);
            return $"Successfully edited {path}";
        }
        catch (UnauthorizedAccessException e) { return $"Error: {e.Message}"; }
        catch (Exception e) { return $"Error editing file: {e.Message}"; }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}

/// <summary>Tool to list directory contents.</summary>
public sealed class ListDirTool : ToolBase
{
    private readonly string? _allowedDir;

    public ListDirTool(string? allowedDir = null) => _allowedDir = allowedDir;

    public override string Name => "list_dir";
    public override string Description => "List the contents of a directory.";
    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The directory path to list" }
        },
        ["required"] = new[] { "path" },
    };

    public override Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var path = GetString(args, "path");
        try
        {
            var dirPath = PathResolver.Resolve(path, _allowedDir);
            if (!Directory.Exists(dirPath)) return Task.FromResult($"Error: Directory not found: {path}");

            var items = new List<string>();
            foreach (var entry in Directory.GetFileSystemEntries(dirPath).OrderBy(x => x))
            {
                var name = Path.GetFileName(entry);
                var prefix = Directory.Exists(entry) ? "📁 " : "📄 ";
                items.Add($"{prefix}{name}");
            }

            return Task.FromResult(items.Count == 0 ? $"Directory {path} is empty" : string.Join("\n", items));
        }
        catch (UnauthorizedAccessException e) { return Task.FromResult($"Error: {e.Message}"); }
        catch (Exception e) { return Task.FromResult($"Error listing directory: {e.Message}"); }
    }
}
