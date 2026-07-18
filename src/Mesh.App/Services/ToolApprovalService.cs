using System.Text.Json;

namespace Mesh.App.Services;

public sealed record ToolApprovalRequest(
    string Id,
    string ToolName,
    string Description,
    ToolOperationKind Operation,
    string Arguments,
    DateTimeOffset RequestedAt);

/// <summary>Coordinates asynchronous tool approvals between agent turns and the global UI.</summary>
public sealed class ToolApprovalService
{
    private sealed record PendingItem(ToolApprovalRequest Request, TaskCompletionSource<bool> Completion);

    private readonly object gate = new();
    private readonly Dictionary<string, PendingItem> pending = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<ToolApprovalRequest> Pending
    {
        get
        {
            lock (gate)
                return pending.Values
                    .Select(item => item.Request)
                    .OrderBy(item => item.RequestedAt)
                    .ToList();
        }
    }

    public async Task<bool> RequestAsync(
        string toolName,
        string description,
        ToolOperationKind operation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        var request = new ToolApprovalRequest(
            id,
            toolName,
            description,
            operation,
            Clip(arguments.GetRawText(), 2_000),
            DateTimeOffset.UtcNow);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (gate)
            pending[id] = new PendingItem(request, completion);
        Changed?.Invoke();

        using var registration = ct.Register(() => Resolve(id, approved: false));
        return await completion.Task;
    }

    public void Approve(string id) => Resolve(id, approved: true);

    public void Deny(string id) => Resolve(id, approved: false);

    private void Resolve(string id, bool approved)
    {
        PendingItem? item;
        lock (gate)
        {
            if (!pending.Remove(id, out item))
                return;
        }

        item.Completion.TrySetResult(approved);
        Changed?.Invoke();
    }

    private static string Clip(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
