using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace Mesh.App.Services;

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

public readonly record struct UpdateProgress(UpdatePhase Phase, long BytesReceived, long TotalBytes, string? Message)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, 100) : -1;
}

public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string AssetName,
    string DownloadUrl,
    long Size,
    string Sha256,
    string? ReleaseNotes,
    string? HtmlUrl);

public sealed record UpdateCheckResult(bool Available, Version Current, Version? Latest, UpdateInfo? Info, string? Error);

/// <summary>
/// Detects, pre-downloads, and starts signed Windows updates. The installer is cached until the
/// user chooses Update, then a bundled helper closes Mesh, installs silently, and relaunches it.
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "MeshRelayAI";
    private const string Repo = "Mesh";
    private const string InstallerPrefix = "Mesh-Setup";
    private const string UpdaterFileName = "Mesh.Updater.exe";

    private readonly IHttpClientFactory httpFactory;
    private readonly IAppControl appControl;
    private readonly ILogger<UpdateService> log;
    private readonly AppShutdownCoordinator shutdown;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly object preparationSync = new();
    private readonly Dictionary<string, Task<PreparedUpdate>> preparationTasks = new(StringComparer.Ordinal);

    private Timer? autoTimer;
    private PreparedUpdate? preparedUpdate;
    private string? preparedTag;
    private int launchRequested;

    public UpdateService(IHttpClientFactory httpFactory, IAppControl appControl,
        ILogger<UpdateService> log, AppShutdownCoordinator shutdown)
    {
        this.httpFactory = httpFactory;
        this.appControl = appControl;
        this.log = log;
        this.shutdown = shutdown;
        shutdown.Register("updates", StopAsync);
        CurrentVersion = DetectCurrentVersion();
        CurrentProgress = new UpdateProgress(UpdatePhase.Idle, 0, 0, null);
    }

    public Version CurrentVersion { get; }
    public bool IsSupported => OperatingSystem.IsWindows();
    public UpdateInfo? Available { get; private set; }
    public bool BannerDismissed { get; private set; }
    public UpdatePhase Phase { get; private set; }
    public UpdateProgress CurrentProgress { get; private set; }
    public string? Status { get; private set; }
    public string? Error { get; private set; }
    public bool IsLaunchRequested => Volatile.Read(ref launchRequested) != 0;
    public int Percent => CurrentProgress.Percent;
    public long BytesReceived => CurrentProgress.BytesReceived;
    public long TotalBytes => CurrentProgress.TotalBytes;

    public string BannerText
    {
        get
        {
            if (Available is null) return string.Empty;
            var version = Available.Version.ToString(3);
            if (Phase == UpdatePhase.Failed) return $"Mesh {version} update failed.";
            if (IsLaunchRequested)
            {
                if (Phase == UpdatePhase.Downloading && Percent >= 0)
                    return $"Downloading Mesh {version} ({Percent}%).";
                return $"Starting the Mesh {version} update.";
            }
            return Phase switch
            {
                UpdatePhase.Downloading when Percent >= 0 => $"Mesh {version} is available. Downloading {Percent}%.",
                UpdatePhase.Downloading => $"Mesh {version} is available. Downloading in the background.",
                UpdatePhase.Extracting or UpdatePhase.Preparing => $"Mesh {version} is available. Preparing in the background.",
                UpdatePhase.ReadyToApply => $"Mesh {version} is ready to update.",
                _ => $"Mesh {version} is available."
            };
        }
    }

    public string ActionText => IsLaunchRequested ? "Updating..." : Phase == UpdatePhase.Failed ? "Retry" : "Update";

    public event Action? Changed;

    public void StartAutoChecks()
    {
        if (!IsSupported || autoTimer is not null) return;
        CleanupTemporaryLaunchers();
        autoTimer = new Timer(_ =>
        {
            if (shutdown.IsStopping) return;
            shutdown.Track(CheckInBackgroundAsync(shutdown.Token), "update check");
        }, null, TimeSpan.Zero, TimeSpan.FromHours(6));
    }

    public async Task CheckInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || disposed) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await CheckNowAsync(cts.Token);
            if (result.Error is not null)
                log.LogInformation("Background update check did not complete: {Error}", result.Error);
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("Background update check timed out");
        }
    }

    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        await checkGate.WaitAsync(cancellationToken);
        try
        {
            var result = await CheckAsync(cancellationToken);
            if (result.Available && result.Info is not null)
            {
                SetAvailable(result.Info);
                StartPreDownload(result.Info);
            }
            else if (result.Error is null && Available is null)
            {
                SetState(UpdatePhase.UpToDate, "Mesh is up to date.");
            }
            return result;
        }
        finally
        {
            checkGate.Release();
        }
    }

    public void DismissBanner()
    {
        if (BannerDismissed) return;
        BannerDismissed = true;
        NotifyChanged();
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsSupported)
            return new UpdateCheckResult(false, CurrentVersion, null, null, "Updates are only supported on Windows.");

        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var http = httpFactory.CreateClient("updater");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, CurrentVersion, null, null,
                    $"GitHub returned {(int)response.StatusCode} when checking for updates.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || !TryParseVersion(tag, out var latest))
                return new UpdateCheckResult(false, CurrentVersion, null, null, "Could not read the latest version.");

            string? assetName = null;
            string? assetUrl = null;
            long assetSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var wantZip in new[] { true, false })
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                        if (name is null || !name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var matchesType = wantZip
                            ? name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                        if (!matchesType) continue;

                        assetName = name;
                        assetUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                            ? urlElement.GetString()
                            : null;
                        assetSize = asset.TryGetProperty("size", out var sizeElement)
                            && sizeElement.TryGetInt64(out var value) ? value : 0;
                        break;
                    }
                    if (assetUrl is not null) break;
                }
            }

            var releaseNotes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;
            var newer = latest > CurrentVersion;
            if (!newer || assetUrl is null || assetName is null)
            {
                return new UpdateCheckResult(false, CurrentVersion, latest, null,
                    assetUrl is null && newer ? "The latest release has no Windows installer asset." : null);
            }

            var info = new UpdateInfo(latest, tag, assetName, assetUrl, assetSize, releaseNotes, htmlUrl);
            return new UpdateCheckResult(true, CurrentVersion, latest, info, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            log.LogWarning(ex, "Update check failed");
            return new UpdateCheckResult(false, CurrentVersion, null, null, $"Update check failed: {ex.Message}");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool> StartUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return false;
        if (Interlocked.CompareExchange(ref launchRequested, 1, 0) != 0) return false;

        var updaterStarted = false;
        UpdateInfo? targetInfo = null;
        Error = null;
        NotifyChanged();
        try
        {
            targetInfo = Available;
            if (targetInfo is null)
            {
                var result = await CheckNowAsync(cancellationToken);
                targetInfo = result.Info;
                if (targetInfo is null)
                    throw new InvalidOperationException(result.Error ?? "No Mesh update is available.");
            }

            var prepared = await EnsurePreparedAsync(targetInfo, cancellationToken);
            SetState(UpdatePhase.Applying, "Opening the Mesh updater.");
            LaunchUpdaterAndExit(prepared, targetInfo);
            updaterStarted = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            log.LogWarning(ex, "Could not start the Mesh update");
            PublishFailure(targetInfo, $"Update failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (!updaterStarted)
            {
                Interlocked.Exchange(ref launchRequested, 0);
                NotifyChanged();
            }
        }
    }

    private void SetAvailable(UpdateInfo info)
    {
        var isNewRelease = !string.Equals(Available?.TagName, info.TagName, StringComparison.Ordinal);
        Available = info;
        if (isNewRelease)
        {
            BannerDismissed = false;
            preparedUpdate = null;
            preparedTag = null;
            Error = null;
            SetState(UpdatePhase.Idle, $"Mesh {info.Version.ToString(3)} is available.");
        }
        else
        {
            NotifyChanged();
        }
    }

    private void StartPreDownload(UpdateInfo info)
    {
        if (shutdown.IsStopping) return;
        shutdown.Track(ObservePreDownloadAsync(info), $"update pre-download {info.Version}");
    }

    private async Task ObservePreDownloadAsync(UpdateInfo info)
    {
        try
        {
            await EnsurePreparedAsync(info, shutdown.Token);
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            log.LogWarning(ex, "Could not pre-download Mesh {Version}", info.Version);
        }
    }

    private async Task<PreparedUpdate> EnsurePreparedAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        if (preparedUpdate is not null
            && string.Equals(preparedTag, info.TagName, StringComparison.Ordinal)
            && File.Exists(preparedUpdate.InstallerPath))
            return preparedUpdate;

        Task<PreparedUpdate> task;
        lock (preparationSync)
        {
            if (!preparationTasks.TryGetValue(info.TagName, out task!))
            {
                task = PrepareTrackedAsync(info);
                preparationTasks[info.TagName] = task;
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (task.IsCompleted)
            {
                lock (preparationSync)
                {
                    if (preparationTasks.TryGetValue(info.TagName, out var current) && ReferenceEquals(current, task))
                        preparationTasks.Remove(info.TagName);
                }
            }
        }
    }

    private async Task<PreparedUpdate> PrepareTrackedAsync(UpdateInfo info)
    {
        try
        {
            var prepared = await PrepareUpdateCoreAsync(info, shutdown.Token);
            if (string.Equals(Available?.TagName, info.TagName, StringComparison.Ordinal))
            {
                preparedUpdate = prepared;
                preparedTag = info.TagName;
            }
            return prepared;
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            PublishFailure(info, $"Update download failed: {ex.Message}");
            throw;
        }
    }

    private async Task<PreparedUpdate> PrepareUpdateCoreAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFileName(info.AssetName), info.AssetName, StringComparison.Ordinal))
            throw new InvalidDataException("The update asset name is invalid.");

        var descriptor = new UpdatePackageDescriptor(info.TagName, info.AssetName, info.DownloadUrl, info.Size);
        var releaseDirectory = UpdatePackageCache.GetReleaseDirectory(
            UpdatePackageCache.DefaultBaseDirectory, info.TagName);

        try
        {
            var cached = await UpdatePackageCache.TryLoadAsync(releaseDirectory, descriptor, cancellationToken);
            if (cached is not null)
            {
                ReportProgress(info, new UpdateProgress(UpdatePhase.ReadyToApply, 0, 0, "Ready to update"));
                return cached;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Discarding an invalid cached Mesh update at {Path}", releaseDirectory);
        }

        ResetReleaseDirectory(releaseDirectory);
        var stagingDirectory = Path.Combine(releaseDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);
        var isZip = info.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var downloadPath = Path.Combine(stagingDirectory, isZip ? "installer.zip" : info.AssetName);

        var http = httpFactory.CreateClient("updater");
        using (var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? (info.Size > 0 ? info.Size : 0);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1024 * 1024];
            long received = 0;
            long lastReport = 0;
            ReportProgress(info, new UpdateProgress(UpdatePhase.Downloading, 0, total, "Starting download"));
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (received - lastReport >= 512 * 1024 || received == total)
                {
                    lastReport = received;
                    ReportProgress(info, new UpdateProgress(UpdatePhase.Downloading, received, total, null));
                }
            }
            await destination.FlushAsync(cancellationToken);
            if (total > 0 && received != total)
                throw new InvalidDataException($"The update download was incomplete ({received} of {total} bytes).");
            if (info.Size > 0 && received != info.Size)
                throw new InvalidDataException($"The update size did not match its release metadata ({received} of {info.Size} bytes).");
        }

        string sourceInstaller;
        if (isZip)
        {
            ReportProgress(info, new UpdateProgress(UpdatePhase.Extracting, 0, 0, "Extracting update"));
            var extractDirectory = Path.Combine(stagingDirectory, "extracted");
            Directory.CreateDirectory(extractDirectory);
            await Task.Run(() => ZipFile.ExtractToDirectory(downloadPath, extractDirectory, overwriteFiles: true), cancellationToken);
            sourceInstaller = FindInstaller(extractDirectory);
        }
        else
        {
            sourceInstaller = downloadPath;
        }

        var installerName = Path.GetFileName(sourceInstaller);
        if (!installerName.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
            || !installerName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update did not contain the Mesh installer.");

        var finalInstallerPath = Path.Combine(releaseDirectory, installerName);
        File.Copy(sourceInstaller, finalInstallerPath, overwrite: true);
        ReportProgress(info, new UpdateProgress(UpdatePhase.Preparing, 0, 0, "Verifying downloaded update"));
        var result = await UpdatePackageCache.SaveAsync(
            releaseDirectory, descriptor, finalInstallerPath, cancellationToken);

        TryDeleteStagingDirectory(stagingDirectory);
        PruneOldUpdates(releaseDirectory);
        ReportProgress(info, new UpdateProgress(UpdatePhase.ReadyToApply, 0, 0, "Ready to update"));
        return result;
    }

    private void LaunchUpdaterAndExit(PreparedUpdate prepared, UpdateInfo info)
    {
        var bundledUpdater = Path.Combine(AppContext.BaseDirectory, UpdaterFileName);
        if (!File.Exists(bundledUpdater))
            throw new FileNotFoundException("The bundled Mesh updater is missing.", bundledUpdater);

        var cleanupDirectory = Path.GetDirectoryName(prepared.InstallerPath)
            ?? throw new InvalidOperationException("The prepared update directory could not be located.");
        var updatesRoot = Path.GetFullPath(UpdatePackageCache.DefaultBaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCleanupDirectory = Path.GetFullPath(cleanupDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullCleanupDirectory.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prepared update is outside the Mesh update cache.");

        var meshExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(meshExe) || !File.Exists(meshExe))
            throw new FileNotFoundException("The running Mesh executable could not be located.", meshExe);

        var launcherDirectory = Path.Combine(Path.GetTempPath(), "MeshUpdater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(launcherDirectory);
        var launcherName = "Mesh.Updater-" + Guid.NewGuid().ToString("N") + ".exe";
        var launcherPath = Path.Combine(launcherDirectory, launcherName);
        File.Copy(bundledUpdater, launcherPath, overwrite: false);

        var bundledConfig = bundledUpdater + ".config";
        if (File.Exists(bundledConfig))
            File.Copy(bundledConfig, launcherPath + ".config", overwrite: false);

        var quitEventName = @"Local\MeshUpdateQuit-" + Guid.NewGuid().ToString("N");
        var quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, quitEventName);
        Process updater;
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = launcherDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--installer");
            startInfo.ArgumentList.Add(prepared.InstallerPath);
            startInfo.ArgumentList.Add("--mesh-exe");
            startInfo.ArgumentList.Add(meshExe);
            startInfo.ArgumentList.Add("--cleanup-dir");
            startInfo.ArgumentList.Add(cleanupDirectory);
            startInfo.ArgumentList.Add("--sha256");
            startInfo.ArgumentList.Add(prepared.Sha256);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(info.Version.ToString(3));
            startInfo.ArgumentList.Add("--quit-event");
            startInfo.ArgumentList.Add(quitEventName);
            startInfo.ArgumentList.Add("--mesh-pid");
            startInfo.ArgumentList.Add(currentProcess.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--mesh-start-ticks");
            startInfo.ArgumentList.Add(currentProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

            updater = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Mesh updater could not be started.");
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            quitEvent.Dispose();
            TryDeleteLauncherDirectory(launcherDirectory);
            throw;
        }

        _ = CoordinateUpdaterShutdownAsync(updater, quitEvent, info);
    }

    private async Task CoordinateUpdaterShutdownAsync(
        Process updater,
        EventWaitHandle quitEvent,
        UpdateInfo info)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!quitEvent.WaitOne(0))
            {
                if (updater.HasExited)
                {
                    Interlocked.Exchange(ref launchRequested, 0);
                    PublishFailure(info, $"The Mesh updater closed before installation (code {updater.ExitCode}).");
                    return;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    try { updater.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        log.LogWarning(ex, "Could not stop an unresponsive Mesh updater");
                    }
                    Interlocked.Exchange(ref launchRequested, 0);
                    PublishFailure(info, "The Mesh updater did not request shutdown in time.");
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }

            try
            {
                await appControl.QuitAsync();
            }
            catch (Exception ex) when (IsExpectedUpdateException(ex))
            {
                log.LogWarning(ex, "Mesh did not quit cleanly after the updater requested shutdown");
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
            try
            {
                Process.GetCurrentProcess().Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                log.LogWarning(ex, "Could not terminate Mesh after the updater requested shutdown");
                Environment.Exit(0);
            }
        }
        catch (Exception ex) when (IsExpectedUpdateException(ex))
        {
            log.LogWarning(ex, "Could not coordinate shutdown with the Mesh updater");
            Interlocked.Exchange(ref launchRequested, 0);
            PublishFailure(info, $"The Mesh updater failed before installation: {ex.Message}");
        }
        finally
        {
            updater.Dispose();
            quitEvent.Dispose();
        }
    }
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref autoTimer, null)?.Dispose();
        Task<PreparedUpdate>[] pending;
        lock (preparationSync)
            pending = preparationTasks.Values.Where(task => !task.IsCompleted).ToArray();
        if (pending.Length == 0) return;

        var completions = pending.Select(task => task.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default));
        await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NotifyChanged()
    {
        if (!shutdown.IsStopping)
            Changed?.Invoke();
    }
    private void ReportProgress(UpdateInfo info, UpdateProgress progress)
    {
        if (!string.Equals(Available?.TagName, info.TagName, StringComparison.Ordinal)) return;
        Phase = progress.Phase;
        CurrentProgress = progress;
        Error = null;
        Status = progress.Message ?? progress.Phase switch
        {
            UpdatePhase.Downloading => "Downloading update.",
            UpdatePhase.Extracting => "Extracting update.",
            UpdatePhase.Preparing => "Preparing update.",
            UpdatePhase.ReadyToApply => "Ready to update.",
            _ => Status
        };
        NotifyChanged();
    }

    private void PublishFailure(UpdateInfo? info, string message)
    {
        if (info is not null && !string.Equals(Available?.TagName, info.TagName, StringComparison.Ordinal)) return;
        Phase = UpdatePhase.Failed;
        CurrentProgress = new UpdateProgress(UpdatePhase.Failed, 0, 0, message);
        Status = message;
        Error = message;
        NotifyChanged();
    }

    private void SetState(UpdatePhase phase, string? status)
    {
        Phase = phase;
        CurrentProgress = new UpdateProgress(phase, 0, 0, status);
        Status = status;
        if (phase != UpdatePhase.Failed) Error = null;
        NotifyChanged();
    }

    private static void ResetReleaseDirectory(string releaseDirectory)
    {
        if (Directory.Exists(releaseDirectory)) Directory.Delete(releaseDirectory, recursive: true);
        Directory.CreateDirectory(releaseDirectory);
    }

    private void TryDeleteStagingDirectory(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not remove update staging directory {Path}", stagingDirectory);
        }
    }

    private void PruneOldUpdates(string currentReleaseDirectory)
    {
        var baseDirectory = UpdatePackageCache.DefaultBaseDirectory;
        if (!Directory.Exists(baseDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
        {
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentReleaseDirectory),
                StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogDebug(ex, "Could not remove old update cache {Path}", directory);
            }
        }
    }

    private static string FindInstaller(string extractDirectory)
    {
        var matches = Directory.EnumerateFiles(extractDirectory, "*.exe", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException("Downloaded update did not contain an installer."),
            _ => throw new InvalidDataException("Downloaded update contained more than one installer.")
        };
    }

    private void TryDeleteLauncherDirectory(string launcherDirectory)
    {
        try
        {
            if (Directory.Exists(launcherDirectory)) Directory.Delete(launcherDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not remove updater launcher {Path}", launcherDirectory);
        }
    }

    private void CleanupTemporaryLaunchers()
    {
        var launcherRoot = Path.Combine(Path.GetTempPath(), "MeshUpdater");
        if (!Directory.Exists(launcherRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(launcherRoot))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogDebug(ex, "Could not remove old updater launcher {Path}", directory);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(launcherRoot).Any()) Directory.Delete(launcherRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not remove the empty updater launcher root {Path}", launcherRoot);
        }
    }

    private static bool IsExpectedUpdateException(Exception exception)
        => exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or Win32Exception
            or JsonException
            or UriFormatException
            or NotSupportedException;

    private static Version DetectCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var version)) return version;
        var named = assembly.GetName().Version;
        return named is not null
            ? new Version(named.Major, named.Minor, Math.Max(named.Build, 0))
            : new Version(0, 0, 0);
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V')) value = value[1..];
        var cut = value.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) value = value[..cut];
        if (value.Length == 0) return false;

        var parts = value.Split('.');
        int major = 0, minor = 0, patch = 0;
        if (parts.Length > 0) int.TryParse(parts[0], out major);
        if (parts.Length > 1) int.TryParse(parts[1], out minor);
        if (parts.Length > 2) int.TryParse(parts[2], out patch);
        version = new Version(major, minor, patch);
        return true;
    }
}
