using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Post-commit in-memory materialiser. Once an inbound replicated event has COMMITTED to the
/// actual domain tables, the very same envelope is replayed onto the live
/// <see cref="MeshProfile"/> so the running UI shows the change without a reload. It is a pure
/// function of (profile, envelope): no storage, no MAUI, no relay, so it is exercised directly by
/// the actual-behaviour tests.
///
/// It never emits: the caller sets the replication-projection suppression flag around this call so
/// a replicated change can never echo back out as a new local event.
/// </summary>
public static class ReplicationProfileMaterializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Applies a committed envelope to the in-memory profile. Returns true when live state actually
    /// changed, so the caller can raise exactly one change notification.
    /// </summary>
    public static bool Apply(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Kind switch
        {
            ReplicationOpKinds.Message => ApplyMessage(profile, envelope),
            ReplicationOpKinds.Conversation => ApplyConversation(profile, envelope),
            ReplicationOpKinds.Topic => ApplyTopic(profile, envelope),
            ReplicationOpKinds.Contact => ApplyContact(profile, envelope),
            ReplicationOpKinds.Circle => ApplyCircle(profile, envelope),
            ReplicationOpKinds.Memory => ApplyMemory(profile, envelope),
            // Assets, ask-user prompts, read watermarks and skill packages are not carried on the
            // in-memory profile; their views read the committed rows straight from the database.
            _ => false
        };
    }

    // -----------------------------------------------------------------------

    private static bool ApplyMessage(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        switch (envelope.Action)
        {
            case ReplicationPayloadCodec.DomainAction.AppendLine:
            case ReplicationPayloadCodec.DomainAction.Upsert:
            {
                var line = Parse<ChatLine>(envelope.BodyJson);
                if (line is null || string.IsNullOrWhiteSpace(line.Id)) return false;
                var conversation = EnsureConversation(profile, envelope.EntityId);
                if (conversation.Lines.Any(existing => string.Equals(existing.Id, line.Id, StringComparison.Ordinal)))
                    return false;
                conversation.Lines.Add(line);
                conversation.LastActivityAt = ActivityTimestamp.Advance(conversation.LastActivityAt, line.At);
                return true;
            }
            case ReplicationPayloadCodec.DomainAction.Delete:
                return RemoveOrClearConversation(profile, envelope);
            default:
                return false;
        }
    }

    private static bool ApplyConversation(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        if (envelope.Action == ReplicationPayloadCodec.DomainAction.Delete)
            return RemoveOrClearConversation(profile, envelope);

        var incoming = Parse<Conversation>(envelope.BodyJson);
        if (incoming is null) return false;
        var handle = string.IsNullOrWhiteSpace(incoming.Handle) ? envelope.EntityId : incoming.Handle;
        var conversation = EnsureConversation(profile, handle);
        conversation.CreatedAt ??= incoming.CreatedAt;
        conversation.IsPinned = incoming.IsPinned;
        conversation.GroupId = incoming.GroupId;
        conversation.GroupName = incoming.GroupName;
        conversation.GroupOwnerHandle = incoming.GroupOwnerHandle;
        conversation.GroupMembers = incoming.GroupMembers.ToList();
        conversation.GroupVersion = incoming.GroupVersion;
        conversation.ServiceId = incoming.ServiceId;
        conversation.ServiceName = incoming.ServiceName;
        conversation.ProviderHandle = incoming.ProviderHandle;
        if (incoming.LastActivityAt.HasValue)
            conversation.LastActivityAt = ActivityTimestamp.Advance(conversation.LastActivityAt, incoming.LastActivityAt.Value);
        foreach (var line in incoming.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Id)) continue;
            if (conversation.Lines.Any(existing => string.Equals(existing.Id, line.Id, StringComparison.Ordinal))) continue;
            conversation.Lines.Add(line);
        }
        return true;
    }

    private static bool RemoveOrClearConversation(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        var conversation = profile.Conversations.FirstOrDefault(c =>
            string.Equals(Normalize(c.Handle), Normalize(envelope.EntityId), StringComparison.Ordinal));
        if (conversation is null) return false;
        if (IsClear(envelope.BodyJson))
        {
            if (conversation.Lines.Count == 0) return false;
            conversation.Lines.Clear();
            return true;
        }
        profile.Conversations.Remove(conversation);
        return true;
    }

    private static Conversation EnsureConversation(MeshProfile profile, string handle)
    {
        var normalized = Normalize(handle);
        var existing = profile.Conversations.FirstOrDefault(c =>
            string.Equals(Normalize(c.Handle), normalized, StringComparison.Ordinal));
        if (existing is not null) return existing;
        var created = new Conversation { Handle = normalized, CreatedAt = DateTimeOffset.UtcNow };
        profile.Conversations.Add(created);
        return created;
    }

    // -----------------------------------------------------------------------

    private static bool ApplyTopic(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        switch (envelope.Action)
        {
            case ReplicationPayloadCodec.DomainAction.AppendLine:
            {
                var line = Parse<ChatLine>(envelope.BodyJson);
                if (line is null || string.IsNullOrWhiteSpace(line.Id)) return false;
                var thread = EnsureThread(profile, envelope.EntityId);
                if (thread.Lines.Any(existing => string.Equals(existing.Id, line.Id, StringComparison.Ordinal)))
                    return false;
                thread.Lines.Add(line);
                thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, line.At);
                return true;
            }
            case ReplicationPayloadCodec.DomainAction.Upsert:
            {
                var body = Parse<ReplicationDomainMaterializer.TopicBody>(envelope.BodyJson);
                if (body is null) return false;
                var thread = EnsureThread(profile, string.IsNullOrWhiteSpace(body.Id) ? envelope.EntityId : body.Id);
                thread.Title = body.Title ?? thread.Title;
                if (body.CreatedAt != default) thread.CreatedAt = body.CreatedAt;
                thread.IsPinned = body.IsPinned;
                if (body.ExecutionRunId is not null || thread.ExecutionRunId is null)
                {
                    thread.ExecutionDeviceId = body.ExecutionDeviceId;
                    thread.ExecutionDeviceName = body.ExecutionDeviceName;
                    thread.ExecutionDevicePlatform = body.ExecutionDevicePlatform;
                    thread.ExecutionAt = body.ExecutionAt;
                    thread.ExecutionRunId = body.ExecutionRunId;
                }
                if (body.LastActivityAt.HasValue)
                    thread.LastActivityAt = ActivityTimestamp.Advance(thread.LastActivityAt, body.LastActivityAt.Value);
                return true;
            }
            case ReplicationPayloadCodec.DomainAction.Delete:
            {
                var thread = profile.OwnThreads.FirstOrDefault(t =>
                    string.Equals(t.Id, envelope.EntityId, StringComparison.Ordinal));
                if (thread is null) return false;
                if (IsClear(envelope.BodyJson))
                {
                    if (thread.Lines.Count == 0) return false;
                    thread.Lines.Clear();
                    return true;
                }
                profile.OwnThreads.Remove(thread);
                return true;
            }
            default:
                return false;
        }
    }

    private static OwnThread EnsureThread(MeshProfile profile, string id)
    {
        var existing = profile.OwnThreads.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        if (existing is not null) return existing;
        var created = new OwnThread { Id = id, Title = "" };
        profile.OwnThreads.Add(created);
        return created;
    }

    // -----------------------------------------------------------------------

    private static bool ApplyContact(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        var normalized = Normalize(envelope.EntityId);
        var existing = profile.Contacts.FirstOrDefault(c =>
            string.Equals(Normalize(c.Handle), normalized, StringComparison.Ordinal));

        if (envelope.Action == ReplicationPayloadCodec.DomainAction.Delete)
        {
            if (existing is null) return false;
            profile.Contacts.Remove(existing);
            return true;
        }

        var projection = Parse<ContactProjection>(envelope.BodyJson);
        if (projection is null) return false;
        var merged = ProfileProjection.MergeContact(existing, projection, profile.Circles.Select(c => c.Name));
        if (existing is null) profile.Contacts.Add(merged);
        else
        {
            existing.Handle = merged.Handle;
            existing.DisplayName = merged.DisplayName;
            existing.Circles = merged.Circles.ToList();
            existing.Allowed = merged.Allowed;
            existing.SigningKeys = merged.SigningKeys.ToList();
            existing.KeyChanged = merged.KeyChanged;
            existing.Muted = merged.Muted;
            existing.Blocked = merged.Blocked;
        }
        return true;
    }

    private static bool ApplyCircle(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        if (envelope.Action == ReplicationPayloadCodec.DomainAction.Delete)
        {
            var doomed = profile.Circles
                .Where(c => string.Equals(ProfileProjection.CircleEntityId(c.Name), envelope.EntityId, StringComparison.Ordinal))
                .ToList();
            if (doomed.Count == 0) return false;
            foreach (var circle in doomed)
            {
                profile.Circles.Remove(circle);
                foreach (var contact in profile.Contacts)
                    contact.Circles.RemoveAll(name =>
                        string.Equals(ProfileProjection.CircleEntityId(name), envelope.EntityId, StringComparison.Ordinal));
            }
            return true;
        }

        var projection = Parse<CircleProjection>(envelope.BodyJson);
        if (projection is null || string.IsNullOrWhiteSpace(projection.Name)) return false;

        var rename = projection.Renames?.FirstOrDefault();
        if (rename is not null && !string.IsNullOrWhiteSpace(rename.PreviousName))
        {
            var previousId = ProfileProjection.CircleEntityId(rename.PreviousName);
            profile.Circles.RemoveAll(c =>
                string.Equals(ProfileProjection.CircleEntityId(c.Name), previousId, StringComparison.Ordinal));
            foreach (var contact in profile.Contacts)
                for (var i = 0; i < contact.Circles.Count; i++)
                    if (string.Equals(ProfileProjection.CircleEntityId(contact.Circles[i]), previousId, StringComparison.Ordinal))
                        contact.Circles[i] = projection.Name;
        }

        var target = profile.Circles.FirstOrDefault(c =>
            string.Equals(ProfileProjection.CircleEntityId(c.Name), envelope.EntityId, StringComparison.Ordinal));
        if (target is null)
            profile.Circles.Add(new Circle { Name = projection.Name, RequireApproval = projection.RequireApproval });
        else
        {
            target.Name = projection.Name;
            target.RequireApproval = projection.RequireApproval;
        }
        foreach (var contact in profile.Contacts)
            contact.Circles = contact.Circles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    // -----------------------------------------------------------------------

    private static bool ApplyMemory(MeshProfile profile, ReplicationPayloadCodec.DomainEnvelope envelope)
    {
        var existing = profile.Memories.FirstOrDefault(m =>
            string.Equals(m.Id, envelope.EntityId, StringComparison.Ordinal));

        if (envelope.Action == ReplicationPayloadCodec.DomainAction.Delete)
        {
            if (existing is null) return false;
            profile.Memories.Remove(existing);
            return true;
        }

        var projection = Parse<MemoryProjection>(envelope.BodyJson);
        if (projection is null) return false;
        MemoryItem memory;
        try { memory = MemoryPolicy.FromSync(projection); }
        catch (ArgumentException) { return false; }
        if (existing is null) profile.Memories.Add(memory);
        else MemoryPolicy.CopyShared(memory, existing);
        return true;
    }

    // -----------------------------------------------------------------------

    private static bool IsClear(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("clear", out var clear)
                   && clear.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static T? Parse<T>(string? bodyJson) where T : class
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return null;
        try { return JsonSerializer.Deserialize<T>(bodyJson, Json); }
        catch (JsonException) { return null; }
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();
}
