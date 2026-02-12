using System.CommandLine;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sharpbot;
using Sharpbot.Api;
using Sharpbot.Bus;
using Sharpbot.Commands;
using Sharpbot.Config;
using Sharpbot.Cron;
using Sharpbot.Database;
using Sharpbot.Logging;
using Sharpbot.Services;
using Sharpbot.Session;
using Sharpbot.Telemetry;

// ============================================================================
// Sharpbot — CLI + ASP.NET 9 Web Application
//
// If a CLI subcommand is provided (agent, cron, channels, status, onboard,
// gateway), run it and exit. Otherwise, start the web server (default).
// ============================================================================

// Known CLI subcommands
var cliCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "agent", "cron", "channels", "status", "onboard", "gateway" };

var hasCliCommand = args.Length > 0 && cliCommands.Contains(args[0]);

if (hasCliCommand)
{
    // ── CLI Mode ────────────────────────────────────────────────────────────
    var root = new RootCommand("Sharpbot — personal AI assistant")
    {
        new AgentCommand(),
        new CronCommand(),
        new ChannelsCommand(),
        new StatusCommand(),
        new OnboardCommand(),
        new GatewayCommand(),
    };

    var parseResult = root.Parse(args);
    await parseResult.InvokeAsync();
    return;
}

// ── Web Server Mode (default — no subcommand) ──────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────────────────
var sharpbotConfig = ConfigLoader.LoadConfig();
builder.Services.AddSingleton(sharpbotConfig);

// ── Database (single SQLite file for sessions, usage, cron, logs) ───────────
// Stored in a persistent user-level location so data survives app rebuilds.
var dbPath = Sharpbot.Utils.Helpers.GetPersistentDbPath();
var db = new SharpbotDb(dbPath);
builder.Services.AddSingleton(db);

// ── Core services ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<MessageBus>(sp =>
    new MessageBus(sp.GetRequiredService<ILoggerFactory>().CreateLogger("bus")));

builder.Services.AddSingleton<SessionManager>(sp =>
    new SessionManager(db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("sessions")));

builder.Services.AddSingleton<CronService>(sp =>
    new CronService(db, sp.GetRequiredService<ILoggerFactory>().CreateLogger("cron")));

// ── Logging configuration ────────────────────────────────────────────────────
// Suppress noisy ASP.NET Core HTTP request/response logs from all providers
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Cors", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);

// Ring buffer captures logs for the web UI (also persists to SQLite)
var logRingBuffer = new LogRingBuffer(capacity: 1000, db: db);
builder.Services.AddSingleton(logRingBuffer);
builder.Logging.AddProvider(new RingBufferLoggerProvider(logRingBuffer));

// ── OpenTelemetry ───────────────────────────────────────────────────────────
var otelResource = ResourceBuilder.CreateDefault()
    .AddService(
        serviceName: SharpbotInstrumentation.ServiceName,
        serviceVersion: SharpbotInstrumentation.ServiceVersion);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        SharpbotInstrumentation.ServiceName,
        serviceVersion: SharpbotInstrumentation.ServiceVersion))
    .WithTracing(tracing => tracing
        .AddSource(SharpbotInstrumentation.ServiceName)
        .AddAspNetCoreInstrumentation(opts =>
        {
            // Filter out noisy polling endpoints from traces
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/api/logs");
        })
        .AddHttpClientInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(SharpbotInstrumentation.ServiceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter())
    .WithLogging(logging => logging
        .AddConsoleExporter());

// ── Usage tracking ──────────────────────────────────────────────────────────
var usageStore = new UsageStore(db);
builder.Services.AddSingleton(usageStore);

// ── Gateway hosted service (agent loop, cron, heartbeat, channels) ──────────
builder.Services.AddSingleton<SharpbotHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SharpbotHostedService>());

// ── CORS (for development) ──────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// ── Middleware ───────────────────────────────────────────────────────────────
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API Endpoints ───────────────────────────────────────────────────────────
app.MapChatApi();
app.MapStatusApi();
app.MapConfigApi();
app.MapCronApi();
app.MapChannelsApi();
app.MapSkillsApi();
app.MapLogsApi();
app.MapUsageApi();
app.MapMemoryApi();
app.MapSlackEventsApi();

// ── Root redirect ───────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Redirect("/index.html"));

// ── Start ───────────────────────────────────────────────────────────────────
var port = sharpbotConfig.Gateway.Port;
var host = sharpbotConfig.Gateway.Host;
var url = $"http://{host}:{port}";

app.Logger.LogInformation("{Logo} Sharpbot v{Version} — Web UI at {Url}", SharpbotInfo.Logo, SharpbotInfo.Version, url);
app.Urls.Add(url);
app.Run();
