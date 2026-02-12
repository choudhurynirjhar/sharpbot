using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sharpbot.Agent.Tools;
using Sharpbot.Bus;
using Sharpbot.Channels;
using Sharpbot.Providers;

namespace Sharpbot.Plugins;

/// <summary>
/// Loads and manages Sharpbot plugins from external .NET assemblies.
/// Each plugin lives in its own subdirectory under the configured plugins path,
/// with a plugin.json manifest and one or more DLLs.
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly ILogger? _logger;
    private bool _disposed;

    public PluginLoader(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>All loaded plugin instances.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>
    /// Scan a directory for plugin subdirectories, load their assemblies,
    /// and initialize each plugin.
    /// </summary>
    public async Task LoadPluginsAsync(
        string pluginsDir,
        string workspace,
        Dictionary<string, Dictionary<string, object>>? pluginConfigs = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (!Directory.Exists(pluginsDir))
        {
            _logger?.LogInformation("Plugins directory does not exist: {Path} — skipping", pluginsDir);
            return;
        }

        var dirs = Directory.GetDirectories(pluginsDir);
        if (dirs.Length == 0)
        {
            _logger?.LogInformation("No plugins found in {Path}", pluginsDir);
            return;
        }

        _logger?.LogInformation("Scanning {Count} plugin directories in {Path}", dirs.Length, pluginsDir);

        foreach (var dir in dirs)
        {
            try
            {
                var loaded = await LoadPluginFromDirectoryAsync(dir, workspace, pluginConfigs, loggerFactory);
                if (loaded != null)
                    _plugins.Add(loaded);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to load plugin from {Dir}", dir);
            }
        }

        _logger?.LogInformation("Loaded {Count} plugin(s)", _plugins.Count);
    }

    /// <summary>Load a single plugin from a directory.</summary>
    private async Task<LoadedPlugin?> LoadPluginFromDirectoryAsync(
        string pluginDir,
        string workspace,
        Dictionary<string, Dictionary<string, object>>? pluginConfigs,
        ILoggerFactory? loggerFactory)
    {
        var manifestPath = Path.Combine(pluginDir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            _logger?.LogDebug("Skipping {Dir} — no plugin.json found", pluginDir);
            return null;
        }

        // Read and parse manifest
        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json);
        if (manifest == null || string.IsNullOrEmpty(manifest.Name))
        {
            _logger?.LogWarning("Invalid plugin.json in {Dir}", pluginDir);
            return null;
        }

        if (!manifest.Enabled)
        {
            _logger?.LogInformation("Plugin '{Name}' is disabled — skipping", manifest.Name);
            return null;
        }

        // Resolve assembly path
        var assemblyPath = Path.Combine(pluginDir, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            _logger?.LogWarning("Plugin '{Name}': assembly not found at {Path}", manifest.Name, assemblyPath);
            return null;
        }

        // Load assembly in isolated context
        var loadContext = new PluginAssemblyLoadContext(assemblyPath, manifest.Name);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Plugin '{Name}': failed to load assembly", manifest.Name);
            return null;
        }

        // Find and instantiate the entry-point type
        var entryPointType = assembly.GetType(manifest.EntryPoint);
        if (entryPointType == null)
        {
            _logger?.LogWarning("Plugin '{Name}': entry point type '{Type}' not found in assembly",
                manifest.Name, manifest.EntryPoint);
            return null;
        }

        if (!typeof(IPlugin).IsAssignableFrom(entryPointType))
        {
            _logger?.LogWarning("Plugin '{Name}': entry point type '{Type}' does not implement IPlugin",
                manifest.Name, manifest.EntryPoint);
            return null;
        }

        var pluginInstance = Activator.CreateInstance(entryPointType) as IPlugin;
        if (pluginInstance == null)
        {
            _logger?.LogWarning("Plugin '{Name}': failed to instantiate entry point", manifest.Name);
            return null;
        }

        // Build plugin context
        var config = pluginConfigs?.GetValueOrDefault(manifest.Name)
            ?? new Dictionary<string, object>();
        var configDict = config.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)kvp.Value);

        var dataDir = Path.Combine(pluginDir, "data");
        Directory.CreateDirectory(dataDir);

        var context = new PluginContext
        {
            Config = configDict.AsReadOnly(),
            Workspace = workspace,
            DataDir = dataDir,
            Logger = loggerFactory?.CreateLogger($"plugin:{manifest.Name}"),
        };

        // Initialize the plugin
        await pluginInstance.InitializeAsync(context);

        _logger?.LogInformation("Plugin '{Name}' v{Version} loaded — provides: [{Provides}]",
            manifest.Name, manifest.Version, string.Join(", ", manifest.Provides));

        return new LoadedPlugin(manifest, pluginInstance, loadContext);
    }

    /// <summary>Collect all tools from all loaded plugins.</summary>
    public List<ITool> GetAllTools()
    {
        var tools = new List<ITool>();
        foreach (var loaded in _plugins)
        {
            try
            {
                tools.AddRange(loaded.Instance.GetTools());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting tools from plugin '{Name}'", loaded.Manifest.Name);
            }
        }
        return tools;
    }

    /// <summary>Collect all channels from all loaded plugins.</summary>
    public List<BaseChannel> GetAllChannels(MessageBus bus, ILogger? logger)
    {
        var channels = new List<BaseChannel>();
        foreach (var loaded in _plugins)
        {
            try
            {
                channels.AddRange(loaded.Instance.GetChannels(bus, logger));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting channels from plugin '{Name}'", loaded.Manifest.Name);
            }
        }
        return channels;
    }

    /// <summary>Collect all named providers from all loaded plugins.</summary>
    public Dictionary<string, ILlmProvider> GetAllProviders()
    {
        var providers = new Dictionary<string, ILlmProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in _plugins)
        {
            try
            {
                foreach (var (name, provider) in loaded.Instance.GetProviders())
                {
                    providers[name] = provider;
                    _logger?.LogInformation("Registered plugin provider: {Name} (from {Plugin})",
                        name, loaded.Manifest.Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting providers from plugin '{Name}'", loaded.Manifest.Name);
            }
        }
        return providers;
    }

    /// <summary>Collect all hooks from all loaded plugins.</summary>
    public List<IPluginHook> GetAllHooks()
    {
        var hooks = new List<IPluginHook>();
        foreach (var loaded in _plugins)
        {
            try
            {
                hooks.AddRange(loaded.Instance.GetHooks());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting hooks from plugin '{Name}'", loaded.Manifest.Name);
            }
        }
        return hooks;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var loaded in _plugins)
        {
            try { loaded.Instance.Dispose(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Error disposing plugin '{Name}'", loaded.Manifest.Name); }
        }
        _plugins.Clear();
    }
}

/// <summary>A loaded plugin with its manifest, instance, and load context.</summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    IPlugin Instance,
    AssemblyLoadContext LoadContext);

/// <summary>
/// Isolated assembly load context for plugins.
/// Resolves dependencies from the plugin's directory first, then falls back to the default context.
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginPath, string name)
        : base(name: $"plugin:{name}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try resolving from the plugin's own directory first
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path != null)
            return LoadFromAssemblyPath(path);

        // Fall back to default (shared framework) assemblies
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
