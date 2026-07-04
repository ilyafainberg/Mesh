using System.Diagnostics;
using System.Text;

namespace Mesh.App.Services;

/// <summary>
/// Runs an external process and captures its output, with a timeout and combined stdout/stderr.
/// Shared by the local script/shell tools. This is a powerful capability, callers are responsible
/// for gating it to the owner (or an explicitly shared circle) via the tool visibility model.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        /// <summary>A compact, model-friendly rendering of the run outcome.</summary>
        public string ToToolOutput(int maxChars = 20000)
        {
            var sb = new StringBuilder();
            if (TimedOut) sb.AppendLine("[timed out]");
            sb.AppendLine($"exit code: {ExitCode}");
            if (!string.IsNullOrWhiteSpace(Stdout)) { sb.AppendLine("stdout:"); sb.AppendLine(Stdout.TrimEnd()); }
            if (!string.IsNullOrWhiteSpace(Stderr)) { sb.AppendLine("stderr:"); sb.AppendLine(Stderr.TrimEnd()); }
            var s = sb.ToString().TrimEnd();
            return s.Length > maxChars ? s[..maxChars] + "\n…(truncated)" : s;
        }
    }

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>, optionally piping
    /// <paramref name="stdin"/>, in <paramref name="workingDir"/> (or the current dir). Never throws
    /// for process failures, it returns the captured result including a timeout flag.
    /// </summary>
    public static async Task<Result> RunAsync(
        string fileName,
        string arguments,
        string? stdin = null,
        string? workingDir = null,
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return new Result(-1, "", $"could not start '{fileName}': {ex.Message}", false); }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdin is not null)
        {
            try { await proc.StandardInput.WriteAsync(stdin); await proc.StandardInput.FlushAsync(ct); }
            catch { /* ignore broken pipe */ }
            finally { try { proc.StandardInput.Close(); } catch { } }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 900)));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return new Result(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new Result(proc.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    /// <summary>Finds an executable on PATH (and common locations), returning null if not present.</summary>
    public static string? Which(params string[] candidates)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exts = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };
        foreach (var name in candidates)
            foreach (var dir in pathDirs)
                foreach (var ext in exts)
                {
                    try
                    {
                        var full = Path.Combine(dir, name + ext);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
        return null;
    }
}
