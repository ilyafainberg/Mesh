using System.Diagnostics;
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

public sealed class UpdateService : IDisposable
{
    internal const long MaxMetadataBytes = 1 * 1024 * 1024;
    internal const long MaxArchiveBytes = 512 * 1024 * 1024;
    internal const long MaxInstallerBytes = 256 * 1024 * 1024;
    private const string Owner = "MeshRelayAI";
    private const string Repo = "Mesh";
    private const string InstallerPrefix = "Mesh-Setup";
    private const string PublisherCommonName = "Quonkel";
    private static readonly Regex ReleaseTagPattern =
        new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern =
        new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory httpFactory;
    private readonly IAppControl appControl;
    private readonly ILogger<UpdateService> log;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Func<bool> isSupported;
    private readonly object timerLock = new();
    private Timer? autoTimer;
    private string? preparedInstallerPath;
    private bool disposed;

    public UpdateService(IHttpClientFactory httpFactory, IAppControl appControl, ILogger<UpdateService> log)
        : this(httpFactory, appControl, log, OperatingSystem.IsWindows)
    {
    }

    internal UpdateService(
        IHttpClientFactory httpFactory,
        IAppControl appControl,
        ILogger<UpdateService> log,
        Func<bool> isSupported)
    {
        this.httpFactory = httpFactory;
        this.appControl = appControl;
        this.log = log;
        this.isSupported = isSupported;
        CurrentVersion = DetectCurrentVersion();
        CleanupStaleTempDirectories();
    }

    public Version CurrentVersion { get; }
    public bool IsSupported => isSupported();
    public UpdateInfo? Available { get; private set; }
    public bool BannerDismissed { get; private set; }
    public event Action? Changed;

    public void StartAutoChecks()
    {
        if (!IsSupported || disposed) return;
        lock (timerLock)
        {
            if (autoTimer is null)
                autoTimer = new Timer(_ => _ = CheckInBackgroundAsync(), null, TimeSpan.Zero, TimeSpan.FromHours(6));
        }
    }

    public async Task CheckInBackgroundAsync()
    {
        if (!IsSupported || disposed) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await CheckAsync(timeout.Token).ConfigureAwait(false);
            if (result.Available && result.Info is not null)
            {
                var changed = Available?.Version != result.Info.Version;
                Available = result.Info;
                if (changed)
                {
                    BannerDismissed = false;
                    Changed?.Invoke();
                }
            }
            else if (result.Error is null && result.Latest is not null)
            {
                var changed = Available is not null;
                Available = null;
                if (changed) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Background update check failed");
        }
    }

    public void DismissBanner()
    {
        if (BannerDismissed) return;
        BannerDismissed = true;
        Changed?.Invoke();
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!IsSupported)
            return new UpdateCheckResult(false, CurrentVersion, null, null, "Updates are only supported on Windows.");

        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var http = httpFactory.CreateClient("updater");
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, CurrentVersion, null, null,
                    $"GitHub returned {(int)response.StatusCode} when checking for updates.");
            if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
                return new UpdateCheckResult(false, CurrentVersion, null, null, "GitHub release metadata is too large.");

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var bounded = new MemoryStream();
            await CopyBoundedAsync(source, bounded, MaxMetadataBytes, ct).ConfigureAwait(false);
            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: ct).ConfigureAwait(false);
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

    public async Task<string> DownloadAndPrepareAsync(
        UpdateInfo info, IProgress<UpdateProgress> progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(progress);
        ThrowIfDisposed();
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");

        await operationGate.WaitAsync(ct).ConfigureAwait(false);
        string? workDirectory = null;
        try
        {
            ValidateInfo(info);
            preparedInstallerPath = null;
            var updateRoot = GetUpdateRoot();
            DeleteDirectoryBestEffort(updateRoot);
            Directory.CreateDirectory(updateRoot);
            workDirectory = Path.Combine(updateRoot, info.TagName);
            Directory.CreateDirectory(workDirectory);
            var archivePath = Path.Combine(workDirectory, "installer.zip");

            var http = httpFactory.CreateClient("updater");
            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxArchiveBytes)
                throw new InvalidDataException("The installer archive is too large.");

            var total = response.Content.Headers.ContentLength ?? info.Size;
            if (total != info.Size)
                throw new InvalidDataException("The installer archive size does not match the GitHub release.");

            progress.Report(new UpdateProgress(UpdatePhase.Downloading, 0, total, "Downloading"));
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(
                archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                long lastReport = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    if (received > MaxArchiveBytes - read)
                        throw new InvalidDataException("The installer archive is too large.");
                    received += read;
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (received - lastReport >= 512 * 1024 || received == total)
                    {
                        lastReport = received;
                        progress.Report(new UpdateProgress(UpdatePhase.Downloading, received, total, null));
                    }
                }

                if (received != info.Size)
                    throw new InvalidDataException("The installer archive size does not match the GitHub release.");
                var actualDigest = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualDigest), Convert.FromHexString(info.Sha256)))
                    throw new CryptographicException("The installer archive SHA-256 digest is invalid.");
            }

            progress.Report(new UpdateProgress(UpdatePhase.Extracting, 0, 0, "Extracting"));
            var installerPath = await ExtractInstallerAsync(archivePath, workDirectory, info, progress, ct)
                .ConfigureAwait(false);
            progress.Report(new UpdateProgress(UpdatePhase.Preparing, 0, 0, "Preparing"));
            VerifySignedInstaller(installerPath);
            preparedInstallerPath = Path.GetFullPath(installerPath);
            progress.Report(new UpdateProgress(UpdatePhase.ReadyToApply, 0, 0, "Ready"));
            return preparedInstallerPath;
        }
        catch
        {
            preparedInstallerPath = null;
            if (workDirectory is not null) DeleteDirectoryBestEffort(workDirectory);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void ApplyAndExit(string installerPath)
    {
        ThrowIfDisposed();
        if (!IsSupported) throw new PlatformNotSupportedException("Updates are only supported on Windows.");
        if (!operationGate.Wait(0))
            throw new InvalidOperationException("Another update operation is already running.");

        try
        {
            var fullPath = Path.GetFullPath(installerPath);
            if (preparedInstallerPath is null ||
                !string.Equals(fullPath, preparedInstallerPath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath))
                throw new InvalidOperationException("Only the currently prepared installer can be applied.");

            VerifySignedInstaller(fullPath);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fullPath)!
            });
            if (process is null)
                throw new InvalidOperationException("Failed to launch the update installer.");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { appControl.Quit(); } catch { }
            });
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                try { Process.GetCurrentProcess().Kill(); }
                catch { Environment.Exit(0); }
            });
        }
        catch
        {
            operationGate.Release();
            throw;
        }
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
            var distinguishedName = new X500DistinguishedName(subject);
            var decoded = distinguishedName.Decode(
                X500DistinguishedNameFlags.UseNewLines | X500DistinguishedNameFlags.DoNotUseQuotes);
            var commonNames = new List<string>();
            var organizations = new List<string>();
            foreach (var line in decoded.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (string.Equals(key, "CN", StringComparison.OrdinalIgnoreCase)) commonNames.Add(value);
                if (string.Equals(key, "O", StringComparison.OrdinalIgnoreCase)) organizations.Add(value);
            }
            return commonNames.Count == 1 && organizations.Count == 1 &&
                   string.Equals(commonNames[0], PublisherCommonName, StringComparison.Ordinal) &&
                   string.Equals(organizations[0], PublisherCommonName, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
        }
        return false;
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
        if (!IsMeshPublisher(certificate.Subject))
            throw new CryptographicException("The installer publisher is not Mesh.");
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
