using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mesh.App.Services;

/// <summary>
/// Phase of an in-progress update, surfaced to the UI so it can show what is happening.
/// </summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,
    Extracting,
    Preparing,
    ReadyToApply,
    Applying,
    UpToDate,
    Failed
}

/// <summary>Progress report for a running update (download/extract), consumed via IProgress.</summary>
public readonly record struct UpdateProgress(UpdatePhase Phase, long BytesReceived, long TotalBytes, string? Message)
{
    /// <summary>0..100, or -1 when the total size is unknown (indeterminate).</summary>
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, 100) : -1;
}

/// <summary>A release found on GitHub that is newer than the running build.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    long Size,
    string? ReleaseNotes,
    string? HtmlUrl);

/// <summary>Outcome of a check: whether an update exists plus the versions involved.</summary>
public sealed record UpdateCheckResult(bool Available, Version Current, Version? Latest, UpdateInfo? Info, string? Error);

/// <summary>
/// Self-update for the Windows client. The client ships as a self-contained zip in the public
/// GitHub releases repo, so updating means: read the latest release via the GitHub API, download
/// the win-x64 client asset with progress, extract it, then hand off to a small .cmd that waits
/// for this process to exit, copies the new files over the install directory, and relaunches.
/// </summary>
/// <remarks>
/// Only supported on Windows (the published asset is win-x64). On other platforms
/// <see cref="IsSupported"/> is false and the UI hides the feature.
/// </remarks>
public sealed class UpdateService
{
    // Public releases repo (binaries only). Source lives in the private repo.
    private const string Owner = "MeshRelayAI";
    private const string Repo = "Mesh";
    private const string AssetPrefix = "Mesh-Client-win-x64";

    private readonly IHttpClientFactory httpFactory;
    private readonly IAppControl appControl;
    private readonly ILogger<UpdateService> log;

    public UpdateService(IHttpClientFactory httpFactory, IAppControl appControl, ILogger<UpdateService> log)
    {
        this.httpFactory = httpFactory;
        this.appControl = appControl;
        this.log = log;
        CurrentVersion = DetectCurrentVersion();
    }

    /// <summary>The version of the running build, parsed from the assembly.</summary>
    public Version CurrentVersion { get; }

    /// <summary>Self-update is only wired up for the Windows client (the published asset is win-x64).</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// The newest available update found by a background check, or null when none is known. Shared
    /// state so the app-wide banner and the Settings panel react to the same result.
    /// </summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>True when the user dismissed the update banner this session (still shown in Settings).</summary>
    public bool BannerDismissed { get; private set; }

    /// <summary>Raised when <see cref="Available"/> or <see cref="BannerDismissed"/> changes.</summary>
    public event Action? Changed;

    private Timer? autoTimer;

    /// <summary>
    /// Starts automatic update checks: one immediately, then every 6 hours. Safe to call more than
    /// once (later calls are no-ops). No-op on unsupported platforms.
    /// </summary>
    public void StartAutoChecks()
    {
        if (!IsSupported || autoTimer is not null) return;
        autoTimer = new Timer(_ => _ = CheckInBackgroundAsync(), null, TimeSpan.Zero, TimeSpan.FromHours(6));
    }

    /// <summary>
    /// Checks for an update in the background and, if a newer version exists, records it in
    /// <see cref="Available"/> and raises <see cref="Changed"/>. Never throws.
    /// </summary>
    public async Task CheckInBackgroundAsync()
    {
        if (!IsSupported) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await CheckAsync(cts.Token);
            if (result.Available && result.Info is not null && result.Info.Version > CurrentVersion)
            {
                var isNew = Available?.Version != result.Info.Version;
                Available = result.Info;
                if (isNew) { BannerDismissed = false; Changed?.Invoke(); }
            }
        }
        catch { /* background check: ignore transient errors */ }
    }

    /// <summary>Hides the update banner for this session. The update stays available in Settings.</summary>
    public void DismissBanner()
    {
        if (BannerDismissed) return;
        BannerDismissed = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Query the GitHub API for the latest release and decide whether it is newer than the running build.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!IsSupported)
            return new UpdateCheckResult(false, CurrentVersion, null, null, "Updates are only supported on Windows.");

        try
        {
            var http = httpFactory.CreateClient("updater");
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult(false, CurrentVersion, null, null,
                    $"GitHub returned {(int)resp.StatusCode} when checking for updates.");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseVersion(tag, out var latest))
                return new UpdateCheckResult(false, CurrentVersion, null, null, "Could not read the latest version.");

            string? assetName = null, assetUrl = null;
            long assetSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null) continue;
                    if (name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                        break;
                    }
                }
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            var newer = latest > CurrentVersion;
            if (!newer || assetUrl is null || assetName is null)
                return new UpdateCheckResult(false, CurrentVersion, latest, null,
                    assetUrl is null && newer ? "The latest release has no Windows client asset." : null);

            var info = new UpdateInfo(latest, tag!, assetName, assetUrl, assetSize, notes, htmlUrl);
            return new UpdateCheckResult(true, CurrentVersion, latest, info, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Update check failed");
            return new UpdateCheckResult(false, CurrentVersion, null, null, $"Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Download the update asset (reporting byte progress), extract it, and stage a .cmd updater.
    /// Returns the path to the updater script; call <see cref="ApplyAndExit"/> to run it.
    /// </summary>
    public async Task<string> DownloadAndPrepareAsync(UpdateInfo info, IProgress<UpdateProgress> progress,
        CancellationToken ct = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");

        var root = Path.Combine(Path.GetTempPath(), "MeshUpdate", SanitizeTag(info.TagName));
        var extractDir = Path.Combine(root, "extracted");
        var zipPath = Path.Combine(root, "client.zip");

        // Start clean so a half-finished previous attempt cannot poison this one.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        // --- download (streamed, with progress) ---
        var http = httpFactory.CreateClient("updater");
        using (var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl))
        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? (info.Size > 0 ? info.Size : 0);

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];
            long received = 0;
            int read;
            var lastReport = 0L;
            progress.Report(new UpdateProgress(UpdatePhase.Downloading, 0, total, "Starting download"));
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                // Throttle UI updates to roughly every 512 KB to avoid flooding the render loop.
                if (received - lastReport >= (1 << 19) || received == total)
                {
                    lastReport = received;
                    progress.Report(new UpdateProgress(UpdatePhase.Downloading, received, total, null));
                }
            }
            progress.Report(new UpdateProgress(UpdatePhase.Downloading, received, total, "Download complete"));
        }

        // --- extract ---
        progress.Report(new UpdateProgress(UpdatePhase.Extracting, 0, 0, "Extracting"));
        Directory.CreateDirectory(extractDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true), ct);

        // The zip contains a single top-level "Mesh-win-x64" folder holding the app. Find the folder
        // that actually contains the executable so we are robust to packaging changes.
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "Mesh.App.exe";
        var sourceDir = FindAppRoot(extractDir, exeName)
            ?? throw new InvalidOperationException("Downloaded update did not contain the application.");

        // --- write the updater script ---
        progress.Report(new UpdateProgress(UpdatePhase.Preparing, 0, 0, "Preparing"));
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var batPath = Path.Combine(Path.GetTempPath(), $"mesh-apply-{SanitizeTag(info.TagName)}.cmd");
        await File.WriteAllTextAsync(batPath,
            BuildUpdaterScript(Environment.ProcessId, sourceDir, installDir, exeName, root), ct);

        progress.Report(new UpdateProgress(UpdatePhase.ReadyToApply, 0, 0, "Ready to install"));
        return batPath;
    }

    /// <summary>
    /// Launch the staged updater script (detached) and quit the app so its files can be replaced.
    /// The script waits for this process to exit, copies the new files in, and relaunches.
    /// </summary>
    public void ApplyAndExit(string batPath)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");

        // Show a small console window so the user sees the update happening (the swap + relaunch can
        // take 10-30s while WebView2 child processes release their file locks). A silent window made
        // it look like "nothing happened".
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = Path.GetTempPath()
        };
        Process.Start(psi);

        // Quit gracefully on the UI thread, then guarantee the process actually exits shortly after so
        // the updater's wait loop can proceed even if the graceful quit hangs (close-to-tray, a stuck
        // WebView, and so on). Without this the app could linger, the swap would never run, and it
        // would look like nothing happened.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { appControl.Quit(); } catch { }
        });
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Environment.Exit(0);
        });
    }

    // ---- helpers ----

    private static string BuildUpdaterScript(int pid, string source, string dest, string exeName, string stagingRoot)
    {
        // A self-deleting batch: wait for the app (by PID) to exit, then mirror the new files over the
        // install directory and relaunch. Uses `ping` for delays instead of `timeout`, because
        // `timeout` fails ("Input redirection is not supported") when launched from a GUI app that has
        // no console. After the main process exits, WebView2 child processes can briefly keep files
        // locked, so robocopy is retried in a loop until the locks clear. All output is logged.
        return
$@"@echo off
setlocal enableextensions
title Updating Mesh...
set ""PID={pid}""
set ""SRC={source}""
set ""DST={dest}""
set ""EXE={exeName}""
set ""ROOT={stagingRoot}""
set ""LOG=%TEMP%\mesh-update.log""

echo Updating Mesh, please wait...
echo [%date% %time%] update start pid=%PID% > ""%LOG%""

REM Wait for the running Mesh process to exit so its files are no longer locked.
:waitloop
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    ping 127.0.0.1 -n 2 >nul
    goto waitloop
)
echo [%time%] main process exited >> ""%LOG%""

REM Grace period so WebView2 child processes can shut down and release their file locks.
ping 127.0.0.1 -n 5 >nul

REM Copy the new build over the install directory, retrying while files are still locked.
REM Robocopy exit codes below 8 are success (files copied / nothing to do); 8+ means a failure.
set /a TRIES=0
:copyloop
robocopy ""%SRC%"" ""%DST%"" /E /R:1 /W:2 /NFL /NDL /NJH /NJS /NP >> ""%LOG%""
if %errorlevel% lss 8 goto copied
set /a TRIES+=1
echo [%time%] robocopy blocked by a lock (attempt %TRIES%), retrying >> ""%LOG%""
if %TRIES% geq 20 goto copyfail
ping 127.0.0.1 -n 3 >nul
goto copyloop

:copyfail
echo [%time%] robocopy FAILED after retries, relaunching existing build >> ""%LOG%""
start """" ""%DST%\%EXE%""
goto cleanup

:copied
echo [%time%] copy complete, relaunching >> ""%LOG%""
start """" ""%DST%\%EXE%""

:cleanup
ping 127.0.0.1 -n 3 >nul
rmdir /s /q ""%ROOT%"" 2>nul
(goto) 2>nul & del ""%~f0""
";
    }

    private static string? FindAppRoot(string extractDir, string exeName)
    {
        if (File.Exists(Path.Combine(extractDir, exeName))) return extractDir;
        // Look one or two levels down for the folder that holds the executable.
        foreach (var dir in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, exeName))) return dir;
        }
        return null;
    }

    private static string SanitizeTag(string tag)
    {
        var cleaned = new string(tag.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "latest" : cleaned;
    }

    private static Version DetectCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(info, out var v)) return v;
        var named = asm.GetName().Version;
        return named is not null ? new Version(named.Major, named.Minor, Math.Max(named.Build, 0)) : new Version(0, 0, 0);
    }

    /// <summary>
    /// Parse a loose version string (with an optional leading v, or trailing +build / -prerelease
    /// metadata) into a normalized Major.Minor.Patch <see cref="Version"/> for reliable comparison.
    /// </summary>
    internal static bool TryParseVersion(string? s, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var cut = s.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) s = s[..cut];
        if (s.Length == 0) return false;

        var parts = s.Split('.');
        int major = 0, minor = 0, patch = 0;
        if (parts.Length > 0) int.TryParse(parts[0], out major);
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        if (parts.Length > 2) int.TryParse(parts[2], out patch);
        version = new Version(major, minor, patch);
        return true;
    }
}
