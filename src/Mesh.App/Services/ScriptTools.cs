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
/// Runs a C# script as a .NET 10 file-based app (<c>dotnet run file.cs</c>) and returns its output.
/// Owner-gated local tool. No extra tooling is required beyond the .NET 10 SDK.
/// </summary>
public sealed class RunCSharpScriptTool : IAgentTool
{
    public string Name => "run_csharp_script";
    public string Description =>
        "Run a C# script (top-level statements, a single file) on the local machine and return its " +
        "stdout, stderr and exit code. Runs as a .NET file-based app (dotnet run file.cs); the .NET SDK is required.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "The C# script source (top-level statements allowed). Use #:package Name@Version to reference NuGet packages." },
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

        // Each run gets its own file so the file-based app builds cleanly and in isolation.
        var tmp = Path.Combine(Path.GetTempPath(), $"mesh-cs-{Guid.NewGuid():n}.cs");
        try
        {
            await File.WriteAllTextAsync(tmp, code, ct);
            var result = await ProcessRunner.RunAsync(
                dotnet, $"run \"{tmp}\"",
                workingDir: string.IsNullOrWhiteSpace(wd) ? null : wd,
                timeoutSeconds: timeout, ct: ct);
            return result.ToToolOutput();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
