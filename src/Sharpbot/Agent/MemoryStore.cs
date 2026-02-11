using Sharpbot.Utils;

namespace Sharpbot.Agent;

/// <summary>
/// Memory system for the agent.
/// Supports daily notes (memory/YYYY-MM-DD.md) and long-term memory (MEMORY.md).
/// </summary>
public sealed class MemoryStore
{
    private readonly string _workspace;
    private readonly string _memoryDir;
    private readonly string _memoryFile;

    public MemoryStore(string workspace)
    {
        _workspace = workspace;
        _memoryDir = Helpers.EnsureDir(Path.Combine(workspace, "memory"));
        _memoryFile = Path.Combine(_memoryDir, "MEMORY.md");
    }

    /// <summary>Get path to today's memory file.</summary>
    public string GetTodayFile() => Path.Combine(_memoryDir, $"{Helpers.TodayDate()}.md");

    /// <summary>Read today's memory notes.</summary>
    public string ReadToday()
    {
        var todayFile = GetTodayFile();
        return File.Exists(todayFile) ? File.ReadAllText(todayFile) : "";
    }

    /// <summary>Append content to today's memory notes.</summary>
    public void AppendToday(string content)
    {
        var todayFile = GetTodayFile();
        if (File.Exists(todayFile))
        {
            var existing = File.ReadAllText(todayFile);
            content = existing + "\n" + content;
        }
        else
        {
            content = $"# {Helpers.TodayDate()}\n\n{content}";
        }
        File.WriteAllText(todayFile, content);
    }

    /// <summary>Read long-term memory (MEMORY.md).</summary>
    public string ReadLongTerm() => File.Exists(_memoryFile) ? File.ReadAllText(_memoryFile) : "";

    /// <summary>Write to long-term memory (MEMORY.md).</summary>
    public void WriteLongTerm(string content) => File.WriteAllText(_memoryFile, content);

    /// <summary>Get memories from the last N days.</summary>
    public string GetRecentMemories(int days = 7)
    {
        var memories = new List<string>();
        var today = DateTime.Now.Date;

        for (int i = 0; i < days; i++)
        {
            var date = today.AddDays(-i);
            var dateStr = date.ToString("yyyy-MM-dd");
            var filePath = Path.Combine(_memoryDir, $"{dateStr}.md");

            if (File.Exists(filePath))
                memories.Add(File.ReadAllText(filePath));
        }

        return string.Join("\n\n---\n\n", memories);
    }

    /// <summary>List all memory files sorted by date (newest first).</summary>
    public List<string> ListMemoryFiles()
    {
        if (!Directory.Exists(_memoryDir)) return [];
        return Directory.GetFiles(_memoryDir, "????-??-??.md")
            .OrderByDescending(f => f)
            .ToList();
    }

    /// <summary>Get memory context for the agent.</summary>
    public string GetMemoryContext()
    {
        var parts = new List<string>();

        var longTerm = ReadLongTerm();
        if (!string.IsNullOrEmpty(longTerm))
            parts.Add($"## Long-term Memory\n{longTerm}");

        var today = ReadToday();
        if (!string.IsNullOrEmpty(today))
            parts.Add($"## Today's Notes\n{today}");

        return string.Join("\n\n", parts);
    }
}
