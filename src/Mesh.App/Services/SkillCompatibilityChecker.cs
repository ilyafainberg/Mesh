using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>The outcome tier of a compatibility evaluation.</summary>
public enum SkillCompatibilityLevel
{
    /// <summary>The skill can be installed, enabled and run on this device.</summary>
    Compatible,
    /// <summary>The skill may be installed but something (e.g. a missing CLI tool) blocks running it.</summary>
    Warning,
    /// <summary>The skill cannot be used on this device at all.</summary>
    Incompatible
}

/// <summary>The result of evaluating a skill's compatibility against the current device.</summary>
public sealed class SkillCompatibilityResult
{
    public SkillCompatibilityLevel Level { get; init; } = SkillCompatibilityLevel.Compatible;

    /// <summary>Human-readable reasons explaining a Warning or Incompatible verdict.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>CLI tools the skill requires that are not available on this device.</summary>
    public IReadOnlyList<string> MissingCliTools { get; init; } = Array.Empty<string>();

    public bool IsCompatible => Level == SkillCompatibilityLevel.Compatible;
    public bool CanInstall => Level != SkillCompatibilityLevel.Incompatible;
    public bool CanRun => Level == SkillCompatibilityLevel.Compatible;

    public static SkillCompatibilityResult Ok() => new();
}

/// <summary>Probes whether a named CLI tool is available on the current device.</summary>
public interface ICliToolProbe
{
    /// <summary>True if <paramref name="tool"/> is resolvable on PATH (or otherwise runnable).</summary>
    bool IsAvailable(string tool);
}

/// <summary>A probe that reports every tool as missing - the safe default on mobile.</summary>
public sealed class NoCliToolsProbe : ICliToolProbe
{
    public static readonly NoCliToolsProbe Instance = new();
    public bool IsAvailable(string tool) => false;
}

/// <summary>
/// Desktop CLI probe that resolves a tool by scanning the <c>PATH</c> directories on the filesystem
/// (respecting <c>PATHEXT</c> on Windows). It never spawns a process, so it is safe to call from any
/// thread and has no shell dependency. Results are cached for the probe's lifetime.
/// </summary>
public sealed class PathCliToolProbe : ICliToolProbe
{
    private readonly Dictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool IsAvailable(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return false;
        lock (_gate)
        {
            if (_cache.TryGetValue(tool, out var cached)) return cached;
            var found = Resolve(tool);
            _cache[tool] = found;
            return found;
        }
    }

    private static bool Resolve(string tool)
    {
        // A path-qualified tool is checked directly.
        if (tool.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return FileExistsWithExt(tool);

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return false;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed;
            try { trimmed = dir.Trim().Trim('"'); }
            catch { continue; }
            if (trimmed.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(trimmed, tool); }
            catch { continue; }
            if (FileExistsWithExt(candidate)) return true;
        }
        return false;
    }

    private static bool FileExistsWithExt(string candidate)
    {
        try
        {
            if (File.Exists(candidate)) return true;
            if (!OperatingSystem.IsWindows()) return false;
            var exts = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrEmpty(exts)) exts = ".EXE;.CMD;.BAT;.COM";
            foreach (var ext in exts.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var e = ext.StartsWith('.') ? ext : "." + ext;
                if (File.Exists(candidate + e)) return true;
            }
        }
        catch
        {
            // A malformed PATH entry never makes a tool "available".
        }
        return false;
    }
}

/// <summary>
/// Pure, injectable device-compatibility checker for Mesh 1.17 skills. It never performs I/O beyond
/// the injected <see cref="ICliToolProbe"/> (which callers must run off the UI thread) and produces a
/// deterministic <see cref="SkillCompatibilityResult"/>. It is the single authority for whether a
/// skill may be installed, enabled or run on a given device:
/// <list type="bullet">
///   <item>The current OS and device class are supplied explicitly (never inferred here).</item>
///   <item>Mobile devices reject Desktop-class packages, any required CLI tool, and any script or
///   supporting file - a mobile skill is Skill.md instructions only.</item>
///   <item>Desktop devices reject unsupported operating systems; a missing required CLI tool is a
///   Warning at install time but blocks running.</item>
/// </list>
/// </summary>
public sealed class SkillCompatibilityChecker
{
    private readonly SkillOperatingSystems _currentOs;
    private readonly bool _isMobile;
    private readonly ICliToolProbe _cliProbe;

    /// <param name="currentOs">The single OS flag for the device the check runs on.</param>
    /// <param name="isMobile">True when the current device is a phone/tablet (iOS or Android).</param>
    /// <param name="cliProbe">Injectable CLI availability probe. Defaults to "nothing available".</param>
    public SkillCompatibilityChecker(SkillOperatingSystems currentOs, bool isMobile, ICliToolProbe? cliProbe = null)
    {
        _currentOs = currentOs;
        _isMobile = isMobile;
        _cliProbe = cliProbe ?? NoCliToolsProbe.Instance;
    }

    /// <summary>Build a checker for the running device using <see cref="PlatformCaps"/>.</summary>
    public static SkillCompatibilityChecker ForCurrentDevice(ICliToolProbe? cliProbe = null)
        => new(CurrentOperatingSystem(), PlatformCaps.IsMobile, cliProbe);

    /// <summary>Map the running platform to a single <see cref="SkillOperatingSystems"/> flag.</summary>
    public static SkillOperatingSystems CurrentOperatingSystem()
    {
        if (OperatingSystem.IsWindows()) return SkillOperatingSystems.Windows;
        if (OperatingSystem.IsMacOS()) return SkillOperatingSystems.MacOS;
        if (OperatingSystem.IsIOS()) return SkillOperatingSystems.IOS;
        if (OperatingSystem.IsAndroid()) return SkillOperatingSystems.Android;
        if (OperatingSystem.IsLinux()) return SkillOperatingSystems.Linux;
        return SkillOperatingSystems.None;
    }

    /// <summary>Evaluate a compatibility declaration against the current device.</summary>
    public SkillCompatibilityResult Check(SkillCompatibility? compatibility)
    {
        // A skill with no explicit compatibility is treated as universal, Skill.md-only instructions:
        // always compatible everywhere.
        if (compatibility is null)
            return SkillCompatibilityResult.Ok();

        var reasons = new List<string>();
        var missing = new List<string>();
        var incompatible = false;

        // 1. Operating-system match. The declared set must include the current OS.
        if (compatibility.OperatingSystems != SkillOperatingSystems.None
            && (compatibility.OperatingSystems & _currentOs) == 0)
        {
            incompatible = true;
            reasons.Add($"This skill does not support {DescribeOs(_currentOs)}.");
        }

        // 2. Device-class rules.
        if (_isMobile)
        {
            if (compatibility.DeviceClass == SkillDeviceClass.Desktop)
            {
                incompatible = true;
                reasons.Add("This is a desktop-only skill and cannot run on a mobile device.");
            }
            if (compatibility.RequiredCliTools.Count > 0)
            {
                incompatible = true;
                reasons.Add("This skill requires command-line tools, which are not available on mobile devices.");
            }
        }
        else
        {
            if (compatibility.DeviceClass == SkillDeviceClass.Mobile)
            {
                incompatible = true;
                reasons.Add("This is a mobile-only skill and cannot run on a desktop device.");
            }

            // 3. CLI probe (desktop only). Missing tools warn at install, block run.
            foreach (var tool in compatibility.RequiredCliTools)
            {
                if (string.IsNullOrWhiteSpace(tool)) continue;
                if (!_cliProbe.IsAvailable(tool))
                    missing.Add(tool);
            }
            if (missing.Count > 0)
                reasons.Add($"Required command-line tool(s) not found: {string.Join(", ", missing)}.");
        }

        var level =
            incompatible ? SkillCompatibilityLevel.Incompatible
            : missing.Count > 0 ? SkillCompatibilityLevel.Warning
            : SkillCompatibilityLevel.Compatible;

        return new SkillCompatibilityResult
        {
            Level = level,
            Reasons = reasons,
            MissingCliTools = missing
        };
    }

    /// <summary>
    /// Validate that a package's file set is admissible on the current device before install. On
    /// mobile, a package must be Skill.md only (no scripts/resources/config); on desktop any file set
    /// declared compatible is allowed. Returns the combined compatibility verdict.
    /// </summary>
    public SkillCompatibilityResult CheckPackage(SkillPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var baseResult = Check(manifest.Compatibility);
        var reasons = new List<string>(baseResult.Reasons);
        var incompatible = baseResult.Level == SkillCompatibilityLevel.Incompatible;

        if (_isMobile)
        {
            var extra = manifest.Files.Count(f => f.Role != SkillFileRole.SkillMarkdown);
            if (extra > 0)
            {
                incompatible = true;
                reasons.Add("Mobile devices only support Skill.md instruction files; this package includes additional files.");
            }
        }

        var level =
            incompatible ? SkillCompatibilityLevel.Incompatible
            : baseResult.Level;

        return new SkillCompatibilityResult
        {
            Level = level,
            Reasons = reasons,
            MissingCliTools = baseResult.MissingCliTools
        };
    }

    private static string DescribeOs(SkillOperatingSystems os) => os switch
    {
        SkillOperatingSystems.Windows => "Windows",
        SkillOperatingSystems.MacOS => "macOS",
        SkillOperatingSystems.Linux => "Linux",
        SkillOperatingSystems.IOS => "iOS",
        SkillOperatingSystems.Android => "Android",
        SkillOperatingSystems.None => "this device",
        _ => os.ToString()
    };
}
