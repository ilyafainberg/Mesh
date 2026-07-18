using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>Managed ACP v1 client for the desktop GitHub Copilot CLI provider.</summary>
public sealed class CopilotAcpHost(ILogger<CopilotAcpHost> logger) : IAsyncDisposable
{
    private sealed class TurnState
    {
        public StringBuilder Answer { get; } = new();
        public StringBuilder Thought { get; } = new();
        public IProgress<AgentStep>? Progress { get; init; }
    }

    private readonly SemaphoreSlim turnGate = new(1, 1);
    private readonly SemaphoreSlim processGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly object writeGate = new();
    private readonly object stderrGate = new();
    private Process? process;
    private StreamWriter? input;
    private Task? readerTask;
    private Task? stderrTask;
    private CancellationTokenSource? processCts;
    private CopilotAcpConfig? activeConfig;
    private TurnState? activeTurn;
    private bool supportsImages;
    private bool supportsClose;
    private long nextId;
    private string stderrTail = "";
    private DateTimeOffset modelsCachedAt;
    private string? modelsExecutable;
    private IReadOnlyList<CopilotModelOption>? modelCache;

    public async Task<IReadOnlyList<CopilotModelOption>> GetModelsAsync(
        string executable,
        bool force = false,
        CancellationToken ct = default)
    {
        if (!force && modelCache is not null
            && string.Equals(modelsExecutable, executable, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - modelsCachedAt < TimeSpan.FromMinutes(5))
            return modelCache;

        await turnGate.WaitAsync(ct);
        try
        {
            var config = new CopilotAcpConfig(executable, "auto", "auto");
            await EnsureProcessAsync(config, ct);
            var session = await NewSessionAsync(ct);
            var models = CopilotAcpProtocol.ParseModels(session);
            await CloseSessionAsync(SessionId(session), ct);
            modelCache = models;
            modelsExecutable = executable;
            modelsCachedAt = DateTimeOffset.UtcNow;
            return models;
        }
        finally
        {
            turnGate.Release();
        }
    }

    public async Task<(bool Ok, string Message)> CheckAsync(
        CopilotAcpConfig config,
        CancellationToken ct = default)
    {
        try
        {
            var result = await CompleteAsync(
                config,
                "You are a connection test.",
                new[] { ("user", "Reply with exactly OK") },
                Array.Empty<(string MimeType, byte[] Data)>(),
                ct: ct);
            return string.IsNullOrWhiteSpace(result)
                ? (false, "GitHub Copilot CLI returned an empty response.")
                : (true, "GitHub Copilot CLI is working.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string> CompleteAsync(
        CopilotAcpConfig config,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> history,
        IReadOnlyList<(string MimeType, byte[] Data)> images,
        IProgress<AgentStep>? progress = null,
        CancellationToken ct = default)
    {
        await turnGate.WaitAsync(ct);
        try
        {
            await EnsureProcessAsync(config, ct);
            var session = await NewSessionAsync(ct);
            var sessionId = SessionId(session);
            var content = new List<object>
            {
                new { type = "text", text = CopilotAcpProtocol.ComposePrompt(systemPrompt, history) }
            };
            if (supportsImages)
                content.AddRange(images.Select(image => (object)new
                {
                    type = "image",
                    mimeType = image.MimeType,
                    data = Convert.ToBase64String(image.Data)
                }));

            activeTurn = new TurnState { Progress = progress };
            using var registration = ct.Register(() =>
            {
                _ = SendNotificationAsync("session/cancel", new { sessionId }, CancellationToken.None);
            });
            try
            {
                var result = await RequestAsync("session/prompt", new { sessionId, prompt = content }, ct);
                var stopReason = result.TryGetProperty("stopReason", out var stop) ? stop.GetString() : null;
                if (string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase))
                    throw new OperationCanceledException(ct);
                if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase))
                    return TruncationDetection.Marker;
                var answer = activeTurn.Answer.ToString().Trim();
                if (answer.Length == 0)
                    throw new InvalidOperationException("GitHub Copilot CLI returned an empty response.");
                return ReasoningExtract.Wrap(activeTurn.Thought.ToString(), answer);
            }
            finally
            {
                activeTurn = null;
                await CloseSessionAsync(sessionId, CancellationToken.None);
            }
        }
        finally
        {
            turnGate.Release();
        }
    }

    private async Task EnsureProcessAsync(CopilotAcpConfig config, CancellationToken ct)
    {
        await processGate.WaitAsync(ct);
        try
        {
            if (process is { HasExited: false } && activeConfig == config) return;
            await StopProcessAsync();

            var executable = ResolveExecutable(config.Executable);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = WorkspaceDirectory()
            };
            foreach (var argument in CopilotAcpProtocol.BuildServerArguments(config.Model, config.Effort))
                start.ArgumentList.Add(argument);

            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("GitHub Copilot CLI did not start.");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "GitHub Copilot CLI is not installed or is not on PATH.", ex);
            }

            input = process.StandardInput;
            input.AutoFlush = true;
            processCts = new CancellationTokenSource();
            readerTask = ReadLoopAsync(process.StandardOutput, processCts.Token);
            stderrTask = ReadStderrAsync(process.StandardError, processCts.Token);
            activeConfig = config;

            var initialized = await RequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new { },
                clientInfo = new { name = "mesh", title = "Mesh", version = "1" }
            }, ct);
            var version = initialized.TryGetProperty("protocolVersion", out var pv) && pv.TryGetInt32(out var v) ? v : 0;
            if (version != 1)
                throw new InvalidOperationException($"GitHub Copilot CLI selected unsupported ACP version {version}.");
            if (initialized.TryGetProperty("agentCapabilities", out var capabilities))
            {
                supportsImages = capabilities.TryGetProperty("promptCapabilities", out var prompt)
                    && prompt.TryGetProperty("image", out var image)
                    && image.ValueKind == JsonValueKind.True;
                supportsClose = capabilities.TryGetProperty("sessionCapabilities", out var sessions)
                    && sessions.TryGetProperty("close", out _);
            }
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
        finally
        {
            processGate.Release();
        }
    }

    private Task<JsonElement> NewSessionAsync(CancellationToken ct)
        => RequestAsync("session/new", new { cwd = WorkspaceDirectory(), mcpServers = Array.Empty<object>() }, ct);

    private async Task CloseSessionAsync(string sessionId, CancellationToken ct)
    {
        if (!supportsClose || string.IsNullOrWhiteSpace(sessionId)) return;
        try { await RequestAsync("session/close", new { sessionId }, ct); }
        catch { }
    }

    private static string SessionId(JsonElement result)
        => result.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? throw new InvalidOperationException("ACP session id was empty.")
            : throw new InvalidOperationException("GitHub Copilot CLI did not create an ACP session.");

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        if (input is null || process is null || process.HasExited)
            throw new InvalidOperationException("GitHub Copilot CLI ACP process is not running.");
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            return await completion.Task.WaitAsync(ct);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken ct)
        => WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, ct);

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        lock (writeGate)
        {
            if (input is null) throw new InvalidOperationException("GitHub Copilot CLI input is unavailable.");
            input.WriteLine(json);
            input.Flush();
        }
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
    }

    private async Task ReadLoopAsync(StreamReader output, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await output.ReadLineAsync(ct);
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
                {
                    if (root.TryGetProperty("method", out _))
                    {
                        await HandleClientRequestAsync(root, id, ct);
                        continue;
                    }
                    if (!pending.TryGetValue(id, out var completion)) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var text) ? text.GetString() : "ACP request failed.";
                        completion.TrySetException(new InvalidOperationException(FriendlyError(message)));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    continue;
                }
                HandleNotification(root);
            }
            FailPending($"GitHub Copilot CLI stopped unexpectedly. {StderrHint()}".Trim());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Copilot ACP read loop failed.");
            FailPending($"GitHub Copilot CLI ACP connection failed. {StderrHint()}".Trim());
        }
    }

    private async Task HandleClientRequestAsync(JsonElement root, long id, CancellationToken ct)
    {
        var method = root.GetProperty("method").GetString();
        if (method == "session/request_permission")
        {
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id,
                result = new { outcome = new { outcome = "cancelled" } }
            }, ct);
            return;
        }
        await WriteAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32601, message = $"Method not supported: {method}" }
        }, ct);
    }

    private void HandleNotification(JsonElement root)
    {
        var turn = activeTurn;
        if (!root.TryGetProperty("method", out var method)
            || method.GetString() != "session/update"
            || !root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("update", out var update)
            || turn is null)
            return;
        var kind = update.TryGetProperty("sessionUpdate", out var type) ? type.GetString() : null;
        if (kind is "agent_message_chunk" or "agent_thought_chunk")
        {
            if (update.TryGetProperty("content", out var content)
                && content.TryGetProperty("type", out var contentType)
                && contentType.GetString() == "text"
                && content.TryGetProperty("text", out var text))
            {
                if (kind == "agent_message_chunk") turn.Answer.Append(text.GetString());
                else turn.Thought.Append(text.GetString());
            }
            return;
        }
        if (kind is not "tool_call" and not "tool_call_update") return;
        var title = update.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : "Copilot action";
        var status = update.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : "pending";
        var state = status switch
        {
            "completed" => AgentStepState.Done,
            "failed" or "cancelled" => AgentStepState.Failed,
            _ => AgentStepState.Started
        };
        turn.Progress?.Report(new AgentStep("copilot", title ?? "Copilot action", state));
    }

    private async Task ReadStderrAsync(StreamReader error, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await error.ReadLineAsync(ct);
                if (line is null) break;
                lock (stderrGate)
                {
                    stderrTail = (stderrTail + "\n" + line).Trim();
                    if (stderrTail.Length > 4000) stderrTail = stderrTail[^4000..];
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private string FriendlyError(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "GitHub Copilot CLI request failed." : message;
        var combined = text + " " + StderrHint();
        if (combined.Contains("login", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("credential", StringComparison.OrdinalIgnoreCase))
            return text + " Run copilot login in a terminal, then try again.";
        return text;
    }

    private string StderrHint()
    {
        lock (stderrGate)
            return stderrTail.Length == 0 ? "" : stderrTail;
    }

    private void FailPending(string message)
    {
        foreach (var completion in pending.Values)
            completion.TrySetException(new InvalidOperationException(FriendlyError(message)));
    }

    private static string WorkspaceDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mesh",
            "CopilotWorkspace");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string ResolveExecutable(string? configured)
    {
        var command = string.IsNullOrWhiteSpace(configured) ? "copilot" : configured.Trim().Trim('"');
        if (Path.IsPathRooted(command))
            return File.Exists(command)
                ? command
                : throw new InvalidOperationException("GitHub Copilot CLI is not installed or is not on PATH.");
        var candidates = OperatingSystem.IsWindows()
            ? new[] { command + ".exe", command + ".cmd", command + ".bat", command }
            : new[] { command };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(directory.Trim().Trim('"'), candidate);
            if (File.Exists(path)) return path;
        }
        if (OperatingSystem.IsWindows())
        {
            var npm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "copilot.cmd");
            if (File.Exists(npm)) return npm;
        }
        throw new InvalidOperationException("GitHub Copilot CLI is not installed or is not on PATH.");
    }

    private async Task StopProcessAsync()
    {
        processCts?.Cancel();
        if (process is { HasExited: false })
        {
            try { process.StandardInput.Close(); } catch { }
            try
            {
                if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
        if (readerTask is not null) try { await readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        if (stderrTask is not null) try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        process?.Dispose();
        processCts?.Dispose();
        process = null;
        input = null;
        readerTask = null;
        stderrTask = null;
        processCts = null;
        activeConfig = null;
        supportsImages = false;
        supportsClose = false;
        stderrTail = "";
    }

    public async ValueTask DisposeAsync()
    {
        await processGate.WaitAsync();
        try { await StopProcessAsync(); }
        finally { processGate.Release(); }
        processGate.Dispose();
        turnGate.Dispose();
    }
}
