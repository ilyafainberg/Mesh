using Microsoft.Extensions.Logging;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Bounded pool of independent Copilot ACP processes. Each worker remains serialized internally,
/// while unrelated topics can use different workers concurrently.
/// </summary>
public sealed class CopilotAcpHost : IAsyncDisposable
{
    internal const int WorkerCount = 3;

    private readonly CopilotAcpLane[] workers;
    private readonly AsyncWorkerPool<CopilotAcpLane> pool;

    public CopilotAcpHost(
        ILogger<CopilotAcpHost> logger,
        CopilotMcpBridge mcpBridge,
        TokenMeter tokenMeter)
    {
        workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => new CopilotAcpLane(logger, mcpBridge, tokenMeter))
            .ToArray();
        pool = new AsyncWorkerPool<CopilotAcpLane>(workers);
    }

    public Task<IReadOnlyList<CopilotModelOption>> GetModelsAsync(
        string executable,
        bool force = false,
        CancellationToken ct = default)
        => ModelCallDispatcher.RunAsync(
            () => pool.UseAsync(
                worker => worker.GetModelsAsync(executable, force, ct), ct), ct);

    public Task<(bool Ok, string Message)> CheckAsync(
        CopilotAcpConfig config,
        CancellationToken ct = default)
        => ModelCallDispatcher.RunAsync(
            () => pool.UseAsync(worker => worker.CheckAsync(config, ct), ct), ct);

    public Task<string> CompleteAsync(
        CopilotAcpConfig config,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> history,
        IReadOnlyList<(string MimeType, byte[] Data)> images,
        IReadOnlyList<IAgentTool> tools,
        IProgress<AgentStep>? progress = null,
        CancellationToken ct = default)
        => pool.UseAsync(
            worker => worker.CompleteAsync(
                config, systemPrompt, history, images, tools, progress, ct), ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in workers)
            await worker.DisposeAsync().ConfigureAwait(false);
    }
}
