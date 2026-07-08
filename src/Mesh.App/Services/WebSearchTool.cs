using System.Text.Json;
using Microsoft.Playwright;
using IBrowser = Microsoft.Playwright.IBrowser;
using IPage = Microsoft.Playwright.IPage;
using IPlaywright = Microsoft.Playwright.IPlaywright;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated web-search tool. Spins up a short-lived headless browser per call and scrapes the
/// top results from Bing, falling back to Google then DuckDuckGo when an engine yields nothing or is
/// blocked. Unlike the browser tools it keeps no shared session: everything is created and disposed
/// within a single ExecuteAsync call.
/// </summary>
public sealed class WebSearchTool : IAgentTool
{
    public string Name => "web_search";
    public string Description =>
        "Search the web and return the top results (title, URL, and snippet). Tries Bing, then " +
        "Google, then DuckDuckGo depending on availability.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "The search query." },
            count = new
            {
                type = "integer",
                description = "Number of results to return (default 5, clamped 1 to 10)."
            }
        },
        required = new[] { "query" }
    };

    private sealed record SearchResult(string Title, string Url, string Snippet);

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var query = ToolArgs.GetString(args, "query").Trim();
        if (string.IsNullOrWhiteSpace(query))
            return "ERROR: missing 'query'.";

        var count = ToolArgs.GetInt(args, "count", 5);
        if (count < 1) count = 1;
        if (count > 10) count = 10;

        if (PlatformCaps.IsMobile)
            return await ExecuteMobileAsync(query, count, ct).ConfigureAwait(false);

        var encoded = Uri.EscapeDataString(query);
        var engines = new (string Name, string Url, string Script)[]
        {
            ("Bing", $"https://www.bing.com/search?q={encoded}", BingScript(count)),
            ("Google", $"https://www.google.com/search?q={encoded}&hl=en", GoogleScript(count)),
            ("DuckDuckGo", $"https://html.duckduckgo.com/html/?q={encoded}", DuckDuckGoScript(count)),
        };

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        string? lastError = null;

        try
        {
            playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
            browser = await BrowserLaunch.LaunchAsync(playwright, headless: true).ConfigureAwait(false);

            foreach (var engine in engines)
            {
                IPage? page = null;
                try
                {
                    page = await browser.NewPageAsync().ConfigureAwait(false);
                    await page.GotoAsync(engine.Url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 20000
                    }).ConfigureAwait(false);

                    var json = await page.EvaluateAsync<string>(engine.Script).ConfigureAwait(false);
                    var results = ParseResults(json);
                    if (results.Count > 0)
                        return Format(query, engine.Name, results);
                }
                catch (Exception ex)
                {
                    lastError = $"{engine.Name}: {ex.Message}";
                }
                finally
                {
                    if (page is not null)
                        await page.CloseAsync().ConfigureAwait(false);
                }
            }

            var reason = lastError is null ? "no results returned." : lastError;
            return $"ERROR: web search failed on all engines (Bing, Google, DuckDuckGo). {reason}";
        }
        catch (Exception ex)
        {
            return $"ERROR: web search failed on all engines (Bing, Google, DuckDuckGo). {ex.Message}";
        }
        finally
        {
            if (browser is not null)
                await browser.DisposeAsync().ConfigureAwait(false);
            playwright?.Dispose();
        }
    }

    private static async Task<string> ExecuteMobileAsync(string query, int count, CancellationToken ct)
    {
        try
        {
            var parsed = await DuckDuckGoHtmlSearch.SearchAsync(query, count, ct).ConfigureAwait(false);
            var results = new List<SearchResult>(parsed.Count);
            foreach (var (title, url, snippet) in parsed)
            {
                var cleanTitle = (title ?? string.Empty).Trim();
                var cleanUrl = (url ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(cleanTitle) && string.IsNullOrEmpty(cleanUrl)) continue;
                results.Add(new SearchResult(cleanTitle, cleanUrl, Truncate((snippet ?? string.Empty).Trim(), 300)));
                if (results.Count >= count) break;
            }

            if (results.Count > 0)
                return Format(query, "DuckDuckGo", results);

            return "ERROR: web search failed on all engines (Bing, Google, DuckDuckGo). no results returned.";
        }
        catch (Exception ex)
        {
            return $"ERROR: web search failed on all engines (Bing, Google, DuckDuckGo). {ex.Message}";
        }
    }

    private static List<SearchResult> ParseResults(string? json)
    {
        var list = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SearchResult>>(json, jsonOptions);
            if (parsed is null) return list;
            foreach (var r in parsed)
            {
                var title = (r.Title ?? string.Empty).Trim();
                var url = (r.Url ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(url)) continue;
                var snippet = Truncate((r.Snippet ?? string.Empty).Trim(), 300);
                list.Add(new SearchResult(title, url, snippet));
            }
        }
        catch (JsonException)
        {
            // Engine returned something that was not our JSON shape: treat as no results.
        }
        return list;
    }

    private static string Format(string query, string engine, List<SearchResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Results for \"").Append(query).Append("\" (via ").Append(engine).Append("):\n");
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append(i + 1).Append(". ").Append(r.Title).Append('\n');
            sb.Append("   ").Append(r.Url).Append('\n');
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.Append("   ").Append(r.Snippet).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "...";

    private static string BingScript(int count) => $$"""
        () => {
          const out = [];
          const items = document.querySelectorAll('li.b_algo');
          for (const it of items) {
            const a = it.querySelector('h2 a');
            if (!a) continue;
            const title = (a.innerText || '').trim();
            const url = a.href || '';
            let snip = it.querySelector('.b_caption p') || it.querySelector('.b_algoSlug');
            const snippet = snip ? (snip.innerText || '').trim() : '';
            if (!title && !url) continue;
            out.push({ title, url, snippet });
            if (out.length >= {{count}}) break;
          }
          return JSON.stringify(out);
        }
        """;

    private static string GoogleScript(int count) => $$"""
        () => {
          const out = [];
          const items = document.querySelectorAll('div.g, div[data-sokoban-container]');
          for (const it of items) {
            const h3 = it.querySelector('h3');
            if (!h3) continue;
            const title = (h3.innerText || '').trim();
            const a = h3.closest('a[href]') || it.querySelector('a[href]');
            const url = a ? (a.href || '') : '';
            let snip = it.querySelector('div[data-sncf]') || it.querySelector('.VwiC3b');
            const snippet = snip ? (snip.innerText || '').trim() : '';
            if (!title && !url) continue;
            out.push({ title, url, snippet });
            if (out.length >= {{count}}) break;
          }
          return JSON.stringify(out);
        }
        """;

    private static string DuckDuckGoScript(int count) => $$"""
        () => {
          const out = [];
          const items = document.querySelectorAll('.result');
          for (const it of items) {
            const a = it.querySelector('a.result__a');
            if (!a) continue;
            const title = (a.innerText || '').trim();
            const url = a.href || '';
            let snip = it.querySelector('.result__snippet');
            const snippet = snip ? (snip.innerText || '').trim() : '';
            if (!title && !url) continue;
            out.push({ title, url, snippet });
            if (out.length >= {{count}}) break;
          }
          return JSON.stringify(out);
        }
        """;
}
