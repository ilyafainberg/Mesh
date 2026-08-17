using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Keeps a small, local-only runtime log so intermittent application failures can be diagnosed after restart.
/// It records lifecycle state, runtime exceptions, memory warnings, and native diagnostic payloads, but
/// does not intentionally add model prompts, message bodies, keys, or tool arguments.
/// </summary>
public sealed class RuntimeDiagnostics
{
    private const long MaxLogBytes = 512 * 1024;
    private const int MaxDiagnosticPayloadChars = 200_000;
    private const int MaxFingerprints = 128;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] UnexpectedPhases = ["launching", "launched", "foreground", "active", "inactive"];

    private sealed record SessionMarker(string SessionId, string Phase, DateTimeOffset UpdatedAt);

    private readonly object gate = new();
    private readonly string directory;
    private readonly string logPath;
    private readonly string previousLogPath;
    private readonly string markerPath;
    private readonly string fingerprintsPath;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly string sessionId = Guid.NewGuid().ToString("n");
    private readonly HashSet<string> fingerprints = new(StringComparer.Ordinal);
    private int managedHandlersInstalled;
    private bool sessionStarted;
    private string? currentPhase;

    public RuntimeDiagnostics(string directory, Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A diagnostics directory is required.", nameof(directory));

        this.directory = directory;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        logPath = Path.Combine(directory, "runtime.log");
        previousLogPath = Path.Combine(directory, "runtime.previous.log");
        markerPath = Path.Combine(directory, "session.json");
        fingerprintsPath = Path.Combine(directory, "diagnostic-fingerprints.txt");
        LoadFingerprints();
    }

    public static RuntimeDiagnostics? Current { get; private set; }
    public bool PreviousSessionEndedUnexpectedly { get; private set; }
    public string? PreviousSessionPhase { get; private set; }

    public bool HasEntries
    {
        get
        {
            lock (gate)
            {
                try
                {
                    return (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
                           || (File.Exists(previousLogPath) && new FileInfo(previousLogPath).Length > 0);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    return false;
                }
            }
        }
    }

    public void StartSession(
        string platform,
        string? version = null,
        bool detectUnexpectedTermination = false)
    {
        lock (gate)
        {
            if (sessionStarted) return;
            sessionStarted = true;

            try
            {
                Directory.CreateDirectory(directory);
                RotateIfNeededUnsafe();
                var previous = ReadMarkerUnsafe();
                if (detectUnexpectedTermination
                    && previous is not null
                    && !string.Equals(previous.SessionId, sessionId, StringComparison.Ordinal)
                    && UnexpectedPhases.Contains(previous.Phase, StringComparer.Ordinal))
                {
                    PreviousSessionEndedUnexpectedly = true;
                    PreviousSessionPhase = previous.Phase;
                    AppendUnsafe(
                        "previous-session",
                        $"Previous session ended unexpectedly while phase={previous.Phase}; last update={previous.UpdatedAt:O}.");
                }

                currentPhase = "launching";
                WriteMarkerUnsafe(currentPhase);
                var appVersion = version
                                 ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                                 ?? "unknown";
                AppendUnsafe(
                    "session-start",
                    $"version={appVersion}; platform={platform}; framework={RuntimeInformation.FrameworkDescription}; "
                    + $"os={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}");
            }
            catch (Exception ex) when (IsFileError(ex) || ex is JsonException)
            {
                // Diagnostics must never prevent the application from starting.
            }
        }
    }

    public void InstallManagedHandlers()
    {
        if (Interlocked.Exchange(ref managedHandlersInstalled, 1) != 0) return;
        Current = this;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                RecordException("managed-unhandled", exception);
            else
                RecordEvent("managed-unhandled", args.ExceptionObject?.ToString() ?? "Unknown managed exception.");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RecordException("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    public void MarkLifecycle(string phase, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(phase)) return;
        lock (gate)
        {
            try
            {
                if (string.Equals(currentPhase, phase, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(detail))
                    return;
                currentPhase = phase;
                WriteMarkerUnsafe(phase);
                AppendUnsafe("lifecycle", string.IsNullOrWhiteSpace(detail) ? phase : $"{phase}; {detail}");
            }
            catch (Exception ex) when (IsFileError(ex) || ex is JsonException)
            {
                // Lifecycle diagnostics are best-effort and must not affect native callbacks.
            }
        }
    }

    public void RecordEvent(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(message)) return;
        lock (gate)
        {
            try
            {
                AppendUnsafe(category, message);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // The application must keep running if its diagnostics directory is unavailable.
            }
        }
    }

    public void RecordException(string category, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecordEvent(category, exception.ToString());
    }

    public void RecordDiagnosticPayload(string category, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(payload))).ToLowerInvariant();
        lock (gate)
        {
            try
            {
                if (fingerprints.Contains(fingerprint)) return;
                var retained = payload.Length <= MaxDiagnosticPayloadChars
                    ? payload
                    : payload[..MaxDiagnosticPayloadChars] + "\n[diagnostic payload truncated]";
                AppendUnsafe(category, $"fingerprint={fingerprint}\n{retained}");

                fingerprints.Add(fingerprint);
                while (fingerprints.Count > MaxFingerprints)
                    fingerprints.Remove(fingerprints.First());
                File.WriteAllLines(fingerprintsPath, fingerprints, Utf8);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // MetricKit delivery must never destabilize the app it is diagnosing.
            }
        }
    }

    public string CreateReport()
    {
        lock (gate)
        {
            var report = new StringBuilder();
            report.AppendLine("Mesh runtime diagnostics");
            report.AppendLine($"Generated: {utcNow():O}");
            report.AppendLine($"Current session: {sessionId}");
            if (PreviousSessionEndedUnexpectedly)
                report.AppendLine($"Previous session ended unexpectedly during: {PreviousSessionPhase}");
            report.AppendLine("Contains technical lifecycle and exception data. Mesh does not intentionally add message or prompt contents.");
            AppendFileUnsafe(report, previousLogPath, "Previous rotated log");
            AppendFileUnsafe(report, logPath, "Current log");
            return report.ToString();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(previousLogPath)) File.Delete(previousLogPath);
            if (File.Exists(fingerprintsPath)) File.Delete(fingerprintsPath);
            if (File.Exists(markerPath + ".corrupt")) File.Delete(markerPath + ".corrupt");
            fingerprints.Clear();
            PreviousSessionEndedUnexpectedly = false;
            PreviousSessionPhase = null;
        }
    }

    private void LoadFingerprints()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(fingerprintsPath)) return;
                foreach (var value in File.ReadLines(fingerprintsPath).TakeLast(MaxFingerprints))
                    if (!string.IsNullOrWhiteSpace(value)) fingerprints.Add(value.Trim());
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                fingerprints.Clear();
            }
        }
    }

    private SessionMarker? ReadMarkerUnsafe()
    {
        if (!File.Exists(markerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(markerPath, Utf8));
        }
        catch (JsonException)
        {
            var corruptPath = markerPath + ".corrupt";
            if (File.Exists(corruptPath)) File.Delete(corruptPath);
            File.Move(markerPath, corruptPath);
            return null;
        }
    }

    private void WriteMarkerUnsafe(string phase)
    {
        Directory.CreateDirectory(directory);
        var temporaryPath = markerPath + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new SessionMarker(sessionId, phase, utcNow())),
                Utf8);
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void AppendUnsafe(string category, string message)
    {
        Directory.CreateDirectory(directory);
        RotateIfNeededUnsafe();
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > MaxDiagnosticPayloadChars)
            normalized = normalized[..MaxDiagnosticPayloadChars] + "\n[diagnostic entry truncated]";
        var indented = normalized.Replace("\n", "\n    ", StringComparison.Ordinal);
#if ANDROID
        Android.Util.Log.Info("MeshRuntime", $"[{category}] {normalized}");
#endif
        File.AppendAllText(logPath, $"[{utcNow():O}] [{sessionId}] [{category}] {indented}{Environment.NewLine}", Utf8);
    }

    private void RotateIfNeededUnsafe()
    {
        if (!File.Exists(logPath) || new FileInfo(logPath).Length < MaxLogBytes) return;
        if (File.Exists(previousLogPath)) File.Delete(previousLogPath);
        File.Move(logPath, previousLogPath);
    }

    private static void AppendFileUnsafe(StringBuilder report, string path, string heading)
    {
        if (!File.Exists(path)) return;
        report.AppendLine();
        report.AppendLine($"--- {heading} ---");
        report.Append(File.ReadAllText(path, Utf8));
    }

    private static bool IsFileError(Exception exception)
        => exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
}
internal sealed class RuntimeDiagnosticsLoggerProvider(RuntimeDiagnostics diagnostics) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
        => new RuntimeDiagnosticsLogger(diagnostics, categoryName);

    public void Dispose()
    {
    }

    private sealed class RuntimeDiagnosticsLogger(
        RuntimeDiagnostics diagnostics,
        string categoryName) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Warning && IsUiCategory(categoryName);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var eventName = string.IsNullOrWhiteSpace(eventId.Name)
                ? eventId.Id.ToString()
                : $"{eventId.Id}:{eventId.Name}";
            var detail = $"level={logLevel}; category={categoryName}; event={eventName}";
            if (!string.IsNullOrWhiteSpace(message)) detail += "\n" + message;
            if (exception is not null) detail += "\n" + exception;
            diagnostics.RecordEvent("ui-log", detail);
        }

        private static bool IsUiCategory(string category)
            => category.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal)
               || category.StartsWith("Microsoft.JSInterop", StringComparison.Ordinal)
               || category.StartsWith("Mesh.App.Components", StringComparison.Ordinal);
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
