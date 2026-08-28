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
    public int Percent
    {
        get
        {
            if (TotalBytes <= 0) return -1;
            var received = Math.Clamp(BytesReceived, 0, TotalBytes);
            return (int)Math.Clamp((decimal)received * 100m / TotalBytes, 0m, 100m);
        }
    }
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
public sealed class UpdateService : IDisposable
{
    private const string Owner = "MeshRelayAI";
    private const string Repo = "Mesh";
    private const string InstallerPrefix = "Mesh-Setup";
    private const string UpdaterFileName = "Mesh.Updater.exe";
    internal const long MaxMetadataBytes = 1 * 1024 * 1024;
    internal const long MaxArchiveBytes = 512 * 1024 * 1024;
    internal const long MaxInstallerBytes = 256 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> TrustedPublisherAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CN"] = "Feincraft",
            ["O"] = "Feincraft",
            ["L"] = "Woluwe-Saint-Lambert",
            ["S"] = "Bruxelles/Brusell",
            ["C"] = "BE"
        };
    private static readonly Regex ReleaseTagPattern =
        new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern =
        new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory httpFactory;
    private readonly IAppControl appControl;
    private readonly ILogger<UpdateService> log;
    private readonly AppShutdownCoordinator shutdown;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts;
    private readonly Func<bool> isSupported;
    private readonly object timerLock = new();
    private readonly object preparationSync = new();
    private readonly Dictionary<string, Task<PreparedUpdate>> preparationTasks = new(StringComparer.Ordinal);

    private Timer? autoTimer;
    private PreparedUpdate? preparedUpdate;
    private string? preparedTag;
    private int launchRequested;
    private bool disposed;

    public UpdateService(
        IHttpClientFactory httpFactory,
        IAppControl appControl,
        ILogger<UpdateService> log)
        : this(httpFactory, appControl, log, OperatingSystem.IsWindows,
            new AppShutdownCoordinator(new AppShutdownState()))
    {
    }

    public UpdateService(
        IHttpClientFactory httpFactory,
        IAppControl appControl,
        ILogger<UpdateService> log,
        AppShutdownCoordinator shutdown)
        : this(httpFactory, appControl, log, OperatingSystem.IsWindows, shutdown)
    {
    }

    internal UpdateService(
        IHttpClientFactory httpFactory,
        IAppControl appControl,
        ILogger<UpdateService> log,
        Func<bool> isSupported)
        : this(httpFactory, appControl, log, isSupported,
            new AppShutdownCoordinator(new AppShutdownState()))
    {
    }

    private UpdateService(
        IHttpClientFactory httpFactory,
        IAppControl appControl,
        ILogger<UpdateService> log,
        Func<bool> isSupported,
        AppShutdownCoordinator shutdown)
    {
        this.httpFactory = httpFactory;
        this.appControl = appControl;
        this.log = log;
        this.isSupported = isSupported;
        this.shutdown = shutdown;
        lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        shutdown.Register("updates", StopAsync);
        CurrentVersion = DetectCurrentVersion();
        CurrentProgress = new UpdateProgress(UpdatePhase.Idle, 0, 0, null);
    }

    public Version CurrentVersion { get; }
    public bool IsSupported => isSupported();
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

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var http = httpFactory.CreateClient("updater");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, CurrentVersion, null, null,
                    $"GitHub returned {(int)response.StatusCode} when checking for updates.");
            if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
                return new UpdateCheckResult(false, CurrentVersion, null, null, "GitHub release metadata is too large.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var bounded = new MemoryStream();
            await CopyBoundedAsync(source, bounded, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseLatestRelease(document.RootElement, CurrentVersion);
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
        ValidateInfo(info);

        var descriptor = new UpdatePackageDescriptor(info.TagName, info.AssetName, info.DownloadUrl, info.Size);
        var releaseDirectory = UpdatePackageCache.GetReleaseDirectory(
            UpdatePackageCache.DefaultBaseDirectory, info.TagName);

        try
        {
            var cached = await UpdatePackageCache.TryLoadAsync(releaseDirectory, descriptor, cancellationToken);
            if (cached is not null)
            {
                VerifySignedInstaller(cached.InstallerPath);
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
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[1024 * 1024];
            long received = 0;
            long lastReport = 0;
            ReportProgress(info, new UpdateProgress(UpdatePhase.Downloading, 0, total, "Starting download"));
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
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
            var actualDigest = hasher.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(
                    actualDigest, Convert.FromHexString(info.Sha256)))
                throw new CryptographicException("The installer archive SHA-256 digest is invalid.");
        }

        string sourceInstaller;
        if (isZip)
        {
            ReportProgress(info, new UpdateProgress(UpdatePhase.Extracting, 0, 0, "Extracting update"));
            var extractDirectory = Path.Combine(stagingDirectory, "extracted");
            Directory.CreateDirectory(extractDirectory);
            sourceInstaller = await ExtractInstallerAsync(
                downloadPath,
                extractDirectory,
                info,
                new InlineProgress<UpdateProgress>(progress => ReportProgress(info, progress)),
                cancellationToken);
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
        VerifySignedInstaller(finalInstallerPath);
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    internal static UpdateCheckResult ParseLatestRelease(JsonElement root, Version currentVersion)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return InvalidRelease(currentVersion, "The latest release metadata is malformed.");
        if (ReadBoolean(root, "draft") is not false || ReadBoolean(root, "prerelease") is not false)
            return InvalidRelease(currentVersion, "The latest release is not a stable published release.");

        var tag = ReadString(root, "tag_name");
        if (!TryParseVersion(tag, out var latest))
            return InvalidRelease(currentVersion, "The latest release tag is invalid.");
        if (latest <= currentVersion)
            return new UpdateCheckResult(false, currentVersion, latest, null, null);

        var expectedAsset = $"{InstallerPrefix}-{tag}.zip";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return InvalidRelease(currentVersion, "The latest release has no valid Windows installer asset.", latest);

        JsonElement? match = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                string.Equals(ReadString(asset, "name"), expectedAsset, StringComparison.Ordinal))
            {
                if (match is not null)
                    return InvalidRelease(currentVersion, "The latest release contains duplicate installer assets.", latest);
                match = asset;
            }
        }

        if (match is null)
            return InvalidRelease(currentVersion, "The latest release has no valid Windows installer asset.", latest);

        var selected = match.Value;
        var url = ReadString(selected, "browser_download_url");
        var digest = ReadString(selected, "digest");
        var size = ReadInt64(selected, "size");
        if (!IsExpectedDownloadUrl(url, tag!, expectedAsset))
            return InvalidRelease(currentVersion, "The installer download URL is invalid.", latest);
        if (!TryNormalizeDigest(digest, out var sha256))
            return InvalidRelease(currentVersion, "The installer asset has no valid GitHub SHA-256 digest.", latest);
        if (size is null or <= 0 or > MaxArchiveBytes)
            return InvalidRelease(currentVersion, "The installer archive size is invalid.", latest);

        var info = new UpdateInfo(
            latest, tag!, expectedAsset, url!, size.Value, sha256,
            ReadString(root, "body"), ReadString(root, "html_url"));
        return new UpdateCheckResult(true, currentVersion, latest, info, null);
    }

    internal static async Task<string> ExtractInstallerAsync(
        string archivePath,
        string destinationDirectory,
        UpdateInfo info,
        IProgress<UpdateProgress> progress,
        CancellationToken ct)
    {
        var expectedName = $"{InstallerPrefix}-{info.TagName}.exe";
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != 1)
            throw new InvalidDataException("The installer archive must contain exactly one file.");
        var entry = archive.Entries[0];
        if (!string.Equals(entry.FullName, expectedName, StringComparison.Ordinal) ||
            !string.Equals(entry.Name, expectedName, StringComparison.Ordinal) ||
            entry.Length <= 0 || entry.Length > MaxInstallerBytes)
            throw new InvalidDataException("The installer archive has an invalid layout.");

        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        var outputPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer archive contains an unsafe path.");

        await using var input = entry.Open();
        await using var output = new FileStream(
            outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        long extracted = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (extracted > MaxInstallerBytes - read)
                throw new InvalidDataException("The extracted installer is too large.");
            extracted += read;
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress.Report(new UpdateProgress(UpdatePhase.Extracting, extracted, entry.Length, null));
        }
        if (extracted != entry.Length)
            throw new InvalidDataException("The extracted installer size is invalid.");
        return outputPath;
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (value is null) return false;
        var match = ReleaseTagPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
            return false;
        try
        {
            version = new Version(major, minor, patch);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool IsMeshPublisher(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try
        {
            return IsMeshPublisher(new X500DistinguishedName(subject));
        }
        catch (CryptographicException)
        {
        }
        return false;
    }

    private static bool IsMeshPublisher(X500DistinguishedName subject)
    {
        var decoded = subject.Decode(
            X500DistinguishedNameFlags.UseNewLines | X500DistinguishedNameFlags.DoNotUseQuotes);
        var matchedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in decoded.Split(
            new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) return false;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!TrustedPublisherAttributes.TryGetValue(key, out var trustedValue) ||
                !string.Equals(value, trustedValue, StringComparison.Ordinal) ||
                !matchedAttributes.Add(key))
                return false;
        }
        return matchedAttributes.Count == TrustedPublisherAttributes.Count;
    }

    private static void VerifySignedInstaller(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode validation requires Windows.");
        if (WinVerifyTrustFile(path) != 0)
            throw new CryptographicException("The installer Authenticode signature is not trusted.");
#pragma warning disable SYSLIB0057
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        if (!IsMeshPublisher(certificate.SubjectName))
            throw new CryptographicException(
                "The installer publisher is not an approved Mesh publisher (expected Feincraft).");
    }

    private static int WinVerifyTrustFile(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData(filePointer);
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static void ValidateInfo(UpdateInfo info)
    {
        if (!TryParseVersion(info.TagName, out var parsed) || parsed != info.Version)
            throw new InvalidDataException("The update version is invalid.");
        var expectedAsset = $"{InstallerPrefix}-{info.TagName}.zip";
        if (!string.Equals(info.AssetName, expectedAsset, StringComparison.Ordinal))
            throw new InvalidDataException("The update asset name is invalid.");
        if (!IsExpectedDownloadUrl(info.DownloadUrl, info.TagName, expectedAsset))
            throw new InvalidDataException("The update download URL is invalid.");
        if (info.Size <= 0 || info.Size > MaxArchiveBytes)
            throw new InvalidDataException("The update archive size is invalid.");
        if (info.Sha256.Length != 64 || !info.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The update digest is invalid.");
    }

    private static bool IsExpectedDownloadUrl(string? value, string tag, string asset)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        var expected = $"/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";
        return string.Equals(uri.AbsolutePath, expected, StringComparison.Ordinal);
    }

    private static bool TryNormalizeDigest(string? value, out string digest)
    {
        digest = string.Empty;
        if (value is null) return false;
        var match = Sha256Pattern.Match(value);
        if (!match.Success) return false;
        digest = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return;
            if (total > maximum - read) throw new InvalidDataException("Content exceeds the allowed size.");
            total += read;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static UpdateCheckResult InvalidRelease(Version current, string error, Version? latest = null) =>
        new(false, current, latest, null, error);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static long? ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static Version DetectCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var core = informational.Split('+', 2)[0].Split('-', 2)[0];
            if (Version.TryParse(core, out var parsed))
                return new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0));
        }
        var named = assembly.GetName().Version;
        return named is null
            ? new Version(0, 0, 0)
            : new Version(named.Major, Math.Max(named.Minor, 0), Math.Max(named.Build, 0));
    }

    private static string GetUpdateRoot() => Path.Combine(Path.GetTempPath(), "MeshUpdate");

    private static void CleanupStaleTempDirectories()
    {
        var root = GetUpdateRoot();
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                        Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetimeCts.Cancel();
        lock (timerLock)
        {
            autoTimer?.Dispose();
            autoTimer = null;
        }
        lifetimeCts.Dispose();
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public int StructSize = Marshal.SizeOf<WinTrustFileInfo>();
        public string FilePath;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;

        public WinTrustFileInfo(string filePath) => FilePath = filePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData
    {
        public int StructSize = Marshal.SizeOf<WinTrustData>();
        public IntPtr PolicyCallbackData = IntPtr.Zero;
        public IntPtr SipClientData = IntPtr.Zero;
        public uint UIChoice = 2;
        public uint RevocationChecks = 1;
        public uint UnionChoice = 1;
        public IntPtr FileInfo;
        public uint StateAction = 0;
        public IntPtr StateData = IntPtr.Zero;
        public string? UrlReference = null;
        public uint ProviderFlags = 0x00000040;
        public uint UIContext = 0;

        public WinTrustData(IntPtr fileInfo) => FileInfo = fileInfo;
    }
}
