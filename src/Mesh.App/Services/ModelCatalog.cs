using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Curated lists of well-known model ids per provider so the Settings UI can offer a dropdown
/// instead of a free-text box. The lists are a convenience, not a constraint: a "Custom..." entry
/// lets the user type any id the provider supports (new models ship faster than this list updates).
/// The relay-hosted free model and Foundry Local pick their model elsewhere, so they are not here.
/// </summary>
public static class ModelCatalog
{
    public sealed record ModelOption(string Id, string Label);

    private static readonly IReadOnlyList<ModelOption> Anthropic = new[]
    {
        new ModelOption("claude-opus-4-6", "Claude Opus 4.6 (most capable)"),
        new ModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6 (balanced)"),
        new ModelOption("claude-haiku-4-5", "Claude Haiku 4.5 (fast, cheap)"),
        new ModelOption("claude-3-5-sonnet-20241022", "Claude 3.5 Sonnet"),
    };

    private static readonly IReadOnlyList<ModelOption> OpenAi = new[]
    {
        new ModelOption("gpt-5.1", "GPT-5.1 (most capable)"),
        new ModelOption("gpt-5-mini", "GPT-5 mini (fast, cheap)"),
        new ModelOption("gpt-4o", "GPT-4o"),
        new ModelOption("gpt-4o-mini", "GPT-4o mini"),
        new ModelOption("o3", "o3 (reasoning)"),
    };

    private static readonly IReadOnlyList<ModelOption> Gemini = new[]
    {
        new ModelOption("gemini-2.5-pro", "Gemini 2.5 Pro (most capable)"),
        new ModelOption("gemini-2.5-flash", "Gemini 2.5 Flash (fast)"),
        new ModelOption("gemini-1.5-flash", "Gemini 1.5 Flash"),
    };

    private static readonly IReadOnlyList<ModelOption> Grok = new[]
    {
        new ModelOption("grok-4", "Grok 4"),
        new ModelOption("grok-3", "Grok 3"),
        new ModelOption("grok-2-latest", "Grok 2"),
    };

    private static readonly IReadOnlyList<ModelOption> Groq = new[]
    {
        new ModelOption("openai/gpt-oss-120b", "GPT-OSS 120B (most capable)"),
        new ModelOption("openai/gpt-oss-20b", "GPT-OSS 20B (fast)"),
        new ModelOption("llama-3.3-70b-versatile", "Llama 3.3 70B"),
        new ModelOption("meta-llama/llama-4-scout-17b-16e-instruct", "Llama 4 Scout 17B"),
        new ModelOption("qwen/qwen3-32b", "Qwen3 32B"),
        new ModelOption("llama-3.1-8b-instant", "Llama 3.1 8B (fastest)"),
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
}
