using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mesh.App.Services;

/// <summary>
/// Opens auth URLs in a dedicated, front-most browser window rather than the user's
/// existing browser session. Uses Edge's "app" mode (a chromeless standalone window)
/// so the sign-in clearly appears in front of Mesh instead of as a background tab.
/// Falls back to the default browser if Edge isn't found.
/// </summary>
public static class BrowserLauncher
{
    private const int Width = 460;
    private const int Height = 640;

    // Handle of the most recently opened auth window (found via enumeration), so we
    // can close it programmatically, window.close() is blocked for navigated pages.
    private static volatile IntPtr lastAuthWindow;

    private static readonly string[] EdgePaths =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    };

    /// <summary>Opens the URL in a dedicated window and resizes it small + centered.</summary>
    public static Process? Open(string url)
    {
        var edge = EdgePaths.FirstOrDefault(File.Exists);
        try
        {
            if (edge is not null)
            {
                // Snapshot existing top-level windows so we can spot the new one and size it.
                lastAuthWindow = IntPtr.Zero;
                var before = SnapshotWindows();

                var psi = new ProcessStartInfo(edge) { UseShellExecute = false };
                psi.ArgumentList.Add($"--app={url}");
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add($"--window-size={Width},{Height}");
                var proc = Process.Start(psi);

                // Edge may hand off to an already-running instance, so the size flag is
                // often ignored, find the new window and resize it ourselves.
                if (OperatingSystem.IsWindows())
                    _ = Task.Run(() => ResizeNewWindow(before));
                return proc;
            }
        }
        catch { /* fall through to default browser */ }

        try { return Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { return null; }
    }

    /// <summary>Async wrapper for APIs (e.g. MSAL) that expect a Task-returning opener.</summary>
    public static Task OpenAsync(string url)
    {
        Open(url);
        return Task.CompletedTask;
    }

    /// <summary>
    /// HTML served on the loopback callback: shows a brief message. The app closes
    /// the window via Win32 (window.close() is blocked for navigated pages).
    /// </summary>
    public static string SuccessHtml(string message) =>
        "<!doctype html><html><head><meta charset='utf-8'><style>" +
        "body{font-family:Segoe UI,system-ui,sans-serif;margin:0;height:100vh;display:flex;" +
        "align-items:center;justify-content:center;color:#1b1b1b;background:#faf9f8}" +
        ".c{text-align:center}.c .t{font-size:1.05rem}.c .s{color:#666;font-size:.85rem;margin-top:6px}" +
        "</style></head><body><div class='c'><div class='t'>" + System.Net.WebUtility.HtmlEncode(message) +
        "</div><div class='s'>You can close this window.</div></div>" +
        "<script>setTimeout(function(){try{window.close();}catch(e){}},500);</script></body></html>";

    /// <summary>
    /// Closes the auth window opened by the last <see cref="Open"/> call. Because a
    /// navigated page can't self-close, the app posts WM_CLOSE to it directly.
    /// </summary>
    public static void CloseAuthWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = lastAuthWindow;
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                lastAuthWindow = IntPtr.Zero;
            }
        }
        catch { /* best-effort */ }
    }

    private const uint WM_CLOSE = 0x0010;

    // ---- Win32 window sizing --------------------------------------------------
    private static HashSet<IntPtr> SnapshotWindows()
    {
        var set = new HashSet<IntPtr>();
        if (!OperatingSystem.IsWindows()) return set;
        EnumWindows((h, _) => { set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    private static void ResizeNewWindow(HashSet<IntPtr> before)
    {
        try
        {
            // Poll briefly for a newly created, visible Edge window.
            for (var i = 0; i < 40; i++)
            {
                Thread.Sleep(150);
                IntPtr found = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    if (before.Contains(h) || !IsWindowVisible(h)) return true;
                    var title = GetTitle(h);
                    if (string.IsNullOrEmpty(title)) return true;
                    if (!IsEdge(h)) return true;
                    found = h;
                    return false; // stop enumeration
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    lastAuthWindow = found;
                    CenterAndResize(found);
                    return;
                }
            }
        }
        catch { /* best-effort */ }
    }

    private static void CenterAndResize(IntPtr hwnd)
    {
        var sw = GetSystemMetrics(0); // SM_CXSCREEN
        var sh = GetSystemMetrics(1); // SM_CYSCREEN
        var x = Math.Max(0, (sw - Width) / 2);
        var y = Math.Max(0, (sh - Height) / 2);
        MoveWindow(hwnd, x, y, Width, Height, true);
        SetForegroundWindow(hwnd);
    }

    private static bool IsEdge(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return string.Equals(Process.GetProcessById((int)pid).ProcessName, "msedge", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
