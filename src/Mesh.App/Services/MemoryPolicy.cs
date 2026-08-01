using System.Text;
using System.Text.RegularExpressions;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

internal sealed record MemoryProjection(
    string Id,
    string Title,
    string Content,
    string Category,
    string Origin,
    double Importance,
    double Confidence,
    double Stability,
    int ReinforcementCount,
    string? SourceThreadId,
    string? SourceLineId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastReinforcedAt);

internal static class MemoryPolicy
{
    public const int MaxTitleChars = 160;
    public const int MaxContentChars = 4000;
    public const int MaxEvidenceChars = 1000;
    public const int DefaultRecallCount = 8;
    public const int MaximumRecallCount = 20;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "has", "have",
        "i", "in", "is", "it", "me", "my", "of", "on", "or", "that", "the", "this", "to",
        "was", "were", "will", "with", "you", "your"
    };

    private static readonly string[] CredentialTerms =
    [
        "password", "passphrase", "api key", "access token", "refresh token", "bearer token",
        "private key", "secret key", "client secret", "credit card", "card number", "security code",
        "cvv", "social security", "recovery code", "one-time code", "otp code", "verification code",
        "2fa code", "mfa code", "authorization code", "pin number", "routing number", "account number",
        "seed phrase", "backup code", "passport number", "driver's license number",
        "drivers license number", "national id", "government id", "tax id"
    ];

    private static readonly string[] SensitiveTerms =
    [
        "medical condition", "diagnosed with", "mental health", "religion", "religious belief",
        "christian", "muslim", "jewish", "hindu", "buddhist", "atheist",
        "political affiliation", "political party", "political views", "democrat", "republican",
        "sexual orientation", "gay", "lesbian", "bisexual", "gender identity", "transgender",
        "pregnant", "disability", "home address", "exact address", "bank account", "biometric",
        "diabetes", "cancer", "hiv", "aids", "bipolar", "depression", "anxiety disorder",
        "autism", "adhd", "ptsd", "schizophrenia", "epilepsy"
    ];

    private static readonly Regex SecretTokenPattern = new(
        @"(?:-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b|\b(?:sk[-_][A-Za-z0-9_-]{10,}|ghp_[A-Za-z0-9]{10,}|github_pat_[A-Za-z0-9_]{10,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{12,})\b|\b\d{3}-\d{2}-\d{4}\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex CardCandidatePattern = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex StreetAddressPattern = new(
        @"\b\d{1,6}\s+[A-Za-z0-9.'-]+(?:\s+[A-Za-z0-9.'-]+){0,4}\s+(?:street|st|avenue|ave|road|rd|boulevard|blvd|lane|ln|drive|dr|court|ct|way|parkway|pkwy)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ExplicitRememberPattern = new(
        @"(?:^|[.!?\r\n]\s*)(?:(?:(?:please|always)\s+)?remember\b|(?:please\s+)?(?:don't|do not)\s+forget\b|(?:can|could|would)\s+you\s+(?:please\s+)?remember\b|i\s+want\s+you\s+to\s+remember\b|(?:please\s+)?(?:keep\s+in\s+mind|save\s+this|store\s+this|make\s+a\s+note|memorize)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ForgetPattern = new(
        @"(?:^|[.!?\r\n]\s*)(?:(?:please\s+)?forget\b|(?:can|could|would)\s+you\s+(?:please\s+)?(?:forget\b|(?:delete|remove)\s+(?:that|this|my|the)\s+memor(?:y|ies)\b)|i\s+want\s+you\s+to\s+(?:forget|delete|remove)\b|(?:please\s+)?(?:delete|remove)\s+(?:that|this|my|the)\s+memor(?:y|ies)\b|(?:please\s+)?(?:don't|do not)\s+remember\b|(?:please\s+)?stop\s+remembering\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static MemoryItem Normalize(MemoryItem source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var now = DateTimeOffset.UtcNow;
        var item = Clone(source);
        item.Id = item.Id.Trim();
        if (!TopicRunProtocol.IsValidIdentifier(item.Id))
            throw new ArgumentException("Memory has an invalid identifier.", nameof(source));

        item.Title = CollapseWhitespace(item.Title);
        item.Content = NormalizeContent(item.Content);
        if (item.Title.Length == 0) item.Title = CreateTitle(item.Content);
        if (item.Title.Length == 0 || item.Title.Length > MaxTitleChars)
            throw new ArgumentException($"Memory title must be between 1 and {MaxTitleChars} characters.", nameof(source));
        if (item.Content.Length == 0 || item.Content.Length > MaxContentChars)
            throw new ArgumentException($"Memory content must be between 1 and {MaxContentChars} characters.", nameof(source));

        item.Category = NormalizeCategory(item.Category);
        item.Origin = NormalizeOrigin(item.Origin);
        var protectedText = item.Title + "\n" + item.Content;
        if (ContainsCredentialLikeData(protectedText))
            throw new ArgumentException("Memory cannot contain credentials, payment data, government identifiers, or recovery material.", nameof(source));
        if (item.Origin == MemoryOrigins.Inferred && ContainsSensitivePersonalData(protectedText))
            throw new ArgumentException("Sensitive personal information requires an explicit owner request.", nameof(source));
        item.Importance = ClampFinite(item.Importance, 0.65);
        item.Confidence = ClampFinite(item.Confidence, 0.8);
        item.Stability = ClampFinite(item.Stability, 0.75);
        item.ReinforcementCount = Math.Clamp(item.ReinforcementCount, 1, 100_000);
        item.RecallCount = Math.Clamp(item.RecallCount, 0, 1_000_000);
        item.SourceThreadId = NormalizeOptionalIdentifier(item.SourceThreadId);
        item.SourceLineId = NormalizeOptionalIdentifier(item.SourceLineId);
        item.CreatedAt = item.CreatedAt == default ? now : item.CreatedAt;
        item.UpdatedAt = item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt;
        item.LastReinforcedAt = item.LastReinforcedAt == default ? item.UpdatedAt : item.LastReinforcedAt;
        if (item.UpdatedAt < item.CreatedAt) item.UpdatedAt = item.CreatedAt;
        if (item.LastReinforcedAt < item.CreatedAt) item.LastReinforcedAt = item.CreatedAt;
        if (item.LastRecalledAt == default) item.LastRecalledAt = null;
        return item;
    }

    public static bool IsValid(MemoryProjection memory)
    {
        if (memory is null
            || !TopicRunProtocol.IsValidIdentifier(memory.Id)
            || string.IsNullOrWhiteSpace(memory.Title)
            || memory.Title.Trim().Length > MaxTitleChars
            || string.IsNullOrWhiteSpace(memory.Content)
            || memory.Content.Trim().Length > MaxContentChars
            || !MemoryCategories.All.Contains(memory.Category, StringComparer.Ordinal)
            || memory.Origin is not (MemoryOrigins.Manual or MemoryOrigins.Explicit or MemoryOrigins.Inferred)
            || !IsUnit(memory.Importance)
            || !IsUnit(memory.Confidence)
            || !IsUnit(memory.Stability)
            || memory.ReinforcementCount is < 1 or > 100_000
            || memory.CreatedAt == default
            || memory.UpdatedAt < memory.CreatedAt
            || memory.LastReinforcedAt < memory.CreatedAt)
            return false;
        var text = memory.Title + "\n" + memory.Content;
        if (ContainsCredentialLikeData(text)
            || memory.Origin == MemoryOrigins.Inferred && ContainsSensitivePersonalData(text))
            return false;
        return IsOptionalIdentifier(memory.SourceThreadId)
               && IsOptionalIdentifier(memory.SourceLineId);
    }

    public static MemoryProjection ToSync(MemoryItem memory)
    {
        var item = Normalize(memory);
        return new MemoryProjection(
            item.Id,
            item.Title,
            item.Content,
            item.Category,
            item.Origin,
            item.Importance,
            item.Confidence,
            item.Stability,
            item.ReinforcementCount,
            item.SourceThreadId,
            item.SourceLineId,
            item.CreatedAt,
            item.UpdatedAt,
            item.LastReinforcedAt);
    }

    public static MemoryItem FromSync(MemoryProjection memory)
    {
        if (!IsValid(memory)) throw new ArgumentException("Synchronized memory is invalid.", nameof(memory));
        return Normalize(new MemoryItem
        {
            Id = memory.Id,
            Title = memory.Title,
            Content = memory.Content,
            Category = memory.Category,
            Origin = memory.Origin,
            Importance = memory.Importance,
            Confidence = memory.Confidence,
            Stability = memory.Stability,
            ReinforcementCount = memory.ReinforcementCount,
            SourceThreadId = memory.SourceThreadId,
            SourceLineId = memory.SourceLineId,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            LastReinforcedAt = memory.LastReinforcedAt
        });
    }

    public static MemoryItem Clone(MemoryItem memory)
        => new()
        {
            Id = memory.Id,
            Title = memory.Title,
            Content = memory.Content,
            Category = memory.Category,
            Origin = memory.Origin,
            Importance = memory.Importance,
            Confidence = memory.Confidence,
            Stability = memory.Stability,
            ReinforcementCount = memory.ReinforcementCount,
            SourceThreadId = memory.SourceThreadId,
            SourceLineId = memory.SourceLineId,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            LastReinforcedAt = memory.LastReinforcedAt,
            RecallCount = memory.RecallCount,
            LastRecalledAt = memory.LastRecalledAt
        };

    public static void CopyShared(MemoryItem source, MemoryItem destination)
    {
        destination.Title = source.Title;
        destination.Content = source.Content;
        destination.Category = source.Category;
        destination.Origin = source.Origin;
        destination.Importance = source.Importance;
        destination.Confidence = source.Confidence;
        destination.Stability = source.Stability;
        destination.ReinforcementCount = source.ReinforcementCount;
        destination.SourceThreadId = source.SourceThreadId;
        destination.SourceLineId = source.SourceLineId;
        destination.CreatedAt = source.CreatedAt;
        destination.UpdatedAt = source.UpdatedAt;
        destination.LastReinforcedAt = source.LastReinforcedAt;
    }

    public static bool SharedEquals(MemoryItem left, MemoryItem right)
        => left.Id == right.Id
           && left.Title == right.Title
           && left.Content == right.Content
           && left.Category == right.Category
           && left.Origin == right.Origin
           && left.Importance.Equals(right.Importance)
           && left.Confidence.Equals(right.Confidence)
           && left.Stability.Equals(right.Stability)
           && left.ReinforcementCount == right.ReinforcementCount
           && left.SourceThreadId == right.SourceThreadId
           && left.SourceLineId == right.SourceLineId
           && left.CreatedAt == right.CreatedAt
           && left.UpdatedAt == right.UpdatedAt
           && left.LastReinforcedAt == right.LastReinforcedAt;

    public static IReadOnlyList<MemoryItem> SelectForPrompt(
        IEnumerable<MemoryItem> memories,
        string? query,
        int maxResults = DefaultRecallCount,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var limit = Math.Clamp(maxResults, 1, MaximumRecallCount);
        var ranked = memories
            .Where(memory => !string.IsNullOrWhiteSpace(memory.Content))
            .Select(memory => new Ranked(
                memory,
                GeneralSalience(memory, at),
                Relevance(memory, query)))
            .ToList();
        if (ranked.Count == 0) return Array.Empty<MemoryItem>();

        var selected = new Dictionary<string, Ranked>(StringComparer.Ordinal);
        foreach (var item in ranked
                     .Where(item => item.Salience >= 0.58 && !IsSensitive(item.Memory))
                     .OrderByDescending(item => item.Salience)
                     .ThenByDescending(item => item.Memory.LastReinforcedAt)
                     .Take(Math.Min(2, limit)))
            selected[item.Memory.Id] = item;

        foreach (var item in ranked
                     .Where(item => item.Relevance > 0)
                     .OrderByDescending(item => item.Combined)
                     .ThenByDescending(item => item.Memory.LastReinforcedAt))
        {
            selected[item.Memory.Id] = item;
            if (selected.Count >= limit) break;
        }

        if (selected.Count == 0)
            foreach (var item in ranked
                         .Where(item => !IsSensitive(item.Memory))
                         .OrderByDescending(item => item.Salience)
                         .Take(limit))
                selected[item.Memory.Id] = item;

        return selected.Values
            .OrderByDescending(item => item.Combined)
            .ThenByDescending(item => item.Salience)
            .Select(item => Clone(item.Memory))
            .Take(limit)
            .ToList();
    }

    public static MemoryItem? FindSimilar(
        IEnumerable<MemoryItem> memories,
        string title,
        string content,
        string category,
        double threshold = 0.58)
        => memories
            .Select(memory => (memory, score: Similarity(memory, title, content, category)))
            .Where(item => item.score >= threshold)
            .OrderByDescending(item => item.score)
            .ThenByDescending(item => item.memory.LastReinforcedAt)
            .Select(item => item.memory)
            .FirstOrDefault();

    public static double GeneralSalience(MemoryItem memory, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var ageDays = Math.Max(0, (at - memory.LastReinforcedAt).TotalDays);
        var decay = memory.Stability >= 0.9
            ? 1
            : Math.Exp(-ageDays / (180 + 900 * memory.Stability));
        var reinforcement = Math.Min(0.12, Math.Log2(Math.Max(1, memory.ReinforcementCount)) * 0.025);
        var recall = Math.Min(0.06, Math.Log2(Math.Max(1, memory.RecallCount + 1)) * 0.015);
        var origin = memory.Origin switch
        {
            MemoryOrigins.Explicit => 0.08,
            MemoryOrigins.Manual => 0.07,
            _ => 0
        };
        var baseScore = memory.Importance * 0.42
                        + memory.Confidence * 0.18
                        + memory.Stability * 0.18
                        + reinforcement
                        + recall
                        + origin;
        return Math.Clamp(baseScore * (0.82 + 0.18 * decay), 0, 1);
    }

    public static bool EvidenceAppearsIn(string ownerText, string evidence)
    {
        var normalizedEvidence = CollapseWhitespace(evidence);
        if (normalizedEvidence.Length is < 2 or > MaxEvidenceChars) return false;
        return CollapseWhitespace(ownerText).Contains(normalizedEvidence, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasExplicitRememberIntent(string text)
        => !string.IsNullOrWhiteSpace(text) && ExplicitRememberPattern.IsMatch(text);

    public static bool HasExplicitRememberIntentForEvidence(string ownerText, string evidence)
    {
        var normalizedOwner = CollapseWhitespace(
            (ownerText ?? "")
            .Replace("\r\n", ". ", StringComparison.Ordinal)
            .Replace('\r', '.')
            .Replace('\n', '.'));
        var normalizedEvidence = CollapseWhitespace(evidence);
        if (normalizedEvidence.Length is < 2 or > MaxEvidenceChars) return false;

        var searchAt = 0;
        while ((searchAt = normalizedOwner.IndexOf(
                   normalizedEvidence,
                   searchAt,
                   StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var start = searchAt;
            while (start > 0 && normalizedOwner[start - 1] is not ('.' or '!' or '?')) start--;
            var end = searchAt + normalizedEvidence.Length;
            while (end < normalizedOwner.Length && normalizedOwner[end] is not ('.' or '!' or '?')) end++;
            if (HasExplicitRememberIntent(normalizedOwner[start..end].Trim())) return true;
            searchAt++;
        }
        return false;
    }

    public static bool HasForgetIntent(string text)
        => !string.IsNullOrWhiteSpace(text) && ForgetPattern.IsMatch(text);

    public static bool ContainsCredentialLikeData(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ContainsAny(text, CredentialTerms) || SecretTokenPattern.IsMatch(text)) return true;
        foreach (Match match in CardCandidatePattern.Matches(text))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits)) return true;
        }
        return false;
    }

    public static bool ContainsSensitivePersonalData(string text)
        => ContainsWholeTerm(text, SensitiveTerms)
           || !string.IsNullOrWhiteSpace(text) && StreetAddressPattern.IsMatch(text);

    public static string CreateTitle(string content)
    {
        var normalized = CollapseWhitespace(content);
        if (normalized.Length == 0) return "";
        var end = normalized.IndexOfAny(['.', '!', '?', ';', ':']);
        var title = end is > 0 and < 80 ? normalized[..end] : normalized;
        if (title.Length > 72) title = title[..72].TrimEnd() + "...";
        return title;
    }

    public static string CategoryLabel(string category)
        => category switch
        {
            MemoryCategories.Preference => "Preference",
            MemoryCategories.PersonalFact => "Personal fact",
            MemoryCategories.Goal => "Goal",
            MemoryCategories.Workflow => "Workflow",
            MemoryCategories.Constraint => "Constraint",
            _ => "Memory"
        };

    public static string OriginLabel(string origin)
        => origin switch
        {
            MemoryOrigins.Manual => "Added or edited by you",
            MemoryOrigins.Explicit => "You asked Mesh to remember",
            _ => "Learned from a Me topic"
        };

    private static bool IsSensitive(MemoryItem memory)
        => ContainsSensitivePersonalData(memory.Title + "\n" + memory.Content);

    private static double Similarity(MemoryItem memory, string title, string content, string category)
    {
        var contentScore = Jaccard(Tokens(memory.Content), Tokens(content));
        var titleScore = Jaccard(Tokens(memory.Title), Tokens(title));
        var categoryScore = string.Equals(memory.Category, category, StringComparison.Ordinal) ? 0.12 : 0;
        return Math.Clamp(contentScore * 0.72 + titleScore * 0.16 + categoryScore, 0, 1);
    }

    private static double Relevance(MemoryItem memory, string? query)
    {
        var queryTokens = Tokens(query);
        if (queryTokens.Count == 0) return 0;
        var contentTokens = Tokens(memory.Title + " " + memory.Content + " " + memory.Category);
        if (contentTokens.Count == 0) return 0;
        var overlap = queryTokens.Count(token => contentTokens.Contains(token));
        if (overlap == 0) return 0;
        var coverage = (double)overlap / queryTokens.Count;
        var jaccard = (double)overlap / queryTokens.Union(contentTokens).Count();
        return Math.Clamp(coverage * 0.75 + jaccard * 0.25, 0, 1);
    }

    private static HashSet<string> Tokens(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return result;
        var token = new StringBuilder();
        void Flush()
        {
            if (token.Length < 2)
            {
                token.Clear();
                return;
            }
            var word = token.ToString().ToLowerInvariant();
            token.Clear();
            if (!StopWords.Contains(word)) result.Add(word);
        }

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) token.Append(ch);
            else Flush();
        }
        Flush();
        return result;
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count(right.Contains);
        return intersection == 0 ? 0 : (double)intersection / left.Union(right).Count();
    }

    private static string NormalizeContent(string? value)
        => (value ?? "").Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string CollapseWhitespace(string? value)
        => string.Join(' ', (value ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeCategory(string? value)
    {
        var category = (value ?? "").Trim().ToLowerInvariant();
        return MemoryCategories.All.Contains(category, StringComparer.Ordinal)
            ? category
            : MemoryCategories.PersonalFact;
    }

    private static string NormalizeOrigin(string? value)
        => (value ?? "").Trim().ToLowerInvariant() switch
        {
            MemoryOrigins.Manual => MemoryOrigins.Manual,
            MemoryOrigins.Explicit => MemoryOrigins.Explicit,
            _ => MemoryOrigins.Inferred
        };

    private static string? NormalizeOptionalIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return TopicRunProtocol.IsValidIdentifier(normalized) ? normalized : null;
    }

    private static bool IsOptionalIdentifier(string? value)
        => value is null || TopicRunProtocol.IsValidIdentifier(value);

    private static bool IsUnit(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;

    private static double ClampFinite(double value, double fallback)
        => Math.Clamp(double.IsFinite(value) ? value : fallback, 0, 1);

    private static bool ContainsAny(string? text, IEnumerable<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsWholeTerm(string? text, IEnumerable<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var term in terms)
        {
            var start = 0;
            while ((start = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
                var end = start + term.Length;
                var after = end == text.Length || !char.IsLetterOrDigit(text[end]);
                if (before && after) return true;
                start++;
            }
        }
        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleNext = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var value = digits[index] - '0';
            if (doubleNext)
            {
                value *= 2;
                if (value > 9) value -= 9;
            }
            sum += value;
            doubleNext = !doubleNext;
        }
        return sum % 10 == 0;
    }

    private sealed record Ranked(MemoryItem Memory, double Salience, double Relevance)
    {
        public double Combined => Salience * 0.42 + Relevance * 0.58;
    }
}
