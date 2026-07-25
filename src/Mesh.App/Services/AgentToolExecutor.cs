using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Executes provider-neutral agent tools and controls user-visible progress.</summary>
internal static class AgentToolExecutor
{
    public static async Task<string> ExecuteAsync(
        IReadOnlyList<IAgentTool> tools,
        string name,
        string argsJson,
        CancellationToken ct,
        IProgress<AgentStep>? progress = null)
    {
        var label = ReasoningExtract.Label(name);
        var args = ToolTrace.Clip(argsJson);
        var tool = tools.FirstOrDefault(candidate => candidate.Name == name);
        var visible = tool?.IsInternal != true;
        if (visible)
            progress?.Report(new AgentStep(name, label, AgentStepState.Started, Arguments: args));
        if (tool is null)
        {
            var miss = $"ERROR: unknown tool '{name}'.";
            progress?.Report(new AgentStep(name, label, AgentStepState.Failed, args, miss));
            return miss;
        }

        try
        {
            using var argsDocument = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var result = await tool.ExecuteAsync(argsDocument.RootElement, ct);
            if (visible)
                progress?.Report(new AgentStep(
                    name,
                    label,
                    AgentStepState.Done,
                    args,
                    ToolTrace.Clip(result)));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = "ERROR: " + ex.Message;
            if (visible)
                progress?.Report(new AgentStep(
                    name,
                    label,
                    AgentStepState.Failed,
                    args,
                    ToolTrace.Clip(error)));
            return error;
        }
    }
}

/// <summary>Clips tool arguments and results without discarding their useful tail.</summary>
internal static class ToolTrace
{
    public const int MaxChars = 4000;

    public static string? Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= MaxChars) return text;
        var head = MaxChars * 2 / 3;
        var tail = MaxChars - head;
        var omitted = text.Length - head - tail;
        return text[..head] + $"\n... [{omitted} characters omitted] ...\n" + text[^tail..];
    }
}
