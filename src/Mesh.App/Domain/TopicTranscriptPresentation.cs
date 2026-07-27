namespace Mesh.App.Domain;

/// <summary>Normalizes topic transcript roles into the supported bubble styles.</summary>
public static class TopicTranscriptPresentation
{
    public static string BubbleRole(string? role)
        => string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
}
