using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mesh.Shared;

namespace Mesh.BuiltIns.Compiler;

public sealed class BuiltInCompilationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class BuiltInContentCompiler
{
    private static readonly HashSet<string> ValidRoles = new(StringComparer.Ordinal)
    {
        "owner", "guest", "service"
    };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredPolicies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["builtin:policy:core"] = ["owner", "guest", "service"],
            ["builtin:policy:owner"] = ["owner"],
            ["builtin:policy:guest"] = ["guest"],
            ["builtin:policy:service"] = ["service"]
        };

    private static readonly Regex ValidId = new(
        "^builtin:(policy|knowledge|skill):[a-z0-9][a-z0-9-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptFence = new(
        "```(?:javascript|js|typescript|ts|html)(?:\\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static BuiltInCatalog Compile(string rootDirectory, string indexPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("A BuiltIns directory is required.", nameof(rootDirectory));
        if (string.IsNullOrWhiteSpace(indexPath))
            throw new ArgumentException("An index output path is required.", nameof(indexPath));

        var root = Path.GetFullPath(rootDirectory);
        var output = Path.GetFullPath(indexPath);
        if (!Directory.Exists(root))
            throw new BuiltInCompilationException([$"Built-ins directory does not exist: {root}"]);

        var errors = new List<string>();
        var previous = ReadPreviousCatalog(output, errors);
        var entries = new List<BuiltInCatalogEntry>();
        var totalBytes = 0L;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(file);
            if (string.Equals(fullPath, output, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = BuiltInContentFormat.NormalizePath(Path.GetRelativePath(root, fullPath));
            if (!string.Equals(Path.GetExtension(file), ".md", StringComparison.Ordinal))
            {
                errors.Add($"{relative}: only UTF-8 Markdown files are allowed.");
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{relative}: could not be read ({ex.Message}).");
                continue;
            }

            string text;
            try
            {
                text = BuiltInContentFormat.DecodeUtf8(bytes);
            }
            catch (DecoderFallbackException)
            {
                errors.Add($"{relative}: file is not valid UTF-8.");
                continue;
            }

            var contentBytes = BuiltInContentFormat.CanonicalUtf8(text);
            totalBytes += contentBytes.Length;
            if (contentBytes.Length > BuiltInContentFormat.MaximumItemBytes)
            {
                errors.Add($"{relative}: {contentBytes.Length} bytes exceeds the {BuiltInContentFormat.MaximumItemBytes}-byte item limit.");
                continue;
            }

            BuiltInMarkdownDocument document;
            try
            {
                document = BuiltInContentFormat.ParseMarkdown(text);
            }
            catch (FormatException ex)
            {
                errors.Add($"{relative}: {ex.Message}");
                continue;
            }

            var entry = ValidateFile(relative, contentBytes, document, errors);
            if (entry is not null) entries.Add(entry);
        }

        if (totalBytes > BuiltInContentFormat.MaximumCatalogBytes)
            errors.Add($"Built-in Markdown totals {totalBytes} bytes, exceeding the {BuiltInContentFormat.MaximumCatalogBytes}-byte catalog limit.");

        foreach (var duplicate in entries.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"Duplicate built-in id '{duplicate.Key}' appears in: {string.Join(", ", duplicate.Select(item => item.Path))}.");

        ValidateRequiredPolicies(entries, errors);
        ValidateImmutableIds(previous, entries, errors);
        if (errors.Count > 0) throw new BuiltInCompilationException(errors);

        entries = entries.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
        var hash = BuiltInContentFormat.CatalogHash(entries);
        var catalog = new BuiltInCatalog
        {
            SchemaVersion = BuiltInContentFormat.SchemaVersion,
            ContentVersion = BuiltInContentFormat.ContentVersion(hash),
            CatalogHash = hash,
            Items = entries
        };
        WriteIndex(output, catalog);
        return catalog;
    }

    private static BuiltInCatalogEntry? ValidateFile(
        string relative,
        byte[] bytes,
        BuiltInMarkdownDocument document,
        List<string> errors)
    {
        var startErrorCount = errors.Count;
        var expectedType = ExpectedType(relative, errors);
        string id = "", declaredType = "";
        List<string> roles = new();
        try
        {
            id = BuiltInContentFormat.Required(document.FrontMatter, "id");
            declaredType = BuiltInContentFormat.Required(document.FrontMatter, "type").ToLowerInvariant();
            roles = BuiltInContentFormat.List(document.FrontMatter, "roles")
                .Select(role => role.ToLowerInvariant())
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToList();
        }
        catch (FormatException ex)
        {
            errors.Add($"{relative}: {ex.Message}");
        }

        if (id.Length > 0)
        {
            if (!id.StartsWith("builtin:", StringComparison.Ordinal))
                errors.Add($"{relative}: id must start with 'builtin:'.");
            if (!ValidId.IsMatch(id))
                errors.Add($"{relative}: id must use lowercase builtin:<type>:<stable-name> syntax.");
        }
        if (expectedType is not null && declaredType.Length > 0 && !string.Equals(expectedType, declaredType, StringComparison.Ordinal))
            errors.Add($"{relative}: declared type '{declaredType}' does not match the {expectedType} source directory.");
        if (declaredType.Length > 0 && id.Length > 0 && !id.StartsWith($"builtin:{declaredType}:", StringComparison.Ordinal))
            errors.Add($"{relative}: id namespace must match type '{declaredType}'.");
        if (roles.Count == 0)
            errors.Add($"{relative}: roles must contain owner, guest, or service.");
        foreach (var role in roles.Where(role => !ValidRoles.Contains(role)))
            errors.Add($"{relative}: invalid role '{role}'.");
        if (document.Body.Length == 0)
            errors.Add($"{relative}: Markdown body is required.");
        ValidateTextContent(
            relative,
            string.Join('\n', document.FrontMatter.Select(field => field.Key + ":" + field.Value).Append(document.Body)),
            errors);

        string? title = null, name = null, description = null;
        List<string> keywords = new(), triggers = new();
        int? priority = null;
        if (expectedType is not null)
        {
            ValidateKnownFields(relative, expectedType, document.FrontMatter.Keys, errors);
            try
            {
                switch (expectedType)
                {
                    case "policy":
                        title = BuiltInContentFormat.Required(document.FrontMatter, "title");
                        priority = BuiltInContentFormat.RequiredInt(document.FrontMatter, "priority");
                        if (priority is < 0 or > 1000)
                            errors.Add($"{relative}: priority must be between 0 and 1000.");
                        break;
                    case "knowledge":
                        title = BuiltInContentFormat.Required(document.FrontMatter, "title");
                        description = BuiltInContentFormat.Required(document.FrontMatter, "description");
                        keywords = BuiltInContentFormat.List(document.FrontMatter, "keywords");
                        break;
                    case "skill":
                        name = BuiltInContentFormat.Required(document.FrontMatter, "name");
                        description = BuiltInContentFormat.Required(document.FrontMatter, "description");
                        triggers = BuiltInContentFormat.List(document.FrontMatter, "triggers");
                        _ = BuiltInContentFormat.ExtractSkillInstructions(document.Body);
                        break;
                }
            }
            catch (FormatException ex)
            {
                errors.Add($"{relative}: {ex.Message}");
            }
        }

        if (errors.Count != startErrorCount || expectedType is null) return null;
        return new BuiltInCatalogEntry
        {
            Id = id,
            Type = expectedType,
            Path = relative,
            Sha256 = BuiltInContentFormat.FileHash(bytes),
            SizeBytes = bytes.Length,
            Roles = roles,
            Title = title,
            Name = name,
            Description = description,
            Keywords = keywords,
            Triggers = triggers,
            Priority = priority
        };
    }

    private static string? ExpectedType(string relative, List<string> errors)
    {
        var segments = relative.Split('/');
        if (segments.Length == 2 && string.Equals(segments[0], "Policies", StringComparison.Ordinal))
            return "policy";
        if (segments.Length == 2 && string.Equals(segments[0], "Knowledge", StringComparison.Ordinal))
            return "knowledge";
        if (segments.Length == 3
            && string.Equals(segments[0], "Skills", StringComparison.Ordinal)
            && string.Equals(segments[2], "SKILL.md", StringComparison.Ordinal))
            return "skill";
        errors.Add($"{relative}: expected Policies/<name>.md, Knowledge/<name>.md, or Skills/<name>/SKILL.md.");
        return null;
    }

    private static void ValidateKnownFields(
        string relative,
        string type,
        IEnumerable<string> fields,
        List<string> errors)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "type", "roles" };
        foreach (var field in type switch
                 {
                     "policy" => new[] { "title", "priority" },
                     "knowledge" => new[] { "title", "description", "keywords" },
                     _ => new[] { "name", "description", "triggers" }
                 })
            allowed.Add(field);
        foreach (var field in fields.Where(field => !allowed.Contains(field)))
            errors.Add($"{relative}: unsupported frontmatter field '{field}'.");
    }

    private static void ValidateTextContent(string relative, string body, List<string> errors)
    {
        if (body.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            errors.Add($"{relative}: binary/control characters are prohibited.");
        if (body.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || body.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
            || body.Contains("data:text/html", StringComparison.OrdinalIgnoreCase)
            || ScriptFence.IsMatch(body))
            errors.Add($"{relative}: executable script or HTML content is prohibited.");
    }

    private static void ValidateRequiredPolicies(List<BuiltInCatalogEntry> entries, List<string> errors)
    {
        var byId = entries.GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var requirement in RequiredPolicies)
        {
            if (!byId.TryGetValue(requirement.Key, out var policy))
            {
                errors.Add($"Required policy '{requirement.Key}' is missing.");
                continue;
            }
            if (!string.Equals(policy.Type, "policy", StringComparison.Ordinal))
                errors.Add($"Required policy '{requirement.Key}' has the wrong type.");
            if (!policy.Roles.OrderBy(role => role, StringComparer.Ordinal)
                    .SequenceEqual(requirement.Value.OrderBy(role => role, StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add($"Required policy '{requirement.Key}' must have roles: {string.Join(',', requirement.Value)}.");
        }
    }

    private static void ValidateImmutableIds(
        BuiltInCatalog? previous,
        IReadOnlyList<BuiltInCatalogEntry> entries,
        List<string> errors)
    {
        if (previous is null) return;
        var currentByPath = entries.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var oldItem in previous.Items)
        {
            if (currentByPath.TryGetValue(oldItem.Path, out var current)
                && !string.Equals(oldItem.Id, current.Id, StringComparison.Ordinal))
                errors.Add($"{oldItem.Path}: id is immutable; keep '{oldItem.Id}' instead of '{current.Id}'.");
        }
    }

    private static BuiltInCatalog? ReadPreviousCatalog(string path, List<string> errors)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BuiltInCatalog>(File.ReadAllText(path), BuiltInContentFormat.JsonOptions())
                   ?? throw new JsonException("Index was empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            errors.Add($"Existing built-in index could not be read: {ex.Message}");
            return null;
        }
    }

    private static void WriteIndex(string path, BuiltInCatalog catalog)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(catalog, BuiltInContentFormat.JsonOptions(writeIndented: true)) + "\n";
        var bytes = BuiltInContentFormat.CanonicalUtf8(json);
        if (bytes.Length > BuiltInContentFormat.MaximumIndexBytes)
            throw new BuiltInCompilationException(
                [$"Generated catalog is {bytes.Length} bytes, exceeding the {BuiltInContentFormat.MaximumIndexBytes}-byte index limit."]);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        var temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
