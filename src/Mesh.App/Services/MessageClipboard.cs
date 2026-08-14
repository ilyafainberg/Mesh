namespace Mesh.App.Services;

public interface IMessageClipboard
{
    Task CopyMarkdownAsync(string markdown);
}

public sealed class MessageClipboard : IMessageClipboard
{
    private readonly Func<string, Task> writeAsync;

    public MessageClipboard(Func<string, Task> writeAsync)
        => this.writeAsync = writeAsync ?? throw new ArgumentNullException(nameof(writeAsync));

    public Task CopyMarkdownAsync(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return writeAsync(markdown);
    }
}

internal static class MessageCopyPolicy
{
    public static bool HasVisibleContent(string? markdown)
        => !string.IsNullOrWhiteSpace(markdown);
}
