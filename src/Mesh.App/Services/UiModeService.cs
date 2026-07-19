using Microsoft.Extensions.Logging;

namespace Mesh.App.Services;

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/// <summary>UI layout mode. Auto means adaptive (resolved from window width).</summary>
public enum UiMode { Auto, Desktop, Phone }

/// <summary>How the current requested mode was set.</summary>
public enum UiModeSource { Default, CommandLine }

// ---------------------------------------------------------------------------
// Parse result (immutable, plain record - no MAUI dependency)
// ---------------------------------------------------------------------------

/// <summary>Result of parsing --ui-mode from a command-line argument array.</summary>
public sealed record UiModeParseResult(UiMode Mode, UiModeSource Source, bool HadInvalidInput);

// ---------------------------------------------------------------------------
// Pure static parser (no MAUI, no logging - fully testable by linking this file)
// ---------------------------------------------------------------------------

/// <summary>
/// Pure parsing and resolution helpers for UI mode. Contains no MAUI or
/// platform-specific references so this file can be compiled in a plain
/// .NET test project by linking it directly.
/// </summary>
public static class UiModeParser
{
    /// <summary>Maximum width at which a window uses the phone shell.</summary>
    public const double PhoneMaxWidth = 1100;

    /// <summary>
    /// Resolves the effective UI mode from a window width. When width is zero or
    /// negative (not yet known), falls back to platform: mobile gets Phone, all
    /// others get Desktop. Above the phone breakpoint the desktop shell is used.
    /// </summary>
    public static UiMode ResolveFromWidth(double width, bool isMobilePlatform)
    {
        if (width <= 0) return isMobilePlatform ? UiMode.Phone : UiMode.Desktop;
        return width <= PhoneMaxWidth ? UiMode.Phone : UiMode.Desktop;
    }

    /// <summary>
    /// Scans <paramref name="args"/> for a --ui-mode flag and returns the parsed
    /// result. Supports both "--ui-mode value" and "--ui-mode=value" forms.
    /// Quoted tokens are unquoted before matching. When no flag is present,
    /// returns Auto/Default. When the flag is present but the value is invalid
    /// or missing, returns Auto/CommandLine with HadInvalidInput=true.
    /// </summary>
    public static UiModeParseResult ParseArgs(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim('"').Trim('\'');

            if (arg.StartsWith("--ui-mode=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("--ui-mode=".Length).Trim('"').Trim('\'');
                return ParseValue(value);
            }

            if (string.Equals(arg, "--ui-mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    var value = args[i + 1].Trim('"').Trim('\'');
                    return ParseValue(value);
                }
                // Flag with no following value.
                return new(UiMode.Auto, UiModeSource.CommandLine, HadInvalidInput: true);
            }
        }
        return new(UiMode.Auto, UiModeSource.Default, HadInvalidInput: false);
    }

    private static UiModeParseResult ParseValue(string value)
    {
        if (Enum.TryParse<UiMode>(value, ignoreCase: true, out var mode))
            return new(mode, UiModeSource.CommandLine, HadInvalidInput: false);
        return new(UiMode.Auto, UiModeSource.CommandLine, HadInvalidInput: true);
    }

    /// <summary>
    /// Splits a raw Windows command-line string into tokens, respecting
    /// double-quoted segments so that quoted paths with spaces are preserved.
    /// Quotes are stripped from the resulting tokens.
    /// </summary>
    public static string[] SplitWindowsArgs(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char ch in commandLine)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }
}

// ---------------------------------------------------------------------------
// Service interface
// ---------------------------------------------------------------------------

/// <summary>
/// Manages the UI layout mode for the current session. The mode is
/// session-only and is never persisted to user settings or profiles.
/// </summary>
public interface IUiModeService
{
    /// <summary>The mode explicitly requested via --ui-mode (Auto = adaptive).</summary>
    UiMode RequestedMode { get; }

    /// <summary>The currently active layout mode after resolution.</summary>
    UiMode EffectiveMode { get; }

    /// <summary>True when a specific (non-Auto) mode was explicitly requested.</summary>
    bool IsForced { get; }

    /// <summary>How the current requested mode was set.</summary>
    UiModeSource Source { get; }

    /// <summary>Most recently reported window width (0 if not yet known).</summary>
    double CurrentWindowWidth { get; }

    /// <summary>Most recently reported window height (0 if not yet known).</summary>
    double CurrentWindowHeight { get; }

    /// <summary>
    /// Raised when any observable state (EffectiveMode, RequestedMode, Source) changes.
    /// May be raised on any thread.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Stores the current window dimensions. In Auto mode, recomputes EffectiveMode
    /// and raises Changed if it changed. In forced mode, stores the size but does
    /// not change EffectiveMode.
    /// </summary>
    void UpdateWindowSize(double width, double height);

    /// <summary>
    /// Live-applies a new requested mode (used by single-instance activation and tests).
    /// Switching to Auto immediately re-resolves from the last known window width.
    /// Raises Changed only when observable state actually changes.
    /// </summary>
    void ApplyRequestedMode(UiMode mode, UiModeSource source);

    /// <summary>
    /// Parses <paramref name="args"/> for a --ui-mode flag and, if found, applies it.
    /// Used by the Windows single-instance activation bridge to propagate a second
    /// launch's flags to the already-running instance.
    /// </summary>
    void ApplyCommandLine(IReadOnlyList<string> args);
}

// ---------------------------------------------------------------------------
// Service implementation
// ---------------------------------------------------------------------------

/// <inheritdoc cref="IUiModeService"/>
public sealed class UiModeService : IUiModeService
{
    private readonly ILogger<UiModeService> _logger;
    private readonly object _lock = new();

    private UiMode _requestedMode;
    private UiMode _effectiveMode;
    private UiModeSource _source;
    private double _width;
    private double _height;

    public event EventHandler? Changed;

    public UiMode RequestedMode { get { lock (_lock) return _requestedMode; } }
    public UiMode EffectiveMode { get { lock (_lock) return _effectiveMode; } }
    public bool IsForced      { get { lock (_lock) return _requestedMode != UiMode.Auto; } }
    public UiModeSource Source { get { lock (_lock) return _source; } }
    public double CurrentWindowWidth  { get { lock (_lock) return _width; } }
    public double CurrentWindowHeight { get { lock (_lock) return _height; } }

    public UiModeService(ILogger<UiModeService> logger, UiModeParseResult initialOptions)
    {
        _logger = logger;
        _requestedMode = initialOptions.Mode;
        _source = initialOptions.Source;

        if (initialOptions.HadInvalidInput)
            _logger.LogWarning(
                "UiMode: --ui-mode had an invalid or missing value; falling back to Auto.");

        _effectiveMode = _requestedMode == UiMode.Auto
            ? UiModeParser.ResolveFromWidth(0, IsMobilePlatform)
            : _requestedMode;

        _logger.LogInformation(
            "UiModeService initialized: requested={Requested}, effective={Effective}, source={Source}",
            _requestedMode, _effectiveMode, _source);
    }

    private static bool IsMobilePlatform =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public void UpdateWindowSize(double width, double height)
    {
        bool raise = false;
        lock (_lock)
        {
            _width = width;
            _height = height;
            if (_requestedMode == UiMode.Auto)
            {
                var resolved = UiModeParser.ResolveFromWidth(width, IsMobilePlatform);
                if (resolved != _effectiveMode)
                {
                    _effectiveMode = resolved;
                    raise = true;
                }
            }
        }
        if (raise)
        {
            _logger.LogInformation(
                "UiMode effective changed to {Mode} after resize (width={Width})",
                EffectiveMode, width);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyRequestedMode(UiMode mode, UiModeSource source)
        => ApplyRequestedMode(mode, source, forceNotify: false);

    private void ApplyRequestedMode(UiMode mode, UiModeSource source, bool forceNotify)
    {
        bool raise = false;
        lock (_lock)
        {
            var prevRequested  = _requestedMode;
            var prevEffective  = _effectiveMode;
            var prevSource     = _source;
            _requestedMode = mode;
            _source        = source;
            _effectiveMode = mode == UiMode.Auto
                ? UiModeParser.ResolveFromWidth(_width, IsMobilePlatform)
                : mode;
            raise = forceNotify
                || _requestedMode != prevRequested
                || _effectiveMode != prevEffective
                || _source != prevSource;
        }
        _logger.LogInformation(
            "UiMode applied: requested={Requested}, effective={Effective}, source={Source}",
            mode, EffectiveMode, source);
        if (raise) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyCommandLine(IReadOnlyList<string> args)
    {
        var result = UiModeParser.ParseArgs(args);
        if (result.Source == UiModeSource.Default) return; // no --ui-mode present; ignore
        if (result.HadInvalidInput)
            _logger.LogWarning(
                "UiMode: forwarded --ui-mode had an invalid or missing value; falling back to Auto.");
        // A forwarded launch should foreground the running app even when the requested
        // mode already matches, so notify subscribers for every explicit CLI request.
        ApplyRequestedMode(result.Mode, result.Source, forceNotify: true);
    }
}

// ---------------------------------------------------------------------------
// Static activation bridge (Windows single-instance forwarding)
// ---------------------------------------------------------------------------

/// <summary>
/// Allows the Windows platform layer (App.xaml.cs) to forward command-line
/// arguments to the DI-managed <see cref="IUiModeService"/> without a
/// service-locator call. Register the service once after DI build, then call
/// <see cref="ApplyCommandLine"/> from any activation callback.
/// </summary>
public static class UiModeActivationBridge
{
    private static IUiModeService? _service;

    /// <summary>
    /// Called once from MauiProgram after the DI container is built, binding the
    /// singleton service to this static bridge.
    /// </summary>
    public static void Register(IUiModeService service) => _service = service;

    /// <summary>
    /// Forwards <paramref name="args"/> to the running service. Safe to call before
    /// Register (returns without effect). Preserves mesh:// deep-link args untouched.
    /// </summary>
    public static void ApplyCommandLine(IReadOnlyList<string> args)
        => _service?.ApplyCommandLine(args);
}
