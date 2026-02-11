# Sharpbot

**A lightweight personal AI assistant framework — .NET 9 Edition**

Sharpbot is a modular, extensible AI agent framework built with C#/.NET 9. It can reason, use tools, browse the web, remember context, and communicate through multiple chat channels — all running locally with a built-in web UI.

---

## What Sharpbot Does

- **Reasons and acts** through an iterative agent loop — receives a message, calls an LLM, and executes tool calls until the task is complete
- **Uses tools** to interact with the world: read/write files, run shell commands, search the web, browse pages with a full Playwright-powered browser, send chat messages, spawn sub-agents, and schedule cron jobs
- **Remembers** important information across sessions using markdown-based memory files and daily notes
- **Learns skills** from markdown skill files that extend its capabilities without code changes
- **Connects to chat apps** — Telegram, WhatsApp, Discord, Feishu, and Slack — through a decoupled async message bus
- **Runs scheduled tasks** via cron-style job scheduling with automatic delivery of results
- **Supports 11 LLM providers** — OpenAI, Anthropic, Gemini, OpenRouter, DeepSeek, Groq, Moonshot, Zhipu, DashScope, AiHubMix, and vLLM — through a unified OpenAI-compatible API interface
- **Tracks usage** with built-in telemetry: token counts, tool call metrics, latency, and cost breakdowns by model/channel/day
- **Ships with a web UI** for chatting, viewing status, managing settings, cron jobs, channels, skills, and usage — all from a single-page dashboard

---

## Installation & Usage

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for building from source)
- An API key from any supported LLM provider (e.g. [Gemini](https://aistudio.google.com/apikey), [OpenRouter](https://openrouter.ai/keys), [OpenAI](https://platform.openai.com/api-keys))

### Option 1 — Run from Source

```bash
# Clone and build
git clone https://github.com/choudhurynirjhar/sharpbot.git
cd sharpbot
dotnet build

# Run the onboard wizard (creates ~/.sharpbot/appsettings.json and workspace)
dotnet run --project src/Sharpbot -- onboard

# Start the web server + agent (default)
dotnet run --project src/Sharpbot
```

Open **http://localhost:56789** in your browser to access the web UI.

### Option 2 — Docker

```bash
# Build the image
docker build -t sharpbot .

# Run with a persistent data volume
docker run -d \
  -p 56789:56789 \
  -v sharpbot-data:/app/data \
  -e SHARPBOT_Providers__Gemini__ApiKey="your-api-key" \
  --name sharpbot \
  sharpbot
```

Open **http://localhost:56789** to access the web UI.

To use docker compose, create a `docker-compose.yml`:

```yaml
services:
  sharpbot:
    build: .
    ports:
      - "56789:56789"
    volumes:
      - sharpbot-data:/app/data
    environment:
      - SHARPBOT_Providers__Gemini__ApiKey=your-api-key
    restart: unless-stopped

volumes:
  sharpbot-data:
```

Then run:

```bash
docker compose up -d
```

### Configuration

Sharpbot uses the standard .NET `IConfiguration` system with layered sources:

1. `appsettings.json` next to the executable (packaged defaults — non-secret settings only)
2. `data/appsettings.json` (user-level overrides — non-secret settings only)
3. **Environment variables** prefixed with `SHARPBOT_` — **required for all secrets** (API keys, tokens)

> **All secrets (API keys, tokens, signing secrets) are loaded exclusively from environment variables.** They are never read from or saved to config files.

Set your LLM provider API key via environment variable:

```powershell
# PowerShell
$env:SHARPBOT_Providers__Gemini__ApiKey = "your-key"
```

```bash
# Bash / Linux / macOS
export SHARPBOT_Providers__Gemini__ApiKey="your-key"
```

Edit `data/appsettings.json` for non-secret settings (model, temperature, etc.):

```json
{
  "Agents": {
    "Defaults": {
      "Model": "gemini-2.5-flash",
      "MaxTokens": 8192,
      "Temperature": 0.7
    }
  }
}
```

#### All Secret Environment Variables

| Variable | Description |
|----------|-------------|
| `SHARPBOT_Providers__Anthropic__ApiKey` | Anthropic API key |
| `SHARPBOT_Providers__OpenAI__ApiKey` | OpenAI API key |
| `SHARPBOT_Providers__OpenRouter__ApiKey` | OpenRouter API key |
| `SHARPBOT_Providers__DeepSeek__ApiKey` | DeepSeek API key |
| `SHARPBOT_Providers__Groq__ApiKey` | Groq API key |
| `SHARPBOT_Providers__Gemini__ApiKey` | Gemini API key |
| `SHARPBOT_Providers__Zhipu__ApiKey` | Zhipu API key |
| `SHARPBOT_Providers__DashScope__ApiKey` | DashScope API key |
| `SHARPBOT_Providers__Moonshot__ApiKey` | Moonshot API key |
| `SHARPBOT_Providers__AiHubMix__ApiKey` | AiHubMix API key |
| `SHARPBOT_Providers__Vllm__ApiKey` | vLLM API key |
| `SHARPBOT_Channels__Telegram__Token` | Telegram bot token |
| `SHARPBOT_Channels__Discord__Token` | Discord bot token |
| `SHARPBOT_Channels__Feishu__AppSecret` | Feishu app secret |
| `SHARPBOT_Channels__Feishu__EncryptKey` | Feishu encrypt key |
| `SHARPBOT_Channels__Feishu__VerificationToken` | Feishu verification token |
| `SHARPBOT_Channels__Slack__BotToken` | Slack bot token (xoxb-...) |
| `SHARPBOT_Channels__Slack__AppToken` | Slack app-level token (xapp-...) |
| `SHARPBOT_Channels__Slack__SigningSecret` | Slack signing secret |
| `SHARPBOT_Tools__Web__Search__ApiKey` | Brave Search API key |

### CLI Commands

Sharpbot can also be used directly from the command line:

```bash
# Single message
dotnet run --project src/Sharpbot -- agent -m "Hello, what can you do?"

# Interactive REPL mode
dotnet run --project src/Sharpbot -- agent

# Start the full gateway (agent + channels + cron + heartbeat)
dotnet run --project src/Sharpbot -- gateway

# Check system status
dotnet run --project src/Sharpbot -- status

# View channel configuration
dotnet run --project src/Sharpbot -- channels status

# WhatsApp QR login
dotnet run --project src/Sharpbot -- channels login

# Manage scheduled tasks
dotnet run --project src/Sharpbot -- cron list
dotnet run --project src/Sharpbot -- cron add -n "Daily summary" -m "Summarize today's notes" -c "0 18 * * *"
dotnet run --project src/Sharpbot -- cron remove <job-id>
```

---

## Code Workflow

Here's how a message flows through the system end-to-end:

```
  User / Chat App / Web UI
       |
       v
  +----------------+
  |    Channel      |  Telegram, WhatsApp, Discord, Feishu, Slack, Web, or CLI
  |    Manager      |  Receives incoming messages
  +--------+-------+
           | InboundMessage
           v
  +----------------+
  |  Message Bus    |  Async Channel<T> queue decoupling channels from the agent
  +--------+-------+
           |
           v
  +--------------------------------------------------+
  |                    Agent Loop                      |
  |                                                    |
  |  1. Receive message from bus                       |
  |  2. Load/create session (SessionManager)           |
  |  3. Build context:                                 |
  |     +-- System prompt (AGENTS.md, SOUL.md, etc.)   |
  |     +-- Memory (MEMORY.md + daily notes)           |
  |     +-- Skills (discovered SKILL.md files)         |
  |     +-- Conversation history                       |
  |  4. Call LLM via provider                          |
  |  5. If tool calls returned:                        |
  |     +-- Execute each tool via ToolRegistry         |
  |     +-- Append results to context                  |
  |     +-- Go to step 4 (iterate)                     |
  |  6. Record telemetry (tokens, duration, tools)     |
  |  7. Return final text response                     |
  +--------------------+-------------------------------+
                       | OutboundMessage
                       v
                +----------------+
                |  Message Bus    |
                +--------+-------+
                         |
                         v
                +----------------+
                |    Channel      |  Routes reply back to the originating chat
                |    Manager      |
                +----------------+
```

---

## Chat Channels

Sharpbot supports 5 chat channels, all sharing the same `BaseChannel` interface and `MessageBus` architecture:

| Channel | Connection Method | Key Features |
|---------|-------------------|--------------|
| **Telegram** | Long polling via `Telegram.Bot` SDK | `/start`, `/reset`, `/help` commands; photo/voice/document media; typing indicators; Markdown→HTML conversion; proxy support |
| **Discord** | Gateway WebSocket + REST API | Heartbeat management; typing indicators; attachment downloads (up to 20 MB); rate limit handling with retry; guild context |
| **WhatsApp** | WebSocket bridge to Node.js (`@whiskeysockets/baileys`) | QR code login via CLI; send/receive through bridge protocol |
| **Feishu** | REST API + event webhook | Tenant access token refresh; markdown+table card messages; event deduplication; reaction support |
| **Slack** | Socket Mode (WebSocket) or HTTP Events API | Bot mention stripping; thread-aware replies; file attachment downloads; message chunking (4000 char limit); HMAC-SHA256 signature verification; rate limit retry with exponential backoff |

All channels support `AllowFrom` sender allowlists for access control.

### Slack Channel Setup

Slack supports two connection modes:

**Socket Mode** (recommended — no public URL required):
1. Create a Slack app at [api.slack.com/apps](https://api.slack.com/apps)
2. Enable **Socket Mode** and generate an App-Level Token (`xapp-...`) with `connections:write` scope
3. Add a Bot Token (`xoxb-...`) with scopes: `chat:write`, `channels:read`, `im:read`, `im:write`, `im:history`, `channels:history`, `users:read`, `files:read`, `reactions:read`, `app_mentions:read`
4. Subscribe to events: `message.im`, `message.channels`, `app_mention`
5. Install the app to your workspace

**HTTP Events API** (requires a public URL):
1. Create a Slack app and Bot Token as above
2. Set up a **Signing Secret** under Basic Information
3. Configure the Request URL to `https://your-domain/slack/events`
4. Subscribe to the same events

Non-secret settings in `appsettings.json`:

```json
{
  "Channels": {
    "Slack": {
      "Enabled": true,
      "Mode": "socket",
      "WebhookPath": "/slack/events",
      "AllowFrom": [],
      "TextChunkLimit": 3900,
      "ReplyInThread": "always"
    }
  }
}
```

Secrets via environment variables:

```bash
export SHARPBOT_Channels__Slack__BotToken="xoxb-..."
export SHARPBOT_Channels__Slack__AppToken="xapp-..."
export SHARPBOT_Channels__Slack__SigningSecret="your-signing-secret"
```

---

## Architecture Overview

| Module | Path | Responsibility |
|--------|------|----------------|
| **Program** | `Program.cs` | CLI + ASP.NET 9 web server entry point |
| **Config** | `Config/` | Layered `IConfiguration` loading; config schema for providers, channels, tools |
| **Bus** | `Bus/` | `InboundMessage` / `OutboundMessage` records; async `MessageBus` using `Channel<T>` |
| **Providers** | `Providers/` | `ILlmProvider` interface; `OpenAiCompatibleProvider` using OpenAI SDK; `ProviderRegistry` for 11 LLM services |
| **Agent Loop** | `Agent/AgentLoop.cs` | Core reasoning loop — message -> LLM -> tool calls -> response |
| **Context Builder** | `Agent/ContextBuilder.cs` | Assembles system prompt from bootstrap files, memory, skills, and conversation history |
| **Memory Store** | `Agent/MemoryStore.cs` | Manages `MEMORY.md` (long-term) and `YYYY-MM-DD.md` (daily notes) |
| **Skills Loader** | `Agent/SkillsLoader.cs` | Discovers and loads agent skills from `SKILL.md` files |
| **Subagent Manager** | `Agent/SubagentManager.cs` | Spawns background agent instances with isolated tool sets |
| **Tools** | `Agent/Tools/` | 15+ built-in tools: file I/O, shell, web search, web fetch, browser automation, HTTP requests, messaging, spawn, cron, load-skill |
| **Browser** | `Agent/Browser/` | Playwright-based headless browser manager for full web automation |
| **Tool Registry** | `Agent/Tools/ToolRegistry.cs` | Dynamic registration and execution of tools by name |
| **Session Manager** | `Session/` | SQLite-backed conversation persistence per channel+chat |
| **Cron Service** | `Cron/` | Scheduled job engine — supports `every N`, cron expressions, and one-time jobs |
| **Heartbeat** | `Heartbeat/` | Periodic agent wake-up to check `HEARTBEAT.md` for pending tasks |
| **Channels** | `Channels/` | `BaseChannel` abstract class and `ChannelManager` for Telegram, WhatsApp, Discord, Feishu, Slack |
| **Web UI** | `wwwroot/` | Single-page dashboard — chat, settings, cron, channels, skills, usage, sessions, and logs |
| **API** | `Api/` | REST endpoints for chat, status, config, cron, channels, skills, logs, usage, and Slack webhook |
| **Database** | `Database/` | SQLite persistence with WAL mode for sessions, messages, usage, cron jobs, and logs |
| **Telemetry** | `Telemetry/` | OpenTelemetry tracing/metrics + SQLite-backed usage store |
| **Logging** | `Logging/` | Ring buffer log capture for the web UI's live log viewer |
| **CLI** | `Commands/` | `System.CommandLine`-based CLI: `onboard`, `gateway`, `agent`, `channels`, `cron`, `status` |

---

## Project Structure

```
sharpbot-dotnet/
├── Sharpbot.sln
├── Dockerfile
├── .dockerignore
└── src/Sharpbot/
    ├── Sharpbot.csproj
    ├── Program.cs                     # CLI + web server entry point
    ├── SharpbotInfo.cs                # Version + logo
    ├── appsettings.json               # Default configuration
    ├── Config/
    │   ├── ConfigSchema.cs            # Full config model
    │   ├── ConfigLoader.cs            # IConfiguration layered loading
    │   └── ConfigMigrator.cs          # Config format migration
    ├── Bus/
    │   ├── Events.cs                  # Message records
    │   └── MessageBus.cs              # Async message queue
    ├── Providers/
    │   ├── ILlmProvider.cs            # LLM interface
    │   ├── ProviderRegistry.cs        # Provider metadata (11 providers)
    │   ├── ProviderResolver.cs        # Auto-detect provider from model name
    │   └── OpenAiCompatibleProvider.cs # OpenAI SDK client
    ├── Agent/
    │   ├── AgentLoop.cs               # Core agent loop
    │   ├── AgentLoopOptions.cs        # Agent configuration options
    │   ├── AgentTelemetry.cs          # Per-turn telemetry collection
    │   ├── ContextBuilder.cs          # Prompt assembly
    │   ├── MemoryStore.cs             # Memory management
    │   ├── SkillsLoader.cs            # Skill discovery
    │   ├── SubagentManager.cs         # Background agents
    │   ├── Browser/
    │   │   └── BrowserManager.cs      # Playwright browser lifecycle
    │   └── Tools/
    │       ├── ITool.cs / ToolBase.cs # Tool interface
    │       ├── ToolRegistry.cs        # Tool registration
    │       ├── FileSystemTools.cs     # File I/O (read, write, edit, list)
    │       ├── ShellTool.cs           # Shell execution
    │       ├── WebTools.cs            # Web search + fetch
    │       ├── BrowserTools.cs        # Browser automation (11 tools)
    │       ├── HttpRequestTool.cs     # Raw HTTP requests
    │       ├── MessageTool.cs         # Chat messaging
    │       ├── SpawnTool.cs           # Subagent spawning
    │       ├── CronTool.cs            # Job scheduling
    │       └── LoadSkillTool.cs       # Dynamic skill loading
    ├── Session/
    │   └── SessionManager.cs          # Conversation history (SQLite)
    ├── Database/
    │   └── SharpbotDb.cs              # SQLite database (sessions, messages, usage, cron, logs)
    ├── Cron/
    │   ├── CronTypes.cs               # Job data models
    │   ├── CronService.cs             # Job scheduler
    │   └── ScheduleKinds.cs           # Schedule type definitions
    ├── Heartbeat/
    │   └── HeartbeatService.cs        # Periodic wake-up
    ├── Channels/
    │   ├── IChannel.cs                # Channel base class
    │   ├── ChannelManager.cs          # Multi-channel router
    │   ├── TelegramChannel.cs         # Telegram bot (long polling)
    │   ├── DiscordChannel.cs          # Discord gateway (WebSocket)
    │   ├── WhatsAppChannel.cs         # WhatsApp bridge (WebSocket)
    │   ├── FeishuChannel.cs           # Feishu/Lark (REST + webhook)
    │   └── SlackChannel.cs            # Slack (Socket Mode + Events API)
    ├── Services/
    │   ├── SharpbotHostedService.cs   # ASP.NET hosted service (orchestrator)
    │   └── SharpbotServiceFactory.cs  # Service/provider factory
    ├── Api/                           # REST API endpoints
    │   ├── ChatApi.cs                 # /api/chat, /api/chat/sessions
    │   ├── StatusApi.cs               # /api/status
    │   ├── ConfigApi.cs               # /api/config, /api/config/onboard
    │   ├── CronApi.cs                 # /api/cron
    │   ├── ChannelsApi.cs             # /api/channels
    │   ├── SkillsApi.cs               # /api/skills
    │   ├── LogsApi.cs                 # /api/logs
    │   ├── UsageApi.cs                # /api/usage, /api/usage/history
    │   └── SlackEventsApi.cs          # /slack/events (Slack HTTP mode webhook)
    ├── Telemetry/
    │   ├── SharpbotInstrumentation.cs # OpenTelemetry setup
    │   └── UsageStore.cs              # SQLite usage persistence
    ├── Logging/
    │   └── LogRingBuffer.cs           # In-memory log ring buffer
    ├── wwwroot/                       # Web UI (SPA)
    │   ├── index.html
    │   ├── css/app.css
    │   └── js/app.js
    ├── skills/                        # Built-in skills
    │   ├── cron/, github/, weather/,
    │   ├── summarize/, tmux/, skill-creator/
    └── data/                          # Default runtime data
        ├── workspace/, appsettings.json
```

---

## Agent Tools

The agent has access to a rich set of built-in tools:

| Category | Tools | Description |
|----------|-------|-------------|
| **File System** | `read_file`, `write_file`, `edit_file`, `list_dir` | Read, write, edit files and list directories within the workspace |
| **Shell** | `exec` | Execute shell commands with configurable timeout |
| **Web** | `web_search`, `web_fetch` | Search the web via Brave Search API; fetch and parse web pages |
| **HTTP** | `http_request` | Make arbitrary HTTP requests (GET, POST, PUT, DELETE, etc.) |
| **Browser** | `browser_navigate`, `browser_snapshot`, `browser_screenshot`, `browser_click`, `browser_type`, `browser_select`, `browser_press_key`, `browser_evaluate`, `browser_wait`, `browser_tabs`, `browser_back` | Full Playwright-powered headless browser automation |
| **Messaging** | `message` | Send messages to chat channels |
| **Subagents** | `spawn` | Spawn background agent instances with isolated tool sets |
| **Scheduling** | `cron` | Create, list, and manage scheduled jobs |
| **Skills** | `load_skill` | Dynamically load and activate skill files |

---

## Supported LLM Providers

| Provider | Keywords | Type |
|----------|----------|------|
| OpenRouter | `openrouter` | Gateway |
| AiHubMix | `aihubmix` | Gateway |
| Anthropic | `anthropic`, `claude` | Cloud |
| OpenAI | `openai`, `gpt` | Cloud |
| DeepSeek | `deepseek` | Cloud |
| Gemini | `gemini` | Cloud |
| Zhipu AI | `zhipu`, `glm` | Cloud |
| DashScope | `qwen`, `dashscope` | Cloud |
| Moonshot | `moonshot`, `kimi` | Cloud |
| Groq | `groq` | Cloud |
| vLLM | `vllm` | Local |

Provider detection is automatic based on model name keywords and API key prefixes.

---

## Built-in Skills

| Skill | Description |
|-------|-------------|
| `cron` | Schedule reminders and recurring tasks using the cron tool |
| `github` | GitHub CLI interactions (issues, PRs, CI runs) |
| `weather` | Weather info via wttr.in and Open-Meteo APIs |
| `summarize` | Summarize URLs, files, and YouTube videos |
| `tmux` | Tmux session management scripts |
| `skill-creator` | Create new skills from templates |

Skills are markdown files (`SKILL.md`) that extend the agent without code changes. Workspace skills override built-in skills of the same name.

---

## REST API

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/chat` | POST | Send a chat message to the agent |
| `/api/chat/sessions` | GET | List all sessions |
| `/api/chat/sessions/{key}` | DELETE | Delete a session |
| `/api/status` | GET | System status overview |
| `/api/config` | GET | Get current configuration (API keys masked) |
| `/api/config` | PUT | Update configuration |
| `/api/config/onboard` | POST | Initialize config and workspace |
| `/api/cron` | GET | List scheduled jobs |
| `/api/cron` | POST | Create a scheduled job |
| `/api/cron/{id}` | DELETE | Remove a scheduled job |
| `/api/cron/{id}/enable` | PUT | Enable/disable a job |
| `/api/cron/{id}/run` | POST | Trigger a job immediately |
| `/api/channels` | GET | Channel status and configuration |
| `/api/skills` | GET | List available skills |
| `/api/skills/{name}` | GET | Get skill details |
| `/api/logs` | GET | Retrieve logs (filterable by level) |
| `/api/logs` | DELETE | Clear logs |
| `/api/usage` | GET | Usage summary with breakdowns |
| `/api/usage/history` | GET | Usage history over time |
| `/api/usage` | DELETE | Clear usage data |
| `/slack/events` | POST | Slack Events API webhook (HTTP mode only) |

---

## Database

Sharpbot uses a single SQLite database with WAL mode for persistence:

| Table | Purpose |
|-------|---------|
| `sessions` | Session keys with creation/update timestamps and metadata |
| `messages` | Per-session message history (role, content, timestamp) |
| `usage` | Telemetry records: channel, model, tokens, tool calls, duration |
| `usage_tools` | Per-usage tool call breakdown |
| `cron_jobs` | Scheduled jobs with schedule, payload, state, and run history |
| `logs` | Persistent log entries (timestamp, level, category, message, exception) |

The database is stored in a persistent user-level location so data survives app rebuilds.

---

## Web UI

The built-in single-page dashboard at `http://localhost:56789` includes:

- **Chat** — Interactive conversation with the agent, markdown rendering, code highlighting
- **Settings** — Configure model, parameters, provider API keys, tool settings
- **Sessions** — View, switch, and manage conversation sessions
- **Skills** — Browse and search available skills with status indicators
- **Cron Jobs** — Create and manage scheduled tasks with cron expressions or interval schedules
- **Channels** — View status of all chat channels (Telegram, Discord, WhatsApp, Feishu, Slack)
- **Logs** — Live log viewer with level filtering, search, and auto-refresh
- **Usage** — Token usage analytics with breakdowns by model, channel, and day; CSV/JSON export
- **Status** — System overview and health check

---

## Acknowledgments

Sharpbot is inspired by [OpenClaw](https://github.com/openclaw/openclaw), the popular open-source personal AI assistant. Core design principles — the agent loop, multi-channel message bus, skill-based extensibility, session management, and local-first philosophy — are drawn from OpenClaw's architecture. Sharpbot re-implements these ideas in C#/.NET 9 for the .NET ecosystem.

---

## License

[MIT License](https://choosealicense.com/licenses/mit/)
