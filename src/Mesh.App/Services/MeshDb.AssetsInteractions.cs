using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;

namespace Mesh.App.Services;

// Asset/interaction schema initialisation and CRUD methods for ask-user prompts,
// suspended contexts, assets, asset content and replicated attachment data.
//
// Protocol 9 is greenfield: assets carry no private outbox of their own. Every non-LocalOnly
// asset mutation is emitted as a signed replication event whose target references live in
// replication_outbox, inside the same transaction that writes the actual asset rows.
public sealed partial class MeshDb
{
    private static readonly JsonSerializerOptions AssetsInteractionsJson =
        new(JsonSerializerDefaults.Web);

    // Called from CreateSchema() after all existing migrations.
    internal void CreateAssetsInteractionsSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS ask_user_prompts(
                prompt_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                question TEXT NOT NULL,
                options_json TEXT NOT NULL,
                recommended_index INTEGER,
                state TEXT NOT NULL DEFAULT 'pending',
                selection TEXT,
                origin_device_id TEXT,
                resolution_device_id TEXT,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                resolved_at TEXT,
                revision INTEGER NOT NULL DEFAULT 1,
                version INTEGER NOT NULL DEFAULT 1,
                idempotency_token TEXT);
            CREATE INDEX IF NOT EXISTS ix_ask_user_prompts_thread_state
                ON ask_user_prompts(thread_id, state, created_at);

            CREATE TABLE IF NOT EXISTS ask_user_suspended_contexts(
                context_id TEXT PRIMARY KEY,
                prompt_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                context_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT,
                resumed_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_ask_user_suspended_contexts_prompt
                ON ask_user_suspended_contexts(prompt_id);

            CREATE TABLE IF NOT EXISTS assets(
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                name TEXT NOT NULL,
                metadata_json TEXT,
                content_mime TEXT,
                content_hash TEXT,
                content_byte_count INTEGER NOT NULL DEFAULT 0,
                version INTEGER NOT NULL DEFAULT 1,
                source_device_id TEXT,
                updated_at TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                local_only INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(kind, id));
            CREATE INDEX IF NOT EXISTS ix_assets_kind_updated
                ON assets(kind, updated_at DESC, id);

            CREATE TABLE IF NOT EXISTS asset_content(
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                bytes BLOB NOT NULL,
                sha256 TEXT NOT NULL,
                PRIMARY KEY(kind, id));

            CREATE TABLE IF NOT EXISTS replicated_attachments(
                attachment_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                name TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                byte_count INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                bytes BLOB NOT NULL,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_replicated_attachments_run
                ON replicated_attachments(run_id, attachment_id);

            INSERT OR IGNORE INTO meta(k, v) VALUES('assets_interactions_schema_version', '2');
            """);
    }

    // ------------------------------------------------------------------
    // Replicated attachment data (the ACTUAL local attachment rows)
    // ------------------------------------------------------------------

    /// <summary>Reads one assembled attachment, or null when it has not landed yet.</summary>
    public (string RunId, string Name, string MimeType, string Sha256, byte[] Bytes)? GetReplicatedAttachment(
        string attachmentId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, name, mime_type, sha256, bytes
            FROM replicated_attachments WHERE attachment_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", attachmentId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (byte[])r.GetValue(4));
    }

    /// <summary>
    /// Stages one local attachment durably in its own transaction. Used only when the owner has no
    /// other device to receive the transfer; otherwise the staging row commits with the chunk events.
    /// </summary>
    public void SaveReplicatedAttachment(
        string attachmentId, string runId, string name, string mimeType,
        string sha256, byte[] bytes, DateTimeOffset createdAt)
    {
        using var tx = conn.BeginTransaction();
        Protocol9DomainTables.UpsertReplicatedAttachment(
            conn, tx, attachmentId, runId, name, mimeType, sha256, bytes, createdAt);
        tx.Commit();
    }

    /// <summary>Counts the assembled attachments staged for a run.</summary>
    public int CountReplicatedAttachments(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM replicated_attachments WHERE run_id = $run;";
        cmd.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Ask-user prompts
    // ------------------------------------------------------------------

    public void InsertAskUserPrompt(AskUserPrompt prompt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ask_user_prompts(
                prompt_id, thread_id, run_id, question, options_json, recommended_index,
                state, selection, origin_device_id, resolution_device_id,
                created_at, expires_at, resolved_at, revision, version, idempotency_token)
            VALUES(
                $promptId, $threadId, $runId, $question, $options, $recommended,
                $state, $selection, $origin, $resolution,
                $created, $expires, $resolved, $revision, $version, NULL);
            """;
        BindAskUserPrompt(cmd, prompt);
        cmd.ExecuteNonQuery();
    }

    public AskUserPrompt? GetAskUserPrompt(string promptId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ask_user_prompts WHERE prompt_id = $id;";
        cmd.Parameters.AddWithValue("$id", promptId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAskUserPrompt(r) : null;
    }

    public IReadOnlyList<AskUserPrompt> ListPendingAskUserPrompts(string threadId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ask_user_prompts
            WHERE thread_id = $tid AND state = 'pending'
            ORDER BY created_at, prompt_id;
            """;
        cmd.Parameters.AddWithValue("$tid", threadId);
        using var r = cmd.ExecuteReader();
        var result = new List<AskUserPrompt>();
        while (r.Read()) result.Add(ReadAskUserPrompt(r));
        return result;
    }

    public IReadOnlyList<AskUserPrompt> ListAllPendingAskUserPrompts()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ask_user_prompts
            WHERE state = 'pending'
            ORDER BY created_at, prompt_id;
            """;
        using var r = cmd.ExecuteReader();
        var result = new List<AskUserPrompt>();
        while (r.Read()) result.Add(ReadAskUserPrompt(r));
        return result;
    }

    public IReadOnlyList<AskUserPrompt> ListResolvedAskUserPrompts(string resolutionDeviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ask_user_prompts
            WHERE state = 'resolved' AND resolution_device_id = $device
            ORDER BY resolved_at, prompt_id;
            """;
        cmd.Parameters.AddWithValue("$device", resolutionDeviceId);
        using var r = cmd.ExecuteReader();
        var result = new List<AskUserPrompt>();
        while (r.Read()) result.Add(ReadAskUserPrompt(r));
        return result;
    }

    /// <summary>
    /// Atomically resolves the prompt identified by <paramref name="promptId"/> in a single
    /// transaction. The transaction first expires the prompt when it is pending and at or
    /// past its <c>expires_at</c>, then resolves only a still-pending row via a
    /// <c>WHERE state='pending'</c> fence so only the first writer wins. The current row is
    /// always returned (win or loss); the caller inspects <c>Selection</c> and
    /// <c>ResolutionDeviceId</c> to determine whether their resolution was accepted.
    /// Re-issuing the same <paramref name="idempotencyToken"/> after a win is a no-op that
    /// returns the winning row.
    /// </summary>
    public AskUserPrompt ResolveAskUserPrompt(
        string promptId,
        string selection,
        string resolutionDeviceId,
        string idempotencyToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var tx = conn.BeginTransaction(deferred: false);

        // 1. Expire the prompt if it is pending and its deadline has passed.
        using (var expire = conn.CreateCommand())
        {
            expire.Transaction = tx;
            expire.CommandText = """
                UPDATE ask_user_prompts
                SET state = 'expired', revision = revision + 1
                WHERE prompt_id = $id AND state = 'pending'
                  AND expires_at IS NOT NULL AND julianday(expires_at) <= julianday($now);
                """;
            expire.Parameters.AddWithValue("$id", promptId);
            expire.Parameters.AddWithValue("$now", now);
            expire.ExecuteNonQuery();
        }

        // 2. Resolve only if still pending (fence). Loser affects zero rows.
        using (var resolve = conn.CreateCommand())
        {
            resolve.Transaction = tx;
            resolve.CommandText = """
                UPDATE ask_user_prompts
                SET state = 'resolved',
                    selection = $selection,
                    resolution_device_id = $device,
                    resolved_at = $resolvedAt,
                    revision = revision + 1,
                    idempotency_token = $token
                WHERE prompt_id = $id AND state = 'pending';
                """;
            resolve.Parameters.AddWithValue("$id", promptId);
            resolve.Parameters.AddWithValue("$selection", selection);
            resolve.Parameters.AddWithValue("$device", resolutionDeviceId);
            resolve.Parameters.AddWithValue("$resolvedAt", now);
            resolve.Parameters.AddWithValue("$token", idempotencyToken);
            resolve.ExecuteNonQuery();
        }

        var current = GetAskUserPromptInTransaction(tx, promptId)
            ?? throw new InvalidOperationException($"Ask-user prompt '{promptId}' not found.");
        tx.Commit();
        return current;
    }

    /// <summary>
    /// Transitions the prompt to <c>expired</c> if it is currently pending.
    /// Returns the current row after the attempted transition.
    /// </summary>
    public AskUserPrompt ExpireAskUserPrompt(string promptId)
    {
        UpdateAskUserPromptState(promptId, "expired");
        return GetAskUserPrompt(promptId)
            ?? throw new InvalidOperationException($"Ask-user prompt '{promptId}' not found.");
    }

    /// <summary>
    /// Transitions the prompt to <c>cancelled</c> if it is currently pending.
    /// Returns the current row after the attempted transition.
    /// </summary>
    public AskUserPrompt CancelAskUserPrompt(string promptId)
    {
        UpdateAskUserPromptState(promptId, "cancelled");
        return GetAskUserPrompt(promptId)
            ?? throw new InvalidOperationException($"Ask-user prompt '{promptId}' not found.");
    }

    private void UpdateAskUserPromptState(string promptId, string newState)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ask_user_prompts
            SET state = $state, revision = revision + 1
            WHERE prompt_id = $id AND state = 'pending';
            """;
        cmd.Parameters.AddWithValue("$id", promptId);
        cmd.Parameters.AddWithValue("$state", newState);
        cmd.ExecuteNonQuery();
    }

    private AskUserPrompt? GetAskUserPromptInTransaction(SqliteTransaction tx, string promptId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM ask_user_prompts WHERE prompt_id = $id;";
        cmd.Parameters.AddWithValue("$id", promptId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAskUserPrompt(r) : null;
    }

    private static void BindAskUserPrompt(SqliteCommand cmd, AskUserPrompt p)
    {
        cmd.Parameters.AddWithValue("$promptId", p.PromptId);
        cmd.Parameters.AddWithValue("$threadId", p.ThreadId);
        cmd.Parameters.AddWithValue("$runId", p.RunId);
        cmd.Parameters.AddWithValue("$question", p.Question);
        cmd.Parameters.AddWithValue("$options",
            JsonSerializer.Serialize(p.Options, AssetsInteractionsJson));
        cmd.Parameters.AddWithValue("$recommended",
            p.RecommendedIndex.HasValue ? (object)p.RecommendedIndex.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$state", p.State.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$selection", (object?)p.Selection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origin", (object?)p.OriginDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resolution",
            (object?)p.ResolutionDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", p.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires",
            p.ExpiresAt.HasValue ? (object)p.ExpiresAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$resolved",
            p.ResolvedAt.HasValue ? (object)p.ResolvedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$revision", p.Revision);
        cmd.Parameters.AddWithValue("$version", p.Version);
    }

    private static AskUserPrompt ReadAskUserPrompt(SqliteDataReader r)
    {
        var options = JsonSerializer.Deserialize<List<AskUserOption>>(
            r.GetString(r.GetOrdinal("options_json")), AssetsInteractionsJson)
            ?? [];
        var rawState = r.GetString(r.GetOrdinal("state"));
        var state = Enum.TryParse<AskUserState>(rawState, ignoreCase: true, out var s)
            ? s : AskUserState.Pending;
        var riOrd = r.GetOrdinal("recommended_index");
        return new AskUserPrompt(
            PromptId: r.GetString(r.GetOrdinal("prompt_id")),
            ThreadId: r.GetString(r.GetOrdinal("thread_id")),
            RunId: r.GetString(r.GetOrdinal("run_id")),
            Question: r.GetString(r.GetOrdinal("question")),
            Options: options,
            RecommendedIndex: r.IsDBNull(riOrd) ? null : r.GetInt32(riOrd),
            State: state,
            Selection: r.IsDBNull(r.GetOrdinal("selection"))
                ? null : r.GetString(r.GetOrdinal("selection")),
            OriginDeviceId: r.IsDBNull(r.GetOrdinal("origin_device_id"))
                ? null : r.GetString(r.GetOrdinal("origin_device_id")),
            ResolutionDeviceId: r.IsDBNull(r.GetOrdinal("resolution_device_id"))
                ? null : r.GetString(r.GetOrdinal("resolution_device_id")),
            CreatedAt: ParseAt(r.GetString(r.GetOrdinal("created_at"))),
            ExpiresAt: r.IsDBNull(r.GetOrdinal("expires_at"))
                ? null : ParseAt(r.GetString(r.GetOrdinal("expires_at"))),
            ResolvedAt: r.IsDBNull(r.GetOrdinal("resolved_at"))
                ? null : ParseAt(r.GetString(r.GetOrdinal("resolved_at"))),
            Revision: r.GetInt32(r.GetOrdinal("revision")),
            Version: r.GetInt32(r.GetOrdinal("version")));
    }

    // ------------------------------------------------------------------
    // Suspended contexts
    // ------------------------------------------------------------------

    public void SaveSuspendedContext(SuspendedAgentContext ctx)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ask_user_suspended_contexts(
                context_id, prompt_id, thread_id, run_id, context_json,
                created_at, expires_at, resumed_at)
            VALUES($ctxId, $promptId, $tid, $runId, $json, $created, $expires, $resumed)
            ON CONFLICT(context_id) DO UPDATE SET
                context_json = excluded.context_json,
                expires_at = excluded.expires_at,
                resumed_at = excluded.resumed_at;
            """;
        cmd.Parameters.AddWithValue("$ctxId", ctx.ContextId);
        cmd.Parameters.AddWithValue("$promptId", ctx.PromptId);
        cmd.Parameters.AddWithValue("$tid", ctx.ThreadId);
        cmd.Parameters.AddWithValue("$runId", ctx.RunId);
        cmd.Parameters.AddWithValue("$json", ctx.ContextJson);
        cmd.Parameters.AddWithValue("$created", ctx.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$expires",
            ctx.ExpiresAt.HasValue ? (object)ctx.ExpiresAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$resumed",
            ctx.ResumedAt.HasValue ? (object)ctx.ResumedAt.Value.ToString("O") : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public SuspendedAgentContext? GetSuspendedContext(string contextId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ask_user_suspended_contexts WHERE context_id = $id;";
        cmd.Parameters.AddWithValue("$id", contextId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSuspendedContext(r) : null;
    }

    /// <summary>
    /// Marks a suspended context as resumed exactly once. The UPDATE is fenced with
    /// <c>resumed_at IS NULL</c> and an unexpired deadline, so under concurrent callers
    /// exactly one observes a single affected row (returns true) and the rest return false.
    /// An already-expired context is never resumed.
    /// </summary>
    public bool MarkContextResumed(string contextId, DateTimeOffset resumedAt)
    {
        var at = resumedAt.ToString("O");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ask_user_suspended_contexts
            SET resumed_at = $resumed
            WHERE context_id = $id AND resumed_at IS NULL
              AND (expires_at IS NULL OR julianday(expires_at) > julianday($resumed));
            """;
        cmd.Parameters.AddWithValue("$id", contextId);
        cmd.Parameters.AddWithValue("$resumed", at);
        return cmd.ExecuteNonQuery() == 1;
    }

    private static SuspendedAgentContext ReadSuspendedContext(SqliteDataReader r)
        => new(
            ContextId: r.GetString(r.GetOrdinal("context_id")),
            PromptId: r.GetString(r.GetOrdinal("prompt_id")),
            ThreadId: r.GetString(r.GetOrdinal("thread_id")),
            RunId: r.GetString(r.GetOrdinal("run_id")),
            ContextJson: r.GetString(r.GetOrdinal("context_json")),
            CreatedAt: ParseAt(r.GetString(r.GetOrdinal("created_at"))),
            ExpiresAt: r.IsDBNull(r.GetOrdinal("expires_at"))
                ? null : ParseAt(r.GetString(r.GetOrdinal("expires_at"))),
            ResumedAt: r.IsDBNull(r.GetOrdinal("resumed_at"))
                ? null : ParseAt(r.GetString(r.GetOrdinal("resumed_at"))));

    // ------------------------------------------------------------------
    // Assets
    // ------------------------------------------------------------------

    public IReadOnlyList<AssetRecord> PageAssetSummaries(
        AssetKind kind, int pageSize, string? afterId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, id, name, metadata_json, content_mime, content_hash,
                   content_byte_count, version, source_device_id, updated_at,
                   is_deleted, local_only
            FROM assets
            WHERE kind = $kind AND ($afterId IS NULL OR id > $afterId)
            ORDER BY id
            LIMIT $pageSize;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$afterId", (object?)afterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pageSize", pageSize);
        using var r = cmd.ExecuteReader();
        var result = new List<AssetRecord>();
        while (r.Read()) result.Add(ReadAssetSummary(r));
        return result;
    }

    public (AssetRecord Summary, byte[] Content)? GetFullAsset(AssetKind kind, string id)
    {
        var summary = GetAssetSummary(kind, id);
        if (summary is null) return null;

        byte[] content = [];
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT bytes FROM asset_content WHERE kind = $kind AND id = $id;";
            cmd.Parameters.AddWithValue("$kind", kind.ToString());
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0))
                content = (byte[])r.GetValue(0);
        }
        return (summary, content);
    }

    /// <summary>
    /// Persists a caller-initiated asset. Content hash and byte count are computed from
    /// <paramref name="content"/>; the content row (including its SHA-256) is always stored,
    /// even when the content is empty. Used for LocalOnly (mobile) assets and for hydration:
    /// a replicated asset is written by the domain materialiser inside its event transaction.
    /// </summary>
    public void UpsertAsset(AssetRecord summary, byte[] content)
    {
        var (sha, count) = HashAndVerify(summary, content);
        var normalized = summary with { ContentHash = sha, ContentByteCount = count };
        using var tx = conn.BeginTransaction(deferred: false);
        WriteAssetSummaryRow(tx, normalized);
        WriteAssetContentRow(tx, normalized.Kind, normalized.Id, content, sha);
        tx.Commit();
    }

    /// <summary>
    /// Tombstones a local asset. The tombstone takes the existing version + 1 and preserves
    /// the stored <see cref="AssetRecord.LocalOnly"/> flag; content is removed. The generated
    /// tombstone is returned.
    /// </summary>
    public AssetRecord DeleteAsset(AssetKind kind, string id, string sourceDeviceId)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        using var tx = conn.BeginTransaction(deferred: false);

        var existing = GetAssetSummaryInTransaction(tx, kind, id);
        int newVersion = (existing?.Version ?? 0) + 1;
        bool localOnly = existing?.LocalOnly ?? false;

        var tombstone = new AssetRecord(
            Kind: kind,
            Id: id,
            Name: existing?.Name ?? string.Empty,
            MetadataJson: existing?.MetadataJson,
            ContentMime: existing?.ContentMime,
            ContentHash: null,
            ContentByteCount: 0,
            Version: newVersion,
            SourceDeviceId: sourceDeviceId,
            UpdatedAt: updatedAt,
            IsDeleted: true,
            LocalOnly: localOnly);

        WriteTombstoneRow(tx, tombstone);
        DeleteAssetContentRow(tx, kind, id);
        tx.Commit();
        return tombstone;
    }

    /// <summary>
    /// Applies a remote upsert using the single deterministic conflict rule
    /// (<see cref="AssetConflict.RemoteWins"/>). Content hash/byte count are recomputed and
    /// the content row is always stored. Returns true when applied, false when rejected.
    /// </summary>
    public bool ApplyRemoteAssetUpsert(AssetRecord summary, byte[] content)
    {
        var (sha, count) = HashAndVerify(summary, content);
        var incoming = summary with { ContentHash = sha, ContentByteCount = count, IsDeleted = false };
        using var tx = conn.BeginTransaction(deferred: false);
        var existing = GetAssetSummaryInTransaction(tx, incoming.Kind, incoming.Id);
        if (!AssetConflict.RemoteWins(existing, incoming))
        {
            tx.Rollback();
            return false;
        }
        WriteAssetSummaryRow(tx, incoming);
        WriteAssetContentRow(tx, incoming.Kind, incoming.Id, content, sha);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Applies a remote delete tombstone using the single deterministic conflict rule
    /// (<see cref="AssetConflict.RemoteWins"/>). Content is removed. Returns true when applied.
    /// </summary>
    public bool ApplyRemoteAssetDelete(AssetRecord tombstone)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        var existing = GetAssetSummaryInTransaction(tx, tombstone.Kind, tombstone.Id);
        if (!AssetConflict.RemoteWins(existing, tombstone))
        {
            tx.Rollback();
            return false;
        }
        WriteTombstoneRow(tx, tombstone);
        DeleteAssetContentRow(tx, tombstone.Kind, tombstone.Id);
        tx.Commit();
        return true;
    }

    private AssetRecord? GetAssetSummary(AssetKind kind, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = AssetSummarySelectById;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAssetSummary(r) : null;
    }

    private AssetRecord? GetAssetSummaryInTransaction(SqliteTransaction tx, AssetKind kind, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = AssetSummarySelectById;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadAssetSummary(r) : null;
    }

    private const string AssetSummarySelectById = """
        SELECT kind, id, name, metadata_json, content_mime, content_hash,
               content_byte_count, version, source_device_id, updated_at,
               is_deleted, local_only
        FROM assets WHERE kind = $kind AND id = $id;
        """;

    private void WriteAssetSummaryRow(SqliteTransaction tx, AssetRecord summary)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO assets(
                kind, id, name, metadata_json, content_mime, content_hash,
                content_byte_count, version, source_device_id, updated_at,
                is_deleted, local_only)
            VALUES(
                $kind, $id, $name, $meta, $mime, $hash,
                $bytes, $version, $device, $updated, 0, $local)
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
            """;
        cmd.Parameters.AddWithValue("$kind", summary.Kind.ToString());
        cmd.Parameters.AddWithValue("$id", summary.Id);
        cmd.Parameters.AddWithValue("$name", summary.Name);
        cmd.Parameters.AddWithValue("$meta", (object?)summary.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mime", (object?)summary.ContentMime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)summary.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytes", summary.ContentByteCount);
        cmd.Parameters.AddWithValue("$version", summary.Version);
        cmd.Parameters.AddWithValue("$device",
            (object?)summary.SourceDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", summary.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$local", summary.LocalOnly ? 1L : 0L);
        cmd.ExecuteNonQuery();
    }

    private void WriteTombstoneRow(SqliteTransaction tx, AssetRecord tombstone)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO assets(
                kind, id, name, metadata_json, content_mime, content_hash,
                content_byte_count, version, source_device_id, updated_at,
                is_deleted, local_only)
            VALUES(
                $kind, $id, $name, $meta, $mime, NULL,
                0, $version, $device, $updated, 1, $local)
            ON CONFLICT(kind, id) DO UPDATE SET
                content_hash = NULL,
                content_byte_count = 0,
                version = excluded.version,
                source_device_id = excluded.source_device_id,
                updated_at = excluded.updated_at,
                is_deleted = 1;
            """;
        cmd.Parameters.AddWithValue("$kind", tombstone.Kind.ToString());
        cmd.Parameters.AddWithValue("$id", tombstone.Id);
        cmd.Parameters.AddWithValue("$name", tombstone.Name);
        cmd.Parameters.AddWithValue("$meta", (object?)tombstone.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mime", (object?)tombstone.ContentMime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", tombstone.Version);
        cmd.Parameters.AddWithValue("$device",
            (object?)tombstone.SourceDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", tombstone.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$local", tombstone.LocalOnly ? 1L : 0L);
        cmd.ExecuteNonQuery();
    }

    private void WriteAssetContentRow(
        SqliteTransaction tx, AssetKind kind, string id, byte[] content, string sha)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO asset_content(kind, id, bytes, sha256)
            VALUES($kind, $id, $bytes, $sha)
            ON CONFLICT(kind, id) DO UPDATE SET
                bytes = excluded.bytes,
                sha256 = excluded.sha256;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$bytes", content);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.ExecuteNonQuery();
    }

    private void DeleteAssetContentRow(SqliteTransaction tx, AssetKind kind, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM asset_content WHERE kind = $kind AND id = $id;";
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static (string Sha, long Count) HashContent(byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return (hash, content.LongLength);
    }

    /// <summary>
    /// Computes the SHA-256 and byte count from <paramref name="content"/> and rejects a
    /// caller-supplied value that disagrees: a non-null <see cref="AssetRecord.ContentHash"/>
    /// that differs, or a non-zero <see cref="AssetRecord.ContentByteCount"/> that differs,
    /// throws <see cref="InvalidOperationException"/>. The computed values are authoritative
    /// and are what gets persisted.
    /// </summary>
    private static (string Sha, long Count) HashAndVerify(AssetRecord summary, byte[] content)
    {
        var (sha, count) = HashContent(content);
        if (summary.ContentHash is not null
            && !string.Equals(summary.ContentHash, sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Content hash mismatch for asset '{summary.Id}': "
                + $"declared '{summary.ContentHash}' but content hashes to '{sha}'.");
        if (summary.ContentByteCount != 0 && summary.ContentByteCount != count)
            throw new InvalidOperationException(
                $"Content byte count mismatch for asset '{summary.Id}': "
                + $"declared {summary.ContentByteCount} but content is {count} bytes.");
        return (sha, count);
    }

    private static AssetRecord ReadAssetSummary(SqliteDataReader r)
    {
        var rawKind = r.GetString(r.GetOrdinal("kind"));
        var kind = Enum.TryParse<AssetKind>(rawKind, ignoreCase: true, out var k)
            ? k : AssetKind.Skill;
        return new AssetRecord(
            Kind: kind,
            Id: r.GetString(r.GetOrdinal("id")),
            Name: r.GetString(r.GetOrdinal("name")),
            MetadataJson: r.IsDBNull(r.GetOrdinal("metadata_json"))
                ? null : r.GetString(r.GetOrdinal("metadata_json")),
            ContentMime: r.IsDBNull(r.GetOrdinal("content_mime"))
                ? null : r.GetString(r.GetOrdinal("content_mime")),
            ContentHash: r.IsDBNull(r.GetOrdinal("content_hash"))
                ? null : r.GetString(r.GetOrdinal("content_hash")),
            ContentByteCount: r.GetInt64(r.GetOrdinal("content_byte_count")),
            Version: r.GetInt32(r.GetOrdinal("version")),
            SourceDeviceId: r.IsDBNull(r.GetOrdinal("source_device_id"))
                ? null : r.GetString(r.GetOrdinal("source_device_id")),
            UpdatedAt: ParseAt(r.GetString(r.GetOrdinal("updated_at"))),
            IsDeleted: r.GetInt64(r.GetOrdinal("is_deleted")) != 0,
            LocalOnly: r.GetInt64(r.GetOrdinal("local_only")) != 0);
    }


    // ------------------------------------------------------------------
    // Batch helper (for efficient bulk testing and import)
    // ------------------------------------------------------------------

    /// <summary>
    /// Inserts or replaces many asset summary rows in a single transaction.
    /// Content is not stored; this is intended for bulk summary population only.
    /// </summary>
    internal void BulkInsertAssetSummaries(IEnumerable<AssetRecord> summaries)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        foreach (var s in summaries)
            WriteAssetSummaryRow(tx, s);
        tx.Commit();
    }
}
