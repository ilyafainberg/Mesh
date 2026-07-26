using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>Reads provider-authored model attribution from completion responses.</summary>
internal static class ProviderModelMetadata
{
    public static string? ReadOpenAi(JsonElement root)
    {
        if (!root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String)
            return null;

        var value = model.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
