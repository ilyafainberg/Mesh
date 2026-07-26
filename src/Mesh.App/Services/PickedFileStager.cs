namespace Mesh.App.Services;

/// <summary>Copies provider-backed picked files into app-owned temporary storage.</summary>
public static class PickedFileStager
{
    public const long MaxAttachmentBytes = 20 * 1024 * 1024;

    public readonly record struct Staged(string Name, string Path, long Size);

    public static Task<Staged> StageAsync(
        string name,
        string? mime,
        Stream source,
        CancellationToken cancellationToken = default)
        => StageAsync(
            name,
            mime,
            source,
            Path.Combine(Path.GetTempPath(), "MeshAttachments"),
            MaxAttachmentBytes,
            cancellationToken);

    internal static async Task<Staged> StageAsync(
        string name,
        string? mime,
        Stream source,
        string destinationRoot,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The picked file stream is not readable.", nameof(source));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var safeName = PasteFiles.SafeName(name, mime);
        Directory.CreateDirectory(destinationRoot);
        var path = Path.Combine(destinationRoot, $"{Guid.NewGuid():n}_{safeName}");

        try
        {
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;

                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"{safeName} is larger than the 20 MB attachment limit.");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total == 0) throw new InvalidOperationException("That file is empty.");
            return new Staged(safeName, path, total);
        }
        catch (Exception stagingError)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Could not remove an incomplete staged attachment.", stagingError, cleanupError);
            }

            throw;
        }
    }
}
