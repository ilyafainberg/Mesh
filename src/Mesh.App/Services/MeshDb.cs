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
                handle TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                via TEXT NOT NULL,
                at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_chat_handle ON chat_lines(handle, id);
            CREATE TABLE IF NOT EXISTS own_chat(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                via TEXT NOT NULL,
                at TEXT NOT NULL);
            INSERT OR IGNORE INTO meta(k, v) VALUES('schema_version', '1');");
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
        profile.OwnChat = LoadOwnChat();
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
            cmd.CommandText = "SELECT handle FROM conversations ORDER BY created_at, handle;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) Get(r.GetString(0));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT handle, role, text, via, at FROM chat_lines ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var conv = Get(r.GetString(0));
                conv.Lines.Add(new ChatLine
                {
                    Role = r.GetString(1),
                    Text = r.GetString(2),
                    Via = r.GetString(3),
                    At = ParseAt(r.GetString(4))
                });
            }
        }
        return order;
    }

    private List<ChatLine> LoadOwnChat()
    {
        var lines = new List<ChatLine>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role, text, via, at FROM own_chat ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            lines.Add(new ChatLine
            {
                Role = r.GetString(0),
                Text = r.GetString(1),
                Via = r.GetString(2),
                At = ParseAt(r.GetString(3))
            });
        return lines;
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

    /// <summary>Appends a single line to a conversation's history (one insert, not a full rewrite).</summary>
    public void AppendChatLine(string handle, ChatLine line)
    {
        EnsureConversation(handle);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chat_lines(handle, role, text, via, at) VALUES($h, $r, $x, $v, $a);";
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Appends a single line to the owner's own-agent chat.</summary>
    public void AppendOwnChat(ChatLine line)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO own_chat(role, text, via, at) VALUES($r, $x, $v, $a);";
        cmd.Parameters.AddWithValue("$r", line.Role);
        cmd.Parameters.AddWithValue("$x", line.Text);
        cmd.Parameters.AddWithValue("$v", line.Via);
        cmd.Parameters.AddWithValue("$a", line.At.ToString("O"));
        cmd.ExecuteNonQuery();
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
