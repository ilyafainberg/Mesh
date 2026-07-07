using Microsoft.Win32;
using IBrowser = Microsoft.Playwright.IBrowser;
using IPlaywright = Microsoft.Playwright.IPlaywright;
using Microsoft.Playwright;

namespace Mesh.App.Services;

/// <summary>
/// Shared Playwright launch helper. Prefers the user's default system browser (Edge or Chrome,
/// via a Playwright channel) so automation reuses an already-installed browser instead of
/// downloading Playwright's bundled Chromium. Falls back to the next Chromium-based channel, and
/// finally to bundled Chromium (installed on demand) only if no system browser can be launched.
/// Used by the headed <see cref="BrowserTool"/> and the headless browser/web-search tools.
/// </summary>
public static class BrowserLaunch
{
    /// <summary>
    /// Chromium channel preferences, most-preferred first, based on the default system browser.
    /// A null entry means "Playwright's bundled Chromium" (last resort).
    /// </summary>
    public static IReadOnlyList<string?> PreferredChannels()
    {
        var order = new List<string?>();
        var def = DetectDefaultChannel();
        if (def is not null) order.Add(def);
        foreach (var c in new[] { "msedge", "chrome" })
            if (!order.Contains(c)) order.Add(c);
        order.Add(null); // bundled Chromium, installed on demand
        return order;
    }

    /// <summary>
    /// Launches a Chromium browser, preferring the default system browser. Returns the launched
    /// browser, or throws the last error if nothing could be launched.
    /// </summary>
    public static async Task<IBrowser> LaunchAsync(IPlaywright pw, bool headless)
    {
        Exception? last = null;
        foreach (var channel in PreferredChannels())
        {
            var options = new BrowserTypeLaunchOptions { Headless = headless };
            if (channel is not null) options.Channel = channel;
            try
            {
                return await pw.Chromium.LaunchAsync(options).ConfigureAwait(false);
            }
            catch (Exception ex) when (channel is null && IsNotInstalled(ex))
            {
                // Bundled Chromium missing: install it on demand, then retry once.
                var installError = TryInstallChromium();
                if (installError is not null) { last = new InvalidOperationException(installError, ex); continue; }
                return await pw.Chromium.LaunchAsync(options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // This channel (system browser) is not available; try the next preference.
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("No browser could be launched.");
    }

    /// <summary>Reads the default https handler from the registry and maps it to a Playwright channel.</summary>
    private static string? DetectDefaultChannel()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return null;
            if (progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "msedge";
            if (progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            return null; // Firefox/other: not a Chromium channel, fall through to msedge/chrome/bundled
        }
        catch { return null; }
    }

    private static bool IsNotInstalled(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not installed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("please install", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs the Playwright installer for Chromium. Returns null on success or an error string.</summary>
    private static string? TryInstallChromium()
    {
        try
        {
            var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            return exit == 0 ? null
                : "ERROR: Chromium is not installed and automatic install failed (exit code " + exit + ").";
        }
        catch (Exception ex)
        {
            return "ERROR: Chromium is not installed and automatic install failed: " + ex.Message;
        }
    }
}
