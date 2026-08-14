using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

public enum AgentRole
{
    Owner,
    Guest,
    Service
}

public sealed record BuiltInPolicy(string Id, string Title, string Content, int Priority);

public sealed record BuiltInContentDiagnostics(
    string ContentVersion,
    string CatalogHash,
    int PolicyCount,
    int KnowledgeCount,
    int SkillCount,
    IReadOnlyList<string> LoadFailures);

public interface IBuiltInContentProvider
{
    IReadOnlyList<BuiltInPolicy> GetPolicies(AgentRole role);
    IReadOnlyList<KnowledgeItem> GetKnowledge(AgentRole role);
    IReadOnlyList<Skill> GetSkills(AgentRole role);

    KnowledgeItem? LoadKnowledge(string id);
    Skill? LoadSkill(string id);
}

public sealed class BuiltInContentException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class BuiltInContentProvider : IBuiltInContentProvider
{
    public const string IndexPackagePath = "BuiltIns/builtins.index.json";

    private static readonly IReadOnlyDictionary<AgentRole, string> RolePolicyIds =
        new Dictionary<AgentRole, string>
        {
            [AgentRole.Owner] = "builtin:policy:owner",
            [AgentRole.Guest] = "builtin:policy:guest",
            [AgentRole.Service] = "builtin:policy:service"
        };

    private static readonly HashSet<string> ValidTypes = new(StringComparer.Ordinal)
    {
        "policy", "knowledge", "skill"
    };
    private static readonly HashSet<string> ValidRoles = new(StringComparer.Ordinal) { "owner", "guest", "service" };

    private readonly Func<string, Task<Stream>> openPackageFile;
    private readonly Action<string>? reportDiagnostic;
    private readonly Lazy<CatalogState> catalog;

    public BuiltInContentProvider(
        Func<string, Task<Stream>> openPackageFile,
        Action<string>? reportDiagnostic = null)
    {
        this.openPackageFile = openPackageFile ?? throw new ArgumentNullException(nameof(openPackageFile));
        this.reportDiagnostic = reportDiagnostic;
        catalog = new Lazy<CatalogState>(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public BuiltInContentDiagnostics Diagnostics => catalog.Value.Diagnostics;

    public IReadOnlyList<BuiltInPolicy> GetPolicies(AgentRole role)
    {
        var rolePolicyId = RolePolicyIds[role];
        return catalog.Value.Policies
            .Where(item => item.Roles.Contains(role))
            .OrderBy(item => string.Equals(item.Value.Id, "builtin:policy:core", StringComparison.Ordinal)
                ? 0
                : string.Equals(item.Value.Id, rolePolicyId, StringComparison.Ordinal) ? 1 : 2)
            .ThenByDescending(item => item.Value.Priority)
            .ThenBy(item => item.Value.Id, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
    }

    public IReadOnlyList<KnowledgeItem> GetKnowledge(AgentRole role)
        => catalog.Value.Knowledge.Values
            .Where(item => item.Roles.Contains(role))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => CloneKnowledge(item.Summary))
            .ToArray();

    public IReadOnlyList<Skill> GetSkills(AgentRole role)
        => catalog.Value.Skills.Values
            .Where(item => item.Roles.Contains(role))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => CloneSkill(item.Summary))
            .ToArray();

    public KnowledgeItem? LoadKnowledge(string id)
        => catalog.Value.Knowledge.TryGetValue(id, out var item)
            ? CloneKnowledge(item.Value)
            : null;

    public Skill? LoadSkill(string id)
        => catalog.Value.Skills.TryGetValue(id, out var item)
            ? CloneSkill(item.Value)
            : null;

    private CatalogState LoadCatalog()
    {
        try
        {
            return LoadCatalogAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (BuiltInContentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or FormatException or DecoderFallbackException)
        {
            throw Fail("catalog could not be loaded", ex);
        }
    }

    private async Task<CatalogState> LoadCatalogAsync()
    {
        byte[] indexBytes;
        try
        {
            await using var indexStream = await openPackageFile(IndexPackagePath).ConfigureAwait(false);
            indexBytes = await ReadLimitedAsync(indexStream, BuiltInContentFormat.MaximumIndexBytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissing(ex))
        {
            throw Fail("required catalog index is missing", ex);
        }

        var indexText = BuiltInContentFormat.DecodeUtf8(indexBytes);
        var index = JsonSerializer.Deserialize<BuiltInCatalog>(indexText, BuiltInContentFormat.JsonOptions())
                    ?? throw Fail("catalog index is empty");
        ValidateIndex(index);

        var policies = new List<LoadedPolicy>();
        var knowledge = new Dictionary<string, LoadedKnowledge>(StringComparer.Ordinal);
        var skills = new Dictionary<string, LoadedSkill>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var entry in index.Items)
        {
            var packagePath = PackagePath(entry.Path);
            byte[] bytes;
            try
            {
                await using var stream = await openPackageFile(packagePath).ConfigureAwait(false);
                bytes = await ReadLimitedAsync(stream, BuiltInContentFormat.MaximumPackagedItemBytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsMissing(ex) && !string.Equals(entry.Type, "policy", StringComparison.Ordinal))
            {
                var failure = $"id={entry.Id}; reason=missing";
                failures.Add(failure);
                reportDiagnostic?.Invoke("optional item unavailable: " + failure);
                continue;
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                throw Fail($"required policy is missing: id={entry.Id}", ex);
            }

            var text = BuiltInContentFormat.DecodeUtf8(bytes);
            var contentBytes = BuiltInContentFormat.CanonicalUtf8(text);
            if (contentBytes.Length != entry.SizeBytes)
                throw Fail($"packaged content size mismatch: id={entry.Id}");
            var hash = BuiltInContentFormat.FileHash(contentBytes);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Fail($"packaged content hash mismatch: id={entry.Id}");

            var document = BuiltInContentFormat.ParseMarkdown(text);
            ValidateMetadata(entry, document);
            var roles = entry.Roles.Select(ParseRole).ToHashSet();

            switch (entry.Type)
            {
                case "policy":
                    policies.Add(new LoadedPolicy(
                        new BuiltInPolicy(entry.Id, entry.Title!, document.Body, entry.Priority!.Value),
                        roles));
                    break;
                case "knowledge":
                    var knowledgeValue = new KnowledgeItem
                    {
                        Id = entry.Id,
                        Title = entry.Title!,
                        Content = document.Body,
                        ContentByteCount = Encoding.UTF8.GetByteCount(document.Body),
                        Visibility = "private",
                        Source = KnowledgeSource.Manual,
                        UpdatedAt = DateTimeOffset.UnixEpoch
                    };
                    var knowledgeSummary = CloneKnowledge(knowledgeValue);
                    knowledgeSummary.Content = entry.Description + SummarySuffix("Keywords", entry.Keywords);
                    knowledge.Add(entry.Id, new LoadedKnowledge(entry.Id, knowledgeValue, knowledgeSummary, roles));
                    break;
                case "skill":
                    var instructions = BuiltInContentFormat.ExtractSkillInstructions(document.Body);
                    var skillValue = new Skill
                    {
                        Id = entry.Id,
                        Name = entry.Name!,
                        Description = entry.Description!,
                        Instructions = instructions,
                        ContentByteCount = Encoding.UTF8.GetByteCount(instructions),
                        Visibility = "private",
                        Enabled = true
                    };
                    var skillSummary = CloneSkill(skillValue);
                    skillSummary.Instructions = string.Join(", ", entry.Triggers);
                    skills.Add(entry.Id, new LoadedSkill(entry.Id, skillValue, skillSummary, roles));
                    break;
                default:
                    throw Fail($"unsupported catalog type: id={entry.Id}; type={entry.Type}");
            }
        }

        ValidateRequiredPolicies(policies);
        var diagnostics = new BuiltInContentDiagnostics(
            index.ContentVersion,
            index.CatalogHash,
            policies.Count,
            knowledge.Count,
            skills.Count,
            failures.AsReadOnly());
        reportDiagnostic?.Invoke(
            $"loaded version={diagnostics.ContentVersion}; catalogHash={diagnostics.CatalogHash}; "
            + $"policies={diagnostics.PolicyCount}; knowledge={diagnostics.KnowledgeCount}; "
            + $"skills={diagnostics.SkillCount}; failures={diagnostics.LoadFailures.Count}");
        return new CatalogState(policies.AsReadOnly(), knowledge, skills, diagnostics);
    }

    private static void ValidateIndex(BuiltInCatalog index)
    {
        if (index.SchemaVersion != BuiltInContentFormat.SchemaVersion)
            throw new BuiltInContentException("Mesh internal content uses an unsupported catalog version.");
        if (index.Items is null)
            throw new BuiltInContentException("Mesh internal content catalog has no item collection.");
        if (index.Items.Count == 0)
            throw new BuiltInContentException("Mesh internal content catalog is empty.");
        if (index.Items.Count > 256)
            throw new BuiltInContentException("Mesh internal content catalog exceeds the item limit.");

        long totalBytes = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in index.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
                throw new BuiltInContentException("Mesh internal content catalog contains an item without an id.");
            if (string.IsNullOrWhiteSpace(item.Type) || !ValidTypes.Contains(item.Type))
                throw new BuiltInContentException($"Mesh internal content catalog contains an invalid type: id={item.Id}.");
            var idPrefix = $"builtin:{item.Type}:";
            if (!item.Id.StartsWith(idPrefix, StringComparison.Ordinal) || item.Id.Length == idPrefix.Length)
                throw new BuiltInContentException($"Mesh internal content catalog contains an invalid id: id={item.Id}.");
            if (string.IsNullOrWhiteSpace(item.Path))
                throw new BuiltInContentException($"Mesh internal content catalog contains an invalid path: id={item.Id}.");
            var normalizedPath = BuiltInContentFormat.NormalizePath(item.Path);
            _ = PackagePath(normalizedPath);
            if (!paths.Add(normalizedPath))
                throw new BuiltInContentException($"Mesh internal content catalog contains a duplicate path: {normalizedPath}.");
            if (!IsSha256(item.Sha256))
                throw new BuiltInContentException($"Mesh internal content catalog contains an invalid content hash: id={item.Id}.");
            if (item.SizeBytes <= 0 || item.SizeBytes > BuiltInContentFormat.MaximumItemBytes)
                throw new BuiltInContentException($"Mesh internal content catalog contains an invalid content size: id={item.Id}.");
            totalBytes += item.SizeBytes;
            if (item.Roles is null || item.Roles.Count == 0
                || item.Roles.Any(role => !ValidRoles.Contains(role))
                || item.Roles.Distinct(StringComparer.Ordinal).Count() != item.Roles.Count)
                throw new BuiltInContentException($"Mesh internal content catalog contains invalid roles: id={item.Id}.");
            if (item.Keywords is null || item.Triggers is null)
                throw new BuiltInContentException($"Mesh internal content catalog contains invalid selection metadata: id={item.Id}.");

            var validMetadata = item.Type switch
            {
                "policy" => !string.IsNullOrWhiteSpace(item.Title) && item.Priority is >= 0 and <= 1000,
                "knowledge" => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Description),
                "skill" => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Description),
                _ => false
            };
            if (!validMetadata)
                throw new BuiltInContentException($"Mesh internal content catalog contains incomplete metadata: id={item.Id}.");
        }

        if (totalBytes > BuiltInContentFormat.MaximumCatalogBytes)
            throw new BuiltInContentException("Mesh internal content catalog exceeds the content size limit.");
        if (index.Items.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new BuiltInContentException("Mesh internal content catalog contains duplicate ids.");
        if (!IsSha256(index.CatalogHash) || string.IsNullOrWhiteSpace(index.ContentVersion))
            throw new BuiltInContentException("Mesh internal content catalog identity is invalid.");
        var hash = BuiltInContentFormat.CatalogHash(index.Items);
        if (!string.Equals(hash, index.CatalogHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(BuiltInContentFormat.ContentVersion(hash), index.ContentVersion, StringComparison.Ordinal))
            throw new BuiltInContentException("Mesh internal content catalog hash is invalid.");
    }

    private static void ValidateMetadata(BuiltInCatalogEntry entry, BuiltInMarkdownDocument document)
    {
        EnsureEqual(entry.Id, BuiltInContentFormat.Required(document.FrontMatter, "id"), entry.Id, "id");
        EnsureEqual(entry.Type, BuiltInContentFormat.Required(document.FrontMatter, "type").ToLowerInvariant(), entry.Id, "type");
        var roles = BuiltInContentFormat.List(document.FrontMatter, "roles")
            .Select(role => role.ToLowerInvariant())
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();
        if (!entry.Roles.OrderBy(role => role, StringComparer.Ordinal).SequenceEqual(roles, StringComparer.Ordinal))
            throw new BuiltInContentException($"Mesh internal content metadata mismatch: id={entry.Id}; field=roles.");

        switch (entry.Type)
        {
            case "policy":
                EnsureEqual(entry.Title, BuiltInContentFormat.Required(document.FrontMatter, "title"), entry.Id, "title");
                if (entry.Priority != BuiltInContentFormat.RequiredInt(document.FrontMatter, "priority"))
                    throw new BuiltInContentException($"Mesh internal content metadata mismatch: id={entry.Id}; field=priority.");
                break;
            case "knowledge":
                EnsureEqual(entry.Title, BuiltInContentFormat.Required(document.FrontMatter, "title"), entry.Id, "title");
                EnsureEqual(entry.Description, BuiltInContentFormat.Required(document.FrontMatter, "description"), entry.Id, "description");
                EnsureList(entry.Keywords, BuiltInContentFormat.List(document.FrontMatter, "keywords"), entry.Id, "keywords");
                break;
            case "skill":
                EnsureEqual(entry.Name, BuiltInContentFormat.Required(document.FrontMatter, "name"), entry.Id, "name");
                EnsureEqual(entry.Description, BuiltInContentFormat.Required(document.FrontMatter, "description"), entry.Id, "description");
                EnsureList(entry.Triggers, BuiltInContentFormat.List(document.FrontMatter, "triggers"), entry.Id, "triggers");
                break;
        }
    }

    private static void ValidateRequiredPolicies(IReadOnlyList<LoadedPolicy> policies)
    {
        var core = policies.FirstOrDefault(item => string.Equals(
            item.Value.Id, "builtin:policy:core", StringComparison.Ordinal));
        if (core is null || !core.Roles.SetEquals(Enum.GetValues<AgentRole>()))
            throw new BuiltInContentException("Mesh required core policy is unavailable.");

        foreach (var required in RolePolicyIds)
        {
            var policy = policies.FirstOrDefault(item => string.Equals(item.Value.Id, required.Value, StringComparison.Ordinal));
            if (policy is null || !policy.Roles.SetEquals([required.Key]))
                throw new BuiltInContentException($"Mesh required {required.Key.ToString().ToLowerInvariant()} policy is unavailable.");
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private BuiltInContentException Fail(string detail, Exception? innerException = null)
    {
        reportDiagnostic?.Invoke("load failure: " + detail);
        return new BuiltInContentException(
            "Mesh internal content is unavailable or invalid. Reinstall or update Mesh.",
            innerException);
    }

    private static string PackagePath(string relativePath)
    {
        var normalized = BuiltInContentFormat.NormalizePath(relativePath);
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new BuiltInContentException("Mesh internal content catalog contains an invalid path.");
        return "BuiltIns/" + normalized;
    }

    private static AgentRole ParseRole(string role) => role switch
    {
        "owner" => AgentRole.Owner,
        "guest" => AgentRole.Guest,
        "service" => AgentRole.Service,
        _ => throw new BuiltInContentException($"Mesh internal content catalog contains an invalid role '{role}'.")
    };

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maximumBytes)
    {
        var buffer = new byte[81920];
        await using var output = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new BuiltInContentException("Mesh internal content exceeds its packaged size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static bool IsMissing(Exception exception)
        => exception is FileNotFoundException or DirectoryNotFoundException;

    private static string SummarySuffix(string label, IReadOnlyList<string> values)
        => values.Count == 0 ? "" : $"\n{label}: {string.Join(", ", values)}";

    private static void EnsureEqual(string? expected, string actual, string id, string field)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new BuiltInContentException($"Mesh internal content metadata mismatch: id={id}; field={field}.");
    }

    private static void EnsureList(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string id,
        string field)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new BuiltInContentException($"Mesh internal content metadata mismatch: id={id}; field={field}.");
    }

    private static KnowledgeItem CloneKnowledge(KnowledgeItem source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Content = source.Content,
        ContentByteCount = source.ContentByteCount,
        Visibility = source.Visibility,
        Source = source.Source,
        SourceRef = source.SourceRef,
        UpdatedAt = source.UpdatedAt
    };

    private static Skill CloneSkill(Skill source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Instructions = source.Instructions,
        ContentByteCount = source.ContentByteCount,
        Visibility = source.Visibility,
        Enabled = source.Enabled,
        SourceMarketplaceId = source.SourceMarketplaceId,
        SourceSkillId = source.SourceSkillId,
        Version = source.Version
    };

    private sealed record LoadedPolicy(BuiltInPolicy Value, HashSet<AgentRole> Roles);
    private sealed record LoadedKnowledge(
        string Id,
        KnowledgeItem Value,
        KnowledgeItem Summary,
        HashSet<AgentRole> Roles);
    private sealed record LoadedSkill(
        string Id,
        Skill Value,
        Skill Summary,
        HashSet<AgentRole> Roles);
    private sealed record CatalogState(
        IReadOnlyList<LoadedPolicy> Policies,
        IReadOnlyDictionary<string, LoadedKnowledge> Knowledge,
        IReadOnlyDictionary<string, LoadedSkill> Skills,
        BuiltInContentDiagnostics Diagnostics);
}
