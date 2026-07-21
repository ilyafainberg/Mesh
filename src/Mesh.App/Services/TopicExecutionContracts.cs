using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Transport-neutral input for one local or device-bound topic turn.</summary>
public sealed record TopicTurnDraft(
    string RunId,
    string ThreadId,
    string TriggerLineId,
    string TriggerHandle,
    string Prompt,
    DateTimeOffset TriggerAt,
    TopicTurnMode TurnMode,
    string? TargetDeviceId = null,
    string? WidgetId = null,
    string? WidgetContext = null,
    IReadOnlyList<ChatAttachment>? Attachments = null);

/// <summary>Result of accepting or rejecting a topic dispatch.</summary>
public sealed record TopicDispatchResult(
    bool Accepted,
    string RunId,
    string Code,
    string? Error = null)
{
    public static TopicDispatchResult Ok(string runId) => new(true, runId, "accepted");

    public static TopicDispatchResult Reject(string code, string runId = "", string? error = null)
        => new(false, runId, code, error);
}

/// <summary>Terminal result from one local topic turn.</summary>
public sealed record TopicRunCompletion(
    string RunId,
    string ThreadId,
    TopicRunPhase Phase,
    DateTimeOffset CompletedAt,
    string? Error = null,
    string? FailureCode = null);

/// <summary>Runs exactly one agent turn locally with progress and cancellation.</summary>
public interface ITopicTurnRunner
{
    Task<TopicRunCompletion> ExecuteAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload> progress,
        CancellationToken cancellationToken);
}

/// <summary>Sends targeted topic requests and cancellation to remote agent-ready devices.</summary>
public interface IDeviceTopicTransport
{
    Task<TopicDispatchResult> DispatchAsync(
        string targetDeviceId,
        TopicRunRequestPayload request,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken cancellationToken);

    Task<bool> CancelAsync(
        string targetDeviceId,
        TopicRunCancelPayload cancel,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken);
}

/// <summary>Routes topic execution through a local runner or targeted device transport.</summary>
public interface ITopicExecutionRouter
{
    Task<TopicDispatchResult> SubmitAsync(
        TopicTurnDraft draft,
        IProgress<TopicRunUpdatePayload>? progress,
        CancellationToken cancellationToken);

    Task<bool> StopAsync(
        string threadId,
        string runId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Mesh.Shared.DeviceInfo>> ListEligibleDevicesAsync(
        CancellationToken cancellationToken);
}
