using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>Runs a Python script on the local machine and returns its output. Owner-gated local tool.</summary>
public sealed class RunPythonTool : IAgentTool
{
    public string Name => "run_python";
    public string Description =>
        "Run a Python 3 script on the local machine and return its stdout, stderr and exit code. " +
        "Use for data processing, calculations, and scripting. Requires Python to be installed.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "The Python source to run." },
            working_directory = new { type = "string", description = "Optional working directory." },
            timeout_seconds = new { type = "integer", description = "Optional timeout (default 120)." }
        },
        required = new[] { "code" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var code = ToolArgs.GetString(args, "code");
        if (string.IsNullOrWhiteSpace(code)) return "ERROR: no code given.";
        var wd = ToolArgs.GetString(args, "working_directory");
        var timeout = ToolArgs.GetInt(args, "timeout_seconds", 120);

        var python = ProcessRunner.Which("python", "python3", "py");
        if (python is null)
            return "ERROR: Python is not installed or not on PATH. Install Python 3 to use this tool.";

        var tmp = Path.Combine(Path.GetTempPath(), $"mesh-py-{Guid.NewGuid():n}.py");
        try
        {
            await File.WriteAllTextAsync(tmp, code, ct);
            var result = await ProcessRunner.RunAsync(
                python, $"-X utf8 \"{tmp}\"",
                workingDir: string.IsNullOrWhiteSpace(wd) ? null : wd,
                timeoutSeconds: timeout, ct: ct);
            return result.ToToolOutput();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}

/// <summary>
/// Runs a C# script via `dotnet script` (if available) and returns its output. Owner-gated local tool.
/// </summary>
public sealed class RunCSharpScriptTool : IAgentTool
{
    public string Name => "run_csharp_script";
    public string Description =>
        "Run a C# script (top-level statements, like a .csx) on the local machine and return its " +
        "stdout, stderr and exit code. Requires the 'dotnet-script' global tool to be installed.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "The C# script source (top-level statements allowed)." },
            working_directory = new { type = "string", description = "Optional working directory." },
            timeout_seconds = new { type = "integer", description = "Optional timeout (default 180)." }
        },
        required = new[] { "code" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var code = ToolArgs.GetString(args, "code");
        if (string.IsNullOrWhiteSpace(code)) return "ERROR: no code given.";
        var wd = ToolArgs.GetString(args, "working_directory");
        var timeout = ToolArgs.GetInt(args, "timeout_seconds", 180);

        var dotnet = ProcessRunner.Which("dotnet");
        if (dotnet is null) return "ERROR: the .NET SDK (dotnet) is not installed or not on PATH.";

        var tmp = Path.Combine(Path.GetTempPath(), $"mesh-csx-{Guid.NewGuid():n}.csx");
        try
        {
            await File.WriteAllTextAsync(tmp, code, ct);
            var result = await ProcessRunner.RunAsync(
                dotnet, $"script \"{tmp}\"",
                workingDir: string.IsNullOrWhiteSpace(wd) ? null : wd,
                timeoutSeconds: timeout, ct: ct);
            if (result.ExitCode != 0 && result.Stderr.Contains("script", StringComparison.OrdinalIgnoreCase)
                && result.Stderr.Contains("not", StringComparison.OrdinalIgnoreCase))
                return result.ToToolOutput() +
                    "\n\nHint: install the C# scripting tool with: dotnet tool install -g dotnet-script";
            return result.ToToolOutput();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
