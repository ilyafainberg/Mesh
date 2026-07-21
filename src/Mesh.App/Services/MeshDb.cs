using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// One encrypted SQLCipher database per identity. Holds everything tied to the user: their
/// profile (keys, config, contacts, circles, knowledge, skills, widgets, sources), plus the
/// chat history stored as append-only rows so it scales instead of being re-serialized on every
/// message. The whole file is encrypted at rest with a 256-bit master key kept in the platform
/// secure enclave (see <see cref="ISecretStore"/>), so it works cross platform including iOS.
///
/// The profile blob deliberately excludes conversations and own-chat, those live in the
/// <c>chat_lines</c> / <c>own_chat</c> tables and are hydrated back onto the profile on load.
/// </summary>
public sealed class MeshDb : IDisposable
{
    internal sealed record SyncVersionWrite(string EntityKey, string Version);
    internal sealed record SyncTombstoneWrite(string Kind, string EntityId, string Version);
    internal sealed record SyncCircleRenameWrite(
        string EntityId,
        IReadOnlyList<DeviceSyncCircleRename> Renames);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static bool nativeInit;

    private readonly SqliteConnection conn;

    private MeshDb(SqliteConnection conn) => this.conn = conn;

    /// <summary>Opens (creating if needed) an encrypted database at <paramref name="path"/> with the given key.</summary>
    public static MeshDb Open(string path, byte[] key)
    {
        EnsureNativeInit();
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        ApplyKey(conn, key);
        var db = new MeshDb(conn);
        db.CreateSchema();
        return db;
    }

    private static void EnsureNativeInit()
    {
        if (nativeInit) return;
        SQLitePCL.Batteries_V2.Init();
        nativeInit = true;
    }

    private static void ApplyKey(SqliteConnection conn, byte[] key)
    {
        var hex = Convert.ToHexString(key);
        using var cmd = conn.CreateCommand();
        // SQLCipher raw key form: x'HEX' skips the passphrase KDF (the key is already 256-bit).
        cmd.CommandText = $"PRAGMA key = \"x'{hex}'\";";
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        Exec(@"
            CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profile(id INTEGER PRIMARY KEY CHECK(id = 1), json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversations(handle TEXT PRIMARY KEY, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat_lines(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                line_id TEXT,
                handle TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                via TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT '',
                at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_chat_handle ON chat_lines(handle, id);
            CREATE INDEX IF NOT EXISTS ix_chat_lineid ON chat_lines(line_id);
            CREATE TABLE IF NOT EXISTS own_chat(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                line_id TEXT,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                via TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT '',
                at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS own_threads(
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_versions(
                entity_key TEXT PRIMARY KEY,
                version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_tombstones(
                kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                version TEXT NOT NULL,
                PRIMARY KEY(kind, entity_id));
            CREATE TABLE IF NOT EXISTS sync_circle_renames(
                entity_id TEXT NOT NULL,
                previous_entity_id TEXT NOT NULL,
                previous_name TEXT NOT NULL,
                delete_version TEXT NOT NULL,
                PRIMARY KEY(entity_id, previous_entity_id));
            INSERT OR IGNORE INTO meta(k, v) VALUES('schema_version', '1');");

        // Idempotent migration for databases created before line_id/status existed.
        AddColumnIfMissing("chat_lines", "line_id", "TEXT");
        AddColumnIfMissing("chat_lines", "status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("own_chat", "line_id", "TEXT");
        AddColumnIfMissing("own_chat", "status", "TEXT NOT NULL DEFAULT ''");
        Exec("""
            UPDATE chat_lines
            SET line_id = 'legacy-conversation-' || printf('%016x', id)
            WHERE line_id IS NULL OR trim(line_id) = '';
            UPDATE own_chat
            SET line_id = 'legacy-topic-' || printf('%016x', id)
            WHERE line_id IS NULL OR trim(line_id) = '';
            """);
        // Service-thread metadata on conversations (null for normal person DMs).
        AddColumnIfMissing("conversations", "service_id", "TEXT");
        AddColumnIfMissing("conversations", "service_name", "TEXT");
        AddColumnIfMissing("conversations", "provider_handle", "TEXT");
        AddColumnIfMissing("conversations", "group_id", "TEXT");
        AddColumnIfMissing("conversations", "group_name", "TEXT");
        AddColumnIfMissing("conversations", "group_owner_handle", "TEXT");
        AddColumnIfMissing("conversations", "group_members_json", "TEXT");
        AddColumnIfMissing("conversations", "group_version", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("conversations", "sort_order", "INTEGER");
        NormalizeConversationOrder();
        AddColumnIfMissing("chat_lines", "sender_handle", "TEXT");
        AddColumnIfMissing("chat_lines", "internal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("chat_lines", "reasoning", "TEXT");
        AddColumnIfMissing("own_chat", "thread_id", "TEXT");
        // Transcript + reasoning persistence: internal lines are the model's hidden execution record;
        // reasoning is the collapsible "thinking" (previously not persisted, so lost on restart).
        AddColumnIfMissing("own_chat", "internal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("own_chat", "reasoning", "TEXT");
        AddColumnIfMissing("own_chat", "sender_handle", "TEXT");
        // User-defined topic order. Existing rows retain their creation order through the fallback sort.
        AddColumnIfMissing("own_threads", "sort_order", "INTEGER");
        NormalizeOwnThreadOrder();
    }

    private void AddColumnIfMissing(string table, string column, string decl)
    {
        bool exists = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        }
        if (!exists) Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
    }

    /// <summary>True when this database has never had a profile written to it.</summary>
    public bool IsEmpty()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM profile;";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    // ---- profile + history --------------------------------------------------

    /// <summary>Loads the full profile including chat history, or null when the database is empty.</summary>
    public MeshProfile? LoadProfile()
    {
        string? json;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM profile WHERE id = 1;";
            json = cmd.ExecuteScalar() as string;
        }
        if (json is null) return null;

        var profile = JsonSerializer.Deserialize<MeshProfile>(json, JsonOpts) ?? new MeshProfile();
        profile.Conversations = LoadConversations();
        profile.OwnThreads = LoadOwnThreads();
        profile.OwnChat = new List<ChatLine>();
        return profile;
    }

    private List<Conversation> LoadConversations()
    {
        var byHandle = new Dictionary<string, Conversation>(StringComparer.OrdinalIgnoreCase);
        var order = new List<Conversation>();

        Conversation Get(string handle)
        {
            if (!byHandle.TryGetValue(handle, out var c))
            {
                c = new Conversation { Handle = handle };
                byHandle[handle] = c;
                order.Add(c);
            }
            return c;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT handle, service_id, service_name, provider_handle,
                       group_id, group_name, group_owner_handle, group_members_json, group_version
                FROM conversations ORDER BY sort_order, created_at, handle;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var c = Get(r.GetString(0));
                if (!r.IsDBNull(1)) c.ServiceId = r.GetString(1);
                if (!r.IsDBNull(2)) c.ServiceName = r.GetString(2);
                if (!r.IsDBNull(3)) c.ProviderHandle = r.GetString(3);
                if (!r.IsDBNull(4)) c.GroupId = r.GetString(4);
                if (!r.IsDBNull(5)) c.GroupName = r.GetString(5);
                if (!r.IsDBNull(6)) c.GroupOwnerHandle = r.GetString(6);
                if (!r.IsDBNull(7))
                    c.GroupMembers = JsonSerializer.Deserialize<List<string>>(r.GetString(7), JsonOpts)
                        ?? throw new InvalidOperationException($"Group members are invalid for conversation '{c.Handle}'.");
                c.GroupVersion = r.GetInt32(8);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT handle, role, text, via, at, line_id, status, sender_handle, internal, reasoning
                FROM chat_lines ORDER BY id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var conv = Get(r.GetString(0));
                conv.Lines.Add(new ChatLine
                {
                    Role = r.GetString(1),
                    Text = r.GetString(2),
                    Via = r.GetString(3),
                    At = ParseAt(r.GetString(4)),
                    Id = r.IsDBNull(5) ? Guid.NewGuid().ToString("n") : r.GetString(5),
                    Status = r.IsDBNull(6) ? "" : r.GetString(6),
                    SenderHandle = r.IsDBNull(7) ? null : r.GetString(7),
                    Internal = !r.IsDBNull(8) && r.GetInt64(8) != 0,
                    Reasoning = r.IsDBNull(9) ? null : r.GetString(9)
                });
            }
        }
        return order;
    }

    private List<OwnThread> LoadOwnThreads()
    {
        // Migrate any legacy own_chat rows (written before threads existed, thread_id IS NULL) into a
        // single default thread so no history is lost.
        long legacyCount;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM own_chat WHERE thread_id IS NULL;";
            legacyCount = Convert.ToInt64(cmd.ExecuteScalar());
        }
        if (legacyCount > 0)
        {
            var defaultId = Guid.NewGuid().ToString("n");
            EnsureOwnThread(defaultId, "General", DateTimeOffset.UtcNow);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE own_chat SET thread_id = $tid WHERE thread_id IS NULL;";
            cmd.Parameters.AddWithValue("$tid", defaultId);
            cmd.ExecuteNonQuery();
        }

        var threads = new List<OwnThread>();
        var byId = new Dictionary<string, OwnThread>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title, created_at FROM own_threads ORDER BY sort_order, created_at, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = new OwnThread { Id = r.GetString(0), Title = r.GetString(1), CreatedAt = ParseAt(r.GetString(2)) };
                threads.Add(t);
                byId[t.Id] = t;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT thread_id, role, text, via, at, line_id, status, internal, reasoning, sender_handle
                FROM own_chat WHERE thread_id IS NOT NULL ORDER BY id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var thread)) continue;
                thread.Lines.Add(new ChatLine
                {
                    Role = r.GetString(1),
                    Text = r.GetString(2),
                    Via = r.GetString(3),
                    At = ParseAt(r.GetString(4)),
                    Id = r.IsDBNull(5) ? Guid.NewGuid().ToString("n") : r.GetString(5),
                    Status = r.IsDBNull(6) ? "" : r.GetString(6),
                    Internal = !r.IsDBNull(7) && r.GetInt64(7) != 0,
                    Reasoning = r.IsDBNull(8) ? null : r.GetString(8),
                    SenderHandle = r.IsDBNull(9) ? null : r.GetString(9)
                });
            }
        }
        return threads;
    }

    /// <summary>
    /// Writes the profile blob (config, keys, contacts, and the rest) EXCLUDING conversations and
    /// own-chat, which are persisted as rows via the append methods so history stays scalable.
    /// </summary>
    public void SaveProfile(MeshProfile profile)
    {
        using var cmd = conn.CreateCommand();
        SaveProfile(cmd, profile);
    }

    internal bool SaveProfileAndSyncState(
        MeshProfile profile,
        IReadOnlyList<SyncVersionWrite> versions,
        IReadOnlyList<SyncTombstoneWrite> tombstones,
        Action? beforeCommit = null,
        IReadOnlyList<SyncCircleRenameWrite>? circleRenames = null)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        var acceptedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            var current = acceptedVersions.TryGetValue(version.EntityKey, out var accepted)
                ? accepted
                : GetSyncVersion(transaction, version.EntityKey);
            var opposing = TryGetDeleteIdentity(version.EntityKey, out var deleteKind, out var entityId)
                ? GetSyncTombstoneVersion(transaction, deleteKind, entityId)
                : null;
            if (!DeviceSyncVersion.IsNewer(version.Version, Newest(current, opposing)))
            {
                transaction.Rollback();
                return false;
            }
            acceptedVersions[version.EntityKey] = version.Version;
        }
        var acceptedTombstones = new Dictionary<(string Kind, string EntityId), string>();
        foreach (var tombstone in tombstones)
        {
            var key = (tombstone.Kind, tombstone.EntityId);
            var current = acceptedTombstones.TryGetValue(key, out var accepted)
                ? accepted
                : GetSyncTombstoneVersion(transaction, tombstone.Kind, tombstone.EntityId);
            var opposing = TryGetUpsertKey(
                tombstone.Kind, tombstone.EntityId, out var upsertKey)
                ? acceptedVersions.TryGetValue(upsertKey, out var acceptedUpsert)
                    ? acceptedUpsert
                    : GetSyncVersion(transaction, upsertKey)
                : null;
            if (!DeviceSyncVersion.IsNewer(tombstone.Version, Newest(current, opposing)))
            {
                transaction.Rollback();
                return false;
            }
            acceptedTombstones[key] = tombstone.Version;
        }
        using (var profileCommand = conn.CreateCommand())
        {
            profileCommand.Transaction = transaction;
            SaveProfile(profileCommand, profile);
        }
        foreach (var version in versions)
            UpsertSyncVersion(transaction, version.EntityKey, version.Version);
        foreach (var tombstone in tombstones)
            UpsertSyncTombstone(transaction, tombstone.Kind, tombstone.EntityId, tombstone.Version);
        foreach (var rename in circleRenames ?? Array.Empty<SyncCircleRenameWrite>())
            WriteCircleRename(transaction, rename);
        beforeCommit?.Invoke();
        transaction.Commit();
        return true;
    }

    private static string? Newest(string? first, string? second)
        => string.Compare(first, second, StringComparison.Ordinal) >= 0 ? first : second;

    private static bool TryGetDeleteIdentity(
        string entityKey,
        out string deleteKind,
        out string entityId)
    {
        const char separator = '\u001f';
        var split = entityKey.IndexOf(separator);
        var kind = split > 0 ? entityKey[..split] : "";
        entityId = split > 0 && split + 1 < entityKey.Length ? entityKey[(split + 1)..] : "";
        deleteKind = kind switch
        {
            DeviceSyncKinds.ContactUpsert => DeviceSyncKinds.ContactDelete,
            DeviceSyncKinds.CircleUpsert => DeviceSyncKinds.CircleDelete,
            _ => ""
        };
        return deleteKind.Length > 0 && entityId.Length > 0;
    }

    private static bool TryGetUpsertKey(
        string deleteKind,
        string entityId,
        out string entityKey)
    {
        var upsertKind = deleteKind switch
        {
            DeviceSyncKinds.ContactDelete => DeviceSyncKinds.ContactUpsert,
            DeviceSyncKinds.CircleDelete => DeviceSyncKinds.CircleUpsert,
            _ => ""
        };
        entityKey = upsertKind.Length == 0 ? "" : upsertKind + "\u001f" + entityId;
        return entityKey.Length > 0;
    }

    private static void SaveProfile(SqliteCommand cmd, MeshProfile profile)
    {
        var node = JsonSerializer.SerializeToNode(profile, JsonOpts)!.AsObject();
        node.Remove("conversations");
        node.Remove("ownChat");
        node.Remove("ownThreads");
        var json = node.ToJsonString(JsonOpts);
        cmd.CommandText = "INSERT INTO profile(id, json) VALUES(1, $j) ON CONFLICT(id) DO UPDATE SET json = $j;";
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    // ---- device sync merge state -------------------------------------------

    public sealed record SyncTombstone(string Kind, string EntityId, string Version);

    public string? GetSyncVersion(string entityKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM sync_versions WHERE entity_key = $key;";
        cmd.Parameters.AddWithValue("$key", entityKey);
        return cmd.ExecuteScalar() as string;
    }

    private string? GetSyncVersion(SqliteTransaction transaction, string entityKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT version FROM sync_versions WHERE entity_key = $key;";
        cmd.Parameters.AddWithValue("$key", entityKey);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSyncVersion(string entityKey, string version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_versions(entity_key, version) VALUES($key, $version)
            ON CONFLICT(entity_key) DO UPDATE SET version = excluded.version;
            """;
        cmd.Parameters.AddWithValue("$key", entityKey);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Atomically advances an entity version only when the candidate is newer.</summary>
    public bool TryAdvanceSyncVersion(string entityKey, string version)
    {
        using var tx = conn.BeginTransaction();
        string? current;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT version FROM sync_versions WHERE entity_key = $key;";
            read.Parameters.AddWithValue("$key", entityKey);
            current = read.ExecuteScalar() as string;
        }
        if (string.Compare(version, current ?? "", StringComparison.Ordinal) <= 0)
        {
            tx.Rollback();
            return false;
        }
        UpsertSyncVersion(tx, entityKey, version);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<SyncTombstone> GetSyncTombstones()
    {
        var result = new List<SyncTombstone>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT kind, entity_id, version FROM sync_tombstones ORDER BY version, kind, entity_id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new SyncTombstone(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    internal IReadOnlyList<DeviceSyncCircleRename> GetSyncCircleRenames(string entityId)
    {
        var result = new List<DeviceSyncCircleRename>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT previous_name, delete_version
            FROM sync_circle_renames
            WHERE entity_id = $id
            ORDER BY previous_entity_id;
            """;
        cmd.Parameters.AddWithValue("$id", entityId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new DeviceSyncCircleRename(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public string? GetSyncTombstoneVersion(string kind, string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM sync_tombstones WHERE kind = $kind AND entity_id = $id;";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        return cmd.ExecuteScalar() as string;
    }

    private string? GetSyncTombstoneVersion(
        SqliteTransaction transaction,
        string kind,
        string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT version FROM sync_tombstones WHERE kind = $kind AND entity_id = $id;";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Atomically inserts or advances a clear/delete tombstone.</summary>
    public bool SetSyncTombstone(string kind, string entityId, string version)
    {
        using var tx = conn.BeginTransaction();
        string? current;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT version FROM sync_tombstones WHERE kind = $kind AND entity_id = $id;";
            read.Parameters.AddWithValue("$kind", kind);
            read.Parameters.AddWithValue("$id", entityId);
            current = read.ExecuteScalar() as string;
        }
        if (string.Compare(version, current ?? "", StringComparison.Ordinal) <= 0)
        {
            tx.Rollback();
            return false;
        }
        using (var write = conn.CreateCommand())
        {
            write.Transaction = tx;
            write.CommandText = """
                INSERT INTO sync_tombstones(kind, entity_id, version) VALUES($kind, $id, $version)
                ON CONFLICT(kind, entity_id) DO UPDATE SET version = excluded.version;
                """;
            write.Parameters.AddWithValue("$kind", kind);
            write.Parameters.AddWithValue("$id", entityId);
            write.Parameters.AddWithValue("$version", version);
            write.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public void ApplyTopicClear(string id, string kind, string version)
    {
        using var tx = conn.BeginTransaction();
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM own_chat WHERE thread_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
        UpsertSyncTombstone(tx, kind, id, version);
        tx.Commit();
    }

    public void ApplyTopicDelete(string id, string kind, string version)
    {
        using var tx = conn.BeginTransaction();
        using (var lines = conn.CreateCommand())
        {
            lines.Transaction = tx;
            lines.CommandText = "DELETE FROM own_chat WHERE thread_id = $id;";
            lines.Parameters.AddWithValue("$id", id);
            lines.ExecuteNonQuery();
        }
        using (var topic = conn.CreateCommand())
        {
            topic.Transaction = tx;
            topic.CommandText = "DELETE FROM own_threads WHERE id = $id;";
            topic.Parameters.AddWithValue("$id", id);
            topic.ExecuteNonQuery();
        }
        UpsertSyncTombstone(tx, kind, id, version);
        tx.Commit();
    }

    public void ApplyConversationClear(string handle, string kind, string version)
    {
        using var tx = conn.BeginTransaction();
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM chat_lines WHERE handle = $handle;";
            delete.Parameters.AddWithValue("$handle", handle);
            delete.ExecuteNonQuery();
        }
        UpsertSyncTombstone(tx, kind, handle, version);
        tx.Commit();
    }

    public void ApplyConversationDelete(string handle, string kind, string version)
    {
        using var tx = conn.BeginTransaction();
        using (var lines = conn.CreateCommand())
        {
            lines.Transaction = tx;
            lines.CommandText = "DELETE FROM chat_lines WHERE handle = $handle;";
            lines.Parameters.AddWithValue("$handle", handle);
            lines.ExecuteNonQuery();
        }
        using (var conversation = conn.CreateCommand())
        {
            conversation.Transaction = tx;
            conversation.CommandText = "DELETE FROM conversations WHERE handle = $handle;";
            conversation.Parameters.AddWithValue("$handle", handle);
            conversation.ExecuteNonQuery();
        }
        UpsertSyncTombstone(tx, kind, handle, version);
        tx.Commit();
    }

    private void UpsertSyncTombstone(
        SqliteTransaction transaction,
        string kind,
        string entityId,
        string version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sync_tombstones(kind, entity_id, version) VALUES($kind, $id, $version)
            ON CONFLICT(kind, entity_id) DO UPDATE SET version = excluded.version;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    private void UpsertSyncVersion(
        SqliteTransaction transaction,
        string entityKey,
        string version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sync_versions(entity_key, version) VALUES($key, $version)
            ON CONFLICT(entity_key) DO UPDATE SET version = excluded.version;
            """;
        cmd.Parameters.AddWithValue("$key", entityKey);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    private void WriteCircleRename(
        SqliteTransaction transaction,
        SyncCircleRenameWrite rename)
    {
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sync_circle_renames WHERE entity_id = $id;";
            delete.Parameters.AddWithValue("$id", rename.EntityId);
            delete.ExecuteNonQuery();
        }
        foreach (var ancestor in rename.Renames)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sync_circle_renames(
                    entity_id, previous_entity_id, previous_name, delete_version)
                VALUES($id, $previousId, $name, $version)
                ON CONFLICT(entity_id, previous_entity_id) DO UPDATE SET
                    previous_name = excluded.previous_name,
                    delete_version = excluded.delete_version;
                """;
            insert.Parameters.AddWithValue("$id", rename.EntityId);
            insert.Parameters.AddWithValue(
                "$previousId",
                ProfileSyncState.CircleEntityId(ancestor.PreviousName));
            insert.Parameters.AddWithValue("$name", ancestor.PreviousName);
            insert.Parameters.AddWithValue("$version", ancestor.DeleteVersion);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Records that a conversation thread exists so an empty thread survives a reload.</summary>
    public void EnsureConversation(string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO conversations(handle, created_at, sort_order)
            VALUES($h, $t, (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM conversations));
            """;
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upserts all persisted conversation metadata and its explicit order.</summary>
    public void UpsertConversation(
        string handle,
        int sortOrder,
        string? serviceId,
        string? serviceName,
        string? providerHandle,
        string? groupId,
        string? groupName,
        string? groupOwnerHandle,
        IReadOnlyList<string> groupMembers,
        int groupVersion)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations(
                handle, created_at, sort_order, service_id, service_name, provider_handle,
                group_id, group_name, group_owner_handle, group_members_json, group_version)
            VALUES($h, $created, $sort, $sid, $sname, $provider, $gid, $gname, $owner, $members, $gversion)
            ON CONFLICT(handle) DO UPDATE SET
                sort_order = excluded.sort_order,
                service_id = excluded.service_id,
                service_name = excluded.service_name,
                provider_handle = excluded.provider_handle,
                group_id = excluded.group_id,
                group_name = excluded.group_name,
                group_owner_handle = excluded.group_owner_handle,
                group_members_json = excluded.group_members_json,
                group_version = excluded.group_version;
            """;
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$sort", sortOrder);
        cmd.Parameters.AddWithValue("$sid", (object?)serviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sname", (object?)serviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", (object?)providerHandle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gid", (object?)groupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gname", (object?)groupName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$owner", (object?)groupOwnerHandle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$members", groupId is null
            ? (object)DBNull.Value
            : JsonSerializer.Serialize(groupMembers, JsonOpts));
        cmd.Parameters.AddWithValue("$gversion", groupVersion);
        cmd.ExecuteNonQuery();
    }

    private void NormalizeConversationOrder()
    {
        var handles = new List<string>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT handle FROM conversations
                ORDER BY COALESCE(sort_order, 2147483647), created_at, handle;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read()) handles.Add(reader.GetString(0));
        }

        using var tx = conn.BeginTransaction();
        for (var i = 0; i < handles.Count; i++)
        {
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE conversations SET sort_order = $o WHERE handle = $h;";
            update.Parameters.AddWithValue("$o", i);
            update.Parameters.AddWithValue("$h", handles[i]);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Persists the complete user-defined order of message conversations atomically.</summary>
    public void ReorderConversations(IReadOnlyList<string> orderedHandles)
    {
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedHandles.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE conversations SET sort_order = $o WHERE handle = $h;";
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$h", orderedHandles[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Marks a conversation as a service thread and persists its service metadata.</summary>
    public void SetConversationService(string handle, string serviceId, string? serviceName, string providerHandle)
    {
        EnsureConversation(handle);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET service_id = $sid, service_name = $sname, provider_handle = $ph WHERE handle = $h;";
        cmd.Parameters.AddWithValue("$sid", serviceId);
        cmd.Parameters.AddWithValue("$sname", (object?)serviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ph", providerHandle);
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Persists the complete client-side metadata for a group conversation.</summary>
    public void SetConversationGroup(
        string handle,
        string groupId,
        string groupName,
        string groupOwnerHandle,
        IReadOnlyList<string> groupMembers,
        int groupVersion)
    {
        EnsureConversation(handle);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE conversations
            SET group_id = $gid,
                group_name = $gname,
                group_owner_handle = $owner,
                group_members_json = $members,
                group_version = $version
            WHERE handle = $h;
            """;
        cmd.Parameters.AddWithValue("$gid", groupId);
        cmd.Parameters.AddWithValue("$gname", groupName);
        cmd.Parameters.AddWithValue("$owner", groupOwnerHandle);
        cmd.Parameters.AddWithValue("$members", JsonSerializer.Serialize(groupMembers, JsonOpts));
        cmd.Parameters.AddWithValue("$version", groupVersion);
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Appends a single line to a conversation's history (one insert, not a full rewrite).</summary>
    public void AppendChatLine(string handle, ChatLine line)
    {
        EnsureConversation(handle);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chat_lines(
                line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning)
            VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning);
            """;
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
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts or replaces one persisted conversation line by its stable id.</summary>
    public void UpsertChatLine(string handle, ChatLine line)
    {
        EnsureConversation(handle);
        using var tx = conn.BeginTransaction();
        int updated;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE chat_lines
                SET role = $r, text = $x, via = $v, status = $s, at = $a,
                    sender_handle = $sender, internal = $internal, reasoning = $reasoning
                WHERE handle = $h AND line_id = $lid;
                """;
            AddChatLineParameters(update, handle, line);
            updated = update.ExecuteNonQuery();
        }
        if (updated == 0)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO chat_lines(
                    line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning)
                VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning);
                """;
            AddChatLineParameters(insert, handle, line);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void AddChatLineParameters(SqliteCommand cmd, string handle, ChatLine line)
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
    }

    /// <summary>Appends a single line to a "Me" topic thread.</summary>
    public void AppendOwnChat(string threadId, ChatLine line)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO own_chat(
                line_id, thread_id, role, text, via, status, at, internal, reasoning, sender_handle)
            VALUES($lid, $tid, $r, $x, $v, $s, $a, $i, $rz, $sender);
            """;
        cmd.Parameters.AddWithValue("$lid", line.Id);
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$s", line.Status);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.Parameters.AddWithValue("$i", line.Internal ? 1 : 0);
        cmd.Parameters.AddWithValue("$rz", (object?)line.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sender", (object?)line.SenderHandle ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts or replaces one persisted topic line by its stable id.</summary>
    public void UpsertOwnChat(string threadId, ChatLine line)
    {
        using var tx = conn.BeginTransaction();
        int updated;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE own_chat
                SET role = $r, text = $x, via = $v, status = $s, at = $a,
                    internal = $internal, reasoning = $reasoning, sender_handle = $sender
                WHERE thread_id = $tid AND line_id = $lid;
                """;
            AddOwnChatParameters(update, threadId, line);
            updated = update.ExecuteNonQuery();
        }
        if (updated == 0)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO own_chat(
                    line_id, thread_id, role, text, via, status, at, internal, reasoning, sender_handle)
                VALUES($lid, $tid, $r, $x, $v, $s, $a, $internal, $reasoning, $sender);
                """;
            AddOwnChatParameters(insert, threadId, line);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void AddOwnChatParameters(SqliteCommand cmd, string threadId, ChatLine line)
    {
        cmd.Parameters.AddWithValue("$lid", line.Id);
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$s", line.Status);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.Parameters.AddWithValue("$internal", line.Internal ? 1 : 0);
        cmd.Parameters.AddWithValue("$reasoning", (object?)line.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sender", (object?)line.SenderHandle ?? DBNull.Value);
    }

    /// <summary>Records that a "Me" thread exists so an empty thread survives a reload.</summary>
    public void EnsureOwnThread(string id, string title, DateTimeOffset createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO own_threads(id, title, created_at) VALUES($id, $t, $c);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", createdAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upserts complete topic metadata and its explicit order.</summary>
    public void UpsertOwnThread(string id, string title, DateTimeOffset createdAt, int sortOrder)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO own_threads(id, title, created_at, sort_order) VALUES($id, $title, $created, $sort)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                created_at = excluded.created_at,
                sort_order = excluded.sort_order;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        cmd.Parameters.AddWithValue("$sort", sortOrder);
        cmd.ExecuteNonQuery();
    }

    private void NormalizeOwnThreadOrder()
    {
        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM own_threads WHERE sort_order IS NULL;";
        if (Convert.ToInt64(count.ExecuteScalar()) == 0) return;

        using var tx = conn.BeginTransaction();
        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT id FROM own_threads ORDER BY COALESCE(sort_order, 2147483647), created_at, id;";
        var ids = new List<string>();
        using (var reader = read.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        for (var i = 0; i < ids.Count; i++)
        {
            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE own_threads SET sort_order = $o WHERE id = $id;";
            update.Parameters.AddWithValue("$o", i);
            update.Parameters.AddWithValue("$id", ids[i]);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Persists the complete user-defined order of "Me" threads atomically.</summary>
    public void ReorderOwnThreads(IReadOnlyList<string> orderedIds)
    {
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE own_threads SET sort_order = $o WHERE id = $id;";
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Renames a "Me" thread.</summary>
    public void RenameOwnThread(string id, string title)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE own_threads SET title = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clears a "Me" thread's messages but keeps the thread.</summary>
    public void ClearOwnThread(string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM own_chat WHERE thread_id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a "Me" thread and all its messages.</summary>
    public void DeleteOwnThread(string id)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM own_chat WHERE thread_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM own_threads WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Updates the delivery status of an outgoing line by its stable id.</summary>
    public void UpdateLineStatus(string lineId, string status)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_lines SET status = $s WHERE line_id = $lid;";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$lid", lineId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Updates the finalized text of an outgoing line by its stable id.</summary>
    public void UpdateLineText(string lineId, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chat_lines SET text = $t WHERE line_id = $lid;";
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$lid", lineId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes all message history for a conversation (keeps the conversation itself).</summary>
    public void ClearConversation(string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chat_lines WHERE handle = $h;";
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a conversation and all its message history.</summary>
    public void DeleteConversation(string handle)
    {
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM chat_lines WHERE handle = $h;";
            cmd.Parameters.AddWithValue("$h", handle);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM conversations WHERE handle = $h;";
            cmd.Parameters.AddWithValue("$h", handle);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>A single search hit across conversations and own-chat. ThreadId is set for "Me" hits.</summary>
    public sealed record SearchHit(string Handle, string Role, string Text, DateTimeOffset At, string? ThreadId);

    /// <summary>Full-text-ish search over all chat history (case-insensitive LIKE). Newest first.</summary>
    public List<SearchHit> Search(string query, int limit = 100)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        var like = "%" + query.Trim() + "%";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT handle, role, text, at, NULL AS thread_id FROM chat_lines WHERE text LIKE $q COLLATE NOCASE
            UNION ALL
            SELECT '(me)' AS handle, role, text, at, thread_id FROM own_chat
                WHERE thread_id IS NOT NULL AND internal = 0 AND text LIKE $q COLLATE NOCASE
            ORDER BY at DESC LIMIT $lim;";
        cmd.Parameters.AddWithValue("$q", like);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hits.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), ParseAt(r.GetString(3)),
                r.IsDBNull(4) ? null : r.GetString(4)));
        return hits;
    }

    // ---- helpers ------------------------------------------------------------

    private static DateTimeOffset ParseAt(string s)
        => DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var v)
            ? v : DateTimeOffset.UtcNow;

    private void Exec(string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { conn.Close(); } catch { }
        conn.Dispose();
    }
}
