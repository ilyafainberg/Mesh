using System.Net;
using System.Text.RegularExpressions;

namespace Mesh.App.Services;

/// <summary>
/// Browserless DuckDuckGo search used on mobile targets (Android/iOS) where Playwright and a
/// headless Chromium are unavailable. It performs a plain HttpClient GET against the DDG HTML
/// endpoint and parses the returned markup server-side with regex/substring matching (no browser,
/// no WebView, no UI thread), so it can run from any background thread.
/// </summary>
internal static class DuckDuckGoHtmlSearch
{
    private const string HtmlEndpoint = "https://html.duckduckgo.com/html/?q=";

    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0 Safari/537.36";

    // Any <a ...>inner</a>. Attribute order varies, so we capture attributes and inner text
    // separately and filter/extract afterwards.
    private static readonly Regex AnchorRegex = new(
        "<a\\b([^>]*)>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The result title anchor carries class="result__a" (possibly among other classes).
    private static readonly Regex ResultClassRegex = new(
        "class\\s*=\\s*\"[^\"]*\\bresult__a\\b[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        "href\\s*=\\s*\"([^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The snippet lives in an element whose class contains result__snippet (usually an <a>).
    private static readonly Regex SnippetRegex = new(
        "<(\\w+)\\b[^>]*class\\s*=\\s*\"[^\"]*\\bresult__snippet\\b[^\"]*\"[^>]*>(.*?)</\\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        "\\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Fetches and parses DuckDuckGo HTML results. Returns up to <paramref name="count"/> tuples of
    /// (Title, Url, Snippet). Never throws: on any failure it returns an empty list.
    /// </summary>
    public static async Task<IReadOnlyList<(string Title, string Url, string Snippet)>> SearchAsync(
        string query, int count, CancellationToken ct = default)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        try
        {
            var url = HtmlEndpoint + Uri.EscapeDataString(query);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return results;

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(html, count);
        }
        catch
        {
            return results;
        }
    }

    /// <summary>
    /// Parses DuckDuckGo HTML markup into result tuples. Exposed for direct parsing of already
    /// fetched markup. Robust to attribute ordering and missing snippets.
    /// </summary>
    public static IReadOnlyList<(string Title, string Url, string Snippet)> Parse(string? html, int count)
    {
        var results = new List<(string Title, string Url, string Snippet)>();
        if (string.IsNullOrWhiteSpace(html)) return results;

        // Collect title anchors (index, title, href).
        var anchors = new List<(int Index, string Title, string Href)>();
        foreach (Match m in AnchorRegex.Matches(html))
        {
            var attrs = m.Groups[1].Value;
            if (!ResultClassRegex.IsMatch(attrs)) continue;

            var hrefMatch = HrefRegex.Match(attrs);
            var href = hrefMatch.Success ? WebUtility.HtmlDecode(hrefMatch.Groups[1].Value) : string.Empty;
            var title = CleanText(m.Groups[2].Value);
            anchors.Add((m.Index, title, href));
        }

        if (anchors.Count == 0) return results;

        // Collect snippets (index, text) so we can associate each with its preceding anchor.
        var snippets = new List<(int Index, string Text)>();
        foreach (Match m in SnippetRegex.Matches(html))
            snippets.Add((m.Index, CleanText(m.Groups[2].Value)));

        for (var i = 0; i < anchors.Count && results.Count < count; i++)
        {
            var anchor = anchors[i];
            var nextIndex = i + 1 < anchors.Count ? anchors[i + 1].Index : int.MaxValue;

            var snippet = string.Empty;
            foreach (var s in snippets)
            {
                if (s.Index > anchor.Index && s.Index < nextIndex)
                {
                    snippet = s.Text;
                    break;
                }
            }

            var title = anchor.Title;
            var realUrl = DecodeRedirect(anchor.Href);
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(realUrl)) continue;

            results.Add((title, realUrl, snippet));
        }

        return results;
    }

    /// <summary>
    /// Converts a DDG anchor href into the real target URL. DDG wraps results in a redirect like
    /// "//duckduckgo.com/l/?uddg=&lt;url-encoded-real-url&gt;&amp;rut=...". When present we extract and
    /// URL-decode the uddg parameter; protocol-relative hrefs get an https: prefix; anything else is
    /// returned as-is.
    /// </summary>
    public static string DecodeRedirect(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        href = href.Trim();

        var uddgIndex = href.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
        if (uddgIndex >= 0)
        {
            var start = uddgIndex + "uddg=".Length;
            var end = href.IndexOf('&', start);
            var encoded = end >= 0 ? href.Substring(start, end - start) : href.Substring(start);
            try
            {
                var decoded = Uri.UnescapeDataString(encoded);
                if (!string.IsNullOrWhiteSpace(decoded))
                    return decoded;
            }
            catch
            {
                // Fall through to the raw href handling below.
            }
        }

        if (href.StartsWith("//", StringComparison.Ordinal))
            return "https:" + href;

        return href;
    }

    private static string CleanText(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var stripped = TagRegex.Replace(raw, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }
}
