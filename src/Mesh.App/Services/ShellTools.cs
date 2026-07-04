using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>Runs a Windows PowerShell script and returns its output. Owner-gated local tool.</summary>
public sealed class RunPowerShellTool : IAgentTool
{
    public string Name => "run_powershell";
    public string Description =>
        "Run a PowerShell script on the local machine and return its stdout, stderr and exit code. " +
        "Use for system tasks, file operations, and automation on Windows.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            script = new { type = "string", description = "The PowerShell script to run." },
            working_directory = new { type = "string", description = "Optional working directory." },
            timeout_seconds = new { type = "integer", description = "Optional timeout (default 120)." }
        },
        required = new[] { "script" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var script = ToolArgs.GetString(args, "script");
        if (string.IsNullOrWhiteSpace(script)) return "ERROR: no script given.";
        var wd = ToolArgs.GetString(args, "working_directory");
        var timeout = ToolArgs.GetInt(args, "timeout_seconds", 120);

        var exe = ProcessRunner.Which("pwsh", "powershell") ?? "powershell";
        // Pass the script via -EncodedCommand to avoid any quoting/escaping issues.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var result = await ProcessRunner.RunAsync(
            exe,
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            workingDir: string.IsNullOrWhiteSpace(wd) ? null : wd,
            timeoutSeconds: timeout,
            ct: ct);
        return result.ToToolOutput();
    }
}

/// <summary>Runs a Windows CMD (cmd.exe) command and returns its output. Owner-gated local tool.</summary>
public sealed class RunCmdTool : IAgentTool
{
    public string Name => "run_cmd";
    public string Description =>
        "Run a Windows Command Prompt (cmd.exe) command and return its stdout, stderr and exit code.";
    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "The cmd.exe command line to run." },
            working_directory = new { type = "string", description = "Optional working directory." },
            timeout_seconds = new { type = "integer", description = "Optional timeout (default 120)." }
        },
        required = new[] { "command" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var command = ToolArgs.GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command)) return "ERROR: no command given.";
        var wd = ToolArgs.GetString(args, "working_directory");
        var timeout = ToolArgs.GetInt(args, "timeout_seconds", 120);

        var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var result = await ProcessRunner.RunAsync(
            comspec,
            "/d /c " + command,
            workingDir: string.IsNullOrWhiteSpace(wd) ? null : wd,
            timeoutSeconds: timeout,
            ct: ct);
        return result.ToToolOutput();
    }
}
