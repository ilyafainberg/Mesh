using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Transaction-composable writers over the <b>actual</b> Mesh domain tables (the same rows the
/// UI reads): conversations / chat_lines, own_threads / own_chat, memories, the bounded profile
/// blob that carries contacts and circles, assets / asset_content, ask_user_prompts, the read
/// watermark table and the skill-package staging tables.
///
/// Every method takes an explicit <see cref="SqliteConnection"/> plus <see cref="SqliteTransaction"/>
/// so a domain mutation can be committed in the very same transaction that appends the signed
/// replication event and its outbox references. That is what makes a local change and its
/// replication record atomic, and what lets an inbound replicated event materialise real domain
/// state (not just a generic convergence index) without a second, separately-failing write.
///
/// Writers are idempotent where the protocol requires it: appending a chat / topic line with an
/// id that already exists is a no-op, so an exact duplicate event never duplicates a line.
/// </summary>
public static class Protocol9DomainTables
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O");

    /// <summary>
    /// True when the connection carries the real Mesh domain schema. A pure convergence store (for
    /// example a bare replication log opened for protocol-level tests, or a database opened before
    /// the profile schema exists) has the replication tables but none of the domain tables; in that
    /// case the convergence record is the only durable materialisation and the actual-table write is
    /// skipped rather than failing the transaction.
    /// </summary>
    public static bool DomainSchemaPresent(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = Command(conn, tx,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'conversations';");
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    // -----------------------------------------------------------------------
    // conversations / chat_lines
    // -----------------------------------------------------------------------

    /// <summary>Creates the conversation row if absent (preserves an existing created_at).</summary>
    public static void EnsureConversation(
        SqliteConnection conn, SqliteTransaction tx, string handle, DateTimeOffset? createdAt = null)
    {
        using var cmd = Command(conn, tx, """
            INSERT OR IGNORE INTO conversations(handle, created_at, sort_order, last_activity_at)
            VALUES($h, $t, (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM conversations), $t);
            """);
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$t", Iso(createdAt ?? DateTimeOffset.UtcNow));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Advances a conversation's activity stamp monotonically.</summary>
    public static void AdvanceConversationActivity(
        SqliteConnection conn, SqliteTransaction tx, string handle, DateTimeOffset at)
    {
        using var cmd = Command(conn, tx, """
            UPDATE conversations
            SET last_activity_at = $at
            WHERE handle = $h
              AND (last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at));
            """);
        cmd.Parameters.AddWithValue("$at", Iso(at));
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when a conversation already holds a line with this stable id.</summary>
    public static bool ChatLineExists(
        SqliteConnection conn, SqliteTransaction tx, string handle, string lineId)
    {
        using var cmd = Command(conn, tx,
            "SELECT 1 FROM chat_lines WHERE handle = $h AND line_id = $lid LIMIT 1;");
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$lid", lineId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    /// <summary>
    /// Appends one line to a conversation's real history and advances its activity. Idempotent by
    /// (handle, line id): a replayed or duplicated event never produces a second row.
    /// </summary>
    public static bool AppendChatLine(
        SqliteConnection conn, SqliteTransaction tx, string handle, ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureConversation(conn, tx, handle, line.At);
        if (ChatLineExists(conn, tx, handle, line.Id)) return false;
        using (var cmd = Command(conn, tx, """
            INSERT INTO chat_lines(
                line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning, model_id)
            VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning, $modelId);
            """))
        {
            cmd.Parameters.AddWithValue("$lid", line.Id);
            cmd.Parameters.AddWithValue("$h", handle);
            cmd.Parameters.AddWithValue("$r", line.Role);
            cmd.Parameters.AddWithValue("$x", line.Text);
            cmd.Parameters.AddWithValue("$v", line.Via);
            cmd.Parameters.AddWithValue("$s", line.Status);
            cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
            cmd.Parameters.AddWithValue("$sender", (object?)line.SenderHandle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$internal", line.Internal ? 1 : 0);
            cmd.Parameters.AddWithValue("$reasoning", (object?)line.Reasoning ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        AdvanceConversationActivity(conn, tx, handle, line.At);
        return true;
    }

    /// <summary>Upserts persisted conversation metadata (never regresses the activity stamp).</summary>
    public static void UpsertConversationMetadata(
        SqliteConnection conn, SqliteTransaction tx, Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        EnsureConversation(conn, tx, conversation.Handle, conversation.CreatedAt);
        using var cmd = Command(conn, tx, """
            UPDATE conversations SET
                service_id = $sid,
                service_name = $sname,
                provider_handle = $provider,
                group_id = $gid,
                group_name = $gname,
                group_owner_handle = $owner,
                group_members_json = $members,
                group_version = $gversion,
                is_pinned = $pinned,
                last_activity_at = CASE
                    WHEN $activity IS NOT NULL
                         AND (last_activity_at IS NULL OR julianday($activity) > julianday(last_activity_at))
                    THEN $activity ELSE last_activity_at END
            WHERE handle = $h;
            """);
        cmd.Parameters.AddWithValue("$h", conversation.Handle);
        cmd.Parameters.AddWithValue("$sid", (object?)conversation.ServiceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sname", (object?)conversation.ServiceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", (object?)conversation.ProviderHandle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gid", (object?)conversation.GroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gname", (object?)conversation.GroupName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)conversation.GroupOwnerHandle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$members", conversation.GroupId is null
            ? (object)DBNull.Value
            : JsonSerializer.Serialize(conversation.GroupMembers, Json));
        cmd.Parameters.AddWithValue("$gversion", conversation.GroupVersion);
        cmd.Parameters.AddWithValue("$pinned", conversation.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$activity", conversation.LastActivityAt.HasValue
            ? Iso(conversation.LastActivityAt.Value)
            : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes every line of a conversation but keeps the thread row.</summary>
    public static void ClearConversation(SqliteConnection conn, SqliteTransaction tx, string handle)
    {
        using var cmd = Command(conn, tx, "DELETE FROM chat_lines WHERE handle = $h;");
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a conversation and all of its lines.</summary>
    public static void DeleteConversation(SqliteConnection conn, SqliteTransaction tx, string handle)
    {
        ClearConversation(conn, tx, handle);
        using var cmd = Command(conn, tx, "DELETE FROM conversations WHERE handle = $h;");
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // own_threads / own_chat ("Me" topics)
    // -----------------------------------------------------------------------

    /// <summary>Creates the topic row if absent.</summary>
    public static void EnsureOwnThread(
        SqliteConnection conn, SqliteTransaction tx, string id, string title, DateTimeOffset createdAt)
    {
        using var cmd = Command(conn, tx, """
            INSERT OR IGNORE INTO own_threads(id, title, created_at, sort_order, last_activity_at)
            VALUES($id, $title, $at, (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM own_threads), $at);
            """);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title ?? "");
        cmd.Parameters.AddWithValue("$at", Iso(createdAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Advances a topic's activity stamp monotonically.</summary>
    public static void AdvanceOwnThreadActivity(
        SqliteConnection conn, SqliteTransaction tx, string threadId, DateTimeOffset at)
    {
        using var cmd = Command(conn, tx, """
            UPDATE own_threads
            SET last_activity_at = $at
            WHERE id = $id
              AND (last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at));
            """);
        cmd.Parameters.AddWithValue("$at", Iso(at));
        cmd.Parameters.AddWithValue("$id", threadId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when a topic already holds a line with this stable id.</summary>
    public static bool OwnChatLineExists(
        SqliteConnection conn, SqliteTransaction tx, string threadId, string lineId)
    {
        using var cmd = Command(conn, tx,
            "SELECT 1 FROM own_chat WHERE thread_id = $tid AND line_id = $lid LIMIT 1;");
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.Parameters.AddWithValue("$lid", lineId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    /// <summary>Appends one line to a "Me" topic's real history. Idempotent by (thread, line id).</summary>
    public static bool AppendOwnChat(
        SqliteConnection conn, SqliteTransaction tx, string threadId, ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureOwnThread(conn, tx, threadId, "", line.At);
        if (OwnChatLineExists(conn, tx, threadId, line.Id)) return false;
        using (var cmd = Command(conn, tx, """
            INSERT OR IGNORE INTO own_chat(
                line_id, thread_id, role, text, reply_to_line_id, via, status, at,
                internal, reasoning, sender_handle, model_id)
            VALUES($lid, $tid, $r, $x, $replyTo, $v, $s, $a, $i, $rz, $sender, $modelId);
            """))
        {
            cmd.Parameters.AddWithValue("$lid", line.Id);
            cmd.Parameters.AddWithValue("$tid", threadId);
            cmd.Parameters.AddWithValue("$r", line.Role);
            cmd.Parameters.AddWithValue("$x", line.Text);
            cmd.Parameters.AddWithValue("$replyTo", (object?)line.ReplyToLineId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$v", line.Via);
            cmd.Parameters.AddWithValue("$s", line.Status);
            cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
            cmd.Parameters.AddWithValue("$i", line.Internal ? 1 : 0);
            cmd.Parameters.AddWithValue("$rz", (object?)line.Reasoning ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sender", (object?)line.SenderHandle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        AdvanceOwnThreadActivity(conn, tx, threadId, line.At);
        return true;
    }

    /// <summary>Upserts a topic's persisted metadata (title, pin, execution binding, activity).</summary>
    public static void UpsertOwnThreadMetadata(
        SqliteConnection conn, SqliteTransaction tx, OwnThread thread, int sortOrder)
    {
        ArgumentNullException.ThrowIfNull(thread);
        EnsureOwnThread(conn, tx, thread.Id, thread.Title, thread.CreatedAt);
        using var cmd = Command(conn, tx, """
            UPDATE own_threads SET
                title = $title,
                sort_order = $sort,
                is_pinned = $pinned,
                conversation_kind = $conversationKind,
                communication_destination_device_id = $communicationDevice,
                communication_destination_device_name = $communicationName,
                communication_destination_device_platform = $communicationPlatform,
                agent_execution_host_device_id = CASE
                    WHEN execution_run_id IS NOT NULL AND $runId IS NULL
                    THEN agent_execution_host_device_id ELSE $device END,
                agent_execution_host_device_name = CASE
                    WHEN execution_run_id IS NOT NULL AND $runId IS NULL
                    THEN agent_execution_host_device_name ELSE $deviceName END,
                agent_execution_host_device_platform = CASE
                    WHEN execution_run_id IS NOT NULL AND $runId IS NULL
                    THEN agent_execution_host_device_platform ELSE $platform END,
                execution_at = CASE
                    WHEN execution_run_id IS NOT NULL AND $runId IS NULL
                    THEN execution_at ELSE $execAt END,
                execution_run_id = CASE
                    WHEN execution_run_id IS NOT NULL AND $runId IS NULL
                    THEN execution_run_id ELSE $runId END,
                last_activity_at = CASE
                    WHEN $activity IS NOT NULL
                         AND (last_activity_at IS NULL OR julianday($activity) > julianday(last_activity_at))
                    THEN $activity ELSE last_activity_at END
            WHERE id = $id;
            """);
        cmd.Parameters.AddWithValue("$id", thread.Id);
        cmd.Parameters.AddWithValue("$title", thread.Title ?? "");
        cmd.Parameters.AddWithValue("$sort", Math.Max(0, sortOrder));
        cmd.Parameters.AddWithValue("$pinned", thread.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$conversationKind", (int)ConversationKind.Assistant);
        cmd.Parameters.AddWithValue(
            "$communicationDevice", DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$communicationName", DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$communicationPlatform", DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)thread.AgentExecutionHostDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deviceName", (object?)thread.AgentExecutionHostDeviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$platform", (object?)thread.AgentExecutionHostDevicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$execAt", thread.ExecutionAt.HasValue ? Iso(thread.ExecutionAt.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$runId", (object?)thread.ExecutionRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity", thread.LastActivityAt.HasValue ? Iso(thread.LastActivityAt.Value) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes every line of a topic but keeps the thread row.</summary>
    public static void ClearOwnThread(SqliteConnection conn, SqliteTransaction tx, string threadId)
    {
        using var cmd = Command(conn, tx, "DELETE FROM own_chat WHERE thread_id = $tid;");
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a topic thread and all of its lines.</summary>
    public static void DeleteOwnThread(SqliteConnection conn, SqliteTransaction tx, string threadId)
    {
        ClearOwnThread(conn, tx, threadId);
        using var cmd = Command(conn, tx, "DELETE FROM own_threads WHERE id = $tid;");
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // memories
    // -----------------------------------------------------------------------

    /// <summary>Upserts one owner memory into the real memories table.</summary>
    public static void UpsertMemory(SqliteConnection conn, SqliteTransaction tx, MemoryItem memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        using var cmd = Command(conn, tx, """
            INSERT INTO memories(
                id, title, content, category, origin, importance, confidence, stability,
                reinforcement_count, source_thread_id, source_line_id, created_at, updated_at,
                last_reinforced_at, recall_count, last_recalled_at)
            VALUES($id, $title, $content, $category, $origin, $importance, $confidence, $stability,
                $reinforcement, $thread, $line, $created, $updated, $reinforced, $recall, $recalled)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                content = excluded.content,
                category = excluded.category,
                origin = excluded.origin,
                importance = excluded.importance,
                confidence = excluded.confidence,
                stability = excluded.stability,
                reinforcement_count = excluded.reinforcement_count,
                source_thread_id = excluded.source_thread_id,
                source_line_id = excluded.source_line_id,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                last_reinforced_at = excluded.last_reinforced_at;
            """);
        cmd.Parameters.AddWithValue("$id", memory.Id);
        cmd.Parameters.AddWithValue("$title", memory.Title);
        cmd.Parameters.AddWithValue("$content", memory.Content);
        cmd.Parameters.AddWithValue("$category", memory.Category);
        cmd.Parameters.AddWithValue("$origin", memory.Origin);
        cmd.Parameters.AddWithValue("$importance", memory.Importance);
        cmd.Parameters.AddWithValue("$confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("$stability", memory.Stability);
        cmd.Parameters.AddWithValue("$reinforcement", memory.ReinforcementCount);
        cmd.Parameters.AddWithValue("$thread", (object?)memory.SourceThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$line", (object?)memory.SourceLineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(memory.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(memory.UpdatedAt));
        cmd.Parameters.AddWithValue("$reinforced", Iso(memory.LastReinforcedAt));
        cmd.Parameters.AddWithValue("$recall", memory.RecallCount);
        cmd.Parameters.AddWithValue("$recalled",
            memory.LastRecalledAt.HasValue ? Iso(memory.LastRecalledAt.Value) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes one owner memory from the real memories table.</summary>
    public static void DeleteMemory(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using var cmd = Command(conn, tx, "DELETE FROM memories WHERE id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // Profile blob (contacts and circles live inside the bounded profile row).
    // -----------------------------------------------------------------------

    /// <summary>Reads the stored profile blob as a mutable object, or an empty object when absent.</summary>
    public static JsonObject ReadProfileObject(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = Command(conn, tx!, "SELECT json FROM profile WHERE id = 1;");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new JsonObject();
        try { return JsonNode.Parse(r.GetString(0))?.AsObject() ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>Writes the profile blob back.</summary>
    public static void WriteProfileObject(SqliteConnection conn, SqliteTransaction tx, JsonObject profile)
    {
        using var cmd = Command(conn, tx,
            "INSERT INTO profile(id, json) VALUES(1, $j) ON CONFLICT(id) DO UPDATE SET json = $j;");
        cmd.Parameters.AddWithValue("$j", profile.ToJsonString(Json));
        cmd.ExecuteNonQuery();
    }

    private static JsonArray ArrayOf(JsonObject profile, string name)
    {
        if (profile[name] is JsonArray existing) return existing;
        var created = new JsonArray();
        profile[name] = created;
        return created;
    }

    private static string StringOf(JsonNode? node, string property)
        => node is JsonObject obj && obj[property] is JsonNode value ? value.GetValue<string>() : "";

    /// <summary>Upserts one contact inside the durable profile blob, keyed by normalised handle.</summary>
    public static void UpsertProfileContact(
        SqliteConnection conn, SqliteTransaction tx, string normalizedHandle, JsonObject contact)
    {
        var profile = ReadProfileObject(conn, tx);
        var contacts = ArrayOf(profile, "contacts");
        for (var i = contacts.Count - 1; i >= 0; i--)
            if (string.Equals(NormalizeHandle(StringOf(contacts[i], "handle")), normalizedHandle, StringComparison.Ordinal))
                contacts.RemoveAt(i);
        contacts.Add(contact);
        WriteProfileObject(conn, tx, profile);
    }

    /// <summary>Removes one contact from the durable profile blob.</summary>
    public static void DeleteProfileContact(
        SqliteConnection conn, SqliteTransaction tx, string normalizedHandle)
    {
        var profile = ReadProfileObject(conn, tx);
        var contacts = ArrayOf(profile, "contacts");
        for (var i = contacts.Count - 1; i >= 0; i--)
            if (string.Equals(NormalizeHandle(StringOf(contacts[i], "handle")), normalizedHandle, StringComparison.Ordinal))
                contacts.RemoveAt(i);
        WriteProfileObject(conn, tx, profile);
    }

    /// <summary>Upserts one circle inside the durable profile blob, keyed by its stable entity id.</summary>
    public static void UpsertProfileCircle(
        SqliteConnection conn, SqliteTransaction tx, string entityId, string name, bool requireApproval)
    {
        var profile = ReadProfileObject(conn, tx);
        var circles = ArrayOf(profile, "circles");
        string? previousName = null;
        for (var i = circles.Count - 1; i >= 0; i--)
        {
            var candidate = StringOf(circles[i], "name");
            if (!string.Equals(CircleId(candidate), entityId, StringComparison.Ordinal)) continue;
            previousName = candidate;
            circles.RemoveAt(i);
        }
        circles.Add(new JsonObject { ["name"] = name, ["requireApproval"] = requireApproval });
        if (previousName is not null && !string.Equals(previousName, name, StringComparison.Ordinal))
            RetargetContactCircles(profile, previousName, name);
        WriteProfileObject(conn, tx, profile);
    }

    /// <summary>
    /// Renames a circle in the durable profile blob, moving every contact membership from the
    /// previous name onto the new one so a replicated rename keeps its references.
    /// </summary>
    public static void RenameProfileCircle(
        SqliteConnection conn, SqliteTransaction tx, string previousName, string newName, bool requireApproval)
    {
        var profile = ReadProfileObject(conn, tx);
        var circles = ArrayOf(profile, "circles");
        for (var i = circles.Count - 1; i >= 0; i--)
        {
            var candidate = StringOf(circles[i], "name");
            if (string.Equals(CircleId(candidate), CircleId(previousName), StringComparison.Ordinal)
                || string.Equals(CircleId(candidate), CircleId(newName), StringComparison.Ordinal))
                circles.RemoveAt(i);
        }
        circles.Add(new JsonObject { ["name"] = newName, ["requireApproval"] = requireApproval });
        RetargetContactCircles(profile, previousName, newName);
        WriteProfileObject(conn, tx, profile);
    }

    /// <summary>Removes a circle and every contact membership that referenced it.</summary>
    public static void DeleteProfileCircle(SqliteConnection conn, SqliteTransaction tx, string entityId)
    {
        var profile = ReadProfileObject(conn, tx);
        var circles = ArrayOf(profile, "circles");
        string? removedName = null;
        for (var i = circles.Count - 1; i >= 0; i--)
        {
            var candidate = StringOf(circles[i], "name");
            if (!string.Equals(CircleId(candidate), entityId, StringComparison.Ordinal)) continue;
            removedName = candidate;
            circles.RemoveAt(i);
        }
        if (removedName is not null) RetargetContactCircles(profile, removedName, null);
        WriteProfileObject(conn, tx, profile);
    }

    private static void RetargetContactCircles(JsonObject profile, string previousName, string? newName)
    {
        if (profile["contacts"] is not JsonArray contacts) return;
        var previousId = CircleId(previousName);
        foreach (var entry in contacts)
        {
            if (entry is not JsonObject contact || contact["circles"] is not JsonArray memberships) continue;
            var replacement = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var membership in memberships)
            {
                var value = membership?.GetValue<string>() ?? "";
                var mapped = string.Equals(CircleId(value), previousId, StringComparison.Ordinal) ? newName : value;
                if (mapped is null || mapped.Length == 0 || !seen.Add(mapped)) continue;
                replacement.Add(mapped);
            }
            contact["circles"] = replacement;
        }
    }

    private static string CircleId(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static string NormalizeHandle(string? handle) => (handle ?? "").Trim().ToLowerInvariant();

    // -----------------------------------------------------------------------
    // assets / asset_content
    // -----------------------------------------------------------------------

    /// <summary>Upserts an asset summary row and its content bytes in the same transaction.</summary>
    public static void UpsertAsset(
        SqliteConnection conn,
        SqliteTransaction tx,
        AssetKind kind,
        string id,
        string name,
        string? metadataJson,
        string? contentMime,
        byte[] content,
        int version,
        string? sourceDeviceId,
        DateTimeOffset updatedAt,
        bool localOnly)
    {
        ArgumentNullException.ThrowIfNull(content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using (var cmd = Command(conn, tx, """
            INSERT INTO assets(
                kind, id, name, metadata_json, content_mime, content_hash, content_byte_count,
                version, source_device_id, updated_at, is_deleted, local_only)
            VALUES($kind, $id, $name, $meta, $mime, $hash, $bytes, $version, $device, $updated, 0, $local)
            ON CONFLICT(kind, id) DO UPDATE SET
                name = excluded.name,
                metadata_json = excluded.metadata_json,
                content_mime = excluded.content_mime,
                content_hash = excluded.content_hash,
                content_byte_count = excluded.content_byte_count,
                version = excluded.version,
                source_device_id = excluded.source_device_id,
                updated_at = excluded.updated_at,
                is_deleted = 0,
                local_only = excluded.local_only;
            """))
        {
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name ?? "");
            cmd.Parameters.AddWithValue("$meta", (object?)metadataJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mime", (object?)contentMime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", hash);
            cmd.Parameters.AddWithValue("$bytes", content.LongLength);
            cmd.Parameters.AddWithValue("$version", version);
            cmd.Parameters.AddWithValue("$device", (object?)sourceDeviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", Iso(updatedAt));
            cmd.Parameters.AddWithValue("$local", localOnly ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        using var body = Command(conn, tx, """
            INSERT INTO asset_content(kind, id, bytes, sha256)
            VALUES($kind, $id, $bytes, $hash)
            ON CONFLICT(kind, id) DO UPDATE SET bytes = excluded.bytes, sha256 = excluded.sha256;
            """);
        body.Parameters.AddWithValue("$kind", kind.ToString());
        body.Parameters.AddWithValue("$id", id);
        body.Parameters.AddWithValue("$bytes", content);
        body.Parameters.AddWithValue("$hash", hash);
        body.ExecuteNonQuery();
    }

    /// <summary>Tombstones an asset and drops its stored body.</summary>
    public static void DeleteAsset(
        SqliteConnection conn, SqliteTransaction tx, AssetKind kind, string id,
        int version, string? sourceDeviceId, DateTimeOffset updatedAt)
    {
        using (var cmd = Command(conn, tx, """
            INSERT INTO assets(
                kind, id, name, metadata_json, content_mime, content_hash, content_byte_count,
                version, source_device_id, updated_at, is_deleted, local_only)
            VALUES($kind, $id, '', NULL, NULL, NULL, 0, $version, $device, $updated, 1, 0)
            ON CONFLICT(kind, id) DO UPDATE SET
                content_hash = NULL,
                content_byte_count = 0,
                version = excluded.version,
                source_device_id = excluded.source_device_id,
                updated_at = excluded.updated_at,
                is_deleted = 1;
            """))
        {
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$version", version);
            cmd.Parameters.AddWithValue("$device", (object?)sourceDeviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", Iso(updatedAt));
            cmd.ExecuteNonQuery();
        }
        using var drop = Command(conn, tx, "DELETE FROM asset_content WHERE kind = $kind AND id = $id;");
        drop.Parameters.AddWithValue("$kind", kind.ToString());
        drop.Parameters.AddWithValue("$id", id);
        drop.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // ask_user_prompts
    // -----------------------------------------------------------------------

    /// <summary>
    /// Upserts an ask-user prompt row; never downgrades an already-resolved prompt.
    /// <paramref name="optionsJson"/> is the canonical serialised option list carrying the full
    /// option identity (id, title, description), so the actual row round-trips without loss.
    /// </summary>
    public static void UpsertAskUserPrompt(
        SqliteConnection conn,
        SqliteTransaction tx,
        string promptId,
        string threadId,
        string runId,
        string question,
        string optionsJson,
        int? recommendedIndex,
        string? originDeviceId,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        int revision = 1,
        int version = 1)
    {
        using var cmd = Command(conn, tx, """
            INSERT INTO ask_user_prompts(
                prompt_id, thread_id, run_id, question, options_json, recommended_index,
                state, origin_device_id, created_at, expires_at, revision, version)
            VALUES($pid, $tid, $rid, $q, $opts, $rec, 'pending', $device, $created, $expires,
                   $revision, $version)
            ON CONFLICT(prompt_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                run_id = excluded.run_id,
                question = excluded.question,
                options_json = excluded.options_json,
                recommended_index = excluded.recommended_index,
                origin_device_id = excluded.origin_device_id,
                expires_at = excluded.expires_at,
                revision = ask_user_prompts.revision + 1
            WHERE ask_user_prompts.state = 'pending';
            """);
        cmd.Parameters.AddWithValue("$pid", promptId);
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.Parameters.AddWithValue("$rid", runId);
        cmd.Parameters.AddWithValue("$q", question);
        cmd.Parameters.AddWithValue("$opts", string.IsNullOrWhiteSpace(optionsJson) ? "[]" : optionsJson);
        cmd.Parameters.AddWithValue("$rec", recommendedIndex.HasValue ? recommendedIndex.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)originDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(createdAt));
        cmd.Parameters.AddWithValue("$expires", expiresAt.HasValue ? Iso(expiresAt.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$revision", revision < 1 ? 1 : revision);
        cmd.Parameters.AddWithValue("$version", version < 1 ? 1 : version);
        cmd.ExecuteNonQuery();
    }

    public static void UpsertAskUserContext(
        SqliteConnection conn,
        SqliteTransaction tx,
        SuspendedAgentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var cmd = Command(conn, tx, """
            INSERT INTO ask_user_suspended_contexts(
                context_id, prompt_id, thread_id, run_id, context_json,
                created_at, expires_at, resumed_at)
            VALUES($cid, $pid, $tid, $rid, $json, $created, $expires, NULL)
            ON CONFLICT(context_id) DO NOTHING;
            """);
        cmd.Parameters.AddWithValue("$cid", context.ContextId);
        cmd.Parameters.AddWithValue("$pid", context.PromptId);
        cmd.Parameters.AddWithValue("$tid", context.ThreadId);
        cmd.Parameters.AddWithValue("$rid", context.RunId);
        cmd.Parameters.AddWithValue("$json", context.ContextJson);
        cmd.Parameters.AddWithValue("$created", Iso(context.CreatedAt));
        cmd.Parameters.AddWithValue(
            "$expires",
            context.ExpiresAt.HasValue ? Iso(context.ExpiresAt.Value) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when the prompt row exists (used by out-of-order resolution snapshots).</summary>
    public static bool AskUserPromptExists(SqliteConnection conn, SqliteTransaction tx, string promptId)
    {
        using var cmd = Command(conn, tx,
            "SELECT 1 FROM ask_user_prompts WHERE prompt_id = $pid LIMIT 1;");
        cmd.Parameters.AddWithValue("$pid", promptId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    /// <summary>
    /// Resolves an ask-user prompt with first-writer-wins semantics: only a still-pending row is
    /// transitioned, so a second (racing) resolution from another device never overwrites the first.
    /// </summary>
    public static bool ResolveAskUserPrompt(
        SqliteConnection conn,
        SqliteTransaction tx,
        string promptId,
        string state,
        string? selection,
        string? resolutionDeviceId,
        DateTimeOffset resolvedAt)
    {
        using var cmd = Command(conn, tx, """
            UPDATE ask_user_prompts
            SET state = $state,
                selection = $selection,
                resolution_device_id = $device,
                resolved_at = $at,
                version = version + 1
            WHERE prompt_id = $pid AND state = 'pending';
            """);
        cmd.Parameters.AddWithValue("$pid", promptId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$selection", (object?)selection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$device", (object?)resolutionDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", Iso(resolvedAt));
        return cmd.ExecuteNonQuery() == 1;
    }

    // -----------------------------------------------------------------------
    // Skill-package staging (content-addressed blobs shared with the installer).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stages one assembled skill-package blob in the same content-addressed table the local
    /// installer uses, so a completed replicated transfer is durable exactly once.
    /// </summary>
    public static void StagePackageBlob(
        SqliteConnection conn, SqliteTransaction tx, string sha256, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var cmd = Command(conn, tx, """
            INSERT INTO skill_package_blobs(sha256, bytes, byte_count, refcount)
            VALUES($sha, $bytes, $count, 0)
            ON CONFLICT(sha256) DO NOTHING;
            """);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.Parameters.AddWithValue("$count", bytes.LongLength);
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when a staged package blob with this hash is present.</summary>
    public static bool PackageBlobExists(SqliteConnection conn, string sha256)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM skill_package_blobs WHERE sha256 = $sha LIMIT 1;";
        cmd.Parameters.AddWithValue("$sha", sha256);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    // -----------------------------------------------------------------------
    // replicated_attachments (the ACTUAL attachment rows)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes one fully assembled attachment into the actual attachment table, inside the caller's
    /// transaction. Attachment bytes never leave the device fabric: they ride the signed event log
    /// as bounded chunks and land here, never in relay storage.
    /// </summary>
    public static void UpsertReplicatedAttachment(
        SqliteConnection conn,
        SqliteTransaction tx,
        string attachmentId,
        string runId,
        string name,
        string mimeType,
        string sha256,
        byte[] bytes,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var cmd = Command(conn, tx, """
            INSERT INTO replicated_attachments(
                attachment_id, run_id, name, mime_type, byte_count, sha256, bytes, created_at)
            VALUES($id, $run, $name, $mime, $count, $sha, $bytes, $created)
            ON CONFLICT(attachment_id) DO UPDATE SET
                run_id = excluded.run_id,
                name = excluded.name,
                mime_type = excluded.mime_type,
                byte_count = excluded.byte_count,
                sha256 = excluded.sha256,
                bytes = excluded.bytes;
            """);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$mime", mimeType);
        cmd.Parameters.AddWithValue("$count", bytes.LongLength);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.Parameters.AddWithValue("$created", Iso(createdAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when the replicated attachment table exists on this connection.</summary>
    public static bool AttachmentSchemaPresent(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = Command(conn, tx,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'replicated_attachments';");
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}

/// <summary>
/// Local-only domain materialisation: writes the ACTUAL domain rows for a local change that has
/// no replication audience at all (no sibling authorised device and no peer), so there is no event
/// and no outbox reference to be atomic with. Everything still commits in one transaction.
/// </summary>
public sealed partial class MeshDb
{
    public bool ApplyLocalDomainChange(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        bool deviceIsDesktop)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using var tx = conn.BeginTransaction(deferred: false);
        var synthetic = new ReplicationEvent(
            EventId: envelope.CausalVersion,
            ConversationId: envelope.ConversationId,
            OriginAccount: "",
            OriginDeviceId: "",
            LogEpoch: "",
            Seq: 0,
            AuthGeneration: 0,
            Kind: envelope.Kind,
            EntityId: envelope.EntityId,
            CausalVersion: envelope.CausalVersion,
            CreatedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ciphertext: "",
            ContentHash: "",
            Signature: "");
        var applied = ReplicationDomainMaterializer.Apply(conn, tx, synthetic, envelope, deviceIsDesktop);
        tx.Commit();
        return applied;
    }
}
