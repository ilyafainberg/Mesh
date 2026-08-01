using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;
using Contact = Mesh.App.Domain.Contact;

namespace Mesh.App.Services;

internal sealed record ContactProjection(
    string Handle,
    string DisplayName,
    IReadOnlyList<string> Circles,
    bool Allowed,
    IReadOnlyList<string> SigningKeys,
    bool KeyChanged,
    bool Muted,
    bool Blocked);

internal sealed record CircleProjection(
    string Name,
    bool RequireApproval,
    IReadOnlyList<CircleRenameProjection>? Renames = null);

internal sealed record CircleRenameProjection(string PreviousName, string DeleteVersion);

internal sealed record ReplicationOperation(string OperationId, string SourceDeviceId, string Kind, string EntityId, string Version, string Payload = "");

internal static class DomainProjectionKinds
{
    public const string ContactUpsert = "contact.upsert";
    public const string ContactDelete = "contact.delete";
    public const string CircleUpsert = "circle.upsert";
    public const string CircleDelete = "circle.delete";
    public const string MemoryUpsert = "memory.upsert";
    public const string MemoryDelete = "memory.delete";
    public const string TopicUpsert = "topic.upsert";
    public const string TopicDelete = "topic.delete";
    public const string TopicLineUpsert = "topic.line.upsert";
    public const string TopicLineDelete = "topic.line.delete";
    public const string TopicClear = "topic.clear";
    public const string ConversationUpsert = "conversation.upsert";
    public const string ConversationDelete = "conversation.delete";
    public const string ConversationLineUpsert = "conversation.line.upsert";
    public const string ConversationClear = "conversation.clear";
}

internal static class DomainProjectionEntityIds
{
    public static string TopicLine(string threadId, string lineId) => $"{threadId}{lineId}";
}

internal static class ProjectionVersion
{
    public static string Create(DateTimeOffset at, string source, string operationId)
        => at.UtcTicks.ToString("D19") + "|" + source + "|" + operationId;

    public static bool IsNewer(string? candidate, string? current)
        => string.Compare(candidate ?? "", current ?? "", StringComparison.Ordinal) > 0;
}

internal sealed record ProfileProjectionState(
    IReadOnlyDictionary<string, CircleProjection> Circles,
    IReadOnlyDictionary<string, ContactProjection> Contacts);

internal static class ProfileProjection
{
    public static ProfileProjectionState Snapshot(MeshProfile profile)
    {
        var circles = profile.Circles
            .Where(circle => !string.IsNullOrWhiteSpace(circle.Name))
            .GroupBy(circle => CircleEntityId(circle.Name), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var circle = group.Last();
                    return new CircleProjection(circle.Name.Trim(), circle.RequireApproval);
                },
                StringComparer.Ordinal);
        var contacts = profile.Contacts
            .Where(contact => !string.IsNullOrWhiteSpace(contact.Handle))
            .GroupBy(contact => NormalizeHandle(contact.Handle), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var contact = group.Last();
                    return ProjectContact(contact, contact.Circles);
                },
                StringComparer.Ordinal);
        return new ProfileProjectionState(circles, contacts);
    }

    public static ContactProjection NormalizeContact(
        ContactProjection contact,
        IEnumerable<string> activeCircleNames)
    {
        var active = activeCircleNames
            .Select(CircleEntityId)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return contact with
        {
            Handle = NormalizeHandle(contact.Handle ?? ""),
            Circles = NormalizeDistinct(contact.Circles, StringComparer.OrdinalIgnoreCase)
                .Where(name => active.Contains(CircleEntityId(name)))
                .ToList(),
            SigningKeys = NormalizeDistinct(contact.SigningKeys, StringComparer.Ordinal)
        };
    }

    public static Contact MergeContact(
        Contact? existing,
        ContactProjection contact,
        IEnumerable<string> activeCircleNames)
    {
        var normalized = NormalizeContact(contact, activeCircleNames);
        return new Contact
        {
            Handle = normalized.Handle,
            DisplayName = normalized.DisplayName,
            Circles = normalized.Circles.ToList(),
            Allowed = normalized.Allowed,
            SigningKeys = normalized.SigningKeys.ToList(),
            KeyChanged = normalized.KeyChanged,
            TokensSpent = existing?.TokensSpent ?? 0,
            Muted = normalized.Muted,
            Blocked = normalized.Blocked
        };
    }

    public static ContactProjection ProjectContact(
        Contact contact,
        IEnumerable<string> activeCircleNames)
        => NormalizeContact(new ContactProjection(
            contact.Handle,
            contact.DisplayName ?? "",
            contact.Circles,
            contact.Allowed,
            contact.SigningKeys,
            contact.KeyChanged,
            contact.Muted,
            contact.Blocked), activeCircleNames);

    public static IReadOnlyList<string> ResolveGuestCircles(
        Contact? contact,
        IEnumerable<Circle> circles)
    {
        if (contact is null) return Array.Empty<string>();
        var active = circles
            .Where(circle => !string.IsNullOrWhiteSpace(circle.Name))
            .GroupBy(circle => circle.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Name.Trim(), StringComparer.OrdinalIgnoreCase);
        return contact.Circles
            .Where(name => !string.IsNullOrWhiteSpace(name) && active.ContainsKey(name.Trim()))
            .Select(name => active[name.Trim()])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string StableVersion(
        string sourceDeviceId,
        string kind,
        string entityId,
        string stablePayload)
    {
        var operationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourceDeviceId + "\0" + kind + "\0" + entityId + "\0" + stablePayload)))
            .ToLowerInvariant();
        return ProjectionVersion.Create(DateTimeOffset.UnixEpoch, "p9", operationId);
    }

    public static bool IsCircleAvailable(
        bool exists,
        string? acceptedUpsertVersion,
        string? deleteTombstoneVersion)
        => exists
           && (deleteTombstoneVersion is null
               || acceptedUpsertVersion is not null
               && ProjectionVersion.IsNewer(acceptedUpsertVersion, deleteTombstoneVersion));

    public static IReadOnlyList<ReplicationOperation> OrderForApplication(
        IEnumerable<ReplicationOperation> operations)
        => operations
            .Select((operation, index) => (operation, index))
            .OrderBy(item => DependencyOrder(item.operation))
            .ThenBy(item => item.operation.Version, StringComparer.Ordinal)
            .ThenBy(item => item.index)
            .Select(item => item.operation)
            .ToList();

    public static void RenameCircleReferences(MeshProfile profile, string oldName, string newName)
    {
        var oldId = CircleEntityId(oldName);
        var replacement = newName.Trim();
        if (oldId.Length == 0 || replacement.Length == 0) return;

        foreach (var contact in profile.Contacts)
        {
            contact.Circles = contact.Circles
               .Select(name => CircleEntityId(name) == oldId ? replacement : name)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();
        }
        RewriteVisibilities(profile, oldId, replacement);
    }

    public static void DeleteCircleReferences(MeshProfile profile, string name)
    {
        var entityId = CircleEntityId(name);
        if (entityId.Length == 0) return;

        foreach (var contact in profile.Contacts)
            contact.Circles.RemoveAll(circle => CircleEntityId(circle) == entityId);
        RewriteVisibilities(profile, entityId, null);
    }

    public static bool HasCircleReferences(MeshProfile profile, string name)
    {
        var entityId = CircleEntityId(name);
        if (entityId.Length == 0) return false;
        if (profile.Contacts.Any(contact =>
                contact.Circles.Any(circle => CircleEntityId(circle) == entityId)))
            return true;
        return Visibilities(profile).Any(visibility =>
            AudiencePolicy.ReferencesCircle(visibility, entityId));
    }

    public static List<Contact> CloneContacts(IEnumerable<Contact> contacts)
        => contacts.Select(contact => new Contact
        {
            Handle = contact.Handle,
            DisplayName = contact.DisplayName,
            Circles = contact.Circles.ToList(),
            Allowed = contact.Allowed,
            SigningKeys = contact.SigningKeys.ToList(),
            KeyChanged = contact.KeyChanged,
            TokensSpent = contact.TokensSpent,
            Muted = contact.Muted,
            Blocked = contact.Blocked
        }).ToList();

    public static List<Circle> CloneCircles(IEnumerable<Circle> circles)
        => circles.Select(circle => new Circle
        {
            Name = circle.Name,
            RequireApproval = circle.RequireApproval
        }).ToList();

    public static bool ContactEquals(ContactProjection left, ContactProjection right)
        => left.Handle == right.Handle
           && left.DisplayName == right.DisplayName
           && left.Circles.SequenceEqual(right.Circles, StringComparer.Ordinal)
           && left.Allowed == right.Allowed
           && left.SigningKeys.SequenceEqual(right.SigningKeys, StringComparer.Ordinal)
           && left.KeyChanged == right.KeyChanged
           && left.Muted == right.Muted
           && left.Blocked == right.Blocked;

    public static string CircleEntityId(string? name)
        => (name ?? "").Trim().ToLowerInvariant();

    private static int DependencyOrder(ReplicationOperation operation)
        => operation.Kind switch
        {
            DomainProjectionKinds.CircleUpsert when HasRenameLineage(operation.Payload) => 0,
            DomainProjectionKinds.CircleUpsert => 1,
            DomainProjectionKinds.CircleDelete => 1,
            DomainProjectionKinds.TopicUpsert => 1,
            DomainProjectionKinds.ConversationUpsert => 1,
            DomainProjectionKinds.MemoryUpsert => 1,
            DomainProjectionKinds.ContactUpsert => 2,
            DomainProjectionKinds.ContactDelete => 2,
            DomainProjectionKinds.TopicLineUpsert => 2,
            DomainProjectionKinds.ConversationLineUpsert => 2,
            DomainProjectionKinds.TopicLineDelete => 3,
            DomainProjectionKinds.TopicClear => 3,
            DomainProjectionKinds.ConversationClear => 3,
            DomainProjectionKinds.TopicDelete => 4,
            DomainProjectionKinds.ConversationDelete => 4,
            DomainProjectionKinds.MemoryDelete => 4,
            _ => 2
        };

    private static bool HasRenameLineage(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CircleProjection>(
                       payload,
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?.Renames?.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RewriteVisibilities(
        MeshProfile profile,
        string circleEntityId,
        string? replacement)
    {
        string Rewrite(string visibility)
            => replacement is null
                ? AudiencePolicy.RemoveCircle(visibility, circleEntityId)
                : AudiencePolicy.RenameCircle(visibility, circleEntityId, replacement);

        void RewriteGrants(List<FolderGrant> grants)
        {
            for (var i = grants.Count - 1; i >= 0; i--)
            {
                var grant = grants[i];
                if (!AudiencePolicy.ReferencesCircle(grant.Visibility, circleEntityId)) continue;
                var rewritten = Rewrite(grant.Visibility);
                if (replacement is null
                    && CapabilityAudience.Parse(rewritten).Mode == CapabilityAudienceMode.Private)
                    grants.RemoveAt(i);
                else
                    grant.Visibility = rewritten;
            }
        }

        foreach (var item in profile.Knowledge) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Skills) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Widgets) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Sources)
        {
            item.Visibility = Rewrite(item.Visibility);
            RewriteGrants(item.Folders);
            RewriteGrants(item.DrivePaths);
        }
        foreach (var item in profile.LocalTools.Values) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.McpServers.Values) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.CustomMcpServers) item.Visibility = Rewrite(item.Visibility);
    }

    private static IEnumerable<string> Visibilities(MeshProfile profile)
    {
        foreach (var item in profile.Knowledge) yield return item.Visibility;
        foreach (var item in profile.Skills) yield return item.Visibility;
        foreach (var item in profile.Widgets) yield return item.Visibility;
        foreach (var item in profile.Sources)
        {
            yield return item.Visibility;
            foreach (var folder in item.Folders) yield return folder.Visibility;
            foreach (var path in item.DrivePaths) yield return path.Visibility;
        }
        foreach (var item in profile.LocalTools.Values) yield return item.Visibility;
        foreach (var item in profile.McpServers.Values) yield return item.Visibility;
        foreach (var item in profile.CustomMcpServers) yield return item.Visibility;
    }

    private static IReadOnlyList<string> NormalizeDistinct(
        IEnumerable<string>? values,
        StringComparer comparer)
        => (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(comparer)
            .ToList();

    private static string NormalizeHandle(string handle)
        => handle.Trim().TrimStart('@').ToLowerInvariant();
}
