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
/// Self-update for the Windows client. The signed installer ships inside a zip in the public
/// GitHub releases repo. Updating means: read the latest release via the GitHub API, download
/// and extract the archive, then launch the installer.
/// </summary>
/// <remarks>
/// Only supported on Windows (the published asset is win-x64). On other platforms
/// <see cref="IsSupported"/> is false and the UI hides the feature.
/// </remarks>
public sealed class UpdateService
{
    // Public releases repo. Source lives in the private repo. The updater downloads the Windows
    // installer asset and runs it, so the install/upgrade is a transparent, branded wizard that
    // uses the Windows Restart Manager to swap files reliably (no manual copy/relaunch dance).
    private const string Owner = "MeshRelayAI";
    private const string Repo = "Mesh";
    // Public releases use a ".zip" wrapping the installer. Raw ".exe" assets remain accepted
    // for backward compatibility with older releases.
    private const string InstallerPrefix = "Mesh-Setup";

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
                // Prefer the current zipped release format; fall back to legacy raw installers.
                foreach (var wantZip in new[] { true, false })
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name is null) continue;
                        var isExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                        var isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        if (!name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (wantZip ? !isZip : !isExe) continue;
                        assetName = name;
                        assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                        break;
                    }
                    if (assetUrl is not null) break;
                }
            }

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            var newer = latest > CurrentVersion;
            if (!newer || assetUrl is null || assetName is null)
                return new UpdateCheckResult(false, CurrentVersion, latest, null,
                    assetUrl is null && newer ? "The latest release has no Windows installer asset." : null);

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
    /// Download the installer asset (reporting byte progress). If the asset is a zip wrapping the
    /// installer, it is unpacked. Returns the path to the installer executable; call
    /// <see cref="ApplyAndExit"/> to run it.
    /// </summary>
    public async Task<string> DownloadAndPrepareAsync(UpdateInfo info, IProgress<UpdateProgress> progress,
        CancellationToken ct = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");

        var root = Path.Combine(Path.GetTempPath(), "MeshUpdate", SanitizeTag(info.TagName));
        var isZip = info.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var downloadPath = Path.Combine(root, isZip ? "installer.zip" : info.AssetName);

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
            await using var dst = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None,
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

        // --- if the installer came zipped, unpack it and find the .exe ---
        string installerPath = downloadPath;
        if (isZip)
        {
            progress.Report(new UpdateProgress(UpdatePhase.Extracting, 0, 0, "Extracting"));
            var extractDir = Path.Combine(root, "installer");
            Directory.CreateDirectory(extractDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true), ct);
            installerPath = FindInstaller(extractDir)
                ?? throw new InvalidOperationException("Downloaded update did not contain an installer.");
        }

        progress.Report(new UpdateProgress(UpdatePhase.ReadyToApply, 0, 0, "Ready to install"));
        return installerPath;
    }

    /// <summary>
    /// Launch the downloaded installer (a visible, branded wizard) and force this app to exit so the
    /// installer can replace files and relaunch. We first ask the app to quit gracefully, then after a
    /// short delay we hard-kill this process and its child tree (including the WebView2
    /// msedgewebview2.exe processes). A force-kill is used instead of Environment.Exit because
    /// Environment.Exit runs finalizers and AppDomain teardown that can hang on a live WebView2 or
    /// SignalR connection and leave an orphaned Mesh.App.exe resident in memory, which would then hold
    /// file locks and fight the installer's Restart Manager.
    /// </summary>
    public void ApplyAndExit(string installerPath)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");

        // Run the installer visibly (no silent flags) so the user sees exactly what is happening.
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
        };

        // Confirm the installer actually started before we tear ourselves down. It is launched with
        // UseShellExecute, so it is a detached process that survives our exit. If it failed to start,
        // do not kill the app, otherwise the user is left with nothing.
        var installer = Process.Start(psi);
        if (installer is null)
            throw new InvalidOperationException("Failed to launch the update installer.");

        // Quit gracefully on the UI thread first (closes the window and tears down the tray), then
        // guarantee the process exits shortly after so the installer never has to fight this process
        // for file locks.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { appControl.Quit(); } catch { }
        });

        // Backstop: after a brief grace period, force-kill the whole process tree. This is the reliable
        // way to remove the orphaned process and its WebView2 children on Windows. Fall back to
        // Environment.Exit only if the kill somehow throws.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            try
            {
                Process.GetCurrentProcess().Kill(entireProcessTree: true);
            }
            catch
            {
                Environment.Exit(0);
            }
        });
    }

    // ---- helpers ----

    /// <summary>Finds the installer executable inside an extracted zip (top level first, then nested).</summary>
    private static string? FindInstaller(string extractDir)
    {
        bool IsInstaller(string p) =>
            Path.GetFileName(p).StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        foreach (var f in Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.TopDirectoryOnly))
            if (IsInstaller(f)) return f;
        foreach (var f in Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories))
            if (IsInstaller(f)) return f;
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
