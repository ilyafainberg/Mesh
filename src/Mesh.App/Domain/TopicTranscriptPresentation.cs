namespace Mesh.App.Domain;

public sealed record TopicTranscriptRenderItem(string Key, ChatLine? Line)
{
    public bool IsActiveRun => Line is null;
}

/// <summary>Builds a stable topic transcript and normalizes its bubble roles.</summary>
public static class TopicTranscriptPresentation
{
    public static IReadOnlyList<TopicTranscriptRenderItem> Compose(
        IEnumerable<ChatLine> lines,
        Func<ChatLine, bool> isQueued,
        string? activeRunKey)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(isQueued);

        var source = lines.ToList();
        var includeActiveRun = !string.IsNullOrWhiteSpace(activeRunKey);
        var result = new List<TopicTranscriptRenderItem>(source.Count + (includeActiveRun ? 1 : 0));
        var activeRunAdded = false;

        void AddActiveRun()
        {
            result.Add(new TopicTranscriptRenderItem($"run:{activeRunKey}", null));
            activeRunAdded = true;
        }

        foreach (var line in source)
        {
            if (includeActiveRun && !activeRunAdded && isQueued(line))
                AddActiveRun();
            result.Add(new TopicTranscriptRenderItem($"line:{line.Id}", line));
        }

        if (includeActiveRun && !activeRunAdded)
            AddActiveRun();
        return result;
    }

    public static string BubbleRole(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return BubbleRole(line.Role);
    }

    public static string BubbleRole(string? role)
        => string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";
}
