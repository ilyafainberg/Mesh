using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mesh.App.Domain;

namespace Mesh.App.Services;

internal sealed record TokenOptimizedRequest(
    string SystemPrompt,
    IReadOnlyList<ChatLine> History,
    int OriginalCharacters,
    int OptimizedCharacters)
{
    public int SavedCharacters => Math.Max(0, OriginalCharacters - OptimizedCharacters);
}

internal sealed record ContextSelection<T>(
    IReadOnlyList<T> Included,
    IReadOnlyList<T> Omitted);

/// <summary>
/// Builds an ephemeral, smaller inference projection. Persisted conversations and tool results are
/// never mutated. Security instructions and the latest user turn remain verbatim at every level.
/// </summary>
internal static class TokenOptimizer
{
    private const string OmittedText = "omitted by Mesh token optimization";
    private static readonly Regex Ansi = new(
        "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ImportantMarkers =
    [
        "error", "warning", "failed", "failure", "exception", "denied", "timeout",
        "constraint", "requirement", "required", "must", "never", "decision", "todo",
        "next step", "exit code", "stderr", "summary", "result", "status"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "but", "by", "can", "do",
        "for", "from", "had", "has", "have", "how", "i", "if", "in", "into", "is", "it",
        "me", "my", "of", "on", "or", "our", "so", "that", "the", "their", "them", "then",
        "there", "these", "they", "this", "to", "up", "was", "we", "were", "what", "when",
        "where", "which", "who", "why", "will", "with", "would", "you", "your"
    };

    public static TokenOptimizationLevel Normalize(TokenOptimizationLevel level)
        => Enum.IsDefined(level) ? level : TokenOptimizationLevel.Balanced;

    public static TokenOptimizedRequest OptimizeRequest(
        string systemPrompt,
        IReadOnlyList<ChatLine> history,
        TokenOptimizationLevel level)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(history);

        level = Normalize(level);
        var originalCharacters = systemPrompt.Length + history.Sum(line => line.Text?.Length ?? 0);
        if (level == TokenOptimizationLevel.Disabled || history.Count == 0)
            return new TokenOptimizedRequest(systemPrompt, history, originalCharacters, originalCharacters);

        var lastUserIndex = LastUserIndex(history);
        var recentCount = level switch
        {
            TokenOptimizationLevel.MaxAccuracy => 12,
            TokenOptimizationLevel.Balanced => 8,
            _ => 4
        };
        var recentStart = Math.Max(0, history.Count - recentCount);
        var selected = new HashSet<int>();
        if (level == TokenOptimizationLevel.MaxAccuracy)
        {
            for (var index = 0; index < recentStart; index++) selected.Add(index);
        }
        else
        {
            foreach (var index in SelectOlderHistory(history, recentStart, history[lastUserIndex].Text, level))
                selected.Add(index);
        }
        for (var index = recentStart; index < history.Count; index++) selected.Add(index);

        var optimized = new List<ChatLine>(selected.Count);
        foreach (var index in selected.Order())
        {
            var line = history[index];
            var text = line.Text ?? "";
            if (index != lastUserIndex)
            {
                var machine = line.Internal || LooksMachineGenerated(text);
                var limit = HistoryLimit(level, index >= recentStart);
                if (machine || text.Length > limit)
                    text = OptimizeText(text, level, limit, machine);
            }
            optimized.Add(string.Equals(text, line.Text, StringComparison.Ordinal)
                ? line
                : CopyWithText(line, text));
        }

        var optimizedCharacters = systemPrompt.Length + optimized.Sum(line => line.Text.Length);
        return new TokenOptimizedRequest(
            systemPrompt,
            optimized,
            originalCharacters,
            optimizedCharacters);
    }

    public static string OptimizeToolResult(
        string toolName,
        string result,
        TokenOptimizationLevel level)
    {
        ArgumentNullException.ThrowIfNull(result);
        level = Normalize(level);
        if (level == TokenOptimizationLevel.Disabled || result.Length == 0) return result;

        var limit = level switch
        {
            TokenOptimizationLevel.MaxAccuracy => 48_000,
            TokenOptimizationLevel.Balanced => 12_000,
            _ => 4_000
        };

        if (TryOptimizeJson(result, level, limit, out var json)) return json;
        return OptimizeText(result, level, limit, machine: true);
    }

    public static ContextSelection<KnowledgeItem> SelectKnowledge(
        IReadOnlyList<KnowledgeItem> items,
        string? query,
        TokenOptimizationLevel level)
    {
        level = Normalize(level);
        var maximum = level switch
        {
            TokenOptimizationLevel.Balanced => 8,
            TokenOptimizationLevel.MaxSavings => 4,
            _ => int.MaxValue
        };
        return SelectByRelevance(
            items,
            query,
            maximum,
            item => item.Title,
            item => item.Content,
            item => item.UpdatedAt.UtcTicks);
    }

    public static ContextSelection<Skill> SelectSkills(
        IReadOnlyList<Skill> items,
        string? query,
        TokenOptimizationLevel level)
    {
        level = Normalize(level);
        var maximum = level switch
        {
            TokenOptimizationLevel.Balanced => 8,
            TokenOptimizationLevel.MaxSavings => 4,
            _ => int.MaxValue
        };
        return SelectByRelevance(
            items,
            query,
            maximum,
            item => item.Name,
            item => (item.Description ?? "") + "\n" + (item.Instructions ?? ""),
            _ => 0);
    }

    public static int KnowledgeContentLimit(TokenOptimizationLevel level, bool compact)
        => Normalize(level) switch
        {
            TokenOptimizationLevel.Balanced => compact ? 500 : 2_400,
            TokenOptimizationLevel.MaxSavings => compact ? 350 : 1_000,
            _ => compact ? 500 : 4_000
        };

    public static int SkillInstructionLimit(TokenOptimizationLevel level)
        => Normalize(level) switch
        {
            TokenOptimizationLevel.Balanced => 1_800,
            TokenOptimizationLevel.MaxSavings => 700,
            _ => int.MaxValue
        };

    public static string FitContextText(string text, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maximumCharacters) return text;
        return BuildExcerpt(text, maximumCharacters);
    }

    private static IReadOnlyList<int> SelectOlderHistory(
        IReadOnlyList<ChatLine> history,
        int olderCount,
        string query,
        TokenOptimizationLevel level)
    {
        if (olderCount <= 0) return Array.Empty<int>();
        var groups = BuildTurnGroups(history, olderCount);
        var maximumGroups = level == TokenOptimizationLevel.Balanced ? 8 : 4;
        if (groups.Count <= maximumGroups) return groups.SelectMany(group => group).ToList();

        var queryTerms = Terms(query);
        var ranked = groups
            .Select((group, index) => new
            {
                Group = group,
                Index = index,
                Score = HistoryScore(history, group, queryTerms, index, groups.Count)
            })
            .Where(item => level != TokenOptimizationLevel.MaxSavings
                           || item.Score > 0.15
                           || !item.Group.All(index => IsLowSignal(history[index].Text)))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Index)
            .Take(maximumGroups)
            .SelectMany(item => item.Group)
            .Distinct()
            .Order()
            .ToList();
        if (ranked.Count > 0) return ranked;
        return groups[^1];
    }

    private static List<List<int>> BuildTurnGroups(IReadOnlyList<ChatLine> history, int count)
    {
        var groups = new List<List<int>>();
        List<int>? current = null;
        for (var index = 0; index < count; index++)
        {
            if (current is null
                || (string.Equals(history[index].Role, "user", StringComparison.OrdinalIgnoreCase)
                    && current.Count > 0))
            {
                current = new List<int>();
                groups.Add(current);
            }
            current.Add(index);
        }
        return groups;
    }

    private static double HistoryScore(
        IReadOnlyList<ChatLine> history,
        IReadOnlyList<int> group,
        HashSet<string> queryTerms,
        int groupIndex,
        int groupCount)
    {
        var combined = string.Join('\n', group.Select(index => history[index].Text));
        var contentTerms = Terms(combined);
        var overlap = queryTerms.Count == 0
            ? 0
            : queryTerms.Count(term => contentTerms.Contains(term)) / (double)queryTerms.Count;
        var important = ContainsImportant(combined) ? 0.35 : 0;
        var recency = (groupIndex + 1d) / Math.Max(1, groupCount);
        return overlap * 4 + important + recency * 0.35;
    }

    private static ContextSelection<T> SelectByRelevance<T>(
        IReadOnlyList<T> items,
        string? query,
        int maximum,
        Func<T, string> title,
        Func<T, string> content,
        Func<T, long> recency)
    {
        if (items.Count <= maximum)
            return new ContextSelection<T>(items, Array.Empty<T>());

        var queryTerms = Terms(query);
        var ranked = items
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Score = RelevanceScore(title(item), content(item), queryTerms),
                Recency = recency(item)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Recency)
            .ThenByDescending(item => item.Index)
            .Take(maximum)
            .ToList();
        var selectedIndices = ranked.Select(item => item.Index).ToHashSet();
        return new ContextSelection<T>(
            ranked.Select(item => item.Item).ToList(),
            items.Where((_, index) => !selectedIndices.Contains(index)).ToList());
    }

    private static double RelevanceScore(
        string title,
        string content,
        HashSet<string> queryTerms)
    {
        if (queryTerms.Count == 0) return 0;
        var titleTerms = Terms(title);
        var contentTerms = Terms(content);
        var titleMatches = queryTerms.Count(term => titleTerms.Contains(term));
        var contentMatches = queryTerms.Count(term => contentTerms.Contains(term));
        return titleMatches * 4 + contentMatches;
    }

    private static string OptimizeText(
        string text,
        TokenOptimizationLevel level,
        int limit,
        bool machine)
    {
        var candidate = machine ? CleanMachineText(text, level) : text;
        if (candidate.Length <= limit) return candidate;
        return BuildExcerpt(candidate, limit);
    }

    private static string CleanMachineText(string text, TokenOptimizationLevel level)
    {
        var normalized = Ansi.Replace(text, "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var output = new List<string>();
        string? previous = null;
        var blank = false;
        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (!blank) output.Add("");
                blank = true;
                previous = null;
                continue;
            }
            blank = false;
            if (string.Equals(line, previous, StringComparison.Ordinal)
                && (level != TokenOptimizationLevel.MaxAccuracy || LooksLikeProgress(line)))
                continue;
            output.Add(line);
            previous = line;
        }
        return string.Join('\n', output).Trim();
    }

    private static bool TryOptimizeJson(
        string text,
        TokenOptimizationLevel level,
        int limit,
        out string result)
    {
        result = "";
        var trimmed = text.Trim();
        if (trimmed.Length < 2
            || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var projected = ProjectJson(document.RootElement, level, depth: 0);
            result = JsonSerializer.Serialize(projected);
            if (result.Length > limit) result = BuildExcerpt(result, limit);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? ProjectJson(
        JsonElement element,
        TokenOptimizationLevel level,
        int depth)
    {
        if (depth >= 16) return $"[nested JSON {OmittedText}]";
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    properties[property.Name] = ProjectJson(property.Value, level, depth + 1);
                return properties;

            case JsonValueKind.Array:
                var source = element.EnumerateArray().ToList();
                var maximum = level switch
                {
                    TokenOptimizationLevel.MaxAccuracy => int.MaxValue,
                    TokenOptimizationLevel.Balanced => 12,
                    _ => 5
                };
                if (source.Count <= maximum)
                    return source.Select(item => ProjectJson(item, level, depth + 1)).ToList();
                var takeFromStart = Math.Max(1, maximum - 2);
                var array = source.Take(takeFromStart)
                    .Select(item => ProjectJson(item, level, depth + 1))
                    .ToList();
                array.Add($"[{source.Count - takeFromStart - 1} items {OmittedText}]");
                array.Add(ProjectJson(source[^1], level, depth + 1));
                return array;

            case JsonValueKind.String:
                var value = element.GetString() ?? "";
                var stringLimit = level switch
                {
                    TokenOptimizationLevel.MaxAccuracy => 48_000,
                    TokenOptimizationLevel.Balanced => 6_000,
                    _ => 2_000
                };
                return value.Length <= stringLimit
                    ? value
                    : BuildExcerpt(value, stringLimit);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.Clone();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    private static string BuildExcerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit < 160) return text[..Math.Max(0, limit)];

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length < 4) return ClipHeadTail(text, limit);

        var selected = new SortedSet<int>();
        AddRange(selected, 0, Math.Min(3, lines.Length));
        AddRange(selected, Math.Max(0, lines.Length - 3), lines.Length);
        for (var index = 0; index < lines.Length; index++)
            if (ContainsImportant(lines[index])) selected.Add(index);

        var builder = new StringBuilder();
        var previous = -1;
        foreach (var index in selected)
        {
            if (previous >= 0 && index > previous + 1)
                builder.AppendLine($"[... {index - previous - 1} lines {OmittedText} ...]");
            builder.AppendLine(lines[index]);
            previous = index;
        }
        var excerpt = builder.ToString().TrimEnd();
        return excerpt.Length <= limit ? excerpt : ClipHeadTail(excerpt, limit);
    }

    private static string ClipHeadTail(string text, int limit)
    {
        var marker = $"\n... [{Math.Max(0, text.Length - limit)} characters {OmittedText}] ...\n";
        var available = Math.Max(0, limit - marker.Length);
        var head = available * 2 / 3;
        var tail = available - head;
        return text[..head] + marker + text[^tail..];
    }

    private static void AddRange(SortedSet<int> selected, int start, int end)
    {
        for (var index = start; index < end; index++) selected.Add(index);
    }

    private static int HistoryLimit(TokenOptimizationLevel level, bool recent)
        => (level, recent) switch
        {
            (TokenOptimizationLevel.MaxAccuracy, _) => 32_000,
            (TokenOptimizationLevel.Balanced, true) => 8_000,
            (TokenOptimizationLevel.Balanced, false) => 1_800,
            (TokenOptimizationLevel.MaxSavings, true) => 3_500,
            _ => 800
        };

    private static int LastUserIndex(IReadOnlyList<ChatLine> history)
    {
        for (var index = history.Count - 1; index >= 0; index--)
            if (string.Equals(history[index].Role, "user", StringComparison.OrdinalIgnoreCase))
                return index;
        return history.Count - 1;
    }

    private static bool LooksMachineGenerated(string text)
    {
        if (text.Length < 500) return false;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return true;
        if (text.Contains("stdout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stderr", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exit code", StringComparison.OrdinalIgnoreCase))
            return true;
        return text.Count(character => character == '\n') >= 12;
    }

    private static bool LooksLikeProgress(string line)
        => line.Contains('%')
           || line.Contains("progress", StringComparison.OrdinalIgnoreCase)
           || line.Contains("downloading", StringComparison.OrdinalIgnoreCase)
           || line.Contains("building", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsImportant(string text)
        => ImportantMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsLowSignal(string? text)
    {
        var normalized = (text ?? "").Trim().Trim('.', '!', '?').ToLowerInvariant();
        return normalized is "" or "ok" or "okay" or "thanks" or "thank you" or "cool"
            or "sounds good" or "got it" or "yes" or "no" or "sure";
    }

    private static HashSet<string> Terms(string? text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return terms;
        var token = new StringBuilder();
        void Flush()
        {
            if (token.Length < 2) { token.Clear(); return; }
            var value = token.ToString().ToLowerInvariant();
            token.Clear();
            if (!StopWords.Contains(value)) terms.Add(value);
        }
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-') token.Append(character);
            else Flush();
        }
        Flush();
        return terms;
    }

    private static ChatLine CopyWithText(ChatLine source, string text)
        => new()
        {
            Id = source.Id,
            Role = source.Role,
            Text = text,
            ReplyToLineId = source.ReplyToLineId,
            WidgetPrompt = source.WidgetPrompt,
            SenderHandle = source.SenderHandle,
            Attachments = source.Attachments.ToList(),
            Via = source.Via,
            AddressedToAgent = source.AddressedToAgent,
            Status = source.Status,
            Reasoning = source.Reasoning,
            ModelId = source.ModelId,
            Internal = source.Internal,
            At = source.At
        };
}
