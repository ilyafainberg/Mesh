using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// The internal, owner-only ask_user tool. A fresh instance is created per owner run and carries the
/// thread/run/trigger identity so the prompt it raises can be recovered and its bubble bound to the
/// correct Me thread. It is marked <see cref="IsInternal"/> so it is hidden from user-facing tool
/// lists and execution progress, yet still offered to the model. Execution suspends the run: it
/// persists the durable prompt and opaque context, surfaces the visual bubble, and awaits the first
/// durable resolution before returning the chosen option to the model tool loop so the SAME run
/// continues.
/// It must never be registered for guest or service agents.
/// </summary>
public sealed class AskUserAgentTool : IAgentTool
{
    private readonly AppState _state;
    private readonly string _threadId;
    private readonly string _runId;
    private readonly string? _triggerLineId;

    public AskUserAgentTool(AppState state, string threadId, string runId, string? triggerLineId)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _threadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        _runId = runId ?? throw new ArgumentNullException(nameof(runId));
        _triggerLineId = triggerLineId;
    }

    public string Name => AskUserToolSchema.ToolName;

    public string Description => AskUserToolSchema.Description;

    public object ParametersSchema => AskUserToolSchema.ParametersSchema;

    public bool IsInternal => true;

    // Asking the owner a question mutates no external system; it is a read-style pause for input.
    public ToolOperationKind Classify(JsonElement args) => ToolOperationKind.Read;

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        AskUserToolRequest request;
        try
        {
            request = AskUserToolSchema.ParseRequest(
                args, _threadId, _runId, _triggerLineId, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException ex)
        {
            // Surface the contract violation back to the model instead of crashing the run.
            return $"ask_user was not shown: {ex.Message}";
        }

        return await _state.RunAskUserToolAsync(request, ct).ConfigureAwait(false);
    }
}
