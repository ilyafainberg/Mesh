using System.Text;
using Mesh.App.Domain;

namespace Mesh.App.Services;

internal sealed record SelectedAgentContent(
    IReadOnlyList<KnowledgeItem> BuiltInKnowledge,
    IReadOnlyList<Skill> BuiltInSkills,
    IReadOnlyList<KnowledgeItem> UserKnowledge,
    IReadOnlyList<Skill> UserSkills,
    IReadOnlyList<string> OmittedUserKnowledgeNames,
    IReadOnlyList<string> OmittedUserSkillNames,
    int DroppedByBudget)
{
    public IReadOnlyList<Skill> AllSkills => BuiltInSkills.Concat(UserSkills).ToArray();
}

internal static class AgentPromptContentSelector
{
    private const int MetadataSelectionCap = 200;

    public static async Task<SelectedAgentContent> SelectAsync(
        IBuiltInContentProvider builtIns,
        AgentRole role,
        IReadOnlyList<KnowledgeItem> userKnowledge,
        IReadOnlyList<Skill> userSkills,
        Func<string, CancellationToken, Task<KnowledgeItem?>> loadUserKnowledge,
        Func<string, CancellationToken, Task<Skill?>> loadUserSkill,
        string query,
        TokenOptimizationLevel optimization,
        bool compact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(userKnowledge);
        ArgumentNullException.ThrowIfNull(userSkills);
        ArgumentNullException.ThrowIfNull(loadUserKnowledge);
        ArgumentNullException.ThrowIfNull(loadUserSkill);

        var builtInKnowledge = builtIns.GetKnowledge(role)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var builtInSkills = builtIns.GetSkills(role)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var userKnowledgePool = userKnowledge
            .Where(item => !item.Id.StartsWith("builtin:", StringComparison.Ordinal))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(item => item.UpdatedAt)
            .Take(MetadataSelectionCap)
            .ToList();
        var userSkillPool = userSkills
            .Where(item => !item.Id.StartsWith("builtin:", StringComparison.Ordinal))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MetadataSelectionCap)
            .ToList();
        var userKnowledgeById = userKnowledgePool.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var userSkillsById = userSkillPool.ToDictionary(item => item.Id, StringComparer.Ordinal);

        var candidates = new List<AgentContentSummary>(
            builtInKnowledge.Count + builtInSkills.Count + userKnowledgePool.Count + userSkillPool.Count);
        foreach (var item in builtInKnowledge.Values)
            candidates.Add(KnowledgeSummary(item, optimization, compact, isBuiltIn: true));
        foreach (var item in builtInSkills.Values)
            candidates.Add(SkillSummary(item, optimization, compact, isBuiltIn: true));
        foreach (var item in userKnowledgePool)
            candidates.Add(KnowledgeSummary(item, optimization, compact, isBuiltIn: false));
        foreach (var item in userSkillPool)
            candidates.Add(SkillSummary(item, optimization, compact, isBuiltIn: false));

        var selection = TokenOptimizer.SelectAgentContent(candidates, query, optimization, compact);
        var selectedBuiltInKnowledge = new List<KnowledgeItem>();
        var selectedBuiltInSkills = new List<Skill>();
        var selectedUserKnowledge = new List<KnowledgeItem>();
        var selectedUserSkills = new List<Skill>();
        var maximumCount = TokenOptimizer.AgentContentCountBudget(optimization);
        var maximumBytes = TokenOptimizer.AgentContentByteBudget(optimization, compact);
        var loadedCount = 0;
        long usedBytes = 0;
        var droppedByBudget = 0;

        foreach (var candidate in selection.Included)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Kind == AgentContentKind.Knowledge)
            {
                KnowledgeItem? item;
                if (candidate.IsBuiltIn)
                    item = builtIns.LoadKnowledge(candidate.Id);
                else if (userKnowledgeById.TryGetValue(candidate.Id, out var summary))
                    item = await LoadKnowledgeAsync(summary, loadUserKnowledge, cancellationToken).ConfigureAwait(false);
                else
                    item = null;
                if (item is null) continue;
                if (!TryReserve(KnowledgeBodyBytes(item, optimization, compact), maximumCount, maximumBytes,
                        ref loadedCount, ref usedBytes))
                {
                    droppedByBudget++;
                    continue;
                }
                if (candidate.IsBuiltIn) selectedBuiltInKnowledge.Add(item);
                else selectedUserKnowledge.Add(item);
                continue;
            }

            Skill? skill;
            if (candidate.IsBuiltIn)
                skill = builtIns.LoadSkill(candidate.Id);
            else if (userSkillsById.TryGetValue(candidate.Id, out var summary))
                skill = await LoadSkillAsync(summary, loadUserSkill, cancellationToken).ConfigureAwait(false);
            else
                skill = null;
            if (skill is null) continue;
            if (!TryReserve(SkillBodyBytes(skill, optimization, compact), maximumCount, maximumBytes,
                    ref loadedCount, ref usedBytes))
            {
                droppedByBudget++;
                continue;
            }
            if (candidate.IsBuiltIn) selectedBuiltInSkills.Add(skill);
            else selectedUserSkills.Add(skill);
        }

        return new SelectedAgentContent(
            selectedBuiltInKnowledge,
            selectedBuiltInSkills,
            selectedUserKnowledge,
            selectedUserSkills,
            selection.Omitted
                .Where(item => !item.IsBuiltIn && item.Kind == AgentContentKind.Knowledge)
                .Select(item => item.Title)
                .ToList(),
            selection.Omitted
                .Where(item => !item.IsBuiltIn && item.Kind == AgentContentKind.Skill)
                .Select(item => item.Title)
                .ToList(),
            droppedByBudget);
    }
    public static string ProjectKnowledgeContent(
        string content,
        TokenOptimizationLevel optimization,
        bool compact)
    {
        var limit = TokenOptimizer.KnowledgeContentLimit(optimization, compact);
        return TokenOptimizer.Normalize(optimization) == TokenOptimizationLevel.Disabled
            ? Truncate(content, limit)
            : TokenOptimizer.FitContextText(content, limit);
    }

    public static string ProjectSkillInstructions(
        string instructions,
        TokenOptimizationLevel optimization,
        bool compact)
    {
        var instructionLimit = TokenOptimizer.SkillInstructionLimit(optimization);
        var projected = instructionLimit == int.MaxValue
            ? instructions
            : TokenOptimizer.FitContextText(instructions, instructionLimit);
        return FitUtf8(projected, TokenOptimizer.AgentContentByteBudget(optimization, compact));
    }

    private static AgentContentSummary KnowledgeSummary(
        KnowledgeItem item,
        TokenOptimizationLevel optimization,
        bool compact,
        bool isBuiltIn)
        => new(
            item.Id,
            AgentContentKind.Knowledge,
            item.Title,
            item.Content,
            EstimateKnowledgeBodyBytes(item, optimization, compact),
            item.UpdatedAt.UtcTicks,
            isBuiltIn);

    private static AgentContentSummary SkillSummary(
        Skill item,
        TokenOptimizationLevel optimization,
        bool compact,
        bool isBuiltIn)
        => new(
            item.Id,
            AgentContentKind.Skill,
            item.Name,
            item.Description + "\n" + item.Instructions,
            EstimateSkillBodyBytes(item, optimization, compact),
            0,
            isBuiltIn);

    private static async Task<KnowledgeItem?> LoadKnowledgeAsync(
        KnowledgeItem summary,
        Func<string, CancellationToken, Task<KnowledgeItem?>> loader,
        CancellationToken cancellationToken)
        => !string.IsNullOrEmpty(summary.Content)
            ? summary
            : await loader(summary.Id, cancellationToken).ConfigureAwait(false);

    private static async Task<Skill?> LoadSkillAsync(
        Skill summary,
        Func<string, CancellationToken, Task<Skill?>> loader,
        CancellationToken cancellationToken)
        => !string.IsNullOrEmpty(summary.Instructions)
            ? summary
            : await loader(summary.Id, cancellationToken).ConfigureAwait(false);

    private static bool TryReserve(
        int bodyBytes,
        int maximumCount,
        int maximumBytes,
        ref int loadedCount,
        ref long usedBytes)
    {
        if (loadedCount >= maximumCount) return false;
        if (maximumBytes != int.MaxValue && usedBytes + bodyBytes > maximumBytes) return false;
        loadedCount++;
        usedBytes += bodyBytes;
        return true;
    }

    private static int EstimateKnowledgeBodyBytes(
        KnowledgeItem item,
        TokenOptimizationLevel optimization,
        bool compact)
        => SaturatingAdd(
            Encoding.UTF8.GetByteCount(item.Title),
            EstimateProjectedBodyBytes(
                item.ContentByteCount,
                item.Content,
                TokenOptimizer.KnowledgeContentLimit(optimization, compact)));

    private static int EstimateSkillBodyBytes(
        Skill item,
        TokenOptimizationLevel optimization,
        bool compact)
        => SaturatingAdd(
            Encoding.UTF8.GetByteCount(item.Name),
            Encoding.UTF8.GetByteCount(item.Description),
            EstimateProjectedBodyBytes(
                item.ContentByteCount,
                item.Instructions,
                TokenOptimizer.SkillInstructionLimit(optimization)));

    private static int KnowledgeBodyBytes(
        KnowledgeItem item,
        TokenOptimizationLevel optimization,
        bool compact)
        => SaturatingAdd(
            Encoding.UTF8.GetByteCount(item.Title),
            Encoding.UTF8.GetByteCount(ProjectKnowledgeContent(item.Content, optimization, compact)));

    private static int SkillBodyBytes(
        Skill item,
        TokenOptimizationLevel optimization,
        bool compact)
        => SaturatingAdd(
            Encoding.UTF8.GetByteCount(item.Name),
            Encoding.UTF8.GetByteCount(item.Description),
            Encoding.UTF8.GetByteCount(ProjectSkillInstructions(item.Instructions, optimization, compact)));
    private static int EstimateProjectedBodyBytes(long knownBytes, string fallback, int characterLimit)
    {
        var bytes = knownBytes > 0 ? knownBytes : Encoding.UTF8.GetByteCount(fallback);
        if (characterLimit != int.MaxValue)
            bytes = Math.Min(bytes, (long)characterLimit * 4);
        return bytes >= int.MaxValue ? int.MaxValue : (int)bytes;
    }

    private static int SaturatingAdd(params int[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total += Math.Max(0, value);
            if (total >= int.MaxValue) return int.MaxValue;
        }
        return (int)total;
    }

    private static string FitUtf8(string text, int maximumBytes)
    {
        if (maximumBytes == int.MaxValue || Encoding.UTF8.GetByteCount(text) <= maximumBytes) return text;
        var characterLimit = Math.Min(text.Length, maximumBytes);
        while (characterLimit > 0)
        {
            var candidate = TokenOptimizer.FitContextText(text, characterLimit);
            var bytes = Encoding.UTF8.GetByteCount(candidate);
            if (bytes <= maximumBytes) return candidate;
            characterLimit = Math.Max(0, characterLimit * maximumBytes / bytes - 1);
        }
        return "";
    }

    private static string Truncate(string value, int maximumCharacters)
        => string.IsNullOrEmpty(value) || value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + ".";
}