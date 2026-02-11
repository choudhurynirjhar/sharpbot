using System.Text;
using System.Text.Json;
using Sharpbot.Agent.Browser;

namespace Sharpbot.Agent.Tools;

// ═══════════════════════════════════════════════════════════════════
//  Browser Automation Tools
//
//  A suite of tools that give the agent full browser control via
//  Playwright: navigate, inspect, click, type, screenshot, and more.
//  Interactive elements are tagged with [ref] numbers by the snapshot
//  tool so subsequent click/type calls can target them by ref.
// ═══════════════════════════════════════════════════════════════════

// ── browser_navigate ────────────────────────────────────────────

/// <summary>Navigate the browser to a URL.</summary>
public sealed class BrowserNavigateTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserNavigateTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_navigate";

    public override string Description =>
        "Navigate the browser to a URL. Launches a browser on first use.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["url"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The URL to navigate to",
            },
        },
        ["required"] = new[] { "url" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url is required";

        try
        {
            var (finalUrl, title, status) = await _browser.NavigateAsync(url);
            return JsonSerializer.Serialize(new { url = finalUrl, title, status });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

// ── browser_snapshot ────────────────────────────────────────────

/// <summary>
/// Get a text-based snapshot of the current page: readable content
/// plus interactive elements tagged with [ref] numbers.
/// </summary>
public sealed class BrowserSnapshotTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserSnapshotTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_snapshot";

    public override string Description =>
        "Capture a text snapshot of the current page showing its content and all interactive " +
        "elements with [ref] numbers. Use the ref numbers with browser_click or browser_type.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>(),
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        try
        {
            var (title, url, pageText, elements) = await _browser.SnapshotAsync();

            var sb = new StringBuilder();
            sb.AppendLine($"Page: {title}");
            sb.AppendLine($"URL: {url}");
            sb.AppendLine();
            sb.AppendLine("--- Page Content ---");
            sb.AppendLine(pageText);
            sb.AppendLine();
            sb.AppendLine($"--- Interactive Elements ({elements.Count}) ---");

            foreach (var (refNum, desc) in elements)
                sb.AppendLine($"  [{refNum}] {desc}");

            if (elements.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Tip: use [ref] numbers with browser_click or browser_type to interact.");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

// ── browser_screenshot ──────────────────────────────────────────

/// <summary>Take a screenshot of the current page and save it to a file.</summary>
public sealed class BrowserScreenshotTool : ToolBase
{
    private readonly BrowserManager _browser;
    private readonly string _workspace;

    public BrowserScreenshotTool(BrowserManager browser, string workspace)
    {
        _browser = browser;
        _workspace = workspace;
    }

    public override string Name => "browser_screenshot";

    public override string Description =>
        "Capture a screenshot of the current browser page and save it to a file. " +
        "Returns the path to the saved image.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["filename"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "File name or path for the screenshot (default: auto-generated .png in workspace)",
            },
            ["full_page"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Capture the full scrollable page instead of just the viewport (default: false)",
            },
        },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var filename = GetString(args, "filename");
        if (string.IsNullOrWhiteSpace(filename))
            filename = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";

        // Resolve relative paths against the workspace
        var path = Path.IsPathRooted(filename)
            ? filename
            : Path.Combine(_workspace, "screenshots", filename);

        var fullPage = GetBool(args, "full_page");

        try
        {
            var savedPath = await _browser.ScreenshotAsync(path, fullPage);
            return JsonSerializer.Serialize(new { path = savedPath });
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

// ── browser_click ───────────────────────────────────────────────

/// <summary>Click an element on the page.</summary>
public sealed class BrowserClickTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserClickTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_click";

    public override string Description =>
        "Click an element on the page. Use a [ref] number from browser_snapshot or a CSS selector.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["selector"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Element ref number (e.g. \"3\") from snapshot, or a CSS selector",
            },
            ["button"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Mouse button: left (default), right, or middle",
                ["enum"] = new[] { "left", "right", "middle" },
            },
            ["double_click"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Perform a double-click (default: false)",
            },
        },
        ["required"] = new[] { "selector" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var selector = GetString(args, "selector");
        if (string.IsNullOrWhiteSpace(selector))
            return "Error: selector is required";

        var button = GetString(args, "button", "left");
        var doubleClick = GetBool(args, "double_click");

        try
        {
            await _browser.ClickAsync(selector, button, doubleClick);
            return $"Clicked element: {selector}";
        }
        catch (Exception ex)
        {
            return $"Error clicking '{selector}': {ex.Message}";
        }
    }
}

// ── browser_type ────────────────────────────────────────────────

/// <summary>Type text into an input element on the page.</summary>
public sealed class BrowserTypeTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserTypeTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_type";

    public override string Description =>
        "Type text into an input, textarea, or contenteditable element. " +
        "Use a [ref] number from browser_snapshot or a CSS selector.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["selector"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Element ref number or CSS selector",
            },
            ["text"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The text to type",
            },
            ["clear"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Clear the field before typing (default: true). Set false to append.",
            },
            ["submit"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Press Enter after typing to submit (default: false)",
            },
        },
        ["required"] = new[] { "selector", "text" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var selector = GetString(args, "selector");
        var text = GetString(args, "text");
        if (string.IsNullOrWhiteSpace(selector))
            return "Error: selector is required";

        var clear = GetBool(args, "clear", defaultValue: true);
        var submit = GetBool(args, "submit");

        try
        {
            await _browser.TypeAsync(selector, text, clear);
            if (submit)
                await _browser.PressKeyAsync("Enter");
            return $"Typed into element: {selector}";
        }
        catch (Exception ex)
        {
            return $"Error typing into '{selector}': {ex.Message}";
        }
    }
}

// ── browser_select ──────────────────────────────────────────────

/// <summary>Select an option in a dropdown/select element.</summary>
public sealed class BrowserSelectTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserSelectTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_select";

    public override string Description =>
        "Select an option in a <select> dropdown. Use a [ref] number or CSS selector.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["selector"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Element ref number or CSS selector for the <select> element",
            },
            ["value"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The option value or visible text to select",
            },
        },
        ["required"] = new[] { "selector", "value" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var selector = GetString(args, "selector");
        var value = GetString(args, "value");
        if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(value))
            return "Error: selector and value are required";

        try
        {
            await _browser.SelectOptionAsync(selector, value);
            return $"Selected '{value}' in element: {selector}";
        }
        catch (Exception ex)
        {
            return $"Error selecting option in '{selector}': {ex.Message}";
        }
    }
}

// ── browser_press_key ───────────────────────────────────────────

/// <summary>Press a keyboard key.</summary>
public sealed class BrowserPressKeyTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserPressKeyTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_press_key";

    public override string Description =>
        "Press a keyboard key (e.g. Enter, Escape, Tab, ArrowDown, Backspace, " +
        "Control+a, Shift+Tab). Acts on the currently focused element.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["key"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Key name or combination (e.g. \"Enter\", \"Escape\", \"Control+a\")",
            },
        },
        ["required"] = new[] { "key" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var key = GetString(args, "key");
        if (string.IsNullOrWhiteSpace(key))
            return "Error: key is required";

        try
        {
            await _browser.PressKeyAsync(key);
            return $"Pressed key: {key}";
        }
        catch (Exception ex)
        {
            return $"Error pressing key '{key}': {ex.Message}";
        }
    }
}

// ── browser_evaluate ────────────────────────────────────────────

/// <summary>Evaluate a JavaScript expression on the current page.</summary>
public sealed class BrowserEvaluateTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserEvaluateTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_evaluate";

    public override string Description =>
        "Evaluate a JavaScript expression on the current page and return its result. " +
        "Use JSON.stringify() for objects. Expression must be a single expression or IIFE.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["expression"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "JavaScript expression to evaluate (e.g. \"document.title\", \"(() => { ... })()\")",
            },
        },
        ["required"] = new[] { "expression" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var expression = GetString(args, "expression");
        if (string.IsNullOrWhiteSpace(expression))
            return "Error: expression is required";

        try
        {
            var result = await _browser.EvaluateAsync(expression);

            // Truncate very large results
            if (result.Length > 50_000)
                result = result[..50_000] + "\n... (truncated)";

            return result;
        }
        catch (Exception ex)
        {
            return $"Error evaluating JS: {ex.Message}";
        }
    }
}

// ── browser_wait ────────────────────────────────────────────────

/// <summary>Wait for an element, text, or a fixed duration.</summary>
public sealed class BrowserWaitTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserWaitTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_wait";

    public override string Description =>
        "Wait for a condition: an element to appear (selector), text to appear, " +
        "or a fixed time in milliseconds. Provide exactly one of the three options.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["selector"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "CSS selector or ref number to wait for",
            },
            ["text"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Text content to wait for on the page",
            },
            ["time"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Time to wait in milliseconds",
                ["minimum"] = 0,
                ["maximum"] = 30000,
            },
        },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var selector = GetString(args, "selector");
        var text = GetString(args, "text");
        var time = GetInt(args, "time");

        if (string.IsNullOrWhiteSpace(selector) && string.IsNullOrWhiteSpace(text) && time == null)
            return "Error: provide at least one of selector, text, or time";

        try
        {
            await _browser.WaitAsync(
                selector: string.IsNullOrWhiteSpace(selector) ? null : selector,
                text: string.IsNullOrWhiteSpace(text) ? null : text,
                timeMs: time);

            if (!string.IsNullOrWhiteSpace(selector))
                return $"Element '{selector}' is now visible";
            if (!string.IsNullOrWhiteSpace(text))
                return $"Text '{text}' is now visible";
            return $"Waited {time}ms";
        }
        catch (Exception ex)
        {
            return $"Error waiting: {ex.Message}";
        }
    }
}

// ── browser_tabs ────────────────────────────────────────────────

/// <summary>Manage browser tabs: list, open new, close, or switch.</summary>
public sealed class BrowserTabsTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserTabsTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_tabs";

    public override string Description =>
        "Manage browser tabs. Actions: \"list\" (show open tabs), \"new\" (open tab), " +
        "\"close\" (close tab), \"select\" (switch to tab).";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["action"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Tab action to perform",
                ["enum"] = new[] { "list", "new", "close", "select" },
            },
            ["index"] = new Dictionary<string, object?>
            {
                ["type"] = "integer",
                ["description"] = "Tab index for close/select actions",
            },
            ["url"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "URL to open in a new tab (for 'new' action)",
            },
        },
        ["required"] = new[] { "action" },
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        var action = GetString(args, "action").ToLowerInvariant();
        var index = GetInt(args, "index");
        var url = GetString(args, "url");

        try
        {
            switch (action)
            {
                case "list":
                {
                    var tabs = await _browser.ListTabsAsync();
                    if (tabs.Count == 0) return "No tabs open";

                    var sb = new StringBuilder();
                    sb.AppendLine($"Open tabs ({tabs.Count}):");
                    foreach (var tab in tabs)
                    {
                        var marker = tab.IsActive ? " *" : "";
                        sb.AppendLine($"  [{tab.Index}] {tab.Title} — {tab.Url}{marker}");
                    }
                    return sb.ToString();
                }

                case "new":
                {
                    var (newIndex, newUrl) = await _browser.NewTabAsync(
                        string.IsNullOrWhiteSpace(url) ? null : url);
                    return $"Opened new tab [{newIndex}]: {newUrl}";
                }

                case "close":
                {
                    await _browser.CloseTabAsync(index);
                    return index.HasValue
                        ? $"Closed tab [{index.Value}]"
                        : "Closed active tab";
                }

                case "select":
                {
                    if (!index.HasValue)
                        return "Error: index is required for select action";
                    await _browser.SelectTabAsync(index.Value);
                    return $"Switched to tab [{index.Value}]";
                }

                default:
                    return $"Error: unknown action '{action}'. Use list, new, close, or select.";
            }
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}

// ── browser_back ────────────────────────────────────────────────

/// <summary>Navigate back in the browser history.</summary>
public sealed class BrowserBackTool : ToolBase
{
    private readonly BrowserManager _browser;

    public BrowserBackTool(BrowserManager browser) => _browser = browser;

    public override string Name => "browser_back";

    public override string Description =>
        "Go back to the previous page in the browser history.";

    public override Dictionary<string, object?> Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>(),
    };

    public override async Task<string> ExecuteAsync(Dictionary<string, object?> args)
    {
        try
        {
            var (url, title) = await _browser.GoBackAsync();
            return JsonSerializer.Serialize(new { url, title });
        }
        catch (Exception ex)
        {
            return $"Error going back: {ex.Message}";
        }
    }
}
