using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

internal static class MultimodalContent
{
    public static object OpenAi(ChatLine line)
    {
        if (line.Attachments.Count == 0) return line.Text;
        var parts = new List<object> { new { type = "text", text = line.Text } };
        parts.AddRange(line.Attachments.Where(a => a.IsImage).Select(a => (object)new
        {
            type = "image_url",
            image_url = new { url = $"data:{a.MimeType};base64,{Convert.ToBase64String(a.Data)}" }
        }));
        return parts.ToArray();
    }

    public static object Anthropic(ChatLine line)
    {
        if (line.Attachments.Count == 0) return line.Text;
        var parts = new List<object>();
        parts.AddRange(line.Attachments.Where(a => a.IsImage).Select(a => (object)new
        {
            type = "image",
            source = new { type = "base64", media_type = a.MimeType, data = Convert.ToBase64String(a.Data) }
        }));
        parts.Add(new { type = "text", text = line.Text });
        return parts.ToArray();
    }

    public static object[] Gemini(ChatLine line)
    {
        var parts = new List<object> { new { text = line.Text } };
        parts.AddRange(line.Attachments.Where(a => a.IsImage).Select(a => (object)new
        {
            inlineData = new { mimeType = a.MimeType, data = Convert.ToBase64String(a.Data) }
        }));
        return parts.ToArray();
    }
}

public interface IChatModel
{
    Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Completes with tool access. The model may call tools zero or more times;
    /// each call is executed and fed back until it produces a final text answer.
    /// Default implementation ignores tools (for models without tool support).
    /// When <paramref name="progress"/> is supplied, an <see cref="AgentStep"/> is reported as each
    /// tool call starts and finishes so the UI can show a live step trace.
    /// </summary>
    Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
        => CompleteAsync(systemPrompt, history, options, ct);
}

/// <summary>
/// Per-call completion tuning. <see cref="MaxOutputTokens"/> caps the model's output: ordinary chat
/// uses the default, while large generations (e.g. building a widget, which is a whole HTML+JS
/// document) request a much higher cap so the reply is not truncated mid-code. A too-small cap was
/// the root cause of widgets rendering as broken, dead documents.
/// </summary>
public sealed record CompletionOptions(int MaxOutputTokens = CompletionOptions.DefaultMaxOutputTokens)
{
    public const int DefaultMaxOutputTokens = 20480;

    /// <summary>Generous cap for whole-document generations such as widgets.</summary>
    public static readonly CompletionOptions Widget = new(40960);

    /// <summary>The effective output cap for an options value that may be null (falls back to default).</summary>
    public static int Resolve(CompletionOptions? options) => options?.MaxOutputTokens ?? DefaultMaxOutputTokens;
}

/// <summary>
/// The reply text used when a provider stopped generating because it hit the output-token limit
/// (finish_reason "length" / stop_reason "max_tokens" / finishReason "MAX_TOKENS"). Surfacing this
/// instead of the partial text stops truncated code being rendered as if it were complete.
/// </summary>
public static class TruncationDetection
{
    public const string Marker = "[the model's answer was cut off because it hit the output length limit. Try again, or ask for something shorter.]";

    /// <summary>True when an OpenAI/Azure-shape choice was truncated by the output-token limit.</summary>
    public static bool IsLengthCappedOpenAi(JsonElement choice)
        => choice.TryGetProperty("finish_reason", out var fr)
           && fr.ValueKind == JsonValueKind.String
           && string.Equals(fr.GetString(), "length", StringComparison.OrdinalIgnoreCase);
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
        "[model error", "[free model",
        "[Azure OpenAI needs", "[set up your Mesh identity", "[the free model needs a relay",
        "[the model's answer was cut off"
    };

    public static bool IsFailure(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return true;
        var t = reply.TrimStart();
        return FailureMarkers.Any(m => t.StartsWith(m, StringComparison.Ordinal));
    }
}

/// <summary>
/// Clips tool arguments/results for the step trace and the model's hidden transcript. Keeps the head
/// and tail (where the useful signal usually is) and marks the elided middle, so a huge tool output
/// (a long file, a big command dump) cannot blow up the UI or the context budget while still leaving
/// the model enough to understand what happened.
/// </summary>
internal static class ToolTrace
{
    public const int MaxChars = 4000;

    public static string? Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.Length <= MaxChars) return text;
        var head = MaxChars * 2 / 3;
        var tail = MaxChars - head;
        var omitted = text.Length - head - tail;
        return text[..head] + $"\n... [{omitted} characters omitted] ...\n" + text[^tail..];
    }
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

internal static class ReasoningControls
{
    public static Dictionary<string, object?> OpenAi(ModelConfig cfg, object[] messages, int maxTokens, object[]? tools = null, bool useMaxCompletionTokens = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = cfg.Model, ["messages"] = messages,
            [useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens
        };
        if (tools is not null) payload["tools"] = tools;
        if (cfg.ReasoningEffort != ReasoningEffort.Auto)
        {
            var effort = cfg.ReasoningEffort.ToString().ToLowerInvariant();
            if (cfg.Provider == ModelProvider.OpenRouter)
                payload["reasoning"] = new { effort };
            else
                payload["reasoning_effort"] = effort;
        }
        return payload;
    }

    public static Dictionary<string, object?> Anthropic(ModelConfig cfg, string systemPrompt,
        List<object> messages, int maxTokens, object[]? tools = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = cfg.Model, ["max_tokens"] = maxTokens,
            ["system"] = systemPrompt, ["messages"] = messages
        };
        if (tools is not null) payload["tools"] = tools;
        if (cfg.ReasoningEffort != ReasoningEffort.Auto)
        {
            payload["thinking"] = new { type = "adaptive" };
            payload["output_config"] = new { effort = cfg.ReasoningEffort.ToString().ToLowerInvariant() };
        }
        return payload;
    }

    public static object GeminiGeneration(ModelConfig cfg, int maxTokens)
        => cfg.ReasoningEffort == ReasoningEffort.Auto
            ? new { maxOutputTokens = maxTokens }
            : (object)new
            {
                maxOutputTokens = maxTokens,
                thinkingConfig = new { thinkingLevel = cfg.ReasoningEffort.ToString().ToUpperInvariant() }
            };
}

/// <summary>Builds an <see cref="IChatModel"/> for the configured provider.</summary>
public sealed class ModelFactory(IHttpClientFactory httpFactory, AppState state, TokenMeter meter, BrowserModelService browserModel, CopilotAcpHost copilot)
{
    public IChatModel Create(ModelConfig cfg) => cfg.Provider switch
    {
        ModelProvider.Anthropic => new AnthropicModel(httpFactory.CreateClient("model"), cfg, meter),
        ModelProvider.Gemini => new GeminiModel(httpFactory.CreateClient("model"), cfg, meter),
        ModelProvider.FoundryLocal => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithFoundryDefault(cfg), meter),
        ModelProvider.Grok => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithEndpoint(cfg, "https://api.x.ai"), meter),
        ModelProvider.Groq => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), WithEndpoint(cfg, "https://api.groq.com/openai"), meter),
        // OpenRouter: the user's OpenRouter account (key) decides the model/provider routing, so the
        // client never sends a specific model; it always requests "openrouter/auto" and lets OpenRouter
        // route. We do not replicate any OpenRouter settings in the client.
        ModelProvider.OpenRouter => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), OpenRouterConfig(cfg), meter),
        ModelProvider.MeshHosted => new MeshHostedModel(httpFactory.CreateClient("model"), state, cfg, meter),
        ModelProvider.AzureOpenAI => new AzureOpenAiModel(httpFactory.CreateClient("model"), cfg, meter),
        ModelProvider.Browser => new BrowserChatModel(browserModel, cfg),
        ModelProvider.GitHubCopilot => new CopilotAcpModel(copilot, cfg),
        _ => new OpenAiCompatibleModel(httpFactory.CreateClient("model"), cfg, meter),
    };

    /// <summary>Applies a default endpoint for OpenAI-compatible hosts (Grok/Groq) when none set.</summary>
    private static ModelConfig WithEndpoint(ModelConfig cfg, string defaultEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Endpoint)) return cfg;
        return new ModelConfig { Provider = cfg.Provider, Model = cfg.Model, ApiKey = cfg.ApiKey, Endpoint = defaultEndpoint, ReasoningEffort = cfg.ReasoningEffort };
    }

    /// <summary>GitHub Copilot CLI provider using its ACP stdio server. Mesh remains the history source.</summary>
    public sealed class CopilotAcpModel(CopilotAcpHost host, ModelConfig cfg) : IChatModel
    {
        public Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
            CompletionOptions? options = null, CancellationToken ct = default)
            => CompleteCoreAsync(systemPrompt, history, Array.Empty<IAgentTool>(), progress: null, options, ct);

        public Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
            IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
            CompletionOptions? options = null, CancellationToken ct = default)
            => CompleteCoreAsync(systemPrompt, history, tools, progress, options, ct);

        private Task<string> CompleteCoreAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
            IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress, CompletionOptions? options, CancellationToken ct)
        {
            var promptLines = history.Select(line => (line.Role, line.Text)).ToList();
            var images = history
                .SelectMany(line => line.Attachments)
                .Where(attachment => attachment.IsImage)
                .Select(attachment => (attachment.MimeType, attachment.Data))
                .ToList();
            var budget = CompletionOptions.Resolve(options);
            var system = systemPrompt + $"\nKeep the response within approximately {budget} output tokens.";
            var config = new CopilotAcpConfig(
                string.IsNullOrWhiteSpace(cfg.CopilotExecutable) ? "copilot" : cfg.CopilotExecutable.Trim(),
                string.IsNullOrWhiteSpace(cfg.Model) ? "auto" : cfg.Model.Trim(),
                cfg.CopilotEffort.ToString());
            return host.CompleteAsync(config, system, promptLines, images, tools, progress, ct);
        }
    }

    /// <summary>
    /// OpenRouter always routes with "openrouter/auto": the user's OpenRouter account settings decide
    /// the actual model/provider, so the client never pins a model. The endpoint is fixed.
    /// </summary>
    private static ModelConfig OpenRouterConfig(ModelConfig cfg)
        => new ModelConfig { Provider = cfg.Provider, Model = "openrouter/auto", ApiKey = cfg.ApiKey, Endpoint = "https://openrouter.ai/api", ReasoningEffort = cfg.ReasoningEffort };

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
            ReasoningEffort = cfg.ReasoningEffort,
            Endpoint = "http://127.0.0.1:5273" // last-resort fallback for older Foundry builds
        };
    }
}

/// <summary>Works for OpenAI, Groq, Mistral, Foundry Local, Ollama (OpenAI-compatible).</summary>
public sealed class OpenAiCompatibleModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(cfg.Endpoint) ? "https://api.openai.com" : cfg.Endpoint!.TrimEnd('/');
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = MultimodalContent.OpenAi(l) }));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
            req.Headers.Authorization = new("Bearer", cfg.ApiKey);
        req.Content = JsonContent.Create(ReasoningControls.OpenAi(cfg, messages.ToArray(), CompletionOptions.Resolve(options)));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportOpenAi(meter, doc.RootElement);
        var choice = doc.RootElement.GetProperty("choices")[0];
        if (TruncationDetection.IsLengthCappedOpenAi(choice)) return TruncationDetection.Marker;
        return ReasoningExtract.WithOpenAiReasoning(choice.GetProperty("message"));
    }

    private static string MapRole(string r) => r is "assistant" ? "assistant" : r is "system" ? "system" : "user";
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, options, ct);

        var baseUrl = string.IsNullOrWhiteSpace(cfg.Endpoint) ? "https://api.openai.com" : cfg.Endpoint!.TrimEnd('/');
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = MultimodalContent.OpenAi(l) }));

        var toolDefs = tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
        }).ToArray();

        // Continue tool calls until the model answers or the user cancels the turn.
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) req.Headers.Authorization = new("Bearer", cfg.ApiKey);
            req.Content = JsonContent.Create(ReasoningControls.OpenAi(cfg, messages.ToArray(), CompletionOptions.Resolve(options), toolDefs));

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Some local models reject the `tools` parameter, degrade to a plain
                // completion so chat still works rather than surfacing an error.
                if (round == 0) return await CompleteAsync(systemPrompt, history, options, ct);
                return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
            }

            using var doc = JsonDocument.Parse(body);
            Usage.ReportOpenAi(meter, doc.RootElement);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var msg = choice.GetProperty("message");

            if (!msg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                return TruncationDetection.IsLengthCappedOpenAi(choice)
                    ? TruncationDetection.Marker
                    : ReasoningExtract.WithOpenAiReasoning(msg);

            // Echo the assistant tool-call message, then append each tool result.
            messages.Add(new { role = "assistant", content = (string?)null, tool_calls = CloneArray(toolCalls) });
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var result = await ExecuteToolAsync(tools, name, argsJson, ct, progress);
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }
    }

    internal static async Task<string> ExecuteToolAsync(IReadOnlyList<IAgentTool> tools, string name, string argsJson,
        CancellationToken ct, IProgress<AgentStep>? progress = null)
    {
        var label = ReasoningExtract.Label(name);
        var args = ToolTrace.Clip(argsJson);
        progress?.Report(new AgentStep(name, label, AgentStepState.Started, Arguments: args));
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            var miss = $"ERROR: unknown tool '{name}'.";
            progress?.Report(new AgentStep(name, label, AgentStepState.Failed, args, miss));
            return miss;
        }
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var result = await tool.ExecuteAsync(argsDoc.RootElement, ct);
            progress?.Report(new AgentStep(name, label, AgentStepState.Done, args, ToolTrace.Clip(result)));
            return result;
        }
        catch (Exception ex)
        {
            var err = "ERROR: " + ex.Message;
            progress?.Report(new AgentStep(name, label, AgentStepState.Failed, args, ToolTrace.Clip(err)));
            return err;
        }
    }

    private static object[] CloneArray(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}

public sealed class AnthropicModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role, content = MultimodalContent.Anthropic(l) })
            .ToList();
        if (messages.Count == 0) messages.Add(new { role = "user", content = "Hello" });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", cfg.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(ReasoningControls.Anthropic(cfg, systemPrompt, messages, CompletionOptions.Resolve(options)));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr0) ? sr0.GetString() : null;
        if (string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)) return TruncationDetection.Marker;
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
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, options, ct);

        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role, content = MultimodalContent.Anthropic(l) })
            .ToList();
        if (messages.Count == 0) messages.Add(new { role = "user", content = (object)"Hello" });

        var toolDefs = tools.Select(t => (object)new
        {
            name = t.Name, description = t.Description, input_schema = t.ParametersSchema
        }).ToArray();

        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = JsonContent.Create(ReasoningControls.Anthropic(cfg, systemPrompt, messages, CompletionOptions.Resolve(options), toolDefs));

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("content");
            var stopReason = doc.RootElement.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
            Usage.ReportAnthropic(meter, doc.RootElement);

            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var toolUses = new List<(string id, string name, string argsJson)>();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text") text.Append(block.GetProperty("text").GetString());
                else if (type == "thinking" && block.TryGetProperty("thinking", out var th))
                    thinking.Append(th.GetString());
                else if (type == "tool_use")
                    toolUses.Add((block.GetProperty("id").GetString() ?? "",
                        block.GetProperty("name").GetString() ?? "",
                        block.GetProperty("input").GetRawText()));
            }

            if (stopReason != "tool_use" || toolUses.Count == 0)
                return string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase)
                    ? TruncationDetection.Marker
                    : ReasoningExtract.Wrap(thinking.ToString(), text.ToString());

            // Append the assistant's tool_use content, then a user turn with tool_result blocks.
            messages.Add(new { role = "assistant", content = CloneContent(content) });
            var results = new List<object>();
            foreach (var (id, name, argsJson) in toolUses)
            {
                var result = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct, progress);
                results.Add(new { type = "tool_result", tool_use_id = id, content = result });
            }
            messages.Add(new { role = "user", content = results.ToArray() });
        }
    }

    private static object[] CloneContent(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}

public sealed class GeminiModel(HttpClient http, ModelConfig cfg, TokenMeter? meter = null) : IChatModel
{
    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        var contents = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => (object)new { role = l.Role == "assistant" ? "model" : "user", parts = MultimodalContent.Gemini(l) })
            .ToList();

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.Model}:generateContent?key={cfg.ApiKey}";
        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents,
            generationConfig = ReasoningControls.GeminiGeneration(cfg, CompletionOptions.Resolve(options))
        };

        using var resp = await http.PostAsJsonAsync(url, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportGemini(meter, doc.RootElement);
        var candidate = doc.RootElement.GetProperty("candidates")[0];
        var finishReason = candidate.TryGetProperty("finishReason", out var frEl) ? frEl.GetString() : null;
        if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)) return TruncationDetection.Marker;
        return candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
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

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        var messages = history
            .Where(l => l.Role is "user" or "assistant")
            .Select(l => new HostedModelMessage(l.Role, l.Text))
            .ToList();
        if (messages.Count == 0) messages.Add(new HostedModelMessage("user", "Hello"));

        var (result, error) = await PostAsync(systemPrompt, messages, toolsJson: null, CompletionOptions.Resolve(options), ct);
        if (error is not null) return error;
        if (IsTruncated(result)) return TruncationDetection.Marker;
        return result?.Content ?? "";
    }

    /// <summary>
    /// Runs the tool-calling loop for the hosted free model. Tools execute on THIS device (the
    /// relay never runs them): each round the relay returns the model's tool_calls, we execute
    /// the requested tools locally, append the results, and continue until the model answers.
    /// </summary>
    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, options, ct);

        var maxTokens = CompletionOptions.Resolve(options);
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

        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            var (result, error) = await PostAsync(systemPrompt, messages, toolsJson, maxTokens, ct);
            if (error is not null)
                // On the first round, degrade to a plain completion so chat still works even if
                // the hosted model rejects tools; later rounds surface the error.
                return round == 0 ? await CompleteAsync(systemPrompt, history, options, ct) : error;

            if (string.IsNullOrWhiteSpace(result?.ToolCallsJson))
                return IsTruncated(result) ? TruncationDetection.Marker : result?.Content ?? "";

            // Record the assistant's tool-call turn, then execute each tool locally and append results.
            messages.Add(new HostedModelMessage("assistant", result!.Content ?? "", ToolCallsJson: result.ToolCallsJson));
            using var calls = JsonDocument.Parse(result.ToolCallsJson!);
            foreach (var call in calls.RootElement.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var toolResult = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct, progress);
                messages.Add(new HostedModelMessage("tool", toolResult, ToolCallId: id));
            }
        }
    }

    private static bool IsTruncated(HostedModelResponse? r)
        => r is not null && string.Equals(r.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

    private async Task<(HostedModelResponse? result, string? error)> PostAsync(
        string systemPrompt, IReadOnlyList<HostedModelMessage> messages, string? toolsJson, int maxTokens, CancellationToken ct)
    {
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.RelayUrl))
            return (null, "[the free model needs a relay configured in Settings]");
        if (string.IsNullOrWhiteSpace(p.Handle) || string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey))
            return (null, "[set up your Mesh identity to use the free model]");

        var promptHash = HostedModelProtocol.PromptHash(systemPrompt, messages);
        var sig = IdentityService.Sign(p.PrivateKey, HostedModelProtocol.Message(p.Handle, promptHash));
        var request = new HostedModelRequest(AppState.Norm(p.Handle), p.PublicKey, sig, systemPrompt, messages, toolsJson, maxTokens);
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
/// authenticates with the <c>api-key</c> header instead of a Bearer token. Azure requests use
/// <c>max_completion_tokens</c>, required by current reasoning models such as GPT-5. The user's
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

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Endpoint)) return "[Azure OpenAI needs a resource endpoint in Settings]";
        if (string.IsNullOrWhiteSpace(cfg.Model)) return "[Azure OpenAI needs a deployment name in Settings]";

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = MultimodalContent.OpenAi(l) }));

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl());
        req.Headers.TryAddWithoutValidation("api-key", cfg.ApiKey);
        // The v1 API carries the deployment name in the body; the legacy URL carries it in the path
        // (and ignores an extra "model" field), so sending it is safe for both.
        req.Content = JsonContent.Create(ReasoningControls.OpenAi(cfg, messages.ToArray(), CompletionOptions.Resolve(options), useMaxCompletionTokens: true));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
        using var doc = JsonDocument.Parse(body);
        Usage.ReportOpenAi(meter, doc.RootElement);
        var choice = doc.RootElement.GetProperty("choices")[0];
        if (TruncationDetection.IsLengthCappedOpenAi(choice)) return TruncationDetection.Marker;
        return choice.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async Task<string> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<ChatLine> history,
        IReadOnlyList<IAgentTool> tools, IProgress<AgentStep>? progress = null,
        CompletionOptions? options = null, CancellationToken ct = default)
    {
        if (tools.Count == 0) return await CompleteAsync(systemPrompt, history, options, ct);
        if (string.IsNullOrWhiteSpace(cfg.Endpoint) || string.IsNullOrWhiteSpace(cfg.Model))
            return await CompleteAsync(systemPrompt, history, options, ct);

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(history.Select(l => (object)new { role = MapRole(l.Role), content = MultimodalContent.OpenAi(l) }));

        var toolDefs = tools.Select(t => (object)new
        {
            type = "function",
            function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
        }).ToArray();

        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl());
            req.Headers.TryAddWithoutValidation("api-key", cfg.ApiKey);
            req.Content = JsonContent.Create(ReasoningControls.OpenAi(cfg, messages.ToArray(), CompletionOptions.Resolve(options), toolDefs, useMaxCompletionTokens: true));

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (round == 0) return await CompleteAsync(systemPrompt, history, options, ct);
                return $"[model error {(int)resp.StatusCode}: {Trim(body)}]";
            }

            using var doc = JsonDocument.Parse(body);
            Usage.ReportOpenAi(meter, doc.RootElement);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var msg = choice.GetProperty("message");

            if (!msg.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() == 0)
                return TruncationDetection.IsLengthCappedOpenAi(choice)
                    ? TruncationDetection.Marker
                    : ReasoningExtract.WithOpenAiReasoning(msg);

            messages.Add(new { role = "assistant", content = (string?)null, tool_calls = CloneArray(toolCalls) });
            foreach (var call in toolCalls.EnumerateArray())
            {
                var id = call.GetProperty("id").GetString();
                var fn = call.GetProperty("function");
                var name = fn.GetProperty("name").GetString() ?? "";
                var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                var result = await OpenAiCompatibleModel.ExecuteToolAsync(tools, name, argsJson, ct, progress);
                messages.Add(new { role = "tool", tool_call_id = id, content = result });
            }
        }
    }

    private static string MapRole(string r) => r is "assistant" ? "assistant" : r is "system" ? "system" : "user";
    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
    private static object[] CloneArray(JsonElement arr)
        => arr.EnumerateArray().Select(e => (object)JsonSerializer.Deserialize<JsonElement>(e.GetRawText())).ToArray();
}
