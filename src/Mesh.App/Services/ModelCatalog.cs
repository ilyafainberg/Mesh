using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Curated lists of well-known model ids per provider so the Settings UI can offer a dropdown
/// instead of a free-text box. The lists are a convenience, not a constraint: a "Custom..." entry
/// lets the user type any id the provider supports (new models ship faster than this list updates).
/// The relay-hosted free model and Foundry Local pick their model elsewhere, so they are not here.
/// Also holds published per-token pricing (USD) so Settings can estimate spend for models that have it.
/// </summary>
public static class ModelCatalog
{
    public sealed record ModelOption(string Id, string Label);

    /// <summary>Published price in USD per 1,000,000 tokens (input and output).</summary>
    public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

    private static readonly IReadOnlyList<ModelOption> Anthropic = new[]
    {
        new ModelOption("claude-opus-4-6", "Claude Opus 4.6 (most capable)"),
        new ModelOption("claude-opus-4-1", "Claude Opus 4.1"),
        new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6 (balanced)"),
        new ModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5"),
        new ModelOption("claude-haiku-4-5", "Claude Haiku 4.5 (fast, cheap)"),
        new ModelOption("claude-3-7-sonnet-latest", "Claude 3.7 Sonnet"),
        new ModelOption("claude-3-5-sonnet-20241022", "Claude 3.5 Sonnet"),
        new ModelOption("claude-3-5-haiku-latest", "Claude 3.5 Haiku"),
    };

    private static readonly IReadOnlyList<ModelOption> OpenAi = new[]
    {
        new ModelOption("gpt-5.1", "GPT-5.1 (most capable)"),
        new ModelOption("gpt-5", "GPT-5"),
        new ModelOption("gpt-5-mini", "GPT-5 mini (fast, cheap)"),
        new ModelOption("gpt-5-nano", "GPT-5 nano (fastest)"),
        new ModelOption("gpt-4.1", "GPT-4.1"),
        new ModelOption("gpt-4.1-mini", "GPT-4.1 mini"),
        new ModelOption("gpt-4o", "GPT-4o"),
        new ModelOption("gpt-4o-mini", "GPT-4o mini"),
        new ModelOption("o3", "o3 (reasoning)"),
        new ModelOption("o4-mini", "o4-mini (reasoning, fast)"),
    };

    private static readonly IReadOnlyList<ModelOption> Gemini = new[]
    {
        new ModelOption("gemini-2.5-pro", "Gemini 2.5 Pro (most capable)"),
        new ModelOption("gemini-2.5-flash", "Gemini 2.5 Flash (fast)"),
        new ModelOption("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite (fastest)"),
        new ModelOption("gemini-2.0-flash", "Gemini 2.0 Flash"),
        new ModelOption("gemini-1.5-pro", "Gemini 1.5 Pro"),
        new ModelOption("gemini-1.5-flash", "Gemini 1.5 Flash"),
    };

    private static readonly IReadOnlyList<ModelOption> Grok = new[]
    {
        new ModelOption("grok-4", "Grok 4 (most capable)"),
        new ModelOption("grok-4-fast", "Grok 4 Fast"),
        new ModelOption("grok-3", "Grok 3"),
        new ModelOption("grok-3-mini", "Grok 3 mini"),
        new ModelOption("grok-2-latest", "Grok 2"),
    };

    private static readonly IReadOnlyList<ModelOption> Groq = new[]
    {
        new ModelOption("openai/gpt-oss-120b", "GPT-OSS 120B (most capable)"),
        new ModelOption("openai/gpt-oss-20b", "GPT-OSS 20B (fast)"),
        new ModelOption("moonshotai/kimi-k2-instruct", "Kimi K2 Instruct"),
        new ModelOption("llama-3.3-70b-versatile", "Llama 3.3 70B"),
        new ModelOption("meta-llama/llama-4-scout-17b-16e-instruct", "Llama 4 Scout 17B"),
        new ModelOption("meta-llama/llama-4-maverick-17b-128e-instruct", "Llama 4 Maverick 17B"),
        new ModelOption("qwen/qwen3-32b", "Qwen3 32B"),
        new ModelOption("llama-3.1-8b-instant", "Llama 3.1 8B (fastest)"),
    };

    // Published list prices, USD per 1,000,000 tokens (input, output). Best-effort and only used to
    // ESTIMATE spend in Settings; providers can change prices and per-account rates vary. Models not
    // listed here (Groq/OSS, on-device, hosted free) show no cost estimate.
    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-6"] = new(15m, 75m),
        ["claude-opus-4-1"] = new(15m, 75m),
        ["claude-sonnet-4-6"] = new(3m, 15m),
        ["claude-sonnet-4-5"] = new(3m, 15m),
        ["claude-haiku-4-5"] = new(1m, 5m),
        ["claude-3-7-sonnet-latest"] = new(3m, 15m),
        ["claude-3-5-sonnet-20241022"] = new(3m, 15m),
        ["claude-3-5-haiku-latest"] = new(0.80m, 4m),
        ["gpt-5.1"] = new(1.25m, 10m),
        ["gpt-5"] = new(1.25m, 10m),
        ["gpt-5-mini"] = new(0.25m, 2m),
        ["gpt-5-nano"] = new(0.05m, 0.40m),
        ["gpt-4.1"] = new(2m, 8m),
        ["gpt-4.1-mini"] = new(0.40m, 1.60m),
        ["gpt-4o"] = new(2.50m, 10m),
        ["gpt-4o-mini"] = new(0.15m, 0.60m),
        ["o3"] = new(2m, 8m),
        ["o4-mini"] = new(1.10m, 4.40m),
        ["gemini-2.5-pro"] = new(1.25m, 10m),
        ["gemini-2.5-flash"] = new(0.30m, 2.50m),
        ["gemini-2.5-flash-lite"] = new(0.10m, 0.40m),
        ["gemini-2.0-flash"] = new(0.10m, 0.40m),
        ["gemini-1.5-pro"] = new(1.25m, 5m),
        ["gemini-1.5-flash"] = new(0.075m, 0.30m),
        ["grok-4"] = new(3m, 15m),
        ["grok-4-fast"] = new(0.20m, 0.50m),
        ["grok-3"] = new(3m, 15m),
        ["grok-3-mini"] = new(0.30m, 0.50m),
        ["grok-2-latest"] = new(2m, 10m),
    };

    /// <summary>Returns the curated options for a provider, or an empty list when the provider is free-text only.</summary>
    public static IReadOnlyList<ModelOption> For(ModelProvider provider) => provider switch
    {
        ModelProvider.Anthropic => Anthropic,
        ModelProvider.OpenAI => OpenAi,
        ModelProvider.Gemini => Gemini,
        ModelProvider.Grok => Grok,
        ModelProvider.Groq => Groq,
        _ => Array.Empty<ModelOption>()
    };

    /// <summary>Whether a provider has a curated dropdown (vs Azure deployment names / on-device / hosted).</summary>
    public static bool HasCatalog(ModelProvider provider) => For(provider).Count > 0;

    /// <summary>Published price for a model id, or null when unknown (no estimate shown).</summary>
    public static ModelPrice? PriceFor(string? modelId)
        => !string.IsNullOrWhiteSpace(modelId) && Prices.TryGetValue(modelId.Trim(), out var p) ? p : null;

    /// <summary>Estimated USD cost for a token split against a model's published price, or null when unknown.</summary>
    public static decimal? EstimateCost(string? modelId, long promptTokens, long completionTokens)
    {
        var price = PriceFor(modelId);
        if (price is null) return null;
        return promptTokens / 1_000_000m * price.InputPerMillion
            + completionTokens / 1_000_000m * price.OutputPerMillion;
    }
}
