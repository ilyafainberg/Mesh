using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Exposes one turn's approval-wrapped Mesh tools to Copilot through a secret loopback MCP endpoint.
/// </summary>
public sealed class CopilotMcpBridge(ILogger<CopilotMcpBridge> logger) : IAsyncDisposable
{
    public sealed record Registration(string Name, string Url, string Token);

    private sealed record ToolScope(
        IReadOnlyDictionary<string, IAgentTool> Tools,
        CancellationToken CancellationToken);

    private readonly ConcurrentDictionary<string, ToolScope> scopes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private HttpListener? listener;
    private CancellationTokenSource? listenerCts;
    private Task? listenerTask;
    private int port;

    public async Task<Registration?> RegisterAsync(
        IReadOnlyList<IAgentTool> tools,
        CancellationToken ct)
    {
        if (tools.Count == 0) return null;
        await EnsureStartedAsync(ct);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        scopes[token] = new ToolScope(
            tools.GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal),
            ct);
        return new Registration("mesh", $"http://127.0.0.1:{port}/mcp/{token}/", token);
    }

    public void Unregister(Registration? registration)
    {
        if (registration is not null)
            scopes.TryRemove(registration.Token, out _);
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (listener is { IsListening: true }) return;
        await startGate.WaitAsync(ct);
        try
        {
            if (listener is { IsListening: true }) return;
            HttpListener? started = null;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 10 && started is null; attempt++)
            {
                var candidatePort = ReservePort();
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
                try
                {
                    candidate.Start();
                    port = candidatePort;
                    started = candidate;
                }
                catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException)
                {
                    lastError = ex;
                    candidate.Close();
                }
            }
            listener = started ?? throw new InvalidOperationException(
                "Could not start the local Mesh tool bridge.", lastError);
            listenerCts = new CancellationTokenSource();
            listenerTask = ListenAsync(listener, listenerCts.Token);
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task ListenAsync(HttpListener activeListener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await activeListener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Copilot MCP bridge stopped unexpectedly.");
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken serverCt)
    {
        object? responseId = null;
        try
        {
            if (context.Request.RemoteEndPoint?.Address is not { } remote || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = 403;
                return;
            }

            var segments = context.Request.Url?.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var token = segments is { Length: 2 } && segments[0] == "mcp" ? segments[1] : null;
            if (token is null || !scopes.TryGetValue(token, out var scope)
                || !string.Equals(
                    context.Request.Headers["Authorization"],
                    $"Bearer {token}",
                    StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                return;
            }

            if (context.Request.HttpMethod == "GET")
            {
                context.Response.StatusCode = 405;
                return;
            }
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(serverCt);
            using var document = JsonDocument.Parse(body);
            var request = document.RootElement;
            var hasId = request.TryGetProperty("id", out var id);
            if (hasId)
                responseId = JsonSerializer.Deserialize<object>(id.GetRawText());
            var method = request.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString()
                : null;

            if (!hasId)
            {
                context.Response.StatusCode = 202;
                return;
            }

            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = RequestedProtocol(request),
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "Mesh Tools", version = "1.0" }
                },
                "ping" => new { },
                "tools/list" => new
                {
                    tools = scope.Tools.Values.Select(tool => new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = tool.ParametersSchema
                    }).ToArray()
                },
                "tools/call" => await CallToolAsync(request, scope, serverCt),
                _ => throw new McpBridgeException(-32601, $"Method not found: {method}")
            };
            context.Response.Headers["Mcp-Session-Id"] = token;
            await WriteJsonAsync(context.Response, new
            {
                jsonrpc = "2.0",
                id = responseId,
                result
            }, serverCt);
        }
        catch (McpBridgeException ex)
        {
            await WriteErrorAsync(context, responseId, ex.Code, ex.Message, serverCt);
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(context, responseId, -32800, "Request cancelled.", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Copilot MCP bridge request failed.");
            await WriteErrorAsync(context, responseId, -32603, ex.Message, CancellationToken.None);
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static async Task<object> CallToolAsync(
        JsonElement request,
        ToolScope scope,
        CancellationToken serverCt)
    {
        if (!request.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("name", out var nameElement)
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
            throw new McpBridgeException(-32602, "Tool name is required.");
        var name = nameElement.GetString()!;
        if (!scope.Tools.TryGetValue(name, out var tool))
            throw new McpBridgeException(-32602, $"Unknown tool: {name}");
        var arguments = parameters.TryGetProperty("arguments", out var args)
            ? args.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            serverCt,
            scope.CancellationToken);
        try
        {
            var output = await tool.ExecuteAsync(arguments, linked.Token);
            var isError = output.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);
            return new
            {
                content = new[] { new { type = "text", text = output } },
                isError
            };
        }
        catch (Exception ex)
        {
            return new
            {
                content = new[] { new { type = "text", text = $"ERROR: {ex.Message}" } },
                isError = true
            };
        }
    }

    private static string RequestedProtocol(JsonElement request)
        => request.TryGetProperty("params", out var parameters)
           && parameters.TryGetProperty("protocolVersion", out var version)
           && version.ValueKind == JsonValueKind.String
            ? version.GetString() ?? "2025-06-18"
            : "2025-06-18";

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        object payload,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct);
    }

    private static Task WriteErrorAsync(
        HttpListenerContext context,
        object? id,
        int code,
        string message,
        CancellationToken ct)
        => WriteJsonAsync(context.Response, new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        }, ct);

    private static int ReservePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        listenerCts?.Cancel();
        try { listener?.Stop(); } catch { }
        if (listenerTask is not null)
            try { await listenerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        listener?.Close();
        listenerCts?.Dispose();
        startGate.Dispose();
    }

    private sealed class McpBridgeException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
