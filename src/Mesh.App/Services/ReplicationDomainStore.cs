using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Protocol-9 durable domain convergence surface. This is the single writer for the
/// replicated domain state that both the local emitter (via its in-transaction domain
/// work) and the inbound projection converge onto. Every write is transaction-composable
/// (takes an explicit <see cref="SqliteConnection"/> / <see cref="SqliteTransaction"/>)
/// so a domain change and the immutable event that carries it commit or roll back as one.
///
/// Chat-graph entities (message / conversation / topic / topic line / contact / circle /
/// memory / attachment / asset) land in the neutral <c>replication_domain_entities</c> and
/// <c>replication_domain_lines</c> tables with deterministic causal last-writer-wins.
/// Read watermarks, custody entries and skill-package chunks land in their dedicated
/// replication tables. The store is free of MAUI / relay dependencies so it can be driven
/// directly in tests.
/// </summary>
public static class ReplicationDomainStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Hard bound on a replicated skill-package transfer (spec item 3: 20&#160;MB).</summary>
    public const long MaxPackageBytes = 20L * 1024 * 1024;

    /// <summary>Maximum raw bytes a single transfer chunk may carry.</summary>
    public const long MaxChunkBytes = 400L * 1024;

    // -----------------------------------------------------------------------
    // Schema (idempotent, created lazily on first use so no MeshDb.Open edit is required).
    // -----------------------------------------------------------------------

    /// <summary>Creates the projected-domain tables if absent. Idempotent; safe on every call.</summary>
    public static void EnsureSchema(SqliteConnection conn, SqliteTransaction? tx)
    {
        ArgumentNullException.ThrowIfNull(conn);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS replication_domain_entities(
                kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                conversation_id TEXT,
                causal_version TEXT NOT NULL,
                tiebreak TEXT NOT NULL,
                body TEXT NOT NULL,
                deleted INTEGER NOT NULL DEFAULT 0,
                origin_account TEXT,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY(kind, entity_id));

            CREATE TABLE IF NOT EXISTS replication_domain_lines(
                kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                line_id TEXT NOT NULL,
                conversation_id TEXT,
                causal_version TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY(kind, entity_id, line_id));
            CREATE INDEX IF NOT EXISTS ix_replication_domain_lines_entity
                ON replication_domain_lines(kind, entity_id, created_at);

            CREATE TABLE IF NOT EXISTS replication_package_chunks(
                package_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                chunk_count INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                chunk_b64 TEXT NOT NULL,
                name TEXT,
                mime_type TEXT,
                run_id TEXT,
                PRIMARY KEY(package_id, chunk_index));
            """;
        cmd.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // Deterministic causal ordering. Longer version strings dominate (so "v10" > "v2"),
    // ties break by ordinal, and equal causal versions break by the event id tiebreak so
    // every device converges on the same winner regardless of arrival order.
    // -----------------------------------------------------------------------

    /// <summary>Deterministic total order over causal-version strings.</summary>
    public static int CompareCausal(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        return string.CompareOrdinal(a, b);
    }

    private static bool Wins(string incomingCausal, string incomingTiebreak, string storedCausal, string storedTiebreak)
    {
        var c = CompareCausal(incomingCausal, storedCausal);
        if (c != 0) return c > 0;
        return string.CompareOrdinal(incomingTiebreak, storedTiebreak) > 0;
    }

    // -----------------------------------------------------------------------
    // Entity upsert / delete (causal LWW).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies a causal last-writer-wins upsert (or tombstone) for a domain entity. Returns true
    /// when the incoming write won and the convergence row was updated, so the caller can gate the
    /// matching mutation of the ACTUAL domain table on the very same decision.
    /// </summary>
    public static bool UpsertEntity(
        SqliteConnection conn,
        SqliteTransaction tx,
        string kind,
        string entityId,
        string? conversationId,
        string causalVersion,
        string tiebreak,
        string bodyJson,
        string? originAccount,
        bool deleted,
        long updatedAtUnixMs)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        EnsureSchema(conn, tx);

        var current = ReadEntityRow(conn, tx, kind, entityId);
        if (current is not null && !Wins(causalVersion, tiebreak, current.Value.Causal, current.Value.Tiebreak))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_domain_entities(
                kind, entity_id, conversation_id, causal_version, tiebreak, body, deleted, origin_account, updated_at)
            VALUES($kind, $eid, $conv, $causal, $tie, $body, $deleted, $account, $updated)
            ON CONFLICT(kind, entity_id) DO UPDATE SET
                conversation_id = excluded.conversation_id,
                causal_version = excluded.causal_version,
                tiebreak = excluded.tiebreak,
                body = excluded.body,
                deleted = excluded.deleted,
                origin_account = excluded.origin_account,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$conv", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$causal", causalVersion);
        cmd.Parameters.AddWithValue("$tie", tiebreak);
        cmd.Parameters.AddWithValue("$body", bodyJson);
        cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$account", (object?)originAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", updatedAtUnixMs);
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// Appends a line to an entity's ordered line log. Exact-once by (kind, entity, line); returns
    /// true only when this call inserted the line, so a duplicate event never re-applies it.
    /// </summary>
    public static bool AppendLine(
        SqliteConnection conn,
        SqliteTransaction tx,
        string kind,
        string entityId,
        string lineId,
        string? conversationId,
        string causalVersion,
        string bodyJson,
        long createdAtUnixMs)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        EnsureSchema(conn, tx);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_domain_lines(
                kind, entity_id, line_id, conversation_id, causal_version, body, created_at)
            VALUES($kind, $eid, $line, $conv, $causal, $body, $created)
            ON CONFLICT(kind, entity_id, line_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$line", lineId);
        cmd.Parameters.AddWithValue("$conv", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$causal", causalVersion);
        cmd.Parameters.AddWithValue("$body", bodyJson);
        cmd.Parameters.AddWithValue("$created", createdAtUnixMs);
        return cmd.ExecuteNonQuery() == 1;
    }

    // -----------------------------------------------------------------------
    // ask_user prompt / resolution. First-writer-wins resolution: once an entity is
    // resolved it never reverts, and an out-of-order prompt arriving after its resolution
    // does not clobber the resolved state (the resolution carries the prompt snapshot).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Projects an ask_user prompt; skipped if a resolution already landed for the entity.
    /// Returns true when the prompt won and should also be written to the real prompt table.
    /// </summary>
    public static bool AskUserPrompt(
        SqliteConnection conn, SqliteTransaction tx,
        string entityId, string? conversationId, string causalVersion, string tiebreak,
        string bodyJson, string? originAccount, long updatedAtUnixMs)
    {
        EnsureSchema(conn, tx);
        var current = ReadEntityRow(conn, tx, ReplicationOpKinds.AskUser, entityId);
        if (current is not null && IsResolved(current.Value.Body))
            return false; // resolution already carries the authoritative (snapshotted) state.
        return UpsertEntity(conn, tx, ReplicationOpKinds.AskUser, entityId, conversationId,
            causalVersion, tiebreak, bodyJson, originAccount, deleted: false, updatedAtUnixMs);
    }

    /// <summary>
    /// Projects an ask_user resolution with atomic first-writer-wins semantics. Returns true when
    /// this resolution won.
    /// </summary>
    public static bool AskUserResolve(
        SqliteConnection conn, SqliteTransaction tx,
        string entityId, string? conversationId, string causalVersion, string tiebreak,
        string bodyJson, string? originAccount, long updatedAtUnixMs)
    {
        EnsureSchema(conn, tx);
        var current = ReadEntityRow(conn, tx, ReplicationOpKinds.AskUser, entityId);
        if (current is not null && IsResolved(current.Value.Body))
            return false; // first resolution already won; later resolutions are ignored.
        // A resolution always supersedes an unresolved prompt regardless of causal order,
        // because it carries the prompt snapshot and is terminal.
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_domain_entities(
                kind, entity_id, conversation_id, causal_version, tiebreak, body, deleted, origin_account, updated_at)
            VALUES($kind, $eid, $conv, $causal, $tie, $body, 0, $account, $updated)
            ON CONFLICT(kind, entity_id) DO UPDATE SET
                conversation_id = excluded.conversation_id,
                causal_version = excluded.causal_version,
                tiebreak = excluded.tiebreak,
                body = excluded.body,
                deleted = 0,
                origin_account = excluded.origin_account,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$kind", ReplicationOpKinds.AskUser);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$conv", (object?)conversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$causal", causalVersion);
        cmd.Parameters.AddWithValue("$tie", tiebreak);
        cmd.Parameters.AddWithValue("$body", bodyJson);
        cmd.Parameters.AddWithValue("$account", (object?)originAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", updatedAtUnixMs);
        cmd.ExecuteNonQuery();
        return true;
    }

    private static bool IsResolved(string bodyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            return doc.RootElement.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    // -----------------------------------------------------------------------
    // Assets (desktop-only bytes). The payload content hash is validated before the row is
    // written so a corrupt asset body is a permanent projection failure, not silent data.
    // -----------------------------------------------------------------------

    /// <summary>Validates an asset payload hash and upserts it (causal LWW). True when it won.</summary>
    public static bool AssetUpsert(
        SqliteConnection conn, SqliteTransaction tx,
        string entityId, string? conversationId, string causalVersion, string tiebreak,
        string bodyJson, string? originAccount, long updatedAtUnixMs)
    {
        ValidateAssetHash(bodyJson);
        return UpsertEntity(conn, tx, ReplicationOpKinds.Asset, entityId, conversationId,
            causalVersion, tiebreak, bodyJson, originAccount, deleted: false, updatedAtUnixMs);
    }

    /// <summary>Tombstones an asset (causal LWW). True when the tombstone won.</summary>
    public static bool AssetDelete(
        SqliteConnection conn, SqliteTransaction tx,
        string entityId, string? conversationId, string causalVersion, string tiebreak,
        string bodyJson, string? originAccount, long updatedAtUnixMs)
        => UpsertEntity(conn, tx, ReplicationOpKinds.Asset, entityId, conversationId,
            causalVersion, tiebreak, bodyJson, originAccount, deleted: true, updatedAtUnixMs);

    private static void ValidateAssetHash(string bodyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("contentHash", out var hashEl)) return;
            var declared = hashEl.GetString();
            if (string.IsNullOrEmpty(declared)) return;
            if (!root.TryGetProperty("contentB64", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
                return;
            var bytes = Convert.FromBase64String(contentEl.GetString()!);
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, declared.ToLowerInvariant(), StringComparison.Ordinal))
                throw new ReplicationProjectionException("Asset payload content hash did not validate.");
        }
        catch (JsonException ex)
        {
            throw new ReplicationProjectionException("Asset payload was malformed: " + ex.Message);
        }
        catch (FormatException ex)
        {
            throw new ReplicationProjectionException("Asset payload content was not valid base64: " + ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Skill-package chunked transfer (desktop-only, 20 MB bound).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stores one package chunk; enforces the 20&#160;MB bound. Exact-once per chunk index.
    /// Returns the assembled content hash once every chunk has landed, otherwise null.
    /// </summary>
    public static string? PackageChunk(SqliteConnection conn, SqliteTransaction tx, string entityId, string bodyJson)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        EnsureSchema(conn, tx);
        PackageChunkBody chunk;
        try
        {
            chunk = JsonSerializer.Deserialize<PackageChunkBody>(bodyJson, Json)
                ?? throw new ReplicationProjectionException("Package chunk body was null.");
        }
        catch (JsonException ex)
        {
            throw new ReplicationProjectionException("Package chunk body was malformed: " + ex.Message);
        }
        if (chunk.TotalBytes < 0 || chunk.TotalBytes > MaxPackageBytes)
            throw new ReplicationProjectionException($"Package transfer exceeds the {MaxPackageBytes}-byte bound.");
        if (chunk.ChunkCount <= 0 || chunk.ChunkIndex < 0 || chunk.ChunkIndex >= chunk.ChunkCount)
            throw new ReplicationProjectionException("Package chunk index/count were out of range.");
        if (string.IsNullOrWhiteSpace(chunk.ContentHash) || chunk.ContentHash.Length != 64)
            throw new ReplicationProjectionException("Package chunk carried no SHA-256 content hash.");
        if (!string.IsNullOrEmpty(chunk.ChunkB64))
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(chunk.ChunkB64); }
            catch (FormatException ex)
            {
                throw new ReplicationProjectionException("Package chunk was not valid base64: " + ex.Message);
            }
            if (raw.LongLength > MaxChunkBytes)
                throw new ReplicationProjectionException(
                    $"Package chunk carried {raw.LongLength} raw bytes, above the {MaxChunkBytes}-byte chunk bound.");
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_package_chunks(
                package_id, chunk_index, chunk_count, total_bytes, content_hash, chunk_b64,
                name, mime_type, run_id)
            VALUES($pkg, $idx, $count, $total, $hash, $chunk, $name, $mime, $run)
            ON CONFLICT(package_id, chunk_index) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$pkg", entityId);
        cmd.Parameters.AddWithValue("$idx", chunk.ChunkIndex);
        cmd.Parameters.AddWithValue("$count", chunk.ChunkCount);
        cmd.Parameters.AddWithValue("$total", chunk.TotalBytes);
        cmd.Parameters.AddWithValue("$hash", chunk.ContentHash ?? string.Empty);
        cmd.Parameters.AddWithValue("$chunk", chunk.ChunkB64 ?? string.Empty);
        cmd.Parameters.AddWithValue("$name", (object?)chunk.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mime", (object?)chunk.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$run", (object?)chunk.RunId ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        if (IsPackageComplete(conn, tx, entityId, out var assembledHash))
        {
            var body = JsonSerializer.Serialize(new { packageId = entityId, complete = true, contentHash = assembledHash }, Json);
            UpsertEntity(conn, tx, ReplicationOpKinds.Asset, entityId, null,
                "pkg", entityId, body, null, deleted: false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return assembledHash;
        }
        return null;
    }

    /// <summary>Concatenates every stored chunk of a completed package into its assembled bytes.</summary>
    public static byte[] AssemblePackage(SqliteConnection conn, SqliteTransaction? tx, string packageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT chunk_b64 FROM replication_package_chunks
            WHERE package_id = $pkg ORDER BY chunk_index;
            """;
        cmd.Parameters.AddWithValue("$pkg", packageId);
        using var reader = cmd.ExecuteReader();
        using var buffer = new MemoryStream();
        while (reader.Read())
        {
            var chunk = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (chunk.Length == 0) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(chunk); }
            catch (FormatException ex)
            {
                throw new ReplicationProjectionException("Package chunk was not valid base64: " + ex.Message);
            }
            buffer.Write(bytes, 0, bytes.Length);
        }
        return buffer.ToArray();
    }

    internal sealed record PackageChunkBody(
        int ChunkIndex, int ChunkCount, long TotalBytes, string? ContentHash, string? ChunkB64,
        string? Name = null, string? MimeType = null, string? RunId = null);

    /// <summary>Descriptor carried alongside a transfer (attachment name, mime type and run).</summary>
    public sealed record TransferDescriptor(string? Name, string? MimeType, string? RunId);

    /// <summary>Reads the descriptor recorded with a transfer's chunks, or null when absent.</summary>
    public static TransferDescriptor? PackageDescriptor(
        SqliteConnection conn, SqliteTransaction? tx, string packageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT MAX(name), MAX(mime_type), MAX(run_id)
            FROM replication_package_chunks WHERE package_id = $pkg;
            """;
        cmd.Parameters.AddWithValue("$pkg", packageId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new TransferDescriptor(
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2));
    }

    private static bool IsPackageComplete(SqliteConnection conn, SqliteTransaction? tx, string packageId, out string? contentHash)
    {
        contentHash = null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COUNT(*), MAX(chunk_count), MAX(content_hash)
            FROM replication_package_chunks WHERE package_id = $pkg;
            """;
        cmd.Parameters.AddWithValue("$pkg", packageId);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(1)) return false;
        var have = r.GetInt64(0);
        var need = r.GetInt64(1);
        contentHash = r.IsDBNull(2) ? null : r.GetString(2);
        return have == need;
    }

    // -----------------------------------------------------------------------
    // Read watermarks (deterministic LWW, tx-composable projection).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Projects a read watermark (LWW by version, then through-event id) into the real
    /// replication_read_watermarks table the unread counters read from. Returns the parsed payload.
    /// </summary>
    public static ReadWatermarkPayload ReadWatermark(SqliteConnection conn, SqliteTransaction tx, string bodyJson)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ReadWatermarkPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReadWatermarkPayload>(bodyJson, Json)
                ?? throw new ReplicationProjectionException("Read watermark body was null.");
        }
        catch (JsonException ex)
        {
            throw new ReplicationProjectionException("Read watermark body was malformed: " + ex.Message);
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replication_read_watermarks(
                conversation_id, account_handle, through_event_id, updated_at, source_device_id, version)
            VALUES($conv, $account, $through, $updated, $device, $version)
            ON CONFLICT(conversation_id, account_handle) DO UPDATE SET
                through_event_id = excluded.through_event_id,
                updated_at = excluded.updated_at,
                source_device_id = excluded.source_device_id,
                version = excluded.version
            WHERE excluded.version > replication_read_watermarks.version
               OR (excluded.version = replication_read_watermarks.version
                   AND excluded.through_event_id > replication_read_watermarks.through_event_id);
            """;
        cmd.Parameters.AddWithValue("$conv", payload.ConversationId);
        cmd.Parameters.AddWithValue("$account", payload.AccountHandle);
        cmd.Parameters.AddWithValue("$through", payload.ThroughEventId);
        cmd.Parameters.AddWithValue("$updated", payload.UpdatedAtUnixMs);
        cmd.Parameters.AddWithValue("$device", payload.SourceDeviceId);
        cmd.Parameters.AddWithValue("$version", payload.Version);
        cmd.ExecuteNonQuery();
        return payload;
    }

    // -----------------------------------------------------------------------
    // Read helpers (used by callers/tests to observe converged domain state).
    // -----------------------------------------------------------------------

    /// <summary>A projected domain entity row.</summary>
    public readonly record struct DomainEntity(
        string Kind, string EntityId, string? ConversationId, string CausalVersion,
        string Body, bool Deleted, string? OriginAccount, long UpdatedAtUnixMs);

    /// <summary>Reads a projected entity, or null when absent.</summary>
    public static DomainEntity? GetEntity(SqliteConnection conn, string kind, string entityId)
    {
        EnsureSchema(conn, null);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, entity_id, conversation_id, causal_version, body, deleted, origin_account, updated_at
            FROM replication_domain_entities WHERE kind = $kind AND entity_id = $eid;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$eid", entityId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DomainEntity(
            r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt64(5) != 0, r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7));
    }

    /// <summary>Counts the projected lines of an entity.</summary>
    public static int CountLines(SqliteConnection conn, string kind, string entityId)
    {
        EnsureSchema(conn, null);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM replication_domain_lines WHERE kind = $kind AND entity_id = $eid;";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$eid", entityId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Counts stored chunks for a package.</summary>
    public static int PackageChunkCount(SqliteConnection conn, string packageId)
    {
        EnsureSchema(conn, null);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM replication_package_chunks WHERE package_id = $pkg;";
        cmd.Parameters.AddWithValue("$pkg", packageId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static (string Causal, string Tiebreak, string Body)? ReadEntityRow(
        SqliteConnection conn, SqliteTransaction? tx, string kind, string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT causal_version, tiebreak, body FROM replication_domain_entities
            WHERE kind = $kind AND entity_id = $eid;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$eid", entityId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }
}
