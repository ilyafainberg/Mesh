using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Asks WorkIQ (the Microsoft 365 knowledge assistant) a natural-language question and returns its
/// answer. WorkIQ reasons across the owner's M365 data (email, Teams, files, calendar, people), so
/// this is owner-scoped by nature: enabling it lets the agent query the owner's work context, and
/// sharing it with a circle lets that circle's agents ask questions grounded in the owner's M365.
/// Backed by the local WorkIQ CLI (workiq.cmd ask -q "...").
/// </summary>
public sealed class AskWorkIqTool : IAgentTool
{
    public string Name => "ask_workiq";

    public string Description =>
        "Ask WorkIQ a natural-language question about the user's Microsoft 365 world: emails, Teams " +
        "chats and messages, meetings and calendar, documents, and people. Use for questions that need " +
        "real work context, for example 'what did Alex say about the deadline?' or 'summarize today's " +
        "messages in the Engineering channel'.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "The natural-language question to ask WorkIQ." }
        },
        required = new[] { "question" }
    };

    /// <summary>Locates the WorkIQ CLI (workiq.cmd) in its known location or on PATH.</summary>
    public static string? ResolveCli()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "bin", "workiq.cmd");
        if (File.Exists(known)) return known;
        return ProcessRunner.Which("workiq.cmd", "workiq");
    }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var question = ToolArgs.GetString(args, "question");
        if (string.IsNullOrWhiteSpace(question)) return "ERROR: no question given.";

        var cli = ResolveCli();
        if (cli is null)
            return "ERROR: WorkIQ is not available on this machine (workiq.cmd not found).";

        // Escape embedded quotes so the question survives the command line.
        var safe = question.Replace("\"", "\\\"");
        var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var result = await ProcessRunner.RunAsync(
            comspec,
            $"/d /c \"\"{cli}\" ask -q \"{safe}\"\"",
            timeoutSeconds: 180,
            ct: ct);

        if (result.TimedOut) return "WorkIQ took too long to answer (timed out).";
        var output = (result.Stdout ?? "").Trim();
        if (output.Length == 0 && result.ExitCode != 0)
            return "WorkIQ could not answer: " + (result.Stderr ?? "").Trim();
        return output.Length == 0 ? "(WorkIQ returned no answer.)" : output;
    }
}
