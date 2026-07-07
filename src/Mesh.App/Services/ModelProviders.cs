using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public interface IChatModel
{
    Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default);

    /// <summary>
    /// Completes with tool access. The model may call tools zero or more times;
    /// each call is executed and fed back until it produces a final text answer.
    /// Default implementation ignores tools (for models without tool support).
    /// </summary>
    Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, history, ct);
}

/// <summary>
/// Recognizes model-layer failure replies (provider errors, the hosted model being unavailable,
/// or the daily limit being hit). Used to avoid sending an error message to a peer as if it were
/// the agent's real reply, and to refund budgets when no real answer was produced.
/// </summary>
public static class ModelReply
{
    private static readonly string[] FailureMarkers =
    {
        "The free model ", "You've reached today's free-model limit",
        "[model error", "[free model", "[stopped after too many tool calls",
        "[Azure OpenAI needs", "[set up your Mesh identity", "[the free model needs a relay"
    };

    public static bool IsFailure(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return true;
        var t = reply.TrimStart();
        return FailureMarkers.Any(m => t.StartsWith(m, StringComparison.Ordinal));
    }
}

/// <summary>Shared limit on how many rounds of tool calls a model may make before being forced to answer.</summary>
internal static class ToolLoop
{
    public const int MaxRounds = 16;
}

/// <summary>Extracts token usage from the various provider response shapes and reports it to the meter.</summary>
internal static class Usage
{
    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>OpenAI / Groq / Azure shape: <c>usage { prompt_tokens, completion_tokens }</c>.</summary>
    public static void ReportOpenAi(TokenMeter? meter, JsonElement root)
    {
        if (meter is null || !root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        meter.Record(GetLong(u, "prompt_tokens"), GetLong(u, "completion_tokens"));
    }

    /// <summary>Anthropic shape: <c>usage { input_tokens, output_tokens }</c>.</summary>
    public static void ReportAnthropic(TokenMeter? meter, JsonElement root)
    {
        if (meter is null || !root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        meter.Record(GetLong(u, "input_tokens"), GetLong(u, "output_tokens"));
    }

    /// <summary>Gemini shape: <c>usageMetadata { promptTokenCount, candidatesTokenCount }</c>.</summary>
    public static void ReportGemini(TokenMeter? meter, JsonElement root)
    {
        if (meter is null || !root.TryGetProperty("usageMetadata", out var u) || u.ValueKind != JsonValueKind.Object) return;
        meter.Record(GetLong(u, "promptTokenCount"), GetLong(u, "candidatesTokenCount"));
    }
}

/// <summary>Builds an <see cref="IChatModel"/> for the configured provider.</summary>
public sealed class ModelFactory(IHttpClientFactory httpFactory, AppState state, TokenMeter meter)
{
    public IChatModel Create(ModelConfig cfg) => cfg.Provider switch
    {
        ModelProvider.Anthropic => new AnthropicModel(httpFactory.CreateClient("model"), cfg, meter),
        ModelProvider.Gemini => new GeminiModel(httpFactory.CreateClient("model"), cfg, meter),
        ModelProvider.FoundryLocal => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithFoundryDefault(cfg), meter),
        ModelProvider.Grok => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithEndpoint(cfg, "https://api.x.ai"), meter),
        ModelProvider.Groq => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithEndpoint(cfg, "https://api.groq.com/openai"), meter),
        ModelProvider.MeshHosted => new MeshHostedModel(httpFactory.CreateClient("model"), state, cfg, meter),
        ModelProvider.AzureOpenAI => new AzureOpenAiModel(httpFactory.CreateClient("model"), cfg, meter),
        _ => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), cfg, meter),
    };

    /// <summary>Applies a default endpoint for OpenAI-compatible hosts (Grok/Groq) when none set.</summary>
    private static ModelConfig WithEndpoint(ModelConfig cfg, string defaultEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Endpoint)) return cfg;
        return new ModelConfig { Provider = cfg.Provider, Model = cfg.Model, ApiKey = cfg.ApiKey, Endpoint = defaultEndpoint };
    }

    /// <summary>Foundry Local exposes an OpenAI-compatible endpoint on a dynamic port.</summary>
    private static ModelConfig WithFoundryDefault(ModelConfig cfg)
    {
        // Foundry's port is dynamic (see `foundry service status`); require an explicit endpoint.
        if (!string.IsNullOrWhiteSpace(cfg.Endpoint)) return cfg;
        return new ModelConfig
        {
            Provider = cfg.Provider,
            Model = cfg.Model,
            ApiKey = cfg.ApiKey,
            Endpoint = "http://127.0.0.1:5273" // last-resort fallback for older Foundry builds
        };
    }
}

/// <summary>Works for OpenAI, Groq, Mistral, Foundry Local, Ollama (OpenAI-compatible).</summary>
public sealed class OpenAiCompatibleModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.Endpoint) ? "https://api.openai.com" : cfg.Endpoint!.TrimEnd('/');
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = l.Text }));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new("Bearer", cfg.ApiKey);
        req.Content = JsonContent.Create(new { model = cfg.Model, messages, max_tokens = 1024 });

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportOpenAi(meter, doc.RootElement);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string MapRole(string r) => r is "assistant" ? "assistant" : r is "system" ? "system" : "user";
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, ct);

        var baseUrl = string.IsNullOrWhiteSpace(cfg.Endpoint) ? "https://api.openai.com" : cfg.Endpoint!.TrimEnd('/');
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = l.Text }));

        var toolDefs = tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
        }).ToArray();

        // Up to 4 rounds of tool calls before forcing an answer.
        for (var round = 0; round < ToolLoop.MaxRounds; round++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers.Authorization = new("Bearer", cfg.ApiKey);
            req.Content = JsonContent.Create(new { model = cfg.Model, messages, tools = toolDefs, max_tokens = 1024 });

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Some local models reject the `tools` parameter, degrade to a plain
                // completion so chat still works rather than surfacing an error.
                if (round == 0) return await CompleteAsync(systemPrompt, history, ct);
                return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
            }

            using var doc = JsonDocument.Parse(body);
            Usage.ReportOpenAi(meter, doc.RootElement);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            if (!msg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                return msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            // Echo the assistant tool-call message, then append each tool result.
            messages.Add(new { role = "assistant", content = (string?)null, tool_calls = CloneArray(toolCalls) });
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var result = await ExecuteToolAsync(tools, name, argsJson, ct);
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }
        return "[stopped after too many tool calls]";
    }

    internal static async Task<string> ExecuteToolAsync(IReadOnlyList<IAgentTool> tools, string name, string argsJson, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return $"ERROR: unknown tool '{name}'.";
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return await tool.ExecuteAsync(argsDoc.RootElement, ct);
        }
        catch (Exception ex) { return "ERROR: " + ex.Message; }
    }

    private static object[] CloneArray(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}

public sealed class AnthropicModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default)
    {
        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role, content = l.Text })
            .ToList();
        if (messages.Count == 0) messages.Add(new { role = "user", content = "Hello" });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(new { model = cfg.Model, max_tokens = 1024, system = systemPrompt, messages });

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("content");
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
            if (block.GetProperty("type").GetString() == "text")
                sb.Append(block.GetProperty("text").GetString());
        Usage.ReportAnthropic(meter, doc.RootElement);
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, ct);

        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role, content = (object)l.Text })
            .ToList();
        if (messages.Count == 0) messages.Add(new { role = "user", content = (object)"Hello" });

        var toolDefs = tools.Select(t => (object)new
        {
            name = t.Name, description = t.Description, input_schema = t.ParametersSchema
        }).ToArray();

        for (var round = 0; round < ToolLoop.MaxRounds; round++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = JsonContent.Create(new { model = cfg.Model, max_tokens = 1024, system = systemPrompt, messages, tools = toolDefs });

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("content");
            var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
            Usage.ReportAnthropic(meter, doc.RootElement);

            var text = new StringBuilder();
            var toolUses = new List<(string id, string name, string argsJson)>();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text") text.Append(block.GetProperty("text").GetString());
                else if (type == "tool_use")
                    toolUses.Add((block.GetProperty("id").GetString() ?? "",
                        block.GetProperty("name").GetString() ?? "",
                        block.GetProperty("input").GetRawText()));
            }

            if (stopReason != "tool_use" || toolUses.Count == 0)
                return text.ToString();

            // Append the assistant's tool_use content, then a user turn with tool_result blocks.
            messages.Add(new { role = "assistant", content = CloneContent(content) });
            var results = new List<object>();
            foreach (var (id, name, argsJson) in toolUses)
            {
                var result = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct);
                results.Add(new { type = "tool_result", tool_use_id = id, content = result });
            }
            messages.Add(new { role = "user", content = results.ToArray() });
        }
        return "[stopped after too many tool calls]";
    }

    private static object[] CloneContent(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}

public sealed class GeminiModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default)
    {
        var contents = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role == "assistant" ? "model" : "user", parts = new[] { new { text = l.Text } } })
            .ToList();

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";
        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents
        };

        using var resp = await http.PostAsJsonAsync(url, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportGemini(meter, doc.RootElement);
        return doc.RootElement.GetProperty("candidates")[0].GetProperty("content")
            .GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// The relay-hosted free model. Sends a signed completion request to the Mesh relay, which
/// injects a server-side upstream key and returns the completion (rate limited per handle).
/// This powers the one-click "start free" onboarding: the user needs no key of their own.
/// Tool calls are not supported on the free tier, so tool requests degrade to plain chat.
/// </summary>
public sealed class MeshHostedModel(HttpClient http, AppState state, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default)
    {
        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => new HostedModelMessage(l.Role, l.Text))
            .ToList();
        if (messages.Count == 0) messages.Add(new HostedModelMessage("user", "Hello"));

        var (result, error) = await PostAsync(systemPrompt, messages, toolsJson: null, ct);
        if (error is not null) return error;
        return result?.Content ?? "";
    }

    /// <summary>
    /// Runs the tool-calling loop for the hosted free model. Tools execute on THIS device (the
    /// relay never runs them): each round the relay returns the model's tool_calls, we execute
    /// the requested tools locally, append the results, and continue until the model answers.
    /// </summary>
    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, ct);

        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
        }).ToArray();
        var toolsJson = JsonSerializer.Serialize(toolDefs, Web);

        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => new HostedModelMessage(l.Role, l.Text))
            .ToList();
        if (messages.Count == 0) messages.Add(new HostedModelMessage("user", "Hello"));

        for (var round = 0; round < ToolLoop.MaxRounds; round++)
        {
            var (result, error) = await PostAsync(systemPrompt, messages, toolsJson, ct);
            if (error is not null)
                // On the first round, degrade to a plain completion so chat still works even if
                // the hosted model rejects tools; later rounds surface the error.
                return round == 0 ? await CompleteAsync(systemPrompt, history, ct) : error;

            if (string.IsNullOrWhiteSpace(result?.ToolCallsJson))
                return result?.Content ?? "";

            // Record the assistant's tool-call turn, then execute each tool locally and append results.
            messages.Add(new HostedModelMessage("assistant", result!.Content ?? "", ToolCallsJson: result.ToolCallsJson));
            using var calls = JsonDocument.Parse(result.ToolCallsJson!);
            foreach (var call in calls.RootElement.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var toolResult = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct);
                messages.Add(new HostedModelMessage("tool", toolResult, ToolCallId: id));
            }
        }
        return "[stopped after too many tool calls]";
    }

    private async Task<(HostedModelResponse? result, string? error)> PostAsync(
        string systemPrompt, IReadOnlyList<HostedModelMessage> messages, string? toolsJson, CancellationToken ct)
    {
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.RelayUrl))
            return (null, "[the free model needs a relay configured in Settings]");
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey))
            return (null, "[set up your Mesh identity to use the free model]");

        var promptHash = HostedModelProtocol.PromptHash(systemPrompt, messages);
        var sig = IdentityService.Sign(p.PrivateKey, HostedModelProtocol.Message(p.Handle, promptHash));
        var request = new HostedModelRequest(AppState.Norm(p.Handle), p.PublicKey, sig, systemPrompt, messages, toolsJson);
        _ = cfg.Model; // the hosted model id is chosen server-side; cfg is kept for parity with other providers

        try
        {
            using var resp = await http.PostAsJsonAsync($"{p.RelayUrl.TrimEnd('/')}/model/chat", request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return (null, "You've reached today's free-model limit. Add your own model key in Settings for unlimited use, or switch to an on-device model.");
            if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                return (null, "The free model is temporarily unavailable. You can add your own model key in Settings, or switch to an on-device model.");
            if (!resp.IsSuccessStatusCode) return (null, "The free model is temporarily unavailable. Please try again shortly, or add your own model key in Settings.");
            var parsed = JsonSerializer.Deserialize<HostedModelResponse>(body, Web);
            if (parsed is not null)
                meter?.Record(parsed.PromptTokens, parsed.CompletionTokens);
            return (parsed, null);
        }
        catch (Exception ex) { return (null, $"The free model could not be reached ({ex.Message}). Check your connection, or add your own model key in Settings."); }
    }
}

/// <summary>
/// Azure OpenAI (bring-your-own-resource). Uses the same chat-completions request/response
/// shape as OpenAI, but targets an Azure deployment URL
/// (<c>{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...</c>) and
/// authenticates with the <c>api-key</c> header instead of a Bearer token. The user's
/// <see cref="ModelConfig.Model"/> is the Azure deployment name and
/// <see cref="ModelConfig.Endpoint"/> is the resource URL. Supports tool calls.
/// </summary>
public sealed class AzureOpenAiModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    // When the user provides no api-version we use Azure OpenAI's newer "v1" API surface
    // ({endpoint}/openai/v1/chat/completions), which takes no api-version query parameter and
    // carries the deployment name in the request body's "model" field. Older resources that need a
    // specific dated version still work: set the API version in Settings to use the legacy
    // deployment URL ({endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...).
    private bool UseV1 => string.IsNullOrWhiteSpace(cfg.ApiVersion);

    private string ChatUrl()
    {
        var baseUrl = (cfg.Endpoint ?? "").TrimEnd('/');
        if (UseV1)
            return $"{baseUrl}/openai/v1/chat/completions";
        var version = cfg.ApiVersion!.Trim();
        return $"{baseUrl}/openai/deployments/{cfg.Model}/chat/completions?api-version={version}";
    }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Endpoint)) return "[Azure OpenAI needs a resource endpoint in Settings]";
        if (string.IsNullOrWhiteSpace(cfg.Model)) return "[Azure OpenAI needs a deployment name in Settings]";

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = l.Text }));

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl());
        req.Headers.TryAddWithoutValidation("api-key", cfg.ApiKey);
        // The v1 API carries the deployment name in the body; the legacy URL carries it in the path
        // (and ignores an extra "model" field), so sending it is safe for both.
        req.Content = JsonContent.Create(new { model = cfg.Model, messages, max_tokens = 1024 });

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportOpenAi(meter, doc.RootElement);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, ct);
        if (string.IsNullOrWhiteSpace(cfg.Endpoint) || string.IsNullOrWhiteSpace(cfg.Model))
            return await CompleteAsync(systemPrompt, history, ct);

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = l.Text }));

        var toolDefs = tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
        }).ToArray();

        for (var round = 0; round < ToolLoop.MaxRounds; round++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl());
            req.Headers.TryAddWithoutValidation("api-key", cfg.ApiKey);
            req.Content = JsonContent.Create(new { model = cfg.Model, messages, tools = toolDefs, max_tokens = 1024 });

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (round == 0) return await CompleteAsync(systemPrompt, history, ct);
                return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
            }

            using var doc = JsonDocument.Parse(body);
            Usage.ReportOpenAi(meter, doc.RootElement);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

            if (!msg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                return msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            messages.Add(new { role = "assistant", content = (string?)null, tool_calls = CloneArray(toolCalls) });
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var result = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct);
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }
        return "[stopped after too many tool calls]";
    }

    private static string MapRole(string r) => r is "assistant" ? "assistant" : r is "system" ? "system" : "user";
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
    private static object[] CloneArray(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}
