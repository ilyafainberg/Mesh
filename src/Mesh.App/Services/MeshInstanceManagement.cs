using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mesh.App.Services;

public enum MeshInstanceCommandKind
{
    Run,
    List,
    Close,
    Invalid
}

public sealed record MeshInstanceCommand(
    MeshInstanceCommandKind Kind,
    string? Handle,
    bool HandleWasExplicit,
    bool Headless,
    bool Json,
    IReadOnlyList<string> ForwardedArguments,
    string? Error)
{
    public static MeshInstanceCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = arguments.Count > 0
                    && !arguments[0].StartsWith("--", StringComparison.Ordinal)
                    && !arguments[0].StartsWith("mesh://", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        string? handle = null;
        string? closeHandle = null;
        var handleExplicit = false;
        var headless = false;
        var list = false;
        var json = false;
        var forwarded = new List<string>();

        for (var i = start; i < arguments.Count; i++)
        {
            var value = arguments[i].Trim();
            switch (value.ToLowerInvariant())
            {
                case "--handle":
                    if (++i >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i]))
                        return Invalid("--handle requires a handle.", forwarded);
                    handle = arguments[i];
                    handleExplicit = true;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--list":
                    list = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--close":
                    if (++i >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i]))
                        return Invalid("--close requires a handle.", forwarded);
                    closeHandle = arguments[i];
                    break;
                default:
                    forwarded.Add(arguments[i]);
                    break;
            }
        }

        if (list && closeHandle is not null)
            return Invalid("--list and --close cannot be used together.", forwarded);
        if (list && (handleExplicit || headless))
            return Invalid("--list cannot be combined with --handle or --headless.", forwarded);
        if (closeHandle is not null && (handleExplicit || headless || json))
            return Invalid("--close cannot be combined with --handle, --headless, or --json.", forwarded);
        if (json && !list)
            return Invalid("--json is supported only with --list.", forwarded);

        if (list)
            return new MeshInstanceCommand(
                MeshInstanceCommandKind.List, null, false, false, json, forwarded, null);
        if (closeHandle is not null)
            return new MeshInstanceCommand(
                MeshInstanceCommandKind.Close, closeHandle, false, false, false, forwarded, null);
        return new MeshInstanceCommand(
            MeshInstanceCommandKind.Run, handle, handleExplicit, headless, false, forwarded, null);
    }

    private static MeshInstanceCommand Invalid(string error, IReadOnlyList<string> forwarded)
        => new(MeshInstanceCommandKind.Invalid, null, false, false, false, forwarded, error);
}

internal sealed record MeshResolvedAccount(string Id, string Handle, string NormalizedHandle);

internal sealed class MeshAccountIndexSnapshot
{
    private sealed class AccountIndexDto
    {
        public string? ActiveId { get; set; }
        public List<AccountDto> Accounts { get; set; } = new();
    }

    private sealed class AccountDto
    {
        public string Id { get; set; } = "";
        public string Handle { get; set; } = "";
    }

    private readonly string? activeId;
    private readonly IReadOnlyList<MeshResolvedAccount> accounts;

    private MeshAccountIndexSnapshot(string? activeId, IReadOnlyList<MeshResolvedAccount> accounts)
    {
        this.activeId = activeId;
        this.accounts = accounts;
    }

    public static MeshAccountIndexSnapshot Load(string path)
    {
        if (!File.Exists(path)) return new MeshAccountIndexSnapshot(null, Array.Empty<MeshResolvedAccount>());
        try
        {
            var dto = JsonSerializer.Deserialize<AccountIndexDto>(
                          File.ReadAllText(path), MeshInstanceJson.Options)
                      ?? new AccountIndexDto();
            var accounts = dto.Accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.Id)
                                  && !string.IsNullOrWhiteSpace(account.Handle))
                .Select(account => new MeshResolvedAccount(
                    account.Id,
                    account.Handle,
                    MeshInstanceNames.NormalizeHandle(account.Handle)))
                .Where(account => account.NormalizedHandle.Length > 0)
                .ToArray();
            return new MeshAccountIndexSnapshot(dto.ActiveId, accounts);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("accounts.json is invalid.", ex);
        }
    }

    public MeshResolvedAccount? Resolve(string? requestedHandle)
    {
        if (!string.IsNullOrWhiteSpace(requestedHandle))
        {
            var normalized = MeshInstanceNames.NormalizeHandle(requestedHandle);
            return accounts.FirstOrDefault(account =>
                string.Equals(account.NormalizedHandle, normalized, StringComparison.Ordinal));
        }

        return activeId is null
            ? null
            : accounts.FirstOrDefault(account =>
                string.Equals(account.Id, activeId, StringComparison.Ordinal));
    }
}

public static class MeshInstanceNames
{
    private const string SetupIdentity = "__setup__";

    public static string NormalizeHandle(string? handle)
        => (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    public static string HandleHash(string normalizedHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHandle);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedHandle))).ToLowerInvariant();
    }

    public static string MutexName(string normalizedHandle)
        => $@"Local\Mesh.Handle.{HandleHash(normalizedHandle)}";

    public static string PipeName(string normalizedHandle)
        => $"Mesh.{HandleHash(normalizedHandle)}";

    internal static string SetupHandle => SetupIdentity;
    internal const string AccountsMutexName = @"Local\Mesh.Accounts.Index";
}

public static class MeshProcessContext
{
    private static readonly object Gate = new();
    private static string? preferredAccountId;
    private static string? normalizedHandle;
    private static bool headless;
    private static int shuttingDown;

    public static string? PreferredAccountId
    {
        get { lock (Gate) return preferredAccountId; }
    }

    public static string? NormalizedHandle
    {
        get { lock (Gate) return normalizedHandle; }
    }

    public static bool IsHeadless
    {
        get { lock (Gate) return headless; }
    }

    public static bool IsShuttingDown => Volatile.Read(ref shuttingDown) != 0;

    internal static void Configure(string? accountId, string? handle, bool isHeadless)
    {
        lock (Gate)
        {
            preferredAccountId = accountId;
            normalizedHandle = handle;
            headless = isHeadless;
        }
        Interlocked.Exchange(ref shuttingDown, 0);
    }

    internal static void UpdateIdentity(string accountId, string handle)
    {
        lock (Gate)
        {
            preferredAccountId = accountId;
            normalizedHandle = handle;
        }
    }

    internal static void BeginShutdown() => Interlocked.Exchange(ref shuttingDown, 1);
}

public static class MeshAccountIndexWriter
{
    public static void WriteAtomic(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        using var mutex = new Mutex(false, MeshInstanceNames.AccountsMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
                throw new IOException("Timed out waiting to write accounts.json.");
            MeshAtomicFile.Write(path, content);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}

internal static class MeshAtomicFile
{
    public static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("The target path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():n}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
            }
        }
    }
}

public sealed record MeshInstanceRecord(
    string Handle,
    int Pid,
    bool Headless,
    DateTimeOffset StartedAt);

internal sealed class MeshInstanceRegistry
{
    private readonly string instancesDirectory;
    private readonly Func<MeshInstanceRecord, bool> isAlive;

    public MeshInstanceRegistry(
        string root,
        Func<MeshInstanceRecord, bool>? isAlive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        instancesDirectory = Path.Combine(root, "Instances");
        this.isAlive = isAlive ?? IsProcessAlive;
    }

    public string PathFor(string normalizedHandle)
        => Path.Combine(instancesDirectory, MeshInstanceNames.HandleHash(normalizedHandle) + ".json");

    public void Write(MeshInstanceRecord record)
    {
        Directory.CreateDirectory(instancesDirectory);
        MeshAtomicFile.Write(
            PathFor(MeshInstanceNames.NormalizeHandle(record.Handle)),
            JsonSerializer.Serialize(record, MeshInstanceJson.Options));
    }

    public MeshInstanceRecord? Find(string normalizedHandle)
    {
        var path = PathFor(normalizedHandle);
        if (!TryRead(path, out var record)) return null;
        return record;
    }

    public IReadOnlyList<MeshInstanceRecord> ListActive()
    {
        if (!Directory.Exists(instancesDirectory)) return Array.Empty<MeshInstanceRecord>();
        var active = new List<MeshInstanceRecord>();
        foreach (var path in Directory.EnumerateFiles(instancesDirectory, "*.json"))
        {
            if (!TryRead(path, out var record))
            {
                TryDelete(path);
                continue;
            }

            if (isAlive(record)) active.Add(record);
            else TryDelete(path);
        }

        return active
            .OrderBy(record => record.Handle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Pid)
            .ToArray();
    }

    public bool IsAlive(MeshInstanceRecord record) => isAlive(record);

    public void Delete(string normalizedHandle) => TryDelete(PathFor(normalizedHandle));

    private static bool TryRead(string path, out MeshInstanceRecord record)
    {
        record = null!;
        try
        {
            if (!File.Exists(path)) return false;
            record = JsonSerializer.Deserialize<MeshInstanceRecord>(
                         File.ReadAllText(path), MeshInstanceJson.Options)!;
            return record is not null
                   && record.Pid > 0
                   && MeshInstanceNames.NormalizeHandle(record.Handle).Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(MeshInstanceRecord record)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            if (process.HasExited) return false;
            var processStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return processStart <= record.StartedAt.AddSeconds(5);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class MeshHandleMutexLease : IDisposable
{
    private sealed record Acquisition(bool Acquired, Exception? Error = null);

    private readonly ManualResetEventSlim release = new(false);
    private readonly TaskCompletionSource<Acquisition> acquisition =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread ownerThread;
    private int disposed;

    private MeshHandleMutexLease(string normalizedHandle)
    {
        ownerThread = new Thread(() => OwnMutex(normalizedHandle))
        {
            IsBackground = true,
            Name = $"Mesh handle mutex {normalizedHandle}"
        };
        ownerThread.Start();
    }

    public static bool TryAcquire(string normalizedHandle, out MeshHandleMutexLease? lease)
    {
        lease = null;
        var candidate = new MeshHandleMutexLease(normalizedHandle);
        var result = candidate.acquisition.Task.GetAwaiter().GetResult();
        if (result.Error is not null)
        {
            candidate.Dispose();
            throw new InvalidOperationException("Could not acquire the Mesh handle mutex.", result.Error);
        }
        if (!result.Acquired)
        {
            candidate.Dispose();
            return false;
        }
        lease = candidate;
        return true;
    }

    private void OwnMutex(string normalizedHandle)
    {
        Mutex? mutex = null;
        var owned = false;
        try
        {
            mutex = new Mutex(false, MeshInstanceNames.MutexName(normalizedHandle));
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            acquisition.TrySetResult(new Acquisition(owned));
            if (!owned) return;
            release.Wait();
        }
        catch (Exception ex)
        {
            acquisition.TrySetResult(new Acquisition(false, ex));
        }
        finally
        {
            if (owned)
            {
                try { mutex!.ReleaseMutex(); } catch (ApplicationException) { }
            }
            mutex?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        if (ownerThread.IsAlive && Thread.CurrentThread != ownerThread)
            ownerThread.Join(TimeSpan.FromSeconds(2));
        release.Dispose();
    }
}

internal sealed record MeshPipeCommand(string Command, IReadOnlyList<string>? Arguments = null);
internal sealed record MeshPipeReply(string Response, bool StopServer = false, Action? AfterResponse = null);

internal sealed class MeshInstancePipeServer : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<MeshPipeCommand, CancellationToken, Task<MeshPipeReply>> handler;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;

    public MeshInstancePipeServer(
        string pipeName,
        Func<MeshPipeCommand, CancellationToken, Task<MeshPipeReply>> handler)
    {
        this.pipeName = pipeName;
        this.handler = handler;
        loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true
                };
                var line = await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false);
                var command = Parse(line);
                var reply = command is null
                    ? new MeshPipeReply("invalid command")
                    : await handler(command, stopping.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(reply.Response).ConfigureAwait(false);
                if (!reply.StopServer) continue;
                try { reply.AfterResponse?.Invoke(); } catch { }
                break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (IOException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.Current?.RecordException("instance-pipe", ex);
        }
    }

    private static MeshPipeCommand? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (string.Equals(line, "shutdown", StringComparison.OrdinalIgnoreCase))
            return new MeshPipeCommand("shutdown");
        if (string.Equals(line, "activate", StringComparison.OrdinalIgnoreCase))
            return new MeshPipeCommand("activate", Array.Empty<string>());
        try
        {
            return JsonSerializer.Deserialize<MeshPipeCommand>(line, MeshInstanceJson.CompactOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (stopping.IsCancellationRequested) return;
        stopping.Cancel();
        try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }
}

internal static class MeshInstancePipeClient
{
    public static async Task<string?> SendAsync(
        string normalizedHandle,
        MeshPipeCommand command,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            MeshInstanceNames.PipeName(normalizedHandle),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(budget.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        var line = string.Equals(command.Command, "shutdown", StringComparison.OrdinalIgnoreCase)
                   && (command.Arguments is null || command.Arguments.Count == 0)
            ? "shutdown"
            : JsonSerializer.Serialize(command, MeshInstanceJson.CompactOptions);
        await writer.WriteLineAsync(line).ConfigureAwait(false);
        return await reader.ReadLineAsync(budget.Token).ConfigureAwait(false);
    }
}

internal static class MeshInstanceJson
{
    public static JsonSerializerOptions CompactOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed record MeshDesktopLaunchDecision(bool ContinueLaunching, int ExitCode);

internal static class MeshDesktopLaunchBootstrap
{
    public static async Task<MeshDesktopLaunchDecision> PrepareAsync(
        IReadOnlyList<string> arguments,
        string storageRoot,
        string dataDirectory,
        TextWriter output,
        CancellationToken ct = default)
    {
        var command = MeshInstanceCommand.Parse(arguments);
        if (command.Kind == MeshInstanceCommandKind.Invalid)
        {
            await output.WriteLineAsync(command.Error).ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 2);
        }

        var registry = new MeshInstanceRegistry(storageRoot);
        if (command.Kind == MeshInstanceCommandKind.List)
        {
            var active = registry.ListActive();
            if (command.Json)
            {
                var json = active.Select(record => new
                {
                    handle = "@" + MeshInstanceNames.NormalizeHandle(record.Handle),
                    pid = record.Pid,
                    mode = record.Headless ? "Headless" : "Desktop",
                    startedAt = record.StartedAt
                });
                await output.WriteLineAsync(JsonSerializer.Serialize(json, MeshInstanceJson.Options))
                    .ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync($"{"HANDLE",-28}{"PID",-10}MODE").ConfigureAwait(false);
                foreach (var record in active)
                {
                    await output.WriteLineAsync(
                            $"{"@" + MeshInstanceNames.NormalizeHandle(record.Handle),-28}{record.Pid,-10}{(record.Headless ? "Headless" : "Desktop")}")
                        .ConfigureAwait(false);
                }
            }
            return new MeshDesktopLaunchDecision(false, 0);
        }

        if (command.Kind == MeshInstanceCommandKind.Close)
        {
            var normalized = MeshInstanceNames.NormalizeHandle(command.Handle);
            if (normalized.Length == 0)
            {
                await output.WriteLineAsync("--close requires a valid handle.").ConfigureAwait(false);
                return new MeshDesktopLaunchDecision(false, 2);
            }

            var record = registry.Find(normalized);
            try
            {
                var response = await MeshInstancePipeClient.SendAsync(
                    normalized,
                    new MeshPipeCommand("shutdown"),
                    TimeSpan.FromSeconds(35),
                    ct).ConfigureAwait(false);
                if (!string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    await output.WriteLineAsync(response ?? $"Could not close @{normalized}.").ConfigureAwait(false);
                    return new MeshDesktopLaunchDecision(false, 1);
                }
                return new MeshDesktopLaunchDecision(false, 0);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                if (record is not null && !registry.IsAlive(record)) registry.Delete(normalized);
                await output.WriteLineAsync($"No responsive Mesh instance is running for @{normalized}.")
                    .ConfigureAwait(false);
                return new MeshDesktopLaunchDecision(false, 1);
            }
        }

        MeshResolvedAccount? account;
        try
        {
            account = MeshAccountIndexSnapshot
                .Load(Path.Combine(dataDirectory, "accounts.json"))
                .Resolve(command.Handle);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 1);
        }

        if (command.HandleWasExplicit && account is null)
        {
            var requested = MeshInstanceNames.NormalizeHandle(command.Handle);
            await output.WriteLineAsync($"Handle @{requested} is not saved on this device.")
                .ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 1);
        }
        if (command.Headless && account is null)
        {
            await output.WriteLineAsync("--headless requires a saved handle.").ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 1);
        }

        var runtimeHandle = account?.NormalizedHandle ?? MeshInstanceNames.SetupHandle;
        MeshProcessContext.Configure(account?.Id, account?.NormalizedHandle, command.Headless);
        if (MeshDesktopInstanceRuntime.TryStart(
                storageRoot,
                account?.Id,
                runtimeHandle,
                account?.NormalizedHandle ?? string.Empty,
                command.Headless,
                account is null))
            return new MeshDesktopLaunchDecision(true, 0);

        if (command.HandleWasExplicit)
        {
            await output.WriteLineAsync($"Handle @{account!.NormalizedHandle} is already running")
                .ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 1);
        }

        try
        {
            await MeshInstancePipeClient.SendAsync(
                runtimeHandle,
                new MeshPipeCommand("activate", command.ForwardedArguments),
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 0);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            var label = account is null ? "Mesh" : $"Handle @{account.NormalizedHandle}";
            await output.WriteLineAsync($"{label} is already running but could not be activated.")
                .ConfigureAwait(false);
            return new MeshDesktopLaunchDecision(false, 1);
        }
    }
}

internal static class MeshDesktopInstanceRuntime
{
    private static readonly object Gate = new();
    private static RuntimeSession? current;
    private static int processExitRegistered;

    public static bool IsActive
    {
        get { lock (Gate) return current is not null; }
    }

    public static bool TryStart(
        string storageRoot,
        string? accountId,
        string runtimeHandle,
        string registryHandle,
        bool headless,
        bool setup)
    {
        lock (Gate)
        {
            if (current is not null) throw new InvalidOperationException("The Mesh instance runtime is already initialized.");
            if (!MeshHandleMutexLease.TryAcquire(runtimeHandle, out var mutex)) return false;
            current = new RuntimeSession(
                storageRoot,
                accountId,
                runtimeHandle,
                registryHandle,
                headless,
                setup,
                mutex!);
            if (Interlocked.Exchange(ref processExitRegistered, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseForProcessExit();
            return true;
        }
    }

    public static void AttachHost(
        Func<IReadOnlyList<string>, Task> activate,
        Func<CancellationToken, Task> prepareShutdown,
        Action exit)
    {
        RuntimeSession? session;
        lock (Gate) session = current;
        session?.AttachHost(activate, prepareShutdown, exit);
    }

    public static AccountSwitchReservation ReserveSwitch(string accountId, string handle)
    {
        RuntimeSession? session;
        lock (Gate) session = current;
        return session?.ReserveSwitch(accountId, MeshInstanceNames.NormalizeHandle(handle))
               ?? AccountSwitchReservation.Allowed();
    }

    public static bool RequestLocalShutdown()
    {
        RuntimeSession? session;
        lock (Gate) session = current;
        if (session is null) return false;
        _ = session.RequestLocalShutdownAsync();
        return true;
    }

    private static void ReleaseForProcessExit()
    {
        RuntimeSession? session;
        lock (Gate)
        {
            session = current;
            current = null;
        }
        session?.ReleaseForProcessExit();
    }

    private sealed class RuntimeSession
    {
        private sealed record Binding(
            string AccountId,
            string RuntimeHandle,
            string RegistryHandle,
            bool Setup,
            MeshHandleMutexLease Mutex,
            MeshInstancePipeServer Server);

        private readonly object gate = new();
        private readonly string storageRoot;
        private readonly bool headless;
        private readonly MeshInstanceRegistry registry;
        private readonly TaskCompletionSource<bool> hostReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Binding? binding;
        private Func<IReadOnlyList<string>, Task>? activate;
        private Func<CancellationToken, Task>? prepareShutdown;
        private Action? exit;
        private readonly List<IReadOnlyList<string>> pendingActivations = new();
        private int shutdownRequested;

        public RuntimeSession(
            string storageRoot,
            string? accountId,
            string runtimeHandle,
            string registryHandle,
            bool headless,
            bool setup,
            MeshHandleMutexLease mutex)
        {
            this.storageRoot = storageRoot;
            this.headless = headless;
            registry = new MeshInstanceRegistry(storageRoot);
            binding = CreateBinding(
                accountId ?? string.Empty,
                runtimeHandle,
                registryHandle,
                setup,
                mutex);
        }

        public void AttachHost(
            Func<IReadOnlyList<string>, Task> activateCallback,
            Func<CancellationToken, Task> prepareShutdownCallback,
            Action exitCallback)
        {
            ArgumentNullException.ThrowIfNull(activateCallback);
            ArgumentNullException.ThrowIfNull(prepareShutdownCallback);
            ArgumentNullException.ThrowIfNull(exitCallback);
            IReadOnlyList<IReadOnlyList<string>> queued;
            lock (gate)
            {
                activate = activateCallback;
                prepareShutdown = prepareShutdownCallback;
                exit = exitCallback;
                queued = pendingActivations.ToArray();
                pendingActivations.Clear();
            }
            hostReady.TrySetResult(true);
            foreach (var arguments in queued) _ = InvokeActivationAsync(arguments);
        }

        public AccountSwitchReservation ReserveSwitch(string accountId, string normalizedHandle)
        {
            if (normalizedHandle.Length == 0)
                return AccountSwitchReservation.Denied("That identity has no valid handle.");
            lock (gate)
            {
                if (binding is not null
                    && string.Equals(binding.RuntimeHandle, normalizedHandle, StringComparison.Ordinal))
                    return AccountSwitchReservation.Allowed();
            }

            if (!MeshHandleMutexLease.TryAcquire(normalizedHandle, out var reserved))
                return AccountSwitchReservation.Denied(
                    "This identity is already running in another Mesh instance.");

            var transferred = false;
            return AccountSwitchReservation.Create(
                switchIdentity =>
                {
                    if (!switchIdentity()) return false;
                    CommitSwitch(accountId, normalizedHandle, reserved!);
                    transferred = true;
                    return true;
                },
                () =>
                {
                    if (!transferred) reserved!.Dispose();
                });
        }

        private void CommitSwitch(
            string accountId,
            string normalizedHandle,
            MeshHandleMutexLease mutex)
        {
            var next = CreateBinding(
                accountId,
                normalizedHandle,
                normalizedHandle,
                setup: false,
                mutex);
            Binding? previous;
            lock (gate)
            {
                previous = binding;
                binding = next;
            }
            MeshProcessContext.UpdateIdentity(accountId, normalizedHandle);
            if (previous is not null) ReleaseBinding(previous, stopServer: true);
        }

        public async Task RequestLocalShutdownAsync()
        {
            if (Interlocked.Exchange(ref shutdownRequested, 1) != 0) return;
            try
            {
                await PrepareShutdownAsync().ConfigureAwait(false);
                Binding? currentBinding;
                Action? exitCallback;
                lock (gate)
                {
                    currentBinding = binding;
                    binding = null;
                    exitCallback = exit;
                }
                if (currentBinding is not null) ReleaseBinding(currentBinding, stopServer: true);
                exitCallback?.Invoke();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref shutdownRequested, 0);
                RuntimeDiagnostics.Current?.RecordException("instance-local-shutdown", ex);
            }
        }

        private Binding CreateBinding(
            string accountId,
            string runtimeHandle,
            string registryHandle,
            bool setup,
            MeshHandleMutexLease mutex)
        {
            var server = new MeshInstancePipeServer(
                MeshInstanceNames.PipeName(runtimeHandle), HandlePipeCommandAsync);
            if (!setup)
            {
                try
                {
                    registry.Write(new MeshInstanceRecord(
                        registryHandle,
                        Environment.ProcessId,
                        headless,
                        ProcessStartedAt()));
                }
                catch (Exception ex)
                {
                    RuntimeDiagnostics.Current?.RecordException("instance-registry-write", ex);
                }
            }
            return new Binding(accountId, runtimeHandle, registryHandle, setup, mutex, server);
        }

        private async Task<MeshPipeReply> HandlePipeCommandAsync(
            MeshPipeCommand command,
            CancellationToken ct)
        {
            if (string.Equals(command.Command, "activate", StringComparison.OrdinalIgnoreCase))
            {
                await InvokeActivationAsync(command.Arguments ?? Array.Empty<string>()).ConfigureAwait(false);
                return new MeshPipeReply("ok");
            }
            if (!string.Equals(command.Command, "shutdown", StringComparison.OrdinalIgnoreCase))
                return new MeshPipeReply("unknown command");
            if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
                return new MeshPipeReply("shutdown already in progress");

            try
            {
                await PrepareShutdownAsync().ConfigureAwait(false);
                Binding? currentBinding;
                Action? exitCallback;
                lock (gate)
                {
                    currentBinding = binding;
                    binding = null;
                    exitCallback = exit;
                }
                if (currentBinding is not null)
                {
                    if (!currentBinding.Setup) registry.Delete(currentBinding.RegistryHandle);
                    currentBinding.Mutex.Dispose();
                }
                return new MeshPipeReply("ok", StopServer: true, AfterResponse: exitCallback);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref shutdownRequested, 0);
                RuntimeDiagnostics.Current?.RecordException("instance-pipe-shutdown", ex);
                return new MeshPipeReply("shutdown failed");
            }
        }

        private async Task PrepareShutdownAsync()
        {
            MeshProcessContext.BeginShutdown();
            try
            {
                await hostReady.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                Func<CancellationToken, Task>? callback;
                lock (gate) callback = prepareShutdown;
                if (callback is null) throw new InvalidOperationException("The Mesh host is not ready to shut down.");
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await callback(budget.Token).ConfigureAwait(false);
            }
            catch
            {
                MeshProcessContext.Configure(
                    MeshProcessContext.PreferredAccountId,
                    MeshProcessContext.NormalizedHandle,
                    headless);
                throw;
            }
        }

        private Task InvokeActivationAsync(IReadOnlyList<string> arguments)
        {
            Func<IReadOnlyList<string>, Task>? callback;
            lock (gate)
            {
                callback = activate;
                if (callback is null)
                {
                    pendingActivations.Add(arguments.ToArray());
                    return Task.CompletedTask;
                }
            }
            return callback(arguments);
        }

        private static DateTimeOffset ProcessStartedAt()
        {
            if (!OperatingSystem.IsWindows()) return DateTimeOffset.UtcNow;
            try
            {
                using var process = Process.GetCurrentProcess();
                return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch
            {
                return DateTimeOffset.UtcNow;
            }
        }

        private void ReleaseBinding(Binding value, bool stopServer)
        {
            if (!value.Setup) registry.Delete(value.RegistryHandle);
            if (stopServer) _ = value.Server.DisposeAsync().AsTask();
            value.Mutex.Dispose();
        }

        public void ReleaseForProcessExit()
        {
            Binding? value;
            lock (gate)
            {
                value = binding;
                binding = null;
            }
            if (value is null) return;
            if (!value.Setup) registry.Delete(value.RegistryHandle);
            value.Mutex.Dispose();
        }
    }
}

public sealed class AccountSwitchReservation : IDisposable
{
    private readonly Func<Func<bool>, bool>? commit;
    private readonly Action? rollback;
    private int completed;

    private AccountSwitchReservation(
        bool acquired,
        string? error,
        Func<Func<bool>, bool>? commit,
        Action? rollback)
    {
        Acquired = acquired;
        Error = error;
        this.commit = commit;
        this.rollback = rollback;
    }

    public bool Acquired { get; }
    public string? Error { get; }

    public bool Commit(Func<bool> switchIdentity)
    {
        ArgumentNullException.ThrowIfNull(switchIdentity);
        if (!Acquired || commit is null) return false;
        if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            throw new InvalidOperationException("The account switch reservation was already completed.");
        var switched = false;
        try
        {
            switched = commit(switchIdentity);
            return switched;
        }
        finally
        {
            if (!switched) rollback?.Invoke();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref completed, 1, 0) == 0)
            rollback?.Invoke();
    }

    internal static AccountSwitchReservation Allowed()
        => new(true, null, switchIdentity => switchIdentity(), null);

    internal static AccountSwitchReservation Denied(string error)
        => new(false, error, null, null);

    internal static AccountSwitchReservation Create(
        Func<Func<bool>, bool> commit,
        Action rollback)
        => new(true, null, commit, rollback);
}
