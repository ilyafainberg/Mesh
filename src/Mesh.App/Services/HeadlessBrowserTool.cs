using System.Text.Json;
using Microsoft.Playwright;
using IBrowser = Microsoft.Playwright.IBrowser;
using IPage = Microsoft.Playwright.IPage;
using IPlaywright = Microsoft.Playwright.IPlaywright;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated browser-automation tool backed by Playwright, running HEADLESS (no visible window).
/// Like <see cref="BrowserTool"/> the browser PERSISTS across calls: a single shared Chromium browser
/// and page are created lazily on first use and reused for the whole app session, so the agent can
/// navigate then act on the same page over multiple calls. Keeps its own separate static state so it
/// does not interfere with the headed browser tool.
/// </summary>
public sealed class HeadlessBrowserTool(AgentMedia media) : IAgentTool
{
    public string Name => "headless_browser";
    public string Description =>
        "Control a background headless web browser (Playwright, invisible): navigate to URLs, read " +
        "page text or HTML, click, type, press keys, screenshot, and evaluate JavaScript. Like the " +
        "browser tool but runs without a visible window.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            action = new
            {
                type = "string",
                description = "One of: navigate, text, html, click, type, press, screenshot, eval."
            },
            url = new { type = "string", description = "URL to open (navigate)." },
            selector = new { type = "string", description = "CSS selector for click, type, and press." },
            text = new
            {
                type = "string",
                description = "Text to type (type), key to press (press), or JavaScript to run (eval)."
            }
        },
        required = new[] { "action" }
    };

    // Shared, session-lived Playwright state, separate from BrowserTool's. Guarded by hbInitLock so
    // concurrent calls do not double-initialize the browser.
    private static readonly SemaphoreSlim hbInitLock = new(1, 1);
    private static IPlaywright? hbPlaywright;
    private static IBrowser? hbBrowser;
    private static IPage? hbPage;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        try
        {
            var action = ToolArgs.GetString(args, "action").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
                return "ERROR: missing 'action'. Valid actions: navigate, text, html, click, type, press, screenshot, eval.";

            var ensured = await EnsurePageAsync().ConfigureAwait(false);
            if (ensured is not null)
                return ensured; // an error string from initialization
            var activePage = hbPage!;

            switch (action)
            {
                case "navigate":
                {
                    var url = ToolArgs.GetString(args, "url");
                    if (string.IsNullOrWhiteSpace(url))
                        return "ERROR: 'navigate' requires 'url'.";
                    if (!url.Contains("://", StringComparison.Ordinal))
                        url = "https://" + url;
                    await activePage.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load,
                        Timeout = 30000
                    }).ConfigureAwait(false);
                    var title = await activePage.TitleAsync().ConfigureAwait(false);
                    return $"OK navigated. Title: {title} | URL: {activePage.Url}";
                }

                case "text":
                {
                    var body = await activePage.EvaluateAsync<string>(
                        "() => document.body ? document.body.innerText : ''").ConfigureAwait(false);
                    return Truncate(body ?? string.Empty, 8000);
                }

                case "html":
                {
                    var html = await activePage.EvaluateAsync<string>(
                        "() => document.documentElement ? document.documentElement.outerHTML : ''").ConfigureAwait(false);
                    return Truncate(html ?? string.Empty, 8000);
                }

                case "click":
                {
                    var selector = ToolArgs.GetString(args, "selector");
                    if (string.IsNullOrWhiteSpace(selector))
                        return "ERROR: 'click' requires 'selector'.";
                    try
                    {
                        await activePage.ClickAsync(selector, new PageClickOptions { Timeout = 5000 }).ConfigureAwait(false);
                        return $"OK clicked '{selector}'.";
                    }
                    catch (TimeoutException)
                    {
                        return $"ERROR: no element matched '{selector}' within 5s.";
                    }
                    catch (PlaywrightException ex)
                    {
                        return $"ERROR: could not click '{selector}': {ex.Message}";
                    }
                }

                case "type":
                {
                    var selector = ToolArgs.GetString(args, "selector");
                    if (string.IsNullOrWhiteSpace(selector))
                        return "ERROR: 'type' requires 'selector'.";
                    var value = ToolArgs.GetString(args, "text");
                    try
                    {
                        await activePage.FillAsync(selector, value, new PageFillOptions { Timeout = 5000 }).ConfigureAwait(false);
                        return $"OK filled '{selector}'.";
                    }
                    catch (TimeoutException)
                    {
                        return $"ERROR: no element matched '{selector}' within 5s.";
                    }
                    catch (PlaywrightException ex)
                    {
                        return $"ERROR: could not fill '{selector}': {ex.Message}";
                    }
                }

                case "press":
                {
                    var key = ToolArgs.GetString(args, "text");
                    if (string.IsNullOrWhiteSpace(key))
                        return "ERROR: 'press' requires 'text' (the key to press, e.g. 'Enter').";
                    var selector = ToolArgs.GetString(args, "selector");
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(selector))
                        {
                            await activePage.PressAsync(selector, key, new PagePressOptions { Timeout = 5000 }).ConfigureAwait(false);
                            return $"OK pressed '{key}' on '{selector}'.";
                        }
                        await activePage.Keyboard.PressAsync(key).ConfigureAwait(false);
                        return $"OK pressed '{key}' on the focused element.";
                    }
                    catch (TimeoutException)
                    {
                        return $"ERROR: no element matched '{selector}' within 5s.";
                    }
                    catch (PlaywrightException ex)
                    {
                        return $"ERROR: could not press '{key}': {ex.Message}";
                    }
                }

                case "screenshot":
                {
                    var path = Path.Combine(Path.GetTempPath(), $"mesh-shot-{Guid.NewGuid():N}.png");
                    await activePage.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = path,
                        FullPage = false
                    }).ConfigureAwait(false);
                    // Surface the shot into the chat so the user actually sees it.
                    media.ReportFile(path);
                    return $"OK screenshot captured and shown to the user in the chat ({path}).";
                }

                case "eval":
                {
                    var script = ToolArgs.GetString(args, "text");
                    if (string.IsNullOrWhiteSpace(script))
                        return "ERROR: 'eval' requires 'text' (the JavaScript to run).";
                    try
                    {
                        var result = await activePage.EvaluateAsync<JsonElement?>(script).ConfigureAwait(false);
                        var rendered = result is null ? "null" : result.Value.ToString();
                        return Truncate(rendered ?? "null", 8000);
                    }
                    catch (PlaywrightException ex)
                    {
                        return $"ERROR: eval failed: {ex.Message}";
                    }
                }

                default:
                    return $"ERROR: unknown action '{action}'. Valid actions: navigate, text, html, click, type, press, screenshot, eval.";
            }
        }
        catch (Exception ex)
        {
            return $"ERROR: headless browser tool failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Ensures the shared headless browser and page exist. Returns null on success, or an error string
    /// to be surfaced to the caller.
    /// </summary>
    private static async Task<string?> EnsurePageAsync()
    {
        if (hbPage is not null && !hbPage.IsClosed && hbBrowser is not null && hbBrowser.IsConnected)
            return null;

        await hbInitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (hbPage is not null && !hbPage.IsClosed && hbBrowser is not null && hbBrowser.IsConnected)
                return null;

            hbPlaywright ??= await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);

            if (hbBrowser is null || !hbBrowser.IsConnected)
            {
                hbBrowser = await BrowserLaunch.LaunchAsync(hbPlaywright, headless: true).ConfigureAwait(false);
            }

            hbPage = await hbBrowser.NewPageAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return $"ERROR: could not start the headless browser: {ex.Message}";
        }
        finally
        {
            hbInitLock.Release();
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "\n...[truncated]";
}
