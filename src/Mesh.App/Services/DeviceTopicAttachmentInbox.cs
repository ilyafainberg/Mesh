using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>Private, bounded staging for encrypted device-topic attachment chunks.</summary>
public sealed class DeviceTopicAttachmentInbox : IDisposable
{
    public const int MaxPendingRuns = 32;
    public const long MaxRunBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly string root;
    private readonly ConcurrentDictionary<string, PendingRun> runs = new(StringComparer.Ordinal);
    private readonly object runCreationGate = new();
    private readonly Timer cleanupTimer;
    private int disposed;

    public DeviceTopicAttachmentInbox(string? root = null)
    {
        this.root = root ?? Path.Combine(StoragePaths.Root, "Temp", "device-topic-inbox");
        Directory.CreateDirectory(this.root);
        cleanupTimer = new Timer(_ => CleanupStale(), null, StaleAfter, StaleAfter);
    }

    public bool TryAdd(string sourceDeviceId, AttachmentChunkPayload chunk, out string error)
    {
        error = "";
        if (Volatile.Read(ref disposed) != 0)
        {
            error = "attachment inbox is disposed";
            return false;
        }
        if (!TopicRunProtocol.IsValidIdentifier(sourceDeviceId) || !IsValidChunk(chunk))
        {
            error = "invalid attachment chunk";
            return false;
        }

        CleanupStale();
        var runKey = RunKey(sourceDeviceId, chunk.RunId);
        if (!runs.TryGetValue(runKey, out var run))
        {
            lock (runCreationGate)
            {
                if (!runs.TryGetValue(runKey, out run))
                {
                    if (runs.Count >= MaxPendingRuns)
                    {
                        error = "attachment inbox is full";
                        return false;
                    }
                    run = new PendingRun(sourceDeviceId, chunk.RunId);
                    if (!runs.TryAdd(runKey, run))
                        run = runs[runKey];
                }
            }
        }

        lock (run!.Gate)
        {
            if (run.Rejected)
            {
                error = "attachment run was rejected";
                return false;
            }
            if (!run.Attachments.TryGetValue(chunk.AttachmentId, out var attachment))
            {
                if (run.Attachments.Count >= TopicRunProtocol.MaxItems)
                {
                    Reject(run);
                    error = "too many attachments";
                    return false;
                }
                attachment = new PendingAttachment(
                    chunk.AttachmentId, chunk.Name, chunk.MimeType, chunk.Count);
                run.Attachments.Add(chunk.AttachmentId, attachment);
            }
            else if (attachment.Count != chunk.Count
                     || !string.Equals(attachment.Name, chunk.Name, StringComparison.Ordinal)
                     || !string.Equals(attachment.MimeType, chunk.MimeType, StringComparison.Ordinal))
            {
                Reject(run);
                error = "conflicting attachment metadata";
                return false;
            }

            if (attachment.ChunkPaths.ContainsKey(chunk.Index))
            {
                error = "duplicate attachment chunk";
                return false;
            }
            if (attachment.Bytes + chunk.Data.LongLength > AttachmentChunkProtocol.MaxAttachmentBytes
                || run.Bytes + chunk.Data.LongLength > MaxRunBytes)
            {
                Reject(run);
                error = "attachment byte limit exceeded";
                return false;
            }

            try
            {
                var path = ChunkPath(sourceDeviceId, chunk.RunId, chunk.AttachmentId, chunk.Index);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = new FileStream(
                           path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           16 * 1024, FileOptions.WriteThrough))
                    stream.Write(chunk.Data);
                attachment.ChunkPaths.Add(chunk.Index, path);
                attachment.ChunkLengths.Add(chunk.Index, chunk.Data.Length);
                attachment.Bytes += chunk.Data.LongLength;
                run.Bytes += chunk.Data.LongLength;
                run.UpdatedAt = DateTimeOffset.UtcNow;
                run.Changed.TrySetResult();
                run.Changed = NewSignal();
                return true;
            }
            catch (IOException)
            {
                error = "duplicate or conflicting attachment storage";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "attachment storage is unavailable";
                return false;
            }
        }
    }

    public async Task<IReadOnlyList<ChatAttachment>> WaitForAsync(
        string sourceDeviceId,
        string runId,
        IReadOnlyList<TopicRunAttachment>? manifest,
        IReadOnlyList<string>? attachmentIds,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!TopicRunProtocol.IsValidIdentifier(sourceDeviceId))
            throw new InvalidDataException("Invalid attachment source device ID.");
        ValidateManifest(runId, manifest, attachmentIds);
        var runKey = RunKey(sourceDeviceId, runId);
        if (manifest is null || manifest.Count == 0)
        {
            RemoveRun(runKey);
            return Array.Empty<ChatAttachment>();
        }

        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultWait);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!runs.TryGetValue(runKey, out var run))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Attachment chunks did not arrive in time.");
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                continue;
            }

            Task signal;
            lock (run.Gate)
            {
                if (run.Rejected)
                    throw new InvalidDataException("Attachment transfer was rejected.");
                if (TryValidateComplete(run, manifest, out var validationError))
                {
                    try
                    {
                        var result = Materialize(run, manifest);
                        RemoveRun(runKey);
                        return result;
                    }
                    catch
                    {
                        RemoveRun(runKey);
                        throw;
                    }
                }
                if (validationError is not null)
                {
                    Reject(run);
                    throw new InvalidDataException(validationError);
                }
                signal = run.Changed.Task;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                RemoveRun(runKey);
                throw new TimeoutException("Attachment chunks did not arrive in time.");
            }
            try
            {
                await signal.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                RemoveRun(runKey);
                throw new TimeoutException("Attachment chunks did not arrive in time.");
            }
        }
    }

    public void RejectRun(string sourceDeviceId, string runId)
        => RemoveRun(RunKey(sourceDeviceId, runId));

    public void CleanupStale()
    {
        var cutoff = DateTimeOffset.UtcNow - StaleAfter;
        foreach (var pair in runs)
            if (pair.Value.UpdatedAt < cutoff)
                RemoveRun(pair.Key);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff.UtcDateTime)
                    TryDeleteDirectory(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsValidChunk(AttachmentChunkPayload chunk)
        => TopicRunProtocol.TryParseChunk(TopicRunProtocol.ChunkBody(chunk), out _);

    private static void ValidateManifest(
        string runId,
        IReadOnlyList<TopicRunAttachment>? manifest,
        IReadOnlyList<string>? attachmentIds)
    {
        if (!TopicRunProtocol.IsValidIdentifier(runId))
            throw new InvalidDataException("Invalid attachment run ID.");
        manifest ??= Array.Empty<TopicRunAttachment>();
        attachmentIds ??= manifest.Select(item => item.Id).ToArray();
        if (manifest.Count > TopicRunProtocol.MaxItems
            || attachmentIds.Count != manifest.Count
            || manifest.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Count
            || !manifest.Select(item => item.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(attachmentIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || manifest.Any(item => !TopicRunProtocol.IsValidIdentifier(item.Id)
                                    || string.IsNullOrWhiteSpace(item.Name)
                                    || item.Name.Length > TopicRunProtocol.MaxIdChars
                                    || string.IsNullOrWhiteSpace(item.MimeType)
                                    || item.MimeType.Length > TopicRunProtocol.MaxIdChars
                                    || item.Length < 0
                                    || item.Length > AttachmentChunkProtocol.MaxAttachmentBytes)
            || manifest.Sum(item => item.Length) > MaxRunBytes)
            throw new InvalidDataException("Invalid attachment manifest.");
    }

    private static bool TryValidateComplete(
        PendingRun run,
        IReadOnlyList<TopicRunAttachment> manifest,
        out string? error)
    {
        error = null;
        var expectedIds = manifest.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (run.Attachments.Keys.Any(id => !expectedIds.Contains(id)))
        {
            error = "Attachment chunks do not match the manifest.";
            return false;
        }

        foreach (var item in manifest)
        {
            if (item.Length == 0)
            {
                if (run.Attachments.ContainsKey(item.Id))
                    error = "An empty attachment contained chunks.";
                if (error is not null) return false;
                continue;
            }
            if (!run.Attachments.TryGetValue(item.Id, out var attachment)) return false;
            var expectedCount = checked((int)((item.Length + AttachmentChunkProtocol.MaxChunkBytes - 1)
                                              / AttachmentChunkProtocol.MaxChunkBytes));
            if (attachment.Count != expectedCount
                || !string.Equals(attachment.Name, item.Name, StringComparison.Ordinal)
                || !string.Equals(attachment.MimeType, item.MimeType, StringComparison.Ordinal)
                || attachment.Bytes > item.Length)
            {
                error = "Attachment chunks conflict with the manifest.";
                return false;
            }
            if (attachment.ChunkPaths.Count < expectedCount) return false;
            for (var index = 0; index < expectedCount; index++)
            {
                if (!attachment.ChunkLengths.TryGetValue(index, out var length)) return false;
                var expectedLength = index == expectedCount - 1
                    ? checked((int)(item.Length
                                    - (long)index * AttachmentChunkProtocol.MaxChunkBytes))
                    : AttachmentChunkProtocol.MaxChunkBytes;
                if (length != expectedLength)
                {
                    error = "Attachment chunk sizes do not match the manifest.";
                    return false;
                }
            }
            if (attachment.Bytes != item.Length)
            {
                error = "Attachment byte count does not match the manifest.";
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<ChatAttachment> Materialize(
        PendingRun run,
        IReadOnlyList<TopicRunAttachment> manifest)
    {
        var result = new List<ChatAttachment>(manifest.Count);
        foreach (var item in manifest)
        {
            if (item.Length == 0)
            {
                result.Add(new ChatAttachment(item.Name, item.MimeType, Array.Empty<byte>()));
                continue;
            }
            var attachment = run.Attachments[item.Id];
            using var output = new MemoryStream(checked((int)item.Length));
            for (var index = 0; index < attachment.Count; index++)
            {
                if (!attachment.ChunkPaths.TryGetValue(index, out var path))
                    throw new InvalidDataException("Attachment chunk sequence is incomplete.");
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.CopyTo(output);
            }
            if (output.Length != item.Length)
                throw new InvalidDataException("Attachment byte count changed during assembly.");
            result.Add(new ChatAttachment(item.Name, item.MimeType, output.ToArray()));
        }
        return result;
    }

    private string ChunkPath(string sourceDeviceId, string runId, string attachmentId, int index)
    {
        var run = Hash(RunKey(sourceDeviceId, runId));
        var chunk = Hash($"{sourceDeviceId}\0{runId}\0{attachmentId}\0{index}");
        return Path.Combine(root, run, chunk + ".part");
    }

    private static string RunKey(string sourceDeviceId, string runId)
        => sourceDeviceId + "\0" + runId;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void Reject(PendingRun run)
    {
        run.Rejected = true;
        run.Changed.TrySetResult();
    }

    private void RemoveRun(string runId)
    {
        if (!runs.TryRemove(runId, out var run)) return;
        lock (run.Gate)
        {
            run.Rejected = true;
            run.Changed.TrySetResult();
        }
        TryDeleteDirectory(Path.Combine(root, Hash(runId)));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cleanupTimer.Dispose();
        foreach (var runId in runs.Keys) RemoveRun(runId);
    }

    private sealed class PendingRun(string sourceDeviceId, string id)
    {
        public string SourceDeviceId { get; } = sourceDeviceId;
        public string Id { get; } = id;
        public object Gate { get; } = new();
        public Dictionary<string, PendingAttachment> Attachments { get; } = new(StringComparer.Ordinal);
        public long Bytes { get; set; }
        public bool Rejected { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }

    private sealed class PendingAttachment(string id, string name, string mimeType, int count)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string MimeType { get; } = mimeType;
        public int Count { get; } = count;
        public long Bytes { get; set; }
        public Dictionary<int, string> ChunkPaths { get; } = new();
        public Dictionary<int, int> ChunkLengths { get; } = new();
    }
}
