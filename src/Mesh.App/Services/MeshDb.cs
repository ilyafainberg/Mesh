using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;

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
            INSERT OR IGNORE INTO meta(k, v) VALUES('schema_version', '1');");

        // Idempotent migration for databases created before line_id/status existed.
        AddColumnIfMissing("chat_lines", "line_id", "TEXT");
        AddColumnIfMissing("chat_lines", "status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("own_chat", "line_id", "TEXT");
        AddColumnIfMissing("own_chat", "status", "TEXT NOT NULL DEFAULT ''");
        // Service-thread metadata on conversations (null for normal person DMs).
        AddColumnIfMissing("conversations", "service_id", "TEXT");
        AddColumnIfMissing("conversations", "service_name", "TEXT");
        AddColumnIfMissing("conversations", "provider_handle", "TEXT");
        AddColumnIfMissing("own_chat", "thread_id", "TEXT");
        // Transcript + reasoning persistence: internal lines are the model's hidden execution record;
        // reasoning is the collapsible "thinking" (previously not persisted, so lost on restart).
        AddColumnIfMissing("own_chat", "internal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("own_chat", "reasoning", "TEXT");
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
            cmd.CommandText = "SELECT handle, service_id, service_name, provider_handle FROM conversations ORDER BY created_at, handle;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var c = Get(r.GetString(0));
                if (!r.IsDBNull(1)) c.ServiceId = r.GetString(1);
                if (!r.IsDBNull(2)) c.ServiceName = r.GetString(2);
                if (!r.IsDBNull(3)) c.ProviderHandle = r.GetString(3);
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT handle, role, text, via, at, line_id, status FROM chat_lines ORDER BY id;";
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
                    Status = r.IsDBNull(6) ? "" : r.GetString(6)
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
            cmd.CommandText = "SELECT thread_id, role, text, via, at, line_id, status, internal, reasoning FROM own_chat WHERE thread_id IS NOT NULL ORDER BY id;";
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
                    Reasoning = r.IsDBNull(8) ? null : r.GetString(8)
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
        var node = JsonSerializer.SerializeToNode(profile, JsonOpts)!.AsObject();
        node.Remove("conversations");
        node.Remove("ownChat");
        node.Remove("ownThreads");
        var json = node.ToJsonString(JsonOpts);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO profile(id, json) VALUES(1, $j) ON CONFLICT(id) DO UPDATE SET json = $j;";
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records that a conversation thread exists so an empty thread survives a reload.</summary>
    public void EnsureConversation(string handle)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO conversations(handle, created_at) VALUES($h, $t);";
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
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

    /// <summary>Appends a single line to a conversation's history (one insert, not a full rewrite).</summary>
    public void AppendChatLine(string handle, ChatLine line)
    {
        EnsureConversation(handle);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chat_lines(line_id, handle, role, text, via, status, at) VALUES($lid, $h, $r, $x, $v, $s, $a);";
        cmd.Parameters.AddWithValue("$lid", line.Id);
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$s", line.Status);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Appends a single line to a "Me" topic thread.</summary>
    public void AppendOwnChat(string threadId, ChatLine line)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO own_chat(line_id, thread_id, role, text, via, status, at, internal, reasoning) VALUES($lid, $tid, $r, $x, $v, $s, $a, $i, $rz);";
        cmd.Parameters.AddWithValue("$lid", line.Id);
        cmd.Parameters.AddWithValue("$tid", threadId);
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$s", line.Status);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.Parameters.AddWithValue("$i", line.Internal ? 1 : 0);
        cmd.Parameters.AddWithValue("$rz", (object?)line.Reasoning ?? DBNull.Value);
        cmd.ExecuteNonQuery();
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
