namespace Mesh.App.Services;

/// <summary>Serializes ACP writes without ever blocking the caller on a full stdio pipe.</summary>
internal sealed class CopilotAcpPipeWriter(TextWriter writer) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
