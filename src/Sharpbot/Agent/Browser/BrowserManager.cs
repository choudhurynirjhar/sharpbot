using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Sharpbot.Agent.Browser;

/// <summary>
/// Manages the Playwright browser lifecycle: lazy initialization, page/tab
/// management, DOM interaction, and snapshot generation.
/// Thread-safe for initialization; operations are expected to be called
/// sequentially from the agent loop.
/// </summary>
public sealed class BrowserManager : IDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly List<IPage> _pages = [];
    private int _activeTabIndex = -1;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private readonly bool _headless;
    private readonly ILogger? _logger;

    public BrowserManager(bool headless = true, ILogger? logger = null)
    {
        _headless = headless;
        _logger = logger;
    }

    public bool IsInitialized => _initialized;

    // ── Initialization ──────────────────────────────────────

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _logger?.LogInformation("Launching Playwright browser (headless={Headless})...", _headless);

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
            });
            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            });

            _initialized = true;
            _logger?.LogInformation("Browser initialized successfully");
        }
        catch (Exception ex) when (
            ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("browserType.launch", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            _playwright?.Dispose();
            _playwright = null;
            throw new InvalidOperationException(
                "Chromium browser binaries are not installed.\n" +
                "Run one of the following to install:\n" +
                "  pwsh bin/Debug/net9.0/playwright.ps1 install chromium\n" +
                "  npx playwright install chromium", ex);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void CleanClosedPages()
    {
        _pages.RemoveAll(p => p.IsClosed);
        if (_activeTabIndex >= _pages.Count)
            _activeTabIndex = _pages.Count - 1;
    }

    /// <summary>Get the active page, creating one if none exists.</summary>
    public async Task<IPage> GetActivePageAsync()
    {
        await EnsureInitializedAsync();
        CleanClosedPages();

        if (_pages.Count == 0 || _activeTabIndex < 0)
        {
            var page = await _context!.NewPageAsync();
            _pages.Add(page);
            _activeTabIndex = _pages.Count - 1;
        }

        return _pages[_activeTabIndex];
    }

    /// <summary>
    /// Resolve a user-provided selector: plain integers are treated as
    /// element refs (from snapshot), everything else as a CSS selector.
    /// </summary>
    public static string ResolveSelector(string input)
    {
        var trimmed = input.Trim();
        if (int.TryParse(trimmed, out var refNum))
            return $"[data-ref=\"{refNum}\"]";
        return trimmed;
    }

    // ── Navigation ──────────────────────────────────────────

    public async Task<(string Url, string Title, int Status)> NavigateAsync(string url, int? timeoutMs = null)
    {
        var page = await GetActivePageAsync();
        var opts = new PageGotoOptions();
        if (timeoutMs.HasValue) opts.Timeout = timeoutMs.Value;

        var response = await page.GotoAsync(url, opts);
        var title = await page.TitleAsync();
        return (page.Url, title, response?.Status ?? 0);
    }

    public async Task<(string Url, string Title)> GoBackAsync()
    {
        var page = await GetActivePageAsync();
        await page.GoBackAsync();
        var title = await page.TitleAsync();
        return (page.Url, title);
    }

    // ── Snapshot ────────────────────────────────────────────

    /// <summary>
    /// Capture a text-based snapshot of the current page:
    /// visible text content plus a list of interactive elements with ref numbers.
    /// </summary>
    public async Task<(string Title, string Url, string PageText, List<(int Ref, string Desc)> Elements)>
        SnapshotAsync(int maxContentChars = 30000)
    {
        var page = await GetActivePageAsync();
        var title = await page.TitleAsync();
        var url = page.Url;

        // ── Page text ──
        string pageText;
        try
        {
            pageText = await page.InnerTextAsync("body");
        }
        catch
        {
            pageText = "(empty page)";
        }

        pageText = Regex.Replace(pageText, @"[ \t]+", " ");
        pageText = Regex.Replace(pageText, @"\n{3,}", "\n\n");
        pageText = pageText.Trim();
        if (pageText.Length > maxContentChars)
            pageText = pageText[..maxContentChars] + "\n... (truncated)";

        // ── Interactive elements ──
        var elementsJson = await page.EvaluateAsync<string>(TagInteractiveElementsJs);
        var elements = new List<(int Ref, string Desc)>();

        try
        {
            using var doc = JsonDocument.Parse(elementsJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var refNum = item.GetProperty("ref").GetInt32();
                var desc = item.GetProperty("desc").GetString() ?? "";
                elements.Add((refNum, desc));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse interactive elements from snapshot JS");
        }

        return (title, url, pageText, elements);
    }

    // ── Screenshot ──────────────────────────────────────────

    public async Task<string> ScreenshotAsync(string savePath, bool fullPage = false)
    {
        var page = await GetActivePageAsync();

        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = savePath,
            FullPage = fullPage,
            Type = savePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   savePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? ScreenshotType.Jpeg
                : ScreenshotType.Png,
        });

        return Path.GetFullPath(savePath);
    }

    // ── Element Interaction ─────────────────────────────────

    public async Task ClickAsync(string selector, string? button = null, bool doubleClick = false)
    {
        var page = await GetActivePageAsync();
        var resolved = ResolveSelector(selector);
        var opts = new LocatorClickOptions();

        if (button == "right") opts.Button = MouseButton.Right;
        else if (button == "middle") opts.Button = MouseButton.Middle;
        if (doubleClick) opts.ClickCount = 2;

        await page.Locator(resolved).ClickAsync(opts);
    }

    public async Task TypeAsync(string selector, string text, bool clear = true)
    {
        var page = await GetActivePageAsync();
        var resolved = ResolveSelector(selector);
        var locator = page.Locator(resolved);

        if (clear)
            await locator.FillAsync(text);
        else
            await locator.PressSequentiallyAsync(text);
    }

    public async Task SelectOptionAsync(string selector, string value)
    {
        var page = await GetActivePageAsync();
        var resolved = ResolveSelector(selector);
        await page.Locator(resolved).SelectOptionAsync(value);
    }

    public async Task PressKeyAsync(string key)
    {
        var page = await GetActivePageAsync();
        await page.Keyboard.PressAsync(key);
    }

    // ── JavaScript ──────────────────────────────────────────

    public async Task<string> EvaluateAsync(string expression)
    {
        var page = await GetActivePageAsync();
        var result = await page.EvaluateAsync<JsonElement?>(expression);
        if (result is null) return "null";

        return result.Value.ValueKind == JsonValueKind.String
            ? result.Value.GetString() ?? ""
            : result.Value.GetRawText();
    }

    // ── Wait ────────────────────────────────────────────────

    public async Task WaitAsync(string? selector = null, string? text = null, int? timeMs = null)
    {
        var page = await GetActivePageAsync();

        if (timeMs.HasValue)
        {
            await Task.Delay(timeMs.Value);
        }
        else if (!string.IsNullOrEmpty(selector))
        {
            var resolved = ResolveSelector(selector);
            await page.Locator(resolved).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
        else if (!string.IsNullOrEmpty(text))
        {
            await page.GetByText(text).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
    }

    // ── Tab Management ──────────────────────────────────────

    public record TabInfo(int Index, string Title, string Url, bool IsActive);

    public async Task<List<TabInfo>> ListTabsAsync()
    {
        await EnsureInitializedAsync();
        CleanClosedPages();

        var tabs = new List<TabInfo>();
        for (int i = 0; i < _pages.Count; i++)
        {
            string title;
            try { title = await _pages[i].TitleAsync(); }
            catch { title = "(closed)"; }
            tabs.Add(new TabInfo(i, title, _pages[i].Url, i == _activeTabIndex));
        }
        return tabs;
    }

    public async Task<(int Index, string Url)> NewTabAsync(string? url = null)
    {
        await EnsureInitializedAsync();

        var page = await _context!.NewPageAsync();
        _pages.Add(page);
        _activeTabIndex = _pages.Count - 1;

        if (!string.IsNullOrEmpty(url))
            await page.GotoAsync(url);

        return (_activeTabIndex, page.Url);
    }

    public async Task CloseTabAsync(int? index = null)
    {
        await EnsureInitializedAsync();
        CleanClosedPages();

        var idx = index ?? _activeTabIndex;
        if (idx < 0 || idx >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Tab index {idx} is out of range (0..{_pages.Count - 1})");

        await _pages[idx].CloseAsync();
        _pages.RemoveAt(idx);

        if (_activeTabIndex >= _pages.Count)
            _activeTabIndex = _pages.Count - 1;
    }

    public async Task SelectTabAsync(int index)
    {
        await EnsureInitializedAsync();
        CleanClosedPages();

        if (index < 0 || index >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Tab index {index} is out of range (0..{_pages.Count - 1})");

        _activeTabIndex = index;
        await _pages[index].BringToFrontAsync();
    }

    // ── Cleanup ─────────────────────────────────────────────

    public void Dispose()
    {
        if (_browser != null)
        {
            try { _browser.CloseAsync().GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _pages.Clear();
        _activeTabIndex = -1;
        _initialized = false;
        _initLock.Dispose();
    }

    // ── Snapshot JavaScript ─────────────────────────────────
    //
    // Injected into the page to tag interactive elements with data-ref
    // attributes and return their descriptions as JSON.

    private const string TagInteractiveElementsJs = """
    (() => {
        let refId = 0;
        const elements = [];

        // Remove refs from a previous snapshot
        document.querySelectorAll('[data-ref]').forEach(el => el.removeAttribute('data-ref'));

        const sel = [
            'a[href]', 'button', 'input', 'textarea', 'select',
            '[role="button"]', '[role="link"]', '[role="tab"]',
            '[role="checkbox"]', '[role="radio"]', '[role="switch"]',
            '[role="menuitem"]', '[contenteditable="true"]'
        ].join(', ');

        document.querySelectorAll(sel).forEach(el => {
            try {
                const rect = el.getBoundingClientRect();
                if (rect.width === 0 && rect.height === 0) return;
                const style = window.getComputedStyle(el);
                if (style.display === 'none' || style.visibility === 'hidden') return;
                if (parseFloat(style.opacity) === 0) return;
            } catch (e) { return; }

            refId++;
            el.setAttribute('data-ref', String(refId));

            const tag = el.tagName.toLowerCase();
            const role = el.getAttribute('role') || '';
            const ariaLabel = el.getAttribute('aria-label') || '';
            const text = (el.innerText || el.textContent || '').trim().substring(0, 80);
            const name = el.getAttribute('name') || '';
            const placeholder = el.getAttribute('placeholder') || '';
            const type = el.getAttribute('type') || '';
            const href = tag === 'a' ? (el.getAttribute('href') || '').substring(0, 120) : '';
            const value = (el.value || '').substring(0, 80);
            const checked = el.checked;
            const disabled = el.disabled;
            const label = ariaLabel || text || name || placeholder;

            let desc;
            if (tag === 'a') {
                desc = 'link "' + (ariaLabel || text) + '"' + (href ? ' -> ' + href : '');
            } else if (tag === 'button' || role === 'button') {
                desc = 'button "' + (ariaLabel || text) + '"';
            } else if (tag === 'input') {
                const t = type || 'text';
                if (t === 'checkbox' || t === 'radio') {
                    desc = t + ' "' + label + '"' + (checked ? ' [checked]' : ' [unchecked]');
                } else if (t === 'submit') {
                    desc = 'submit button "' + (el.value || label) + '"';
                } else {
                    desc = t + ' input "' + (ariaLabel || name || placeholder) + '"';
                    if (value) desc += ' value="' + value + '"';
                }
            } else if (tag === 'textarea') {
                desc = 'textarea "' + (ariaLabel || name || placeholder) + '"';
                if (value) desc += ' value="' + value.substring(0, 50) + '"';
            } else if (tag === 'select') {
                const opt = (el.selectedIndex >= 0 && el.options[el.selectedIndex])
                    ? el.options[el.selectedIndex].text : '';
                desc = 'select "' + (ariaLabel || name) + '" selected="' + opt + '"';
            } else {
                desc = (role || tag) + ' "' + label + '"';
            }

            if (disabled) desc += ' [disabled]';
            elements.push({ ref: refId, desc });
        });

        return JSON.stringify(elements);
    })()
    """;
}
