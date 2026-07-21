using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;
using Contact = Mesh.App.Domain.Contact;

namespace Mesh.App.Services;

internal sealed record ProfileSyncProjection(
    IReadOnlyDictionary<string, DeviceSyncCircle> Circles,
    IReadOnlyDictionary<string, DeviceSyncContact> Contacts);

internal static class ProfileSyncState
{
    public static ProfileSyncProjection Snapshot(MeshProfile profile)
    {
        var circles = profile.Circles
            .Where(circle => !string.IsNullOrWhiteSpace(circle.Name))
            .GroupBy(circle => CircleEntityId(circle.Name), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var circle = group.Last();
                    return new DeviceSyncCircle(circle.Name.Trim(), circle.RequireApproval);
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
        return new ProfileSyncProjection(circles, contacts);
    }

    public static DeviceSyncContact NormalizeContact(
        DeviceSyncContact contact,
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
        DeviceSyncContact contact,
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

    public static DeviceSyncContact ProjectContact(
        Contact contact,
        IEnumerable<string> activeCircleNames)
        => NormalizeContact(new DeviceSyncContact(
            contact.Handle,
            contact.DisplayName,
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

    public static string LegacyVersion(
        string sourceDeviceId,
        string kind,
        string entityId,
        string stablePayload)
    {
        var operationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourceDeviceId + "\0" + kind + "\0" + entityId + "\0" + stablePayload)))
            .ToLowerInvariant();
        return DeviceSyncVersion.Create(DateTimeOffset.UnixEpoch, "legacy", operationId);
    }

    public static bool IsCircleAvailable(
        bool exists,
        string? acceptedUpsertVersion,
        string? deleteTombstoneVersion)
        => exists
           && (deleteTombstoneVersion is null
               || acceptedUpsertVersion is not null
               && DeviceSyncVersion.IsNewer(acceptedUpsertVersion, deleteTombstoneVersion));

    public static IReadOnlyList<DeviceSyncOperation> OrderForApplication(
        IEnumerable<DeviceSyncOperation> operations)
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
            visibility.StartsWith("shared:", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                visibility,
                SystemCircles.PublicVisibility,
                StringComparison.Ordinal)
            && CircleEntityId(visibility["shared:".Length..]) == entityId);
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

    public static bool ContactEquals(DeviceSyncContact left, DeviceSyncContact right)
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

    private static int DependencyOrder(DeviceSyncOperation operation)
        => operation.Kind switch
        {
            DeviceSyncKinds.CircleUpsert when HasRenameLineage(operation.Payload) => 0,
            DeviceSyncKinds.CircleUpsert => 1,
            DeviceSyncKinds.CircleDelete => 1,
            DeviceSyncKinds.TopicUpsert => 1,
            DeviceSyncKinds.ConversationUpsert => 1,
            DeviceSyncKinds.ContactUpsert => 2,
            DeviceSyncKinds.ContactDelete => 2,
            DeviceSyncKinds.TopicLineUpsert => 2,
            DeviceSyncKinds.ConversationLineUpsert => 2,
            DeviceSyncKinds.TopicClear => 3,
            DeviceSyncKinds.ConversationClear => 3,
            DeviceSyncKinds.TopicDelete => 4,
            DeviceSyncKinds.ConversationDelete => 4,
            _ => 2
        };

    private static bool HasRenameLineage(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceSyncCircle>(
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
        {
            if (string.Equals(
                    visibility,
                    SystemCircles.PublicVisibility,
                    StringComparison.Ordinal))
                return visibility;
            const string prefix = "shared:";
            if (!visibility.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || CircleEntityId(visibility[prefix.Length..]) != circleEntityId)
                return visibility;
            return replacement is null ? "private" : prefix + replacement;
        }

        foreach (var item in profile.Knowledge) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Skills) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Widgets) item.Visibility = Rewrite(item.Visibility);
        foreach (var item in profile.Sources)
        {
            item.Visibility = Rewrite(item.Visibility);
            foreach (var folder in item.Folders) folder.Visibility = Rewrite(folder.Visibility);
            foreach (var path in item.DrivePaths) path.Visibility = Rewrite(path.Visibility);
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
