using System.Text.Json.Serialization;

namespace Sharpbot.Plugins;

/// <summary>
/// Deserialization model for the plugin.json manifest file.
/// Each plugin directory contains a plugin.json that describes
/// the plugin and how to load it.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Unique plugin name (should match directory name).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Semantic version string.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>DLL filename relative to the plugin directory.</summary>
    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = "";

    /// <summary>Fully-qualified type name of the IPlugin entry point.</summary>
    [JsonPropertyName("entryPoint")]
    public string EntryPoint { get; set; } = "";

    /// <summary>What the plugin provides: "tool", "channel", "hook", "provider".</summary>
    [JsonPropertyName("provides")]
    public List<string> Provides { get; set; } = [];

    /// <summary>Whether the plugin is enabled (default: true).</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
