namespace Mesh.App.Services;

public sealed record AssistantAiRequestProjection(
    bool Busy,
    bool Thinking,
    bool StopVisible,
    bool RetryVisible,
    string? Error)
{
    public static AssistantAiRequestProjection Empty { get; } =
        new(false, false, false, false, null);
}

public static class AssistantAiRequestReducer
{
    public static AssistantAiRequestProjection Project(AssistantAiRequest? request)
    {
        if (request is null || request.IsTerminal)
            return AssistantAiRequestProjection.Empty;

        return request.State switch
        {
            AssistantAiRequestState.MessageCommitted or
            AssistantAiRequestState.DispatchPending =>
                new(true, true, true, false, null),
            AssistantAiRequestState.Dispatched =>
                new(true, true, true, false, null),
            AssistantAiRequestState.AwaitingHost =>
                new(
                    false,
                    false,
                    false,
                    true,
                    request.LastError
                    ?? "Message saved. AI unavailable — choose an online agent-ready Desktop, then retry."),
            AssistantAiRequestState.RetryPending =>
                new(
                    false,
                    false,
                    false,
                    true,
                    string.IsNullOrWhiteSpace(request.LastError)
                        ? "Message saved. AI response unavailable."
                        : $"Message saved. AI unavailable: {request.LastError}"),
            _ => AssistantAiRequestProjection.Empty
        };
    }

    public static AssistantAiRequestProjection Reduce(
        AssistantAiRequestTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return Project(transition.Request);
    }
}
