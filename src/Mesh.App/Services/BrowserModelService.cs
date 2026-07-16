using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Microsoft.Playwright;

namespace Mesh.App.Services;

/// <summary>Windows-only, user-scripted provider that drives a web LLM in a persistent browser profile.</summary>
public sealed class BrowserModelService(AppState state) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPlaywright? playwright;
    private IBrowserContext? context;
    private IPage? page;
    private bool sessionConfirmed;
    private string? activeUrl;
    private string? activeEngine;

    public async Task<string> CompleteAsync(ModelConfig cfg, string systemPrompt,
        IReadOnlyList<ChatLine> history, IReadOnlyList<IAgentTool>? tools,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return "[model error: Browser provider is available on Windows only.]";
        if (string.IsNullOrWhiteSpace(cfg.BrowserUrl)) return "[model error: Browser provider URL is missing.]";
        if (string.IsNullOrWhiteSpace(cfg.BrowserExecuteScript) || string.IsNullOrWhiteSpace(cfg.BrowserPollScript)
            || string.IsNullOrWhiteSpace(cfg.BrowserResultScript))
            return "[model error: Configure the Execute prompt, Poll for response, and Get result scripts.]";

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsurePageAsync(cfg, ct).ConfigureAwait(false);
            if (!sessionConfirmed)
            {
                var ok = await ConfirmFirstUseAsync().ConfigureAwait(false);
                if (!ok) return "[model error: Browser provider authentication was cancelled.]";
                sessionConfirmed = true;
            }

            var requestId = Guid.NewGuid().ToString("n");
            var fullPrompt = RenderPrompt(systemPrompt, history);
            var currentTurn = LastUserMessage(history);
            var contextMode = NormalizeContextMode(cfg.BrowserContextMode);
            var submissionText = contextMode == "CurrentTurn" ? currentTurn : fullPrompt;
            if (string.IsNullOrWhiteSpace(submissionText))
                return "[model error: Browser provider found no user message to send.]";
            var request = new
            {
                requestId,
                prompt = fullPrompt,
                currentTurn,
                lastUserMessage = currentTurn,
                submissionText,
                contextMode,
                systemPrompt,
                messages = history.Select(x => new { role = x.Role, content = x.Text }).ToArray(),
                skills = state.Profile.Skills.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    instructions = s.Instructions
                }).ToArray(),
                tools = (tools ?? Array.Empty<IAgentTool>()).Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.ParametersSchema
                }).ToArray(),
                metadata = new { model = cfg.Model, provider = "Browser" }
            };

            var ticket = await RunScriptAsync(cfg.BrowserExecuteScript!, request, "Execute prompt", ct).ConfigureAwait(false);
            ticket = await ApplyHostActionAsync(ticket, submissionText, ct).ConfigureAwait(false);
            JsonElement? lastPoll = null;

            var pollSeconds = Math.Clamp(cfg.BrowserPollSeconds, 1, 60);
            var timeout = TimeSpan.FromMinutes(Math.Clamp(cfg.BrowserTimeoutMinutes, 1, 120));
            var started = DateTimeOffset.UtcNow;
            var pollCount = 0;
            while (DateTimeOffset.UtcNow - started < timeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct).ConfigureAwait(false);
                pollCount++;
                var pollState = new
                {
                    requestId,
                    elapsedSeconds = (int)(DateTimeOffset.UtcNow - started).TotalSeconds,
                    pollCount,
                    request,
                    ticket,
                    previousPoll = lastPoll
                };
                var poll = await RunScriptAsync(cfg.BrowserPollScript!, pollState, "Poll for response", ct).ConfigureAwait(false);
                lastPoll = poll.Clone();
                var status = ParsePoll(poll);
                if (status == "pending") continue;
                if (status == "authentication-required")
                {
                    sessionConfirmed = false;
                    return "[model error: Browser provider requires authentication. Retry to open the sign-in window.]";
                }
                if (status == "error") return "[model error: " + ReadMessage(poll, "Browser provider reported an error.") + "]";
                if (status != "complete") return "[model error: Poll for response returned an invalid value.]";

                var resultState = new { requestId, request, ticket, pollResult = poll };
                var result = await RunScriptAsync(cfg.BrowserResultScript!, resultState, "Get result", ct).ConfigureAwait(false);
                var text = ParseResult(result);
                return string.IsNullOrWhiteSpace(text)
                    ? "[model error: Get result returned no text.]"
                    : text;
            }
            return "[model error: Browser provider timed out waiting for a response.]";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "[model error: " + ex.Message + "]"; }
        finally { gate.Release(); }
    }

    private async Task EnsurePageAsync(ModelConfig cfg, CancellationToken ct)
    {
        var engine = NormalizeEngine(cfg.BrowserEngine);
        if (page is not null && !page.IsClosed
            && string.Equals(activeUrl, cfg.BrowserUrl, StringComparison.Ordinal)
            && string.Equals(activeEngine, engine, StringComparison.Ordinal)) return;

        if (context is not null) await context.DisposeAsync().ConfigureAwait(false);
        context = null;
        page = null;
        playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        var profile = Path.Combine(StoragePaths.Root, "BrowserProviders", engine.ToLowerInvariant(), "Profile");
        Directory.CreateDirectory(profile);

        try
        {
            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                ViewportSize = null
            };

            if (engine == "Firefox")
            {
                context = await playwright.Firefox.LaunchPersistentContextAsync(profile, options).ConfigureAwait(false);
            }
            else
            {
                options.Channel = engine == "Edge" ? "msedge" : "chrome";
                options.Args = new[] { "--start-maximized" };
                context = await playwright.Chromium.LaunchPersistentContextAsync(profile, options).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (engine == "Firefox" && IsBrowserMissing(ex))
        {
            var exit = Microsoft.Playwright.Program.Main(new[] { "install", "firefox" });
            if (exit != 0)
                throw new InvalidOperationException("Firefox is not available and Playwright could not install it.", ex);
            context = await playwright.Firefox.LaunchPersistentContextAsync(profile,
                new BrowserTypeLaunchPersistentContextOptions { Headless = false, ViewportSize = null }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBrowserMissing(ex))
        {
            throw new InvalidOperationException($"{engine} is not installed or cannot be launched by Playwright.", ex);
        }

        page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(cfg.BrowserUrl!, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000
        }).ConfigureAwait(false);
        activeUrl = cfg.BrowserUrl;
        activeEngine = engine;
        sessionConfirmed = false;
        ct.ThrowIfCancellationRequested();
    }

    private static string NormalizeEngine(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "edge" => "Edge",
        _ => "Firefox"
    };

    private static string NormalizeContextMode(string? value)
        => string.Equals(value?.Trim(), "CurrentTurn", StringComparison.OrdinalIgnoreCase)
            ? "CurrentTurn" : "FullPrompt";

    private static string LastUserMessage(IReadOnlyList<ChatLine> history)
        => history.LastOrDefault(line => string.Equals(line.Role, "user", StringComparison.OrdinalIgnoreCase))?.Text ?? "";

    private static bool IsBrowserMissing(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not installed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<bool> ConfirmFirstUseAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                var ok = page is not null && await page.DisplayAlertAsync("Browser model sign-in",
                    "Sign in to the model provider in the browser. When the site is ready, return to Mesh and select OK.",
                    "OK", "Cancel");
                tcs.TrySetResult(ok);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    private async Task<JsonElement> RunScriptAsync(string source, object arg, string name, CancellationToken ct)
    {
        if (page is null || page.IsClosed) throw new InvalidOperationException("The browser window was closed.");
        ct.ThrowIfCancellationRequested();
        try
        {
            // Serialize the complete argument graph ourselves. Playwright's argument serializer
            // reflects over nested JsonElement values and can mistake JsonValueKind for an Int32.
            // Passing JSON as a string also keeps request data out of the JavaScript source.
            var json = JsonSerializer.Serialize(arg);
            return await page.EvaluateAsync<JsonElement>(
                $"async json => await ({source})(JSON.parse(json))", json).ConfigureAwait(false);
        }
        catch (Exception ex) { throw new InvalidOperationException(name + " script failed: " + ex.Message, ex); }
    }

    private async Task<JsonElement> ApplyHostActionAsync(JsonElement value, string prompt, CancellationToken ct)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("hostAction", out var action)
            || !string.Equals(action.GetString(), "playwright-send", StringComparison.OrdinalIgnoreCase))
            return value;
        if (page is null || page.IsClosed) throw new InvalidOperationException("The browser window was closed.");

        static string Required(JsonElement objectValue, string name)
            => objectValue.TryGetProperty(name, out var property) && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()! : throw new InvalidOperationException($"Execute prompt host action requires {name}.");

        var editorSelector = Required(value, "editorSelector");
        var sendSelector = Required(value, "sendSelector");
        var responseSelector = Required(value, "responseSelector");
        var timeoutMs = value.TryGetProperty("timeoutMs", out var timeoutValue) && timeoutValue.TryGetInt32(out var parsed)
            ? Math.Clamp(parsed, 1000, 120000) : 10000;
        var previousResponseCount = await page.Locator(responseSelector).CountAsync().ConfigureAwait(false);
        var editor = page.Locator(editorSelector);
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs }).ConfigureAwait(false);
        await editor.ClickAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Control+A").ConfigureAwait(false);
        await page.Keyboard.InsertTextAsync(prompt).ConfigureAwait(false);
        var send = page.Locator(sendSelector).Last;
        await send.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs }).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await send.IsEnabledAsync().ConfigureAwait(false))
            {
                await send.ClickAsync().ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new
                {
                    previousResponseCount,
                    sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    responseSelector
                });
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The configured Send button did not become enabled.");
    }

    private static string RenderPrompt(string systemPrompt, IReadOnlyList<ChatLine> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[SYSTEM]").AppendLine(systemPrompt).AppendLine("[CONVERSATION]");
        foreach (var line in history) sb.Append('[').Append(line.Role.ToUpperInvariant()).AppendLine("]").AppendLine(line.Text);
        return sb.ToString();
    }

    private static string ParsePoll(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return "complete";
        if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.Null) return "pending";
        if (value.ValueKind == JsonValueKind.String) return value.GetString()?.ToLowerInvariant() ?? "pending";
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out var s))
            return s.GetString()?.ToLowerInvariant() ?? "pending";
        return "invalid";
    }

    private static string ParseResult(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("text", out var text)) return text.GetString() ?? "";
        return "";
    }

    private static string ReadMessage(JsonElement value, string fallback)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var m) ? m.GetString() ?? fallback : fallback;

    public async ValueTask DisposeAsync()
    {
        if (context is not null) await context.DisposeAsync().ConfigureAwait(false);
        playwright?.Dispose();
        gate.Dispose();
    }
}

public sealed class BrowserChatModel(BrowserModelService service, ModelConfig cfg) : IChatModel
{
    public Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
        => service.CompleteAsync(cfg, systemPrompt, history, null, ct);

    public Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
        => service.CompleteAsync(cfg, systemPrompt, history, tools, ct);
}
