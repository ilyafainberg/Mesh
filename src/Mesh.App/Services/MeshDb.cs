using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
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
public sealed partial class MeshDb :
    IDisposable,
    ITopicDurabilityStore,
    ITopicRequestOutboxStore,
    ITopicCorrelationMaintenanceStore
{
    public sealed record ComposerDraftAttachment(
        string Id,
        string Name,
        string Path,
        long Size)
    {
        public static ComposerDraftAttachment Create(string name, string path, long size)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            var normalizedPath = System.IO.Path.GetFullPath(path);
            var identity = string.Join(
                "\0",
                name,
                normalizedPath,
                size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new(
                TopicSendSnapshot.StableId("composer-attachment", identity),
                name,
                normalizedPath,
                size);
        }
    }

    public sealed record ComposerDraftWidget(
        string Id,
        string Name,
        string Prompt,
        string Html)
    {
        public static ComposerDraftWidget Create(Widget widget)
        {
            ArgumentNullException.ThrowIfNull(widget);
            ArgumentException.ThrowIfNullOrWhiteSpace(widget.Id);
            return new(widget.Id, widget.Name, widget.Prompt, widget.Html);
        }
    }

    public sealed record TopicComposerSnapshot(
        string Text,
        IReadOnlyList<ComposerDraftAttachment> Attachments,
        bool WidgetMode,
        string? WidgetId,
        string TargetDeviceId,
        ComposerDraftWidget? Widget = null)
    {
        public static TopicComposerSnapshot TextOnly(string text)
            => new(text, Array.Empty<ComposerDraftAttachment>(), false, null, "");

        [System.Text.Json.Serialization.JsonIgnore]
        public string Fingerprint => ComputeTopicComposerFingerprint(this);
    }

    public sealed record ComposerDraft(
        string Text,
        long Revision,
        bool IsMalformed = false,
        TopicComposerSnapshot? TopicSnapshot = null);

    public enum ComposerDraftClearResult
    {
        Cleared,
        Superseded,
        Missing
    }

    internal enum ComposerDraftTransactionCheckpoint
    {
        CleanupObserved,
        BeforeNewerSnapshotWrite,
        BeforeDraftWrite
    }

    internal interface IComposerDraftTransactionObserver
    {
        void Checkpoint(
            ComposerDraftTransactionCheckpoint checkpoint,
            string threadId,
            long expectedRevision);
    }

    internal sealed record SyncVersionWrite(string EntityKey, string Version);
    internal sealed record SyncTombstoneWrite(string Kind, string EntityId, string Version);
    internal sealed record SyncCircleRenameWrite(
        string EntityId,
        IReadOnlyList<CircleRenameProjection> Renames);
    public sealed record TopicOutboxItem(
        string RunId, string ThreadId, string TriggerLineId, string TargetDeviceId,
        TopicRunRequestPayload Request, IReadOnlyList<ChatAttachment> Attachments,
        string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? LastError = null,
        string? RemoteStage = null, int RemoteStageOrdinal = 0,
        int TransportAttemptOrdinal = 0)
    {
        // Protocol 9 deliberately uses the stable run identity as the request envelope identity.
        public string EnvelopeId => RunId;
    }

    public sealed record TopicRunTriggerItem(
        string TriggerId,
        string RunId,
        TopicRunBeginMode Mode,
        string ThreadId,
        string TriggerLineId,
        string TargetDeviceId,
        string PayloadHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset? TerminalAt);

    public sealed record TopicTransportAttempt(
        string TriggerId,
        string RunId,
        int Ordinal);

    public sealed record InboundTopicRunItem(
        string RunId, string SourceDeviceId, TopicRunRequestPayload Request,
        string State, DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt,
        string? TerminalUpdateJson = null, long QueueSequence = 0);

    public sealed record InboundTopicCancellationItem(
        string RunId,
        string SourceDeviceId,
        string ThreadId,
        string TerminalUpdateJson,
        DateTimeOffset CreatedAt);

    public sealed record InboundRejectionItem(
        string RejectionId,
        string EnvelopeId,
        string? RelayReceiptId,
        string Kind,
        string FromHandle,
        string? FromDeviceId,
        string Reason,
        DateTimeOffset RejectedAt);
    public sealed record DeviceEnvelopeOutboxItem(
        string EnvelopeId, string TargetDeviceId, string Kind, string Plaintext,
        string? PushHint, DateTimeOffset CreatedAt,
        string State = TopicOutboxStates.Pending,
        DateTimeOffset? LastAttemptAt = null,
        string? LastError = null,
        int RecoveryCount = 0,
        DateTimeOffset? RecoveryStartedAt = null);

    public sealed record ReceivedTopicControlItem(
        string EnvelopeId,
        string SourceDeviceId,
        string RunId,
        string ThreadId,
        string ControlKind,
        string UpdateJson,
        DateTimeOffset ReceivedAt);

    public sealed record TopicRunCorrelationItem(
        string RunId,
        string ThreadId,
        string TargetDeviceId,
        string? TriggerLineId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? TerminalAt,
        DateTimeOffset? TerminalEventAt = null);

    public sealed record LocalTopicRunItem(
        string RunId,
        string ThreadId,
        string TriggerLineId,
        string TargetDeviceId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? TerminalAt);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string ConversationDraftKind = "conversation";
    private const string TopicDraftKind = "topic";
    private const string LastDesktopTopicMetaKey = "ui.desktop.last_topic";
    private const string LastDesktopConversationMetaKey = "ui.desktop.last_conversation";
    private static bool nativeInit;
    private string? lastDesktopTopicId;
    private string? lastDesktopConversationKey;

    private readonly string connectionString;
    private readonly string legacyPendingComposerPath;
    private readonly IComposerDraftTransactionObserver? composerDraftObserver;
    private readonly byte[] key;
    private readonly TimeProvider timeProvider;
    private readonly ThreadLocal<SqliteConnection> connections;
    // Lock order for all durable mutations is:
    //   caller state/operation gate (optional)
    //   -> localOriginJournalGate (journal writes only) -> durableWriteGate -> SQLite transaction.
    // Journal writes acquire the local-origin journal lock BEFORE the durable-write gate so a
    // caller already holding the journal lock (e.g. the bootstrap-snapshot boundary) can perform a
    // journal write without inverting against a concurrent local emit, which would otherwise take
    // the durable-write gate first and then block on the journal lock.
    // MeshDb never calls back into a caller state/operation gate while holding durableWriteGate.
    // Inbound replication captures caller state before this gate, performs only DB projection while
    // holding it, then performs live-state/UI projection after release. Reverse acquisition
    // (durableWriteGate -> caller state gate) is forbidden.
    private readonly SemaphoreSlim durableWriteGate = new(1, 1);
    private sealed record DurableWriteOwner(string Operation, int ManagedThreadId, long AcquiredAt);
    private DurableWriteOwner? durableWriteOwner;
    [ThreadStatic]
    private static HashSet<MeshDb>? durableWriteOwners;
    private int disposed;
    internal const int DurableWriteAttemptLimit = 2;
    private const int SqliteBusyTimeoutMilliseconds = 250;
    private long coordinatedWriteCount;
    private int activeCoordinatedWriters;
    private int maxConcurrentCoordinatedWriters;
    private int durableWriteWaiterCount;

    internal long CoordinatedWriteCount => Interlocked.Read(ref coordinatedWriteCount);
    internal int MaxConcurrentCoordinatedWriters => Volatile.Read(ref maxConcurrentCoordinatedWriters);
    internal int DurableWriteWaiterCount => Volatile.Read(ref durableWriteWaiterCount);

    private SqliteConnection conn
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return connections.Value!;
        }
    }

    private MeshDb(
        string path,
        byte[] key,
        TimeProvider timeProvider,
        IComposerDraftTransactionObserver? composerDraftObserver)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 1
        }.ToString();
        legacyPendingComposerPath = $"{path}.composer-pending";
        this.key = key.ToArray();
        this.timeProvider = timeProvider;
        this.composerDraftObserver = composerDraftObserver;
        connections = new ThreadLocal<SqliteConnection>(CreateConnection, trackAllValues: true);
    }

    /// <summary>Opens (creating if needed) an encrypted database at <paramref name="path"/> with the given key.</summary>
    public static MeshDb Open(string path, byte[] key, TimeProvider? timeProvider = null)
        => OpenCore(path, key, timeProvider, null);

    internal static MeshDb OpenForTesting(
        string path,
        byte[] key,
        IComposerDraftTransactionObserver composerDraftObserver,
        TimeProvider? timeProvider = null)
        => OpenCore(path, key, timeProvider, composerDraftObserver);

    private static MeshDb OpenCore(
        string path,
        byte[] key,
        TimeProvider? timeProvider,
        IComposerDraftTransactionObserver? composerDraftObserver)
    {
        EnsureNativeInit();
        StorageProtection.TryEnsureBackgroundReadable(Path.GetDirectoryName(path) ?? path);
        var db = new MeshDb(
            path,
            key,
            timeProvider ?? TimeProvider.System,
            composerDraftObserver);
        _ = db.conn;
        StorageProtection.TryEnsureBackgroundReadable(path);
        db.ExecuteDurableWrite(() =>
        {
            using var command = db.conn.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
            db.CreateSchema();
        });
        db.ReplayPendingTopicSnapshots();
        db.lastDesktopTopicId = db.GetMetaValue(LastDesktopTopicMetaKey);
        db.lastDesktopConversationKey = db.GetMetaValue(LastDesktopConversationMetaKey);
        return db;
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyKey(connection, key);
        connection.CreateFunction<string?, bool>(
            "topic_valid_id",
            TopicRunProtocol.IsValidIdentifier,
            isDeterministic: true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA busy_timeout = {SqliteBusyTimeoutMilliseconds};
            PRAGMA synchronous = NORMAL;
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    internal T ExecuteDurableWrite<T>(
        Func<T> write,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "")
    {
        ArgumentNullException.ThrowIfNull(write);
        if (durableWriteOwners?.Contains(this) == true)
            return write();
        if (!durableWriteGate.Wait(0))
        {
            Interlocked.Increment(ref durableWriteWaiterCount);
            try
            {
                using (ManagedOperationDiagnostics.Wait(
                           "meshdb.durable-write-gate",
                           () =>
                           {
                               var owner = Volatile.Read(ref durableWriteOwner);
                               return owner is null
                                   ? "none"
                                   : $"{owner.Operation}:thread-{owner.ManagedThreadId}:held-"
                                     + $"{Environment.TickCount64 - owner.AcquiredAt}ms";
                           }))
                {
                    durableWriteGate.Wait(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref durableWriteWaiterCount);
            }
        }
        Volatile.Write(
            ref durableWriteOwner,
            new DurableWriteOwner(
                ManagedOperationDiagnostics.CurrentOperation == "untracked"
                    ? caller
                    : ManagedOperationDiagnostics.CurrentOperation,
                Environment.CurrentManagedThreadId,
                Environment.TickCount64));
        var activeWriters = Interlocked.Increment(ref activeCoordinatedWriters);
        UpdateMaximum(ref maxConcurrentCoordinatedWriters, activeWriters);
        Interlocked.Increment(ref coordinatedWriteCount);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            (durableWriteOwners ??= []).Add(this);
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return write();
                }
                catch (SqliteException ex) when (
                    ex.SqliteErrorCode is 5 or 6
                    && attempt < DurableWriteAttemptLimit)
                {
                    var delay = Math.Min(25 << (attempt - 1), 400);
                    RuntimeDiagnostics.Current?.RecordEvent(
                        "sqlite-writer",
                        $"busy-retry;attempt={attempt};error={ex.SqliteErrorCode}");
                    if (cancellationToken.WaitHandle.WaitOne(delay))
                        cancellationToken.ThrowIfCancellationRequested();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    RuntimeDiagnostics.Current?.RecordException("sqlite-writer-exhausted", ex);
                    throw;
                }
            }
        }
        finally
        {
            durableWriteOwners!.Remove(this);
            Interlocked.Decrement(ref activeCoordinatedWriters);
            Volatile.Write(ref durableWriteOwner, null);
            durableWriteGate.Release();
        }
    }

    internal void ExecuteDurableWrite(
        Action write,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "")
        => ExecuteDurableWrite(
            () =>
            {
                write();
                return true;
            },
            cancellationToken,
            caller);

    internal Task<T> ExecuteDurableWriteAsync<T>(
        Func<T> write,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "")
        => Task.Run(
            () => ExecuteDurableWrite(write, cancellationToken, caller),
            cancellationToken);

    internal Task ExecuteDurableWriteAsync(
        Action write,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string caller = "")
        => Task.Run(
            () => ExecuteDurableWrite(write, cancellationToken, caller),
            cancellationToken);

    internal T ExecuteJournalWrite<T>(
        Func<T> write,
        CancellationToken cancellationToken = default)
    {
        // Take the local-origin journal lock as the OUTER lock, before the durable-write gate, so a
        // caller already holding the journal lock (for example the bootstrap-snapshot boundary) can
        // perform a journal write without deadlocking against a concurrent EmitLocal that would
        // otherwise acquire the durable-write gate first and then block on the journal lock.
        // Monitor re-entry makes the nested acquisition inside AllocateAndAppendLocalEvent(s) a
        // no-op on the same thread, and holding it across durable-write busy retries keeps local
        // sequence allocation serialized.
        using var journalLock = EnterLocalOriginJournalLock();
        return ExecuteDurableWrite(write, cancellationToken);
    }

    internal Task<T> ExecuteJournalWriteAsync<T>(
        Func<T> write,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => ExecuteJournalWrite(write, cancellationToken),
            cancellationToken);

    internal Task ExecuteJournalWriteAsync(
        Action write,
        CancellationToken cancellationToken = default)
        => Task.Run(
            () => ExecuteJournalWrite(
                () =>
                {
                    write();
                    return true;
                },
                cancellationToken),
            cancellationToken);

    private static void EnsureNativeInit()
    {
        if (nativeInit) return;
        SQLitePCL.Batteries_V2.Init();
        nativeInit = true;
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current) return;
            current = observed;
        }
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
                reply_to_line_id TEXT,
                via TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT '',
                at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS own_threads(
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS topic_outbox(
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                attachments_json TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                remote_stage TEXT,
                remote_stage_ordinal INTEGER NOT NULL DEFAULT 0,
                transport_attempt_ordinal INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_topic_outbox_state ON topic_outbox(state, created_at);
            CREATE TABLE IF NOT EXISTS topic_run_correlations(
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                trigger_line_id TEXT,
                created_at TEXT NOT NULL,
                terminal_at TEXT,
                terminal_event_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_topic_run_correlations_terminal
                ON topic_run_correlations(terminal_at);
            CREATE TABLE IF NOT EXISTS topic_local_runs(
                run_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                terminal_at TEXT);
            CREATE TABLE IF NOT EXISTS topic_run_triggers(
                trigger_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL UNIQUE,
                mode TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                terminal_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_topic_run_triggers_terminal
                ON topic_run_triggers(terminal_at);
            CREATE TABLE IF NOT EXISTS inbound_topic_runs(
                run_id TEXT PRIMARY KEY,
                source_device_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                state TEXT NOT NULL,
                accepted_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                terminal_update_json TEXT);
            CREATE INDEX IF NOT EXISTS ix_inbound_topic_runs_state ON inbound_topic_runs(state, accepted_at);
            CREATE TABLE IF NOT EXISTS inbound_topic_cancellations(
                run_id TEXT PRIMARY KEY,
                source_device_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                terminal_update_json TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_inbound_topic_cancellations_created
                ON inbound_topic_cancellations(created_at);
            CREATE TABLE IF NOT EXISTS inbound_rejections(
                rejection_id TEXT PRIMARY KEY,
                envelope_id TEXT NOT NULL,
                relay_receipt_id TEXT,
                kind TEXT NOT NULL,
                from_handle TEXT NOT NULL,
                from_device_id TEXT,
                reason TEXT NOT NULL,
                rejected_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_inbound_rejections_rejected
                ON inbound_rejections(rejected_at);
            CREATE TABLE IF NOT EXISTS device_envelope_outbox(
                envelope_id TEXT PRIMARY KEY,
                target_device_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                plaintext TEXT NOT NULL,
                push_hint TEXT,
                created_at TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',
                last_attempt_at TEXT,
                last_error TEXT,
                recovery_count INTEGER NOT NULL DEFAULT 0,
                recovery_started_at TEXT);
            CREATE TABLE IF NOT EXISTS received_topic_controls(
                envelope_id TEXT PRIMARY KEY,
                source_device_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                control_kind TEXT NOT NULL,
                update_json TEXT NOT NULL,
                received_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_received_topic_controls_run
                ON received_topic_controls(run_id, received_at);
            CREATE TABLE IF NOT EXISTS composer_drafts(
                kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                text TEXT NOT NULL,
                revision INTEGER,
                snapshot_json TEXT,
                PRIMARY KEY(kind, entity_id));
            CREATE TABLE IF NOT EXISTS pending_topic_drafts(
                entity_id TEXT PRIMARY KEY,
                text TEXT NOT NULL,
                revision INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memories(
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                category TEXT NOT NULL,
                origin TEXT NOT NULL,
                importance REAL NOT NULL,
                confidence REAL NOT NULL,
                stability REAL NOT NULL,
                reinforcement_count INTEGER NOT NULL,
                source_thread_id TEXT,
                source_line_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_reinforced_at TEXT NOT NULL,
                recall_count INTEGER NOT NULL DEFAULT 0,
                last_recalled_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_memories_updated ON memories(updated_at DESC, id);
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
            INSERT OR IGNORE INTO meta(k, v) VALUES('schema_version', '1');
            INSERT OR IGNORE INTO meta(k, v) VALUES('topic_trigger_epoch', '1');");

        // Idempotent migration for databases created before line_id/status existed.
        AddColumnIfMissing("chat_lines", "line_id", "TEXT");
        AddColumnIfMissing("chat_lines", "status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("own_chat", "line_id", "TEXT");
        AddColumnIfMissing("own_chat", "status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("composer_drafts", "revision", "INTEGER");
        AddColumnIfMissing("composer_drafts", "snapshot_json", "TEXT");
        Exec("CREATE INDEX IF NOT EXISTS ix_own_chat_lineid ON own_chat(line_id);");
        Exec("""
            UPDATE chat_lines
            SET line_id = 'conversation-' || printf('%016x', id)
            WHERE line_id IS NULL OR trim(line_id) = '';
            UPDATE own_chat
            SET line_id = 'topic-' || printf('%016x', id)
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
        AddColumnIfMissing("chat_lines", "model_id", "TEXT");
        AddColumnIfMissing("own_chat", "thread_id", "TEXT");
        // Transcript + reasoning persistence: internal lines are the model's hidden execution record;
        // reasoning is the collapsible "thinking" (previously not persisted, so lost on restart).
        AddColumnIfMissing("own_chat", "internal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("own_chat", "reasoning", "TEXT");
        AddColumnIfMissing("own_chat", "model_id", "TEXT");
        AddColumnIfMissing("own_chat", "sender_handle", "TEXT");
        AddColumnIfMissing("own_chat", "reply_to_line_id", "TEXT");
        // User-defined topic order. Existing rows retain their creation order through the fallback sort.
        AddColumnIfMissing("own_threads", "sort_order", "INTEGER");
        NormalizeOwnThreadOrder();
        // Phase 1: execution metadata and activity tracking.
        AddColumnIfMissing("own_threads", "last_activity_at", "TEXT");
        AddColumnIfMissing("own_threads", "is_pinned", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("own_threads", "execution_device_id", "TEXT");
        AddColumnIfMissing("own_threads", "execution_device_name", "TEXT");
        AddColumnIfMissing("own_threads", "execution_device_platform", "TEXT");
        AddColumnIfMissing("own_threads", "execution_at", "TEXT");
        AddColumnIfMissing("own_threads", "execution_run_id", "TEXT");
        AddColumnIfMissing("inbound_topic_runs", "terminal_update_json", "TEXT");
        AddColumnIfMissing("inbound_topic_runs", "queue_sequence", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("topic_outbox", "remote_stage", "TEXT");
        AddColumnIfMissing(
            "topic_outbox", "remote_stage_ordinal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            "topic_outbox", "transport_attempt_ordinal", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("topic_run_correlations", "trigger_line_id", "TEXT");
        AddColumnIfMissing(
            "topic_run_correlations",
            "trigger_identity_state",
            "TEXT NOT NULL DEFAULT 'strict'");
        AddColumnIfMissing("topic_run_correlations", "terminal_event_at", "TEXT");
        AddColumnIfMissing(
            "device_envelope_outbox", "state", "TEXT NOT NULL DEFAULT 'pending'");
        AddColumnIfMissing("device_envelope_outbox", "last_attempt_at", "TEXT");
        AddColumnIfMissing("device_envelope_outbox", "last_error", "TEXT");
        AddColumnIfMissing(
            "device_envelope_outbox", "recovery_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("device_envelope_outbox", "recovery_started_at", "TEXT");
        Exec("""
            UPDATE inbound_topic_runs AS current
            SET queue_sequence = (
                SELECT COUNT(*)
                FROM inbound_topic_runs AS prior
                WHERE prior.accepted_at < current.accepted_at
                   OR (prior.accepted_at = current.accepted_at AND prior.run_id <= current.run_id))
            WHERE queue_sequence = 0;
            UPDATE topic_outbox
            SET remote_stage = CASE state
                    WHEN 'device_accepted' THEN 'accepted'
                    WHEN 'device_queued' THEN 'queued'
                    WHEN 'running' THEN 'executing'
                    ELSE remote_stage
                END,
                remote_stage_ordinal = CASE state
                    WHEN 'device_accepted' THEN 10
                    WHEN 'device_queued' THEN 20
                    WHEN 'running' THEN 40
                    ELSE remote_stage_ordinal
                END
            WHERE remote_stage_ordinal = 0;
            CREATE INDEX IF NOT EXISTS ix_inbound_topic_runs_queue
                ON inbound_topic_runs(queue_sequence, run_id);
            """);
        MigrateTopicRunCorrelationTriggerIdentity();
        using (var migrateTerminalObservation = conn.CreateCommand())
        {
            migrateTerminalObservation.CommandText = """
                UPDATE topic_run_correlations
                SET terminal_event_at = terminal_at,
                    terminal_at = $observed
                WHERE terminal_at IS NOT NULL
                  AND terminal_event_at IS NULL;
                """;
            migrateTerminalObservation.Parameters.AddWithValue(
                "$observed", timeProvider.GetUtcNow().ToString("O"));
            migrateTerminalObservation.ExecuteNonQuery();
        }
        MigrateOwnThreadActivity();
        AddColumnIfMissing("conversations", "last_activity_at", "TEXT");
        AddColumnIfMissing("conversations", "is_pinned", "INTEGER NOT NULL DEFAULT 0");
        MigrateConversationActivity();
        CreateAssetsInteractionsSchema();
        CreateSkillPackagesSchema();
        CreateOnlineReplicationSchema();
        CreateNotificationSchema();
        CreateDeferredTopicUpdateSchema();
        MigrateTopicRunTriggerLedger();
    }

    private void MigrateTopicRunCorrelationTriggerIdentity()
    {
        using (var marker = conn.CreateCommand())
        {
            marker.CommandText =
                "SELECT v FROM meta WHERE k = 'topic_run_trigger_schema_version';";
            if (string.Equals(marker.ExecuteScalar() as string, "3", StringComparison.Ordinal))
            {
                using var incomplete = conn.CreateCommand();
                incomplete.CommandText = """
                    SELECT EXISTS(
                        SELECT 1 FROM topic_run_correlations
                        WHERE trigger_identity_state = 'strict'
                          AND NOT topic_valid_id(trigger_line_id));
                    """;
                if (Convert.ToInt64(incomplete.ExecuteScalar()) == 0)
                    return;
            }
        }

        using var transaction = conn.BeginTransaction(deferred: false);
        ExecuteMigrationCommand(transaction, """
            UPDATE topic_run_correlations
            SET trigger_line_id = NULL,
                trigger_identity_state = 'legacy-active-null'
            WHERE trigger_identity_state = 'strict'
              AND NOT topic_valid_id(trigger_line_id);
            """);
        var preexistingNull = ScalarCount(
            transaction,
            "SELECT COUNT(*) FROM topic_run_correlations WHERE trigger_line_id IS NULL;");

        ExecuteMigrationCommand(transaction, """
            INSERT INTO topic_run_correlations(
                run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                terminal_event_at, trigger_identity_state)
            SELECT outbox.run_id, outbox.thread_id, outbox.target_device_id,
                   NULL, outbox.created_at, NULL, NULL, 'legacy-active-null'
            FROM topic_outbox AS outbox
            WHERE NOT EXISTS(
                SELECT 1 FROM topic_run_correlations AS correlation
                WHERE correlation.run_id = outbox.run_id);

            WITH ranked_controls AS (
                SELECT control.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY control.run_id
                           ORDER BY
                               CASE WHEN control.control_kind = 'topic.terminal' THEN 0 ELSE 1 END,
                               control.received_at DESC,
                               control.envelope_id DESC) AS candidate_rank
                FROM received_topic_controls AS control
                WHERE control.control_kind = 'topic.terminal')
            INSERT INTO topic_run_correlations(
                run_id, thread_id, target_device_id, trigger_line_id, created_at, terminal_at,
                terminal_event_at, trigger_identity_state)
            SELECT control.run_id, control.thread_id, control.source_device_id,
                   NULL, control.received_at, control.received_at, control.received_at,
                   'legacy-active-null'
            FROM ranked_controls AS control
            WHERE control.candidate_rank = 1
              AND NOT EXISTS(
                  SELECT 1 FROM topic_run_correlations AS correlation
                  WHERE correlation.run_id = control.run_id);
            """);

        var derived = 0;
        // Every candidate is protocol-valid before ranking. Any disagreement across durable
        // sources remains unresolved; semantic priority only chooses among identical candidates.
        ExecuteMigrationCommand(transaction, """
            DROP TABLE IF EXISTS temp.topic_trigger_migration_candidates;
            CREATE TEMP TABLE topic_trigger_migration_candidates(
                run_id TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                target_device_id TEXT NOT NULL,
                trigger_line_id TEXT NOT NULL,
                semantic_priority INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                sequence_key TEXT NOT NULL);

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   correlation.trigger_line_id, 5, correlation.created_at, correlation.run_id
            FROM topic_run_correlations AS correlation
            WHERE topic_valid_id(correlation.trigger_line_id);

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   outbox.trigger_line_id, 10, outbox.created_at, outbox.run_id
            FROM topic_run_correlations AS correlation
            JOIN topic_outbox AS outbox
              ON outbox.run_id = correlation.run_id
             AND outbox.thread_id = correlation.thread_id
             AND outbox.target_device_id = correlation.target_device_id
            WHERE topic_valid_id(outbox.trigger_line_id);

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   trigger.trigger_line_id, 20, trigger.created_at, trigger.trigger_id
            FROM topic_run_correlations AS correlation
            JOIN topic_run_triggers AS trigger
              ON trigger.run_id = correlation.run_id
             AND trigger.thread_id = correlation.thread_id
             AND trigger.target_device_id = correlation.target_device_id
            WHERE topic_valid_id(trigger.trigger_line_id);

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   COALESCE(
                       json_extract(inbound.request_json, '$.triggerLineId'),
                       json_extract(inbound.request_json, '$.TriggerLineId')),
                   30, inbound.accepted_at, inbound.run_id
            FROM topic_run_correlations AS correlation
            JOIN inbound_topic_runs AS inbound
              ON inbound.run_id = correlation.run_id
             AND inbound.source_device_id = correlation.target_device_id
             AND COALESCE(
                 json_extract(inbound.request_json, '$.threadId'),
                 json_extract(inbound.request_json, '$.ThreadId')) = correlation.thread_id
            WHERE topic_valid_id(COALESCE(
                json_extract(inbound.request_json, '$.triggerLineId'),
                json_extract(inbound.request_json, '$.TriggerLineId')));

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   COALESCE(
                       json_extract(control.update_json, '$.triggerLineId'),
                       json_extract(control.update_json, '$.TriggerLineId')),
                   40, control.received_at,
                   printf('%02d:', CASE WHEN control.control_kind = 'topic.terminal' THEN 0 ELSE 1 END)
                       || control.envelope_id
            FROM topic_run_correlations AS correlation
            JOIN received_topic_controls AS control
              ON control.run_id = correlation.run_id
             AND control.thread_id = correlation.thread_id
             AND control.source_device_id = correlation.target_device_id
            WHERE topic_valid_id(COALESCE(
                json_extract(control.update_json, '$.triggerLineId'),
                json_extract(control.update_json, '$.TriggerLineId')));

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   local.trigger_line_id, 50, local.created_at, local.run_id
            FROM topic_run_correlations AS correlation
            JOIN topic_local_runs AS local
              ON local.run_id = correlation.run_id
             AND local.thread_id = correlation.thread_id
             AND local.target_device_id = correlation.target_device_id
            WHERE topic_valid_id(local.trigger_line_id);

            INSERT INTO topic_trigger_migration_candidates
            SELECT correlation.run_id, correlation.thread_id, correlation.target_device_id,
                   line.line_id, 60, line.at, printf('%020d', line.id)
            FROM topic_run_correlations AS correlation
            JOIN own_threads AS thread
              ON thread.id = correlation.thread_id
             AND thread.execution_run_id = correlation.run_id
            JOIN own_chat AS line ON line.thread_id = thread.id
            WHERE line.role = 'user'
              AND topic_valid_id(line.line_id)
              AND (thread.execution_at IS NULL
                   OR julianday(line.at) <= julianday(thread.execution_at));
            """);

        ExecuteMigrationCommand(transaction, """
            UPDATE topic_run_correlations AS correlation
            SET trigger_line_id = NULL,
                trigger_identity_state = 'legacy-conflict'
            WHERE EXISTS(
                  SELECT 1
                  FROM topic_trigger_migration_candidates AS candidate
                  WHERE candidate.run_id = correlation.run_id
                    AND candidate.thread_id = correlation.thread_id
                    AND candidate.target_device_id = correlation.target_device_id
                  GROUP BY candidate.run_id
                  HAVING COUNT(DISTINCT candidate.trigger_line_id) > 1);
            """);
        derived += ExecuteMigrationCommand(transaction, """
            UPDATE topic_run_correlations AS correlation
            SET trigger_line_id = (
                    SELECT candidate.trigger_line_id
                    FROM topic_trigger_migration_candidates AS candidate
                    WHERE candidate.run_id = correlation.run_id
                      AND candidate.thread_id = correlation.thread_id
                      AND candidate.target_device_id = correlation.target_device_id
                    ORDER BY candidate.semantic_priority,
                             candidate.created_at DESC,
                             candidate.sequence_key DESC,
                             candidate.trigger_line_id
                    LIMIT 1),
                trigger_identity_state = 'strict'
            WHERE correlation.trigger_line_id IS NULL
              AND correlation.trigger_identity_state <> 'legacy-conflict'
              AND EXISTS(
                  SELECT 1
                  FROM topic_trigger_migration_candidates AS candidate
                  WHERE candidate.run_id = correlation.run_id
                    AND candidate.thread_id = correlation.thread_id
                    AND candidate.target_device_id = correlation.target_device_id
                    AND candidate.semantic_priority = (
                        SELECT MIN(authoritative.semantic_priority)
                        FROM topic_trigger_migration_candidates AS authoritative
                        WHERE authoritative.run_id = correlation.run_id
                          AND authoritative.thread_id = correlation.thread_id
                          AND authoritative.target_device_id = correlation.target_device_id)
                  GROUP BY candidate.run_id
                  HAVING COUNT(DISTINCT candidate.trigger_line_id) = 1);
            """);

        ExecuteMigrationCommand(transaction, """
            UPDATE topic_run_correlations AS correlation
            SET terminal_at = COALESCE(terminal_at, (
                    SELECT control.received_at
                    FROM received_topic_controls AS control
                    WHERE control.run_id = correlation.run_id
                      AND control.thread_id = correlation.thread_id
                      AND control.source_device_id = correlation.target_device_id
                      AND control.control_kind = 'topic.terminal'
                    ORDER BY control.received_at DESC, control.envelope_id
                    LIMIT 1)),
                terminal_event_at = COALESCE(terminal_event_at, (
                    SELECT control.received_at
                    FROM received_topic_controls AS control
                    WHERE control.run_id = correlation.run_id
                      AND control.thread_id = correlation.thread_id
                      AND control.source_device_id = correlation.target_device_id
                      AND control.control_kind = 'topic.terminal'
                    ORDER BY control.received_at DESC, control.envelope_id
                    LIMIT 1))
            WHERE EXISTS(
                SELECT 1 FROM received_topic_controls AS control
                WHERE control.run_id = correlation.run_id
                  AND control.thread_id = correlation.thread_id
                  AND control.source_device_id = correlation.target_device_id
                  AND control.control_kind = 'topic.terminal');

            UPDATE topic_run_correlations AS correlation
            SET trigger_identity_state = CASE
                WHEN correlation.terminal_at IS NOT NULL
                     OR NOT (
                         EXISTS(
                             SELECT 1 FROM topic_outbox AS outbox
                             WHERE outbox.run_id = correlation.run_id
                               AND outbox.thread_id = correlation.thread_id
                               AND outbox.target_device_id = correlation.target_device_id
                               AND outbox.state NOT IN ('expired', 'dead_letter', 'failed'))
                         OR EXISTS(
                             SELECT 1 FROM topic_local_runs AS local
                             WHERE local.run_id = correlation.run_id
                               AND local.thread_id = correlation.thread_id
                               AND local.target_device_id = correlation.target_device_id
                               AND local.terminal_at IS NULL)
                         OR EXISTS(
                             SELECT 1 FROM own_threads AS thread
                             WHERE thread.id = correlation.thread_id
                               AND thread.execution_run_id = correlation.run_id
                               AND thread.execution_device_id = correlation.target_device_id))
                    THEN 'legacy-tombstone'
                ELSE 'legacy-active-null'
                END
            WHERE correlation.trigger_line_id IS NULL
              AND correlation.trigger_identity_state <> 'legacy-conflict';
            """);

        var activeNull = ScalarCount(
            transaction,
            """
            SELECT COUNT(*) FROM topic_run_correlations
            WHERE trigger_line_id IS NULL AND trigger_identity_state = 'legacy-active-null';
            """);
        var tombstones = ScalarCount(
            transaction,
            """
            SELECT COUNT(*) FROM topic_run_correlations
            WHERE trigger_line_id IS NULL AND trigger_identity_state = 'legacy-tombstone';
            """);
        var conflicts = ScalarCount(
            transaction,
            """
            SELECT COUNT(*) FROM topic_run_correlations
            WHERE trigger_line_id IS NULL AND trigger_identity_state = 'legacy-conflict';
            """);
        var unresolvedHashes = new List<string>();
        var conflictHashes = new List<string>();
        using (var unresolved = conn.CreateCommand())
        {
            unresolved.Transaction = transaction;
            unresolved.CommandText = """
                SELECT run_id, trigger_identity_state FROM topic_run_correlations
                WHERE trigger_line_id IS NULL
                ORDER BY run_id;
                """;
            using var reader = unresolved.ExecuteReader();
            while (reader.Read())
            {
                unresolvedHashes.Add(HashMigrationIdentifier(reader.GetString(0)));
                if (string.Equals(reader.GetString(1), "legacy-conflict", StringComparison.Ordinal))
                    conflictHashes.Add(HashMigrationIdentifier(reader.GetString(0)));
            }
        }

        var diagnostics = JsonSerializer.Serialize(new
        {
            version = 3,
            migratedAt = timeProvider.GetUtcNow().ToString("O"),
            preexistingNull,
            derived,
            legacyActiveNull = activeNull,
            legacyTombstone = tombstones,
            legacyConflicts = conflicts,
            unresolvedRunHashes = unresolvedHashes,
            conflictRunHashes = conflictHashes
        }, JsonOpts);
        using (var meta = conn.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = """
                INSERT INTO meta(k, v) VALUES('topic_run_trigger_schema_version', '3')
                ON CONFLICT(k) DO UPDATE SET v = excluded.v;
                INSERT INTO meta(k, v) VALUES('topic_run_trigger_migration_diagnostics', $diagnostics)
                ON CONFLICT(k) DO UPDATE SET v = excluded.v;
                """;
            meta.Parameters.AddWithValue("$diagnostics", diagnostics);
            meta.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private int ExecuteMigrationCommand(SqliteTransaction transaction, string sql)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private long ScalarCount(SqliteTransaction transaction, string sql)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string HashMigrationIdentifier(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private bool AddColumnIfMissing(string table, string column, string decl)
    {
        bool exists = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
        }
        if (exists) return false;
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
        return true;
    }

    /// <summary>True when this database has never had a profile written to it.</summary>
    public bool IsEmpty()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM profile;";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    // ---- composer drafts ---------------------------------------------------

    public string GetConversationDraft(string handle)
        => GetConversationDraftState(handle)?.Text ?? "";

    public ComposerDraft? GetConversationDraftState(string handle)
        => GetComposerDraftState(ConversationDraftKind, handle);

    public void SetConversationDraft(string handle, string text)
        => ExecuteDurableWrite(() => SetComposerDraft(
            ConversationDraftKind, handle, text, ComposerDraftRevision.New(), null));

    internal Task SetConversationDraftAsync(
        string handle,
        string text,
        CancellationToken cancellationToken = default)
        => ExecuteDurableWriteAsync(
            () => SetComposerDraft(
                ConversationDraftKind, handle, text, ComposerDraftRevision.New(), null),
            cancellationToken);

    internal Task<ComposerDraftMutationResult> TrySetConversationDraftAsync(
        string handle,
        string text,
        long revision,
        Func<bool> shouldPersist,
        CancellationToken cancellationToken = default)
        => ExecuteDurableWriteAsync(
            () =>
            {
                if (!shouldPersist())
                    return ComposerDraftMutationResult.Superseded;
                return SetComposerDraft(
                    ConversationDraftKind, handle, text, revision, null);
            },
            cancellationToken);

    public string GetTopicDraft(string threadId)
        => GetTopicDraftState(threadId)?.Text ?? "";

    public ComposerDraft? GetTopicDraftState(string threadId)
        => GetComposerDraftState(TopicDraftKind, threadId);

    public void SetTopicDraft(string threadId, string text)
    {
        var revision = ComposerDraftRevision.New();
        var snapshot = TopicComposerSnapshot.TextOnly(text);
        StagePendingTopicSnapshot(threadId, snapshot, revision);
        ExecuteDurableWrite(() => CommitTopicSnapshot(threadId, snapshot, revision));
    }

    internal Task SetTopicDraftAsync(
        string threadId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var revision = ComposerDraftRevision.New();
        var snapshot = TopicComposerSnapshot.TextOnly(text);
        StagePendingTopicSnapshot(threadId, snapshot, revision);
        return ExecuteDurableWriteAsync(
            () => CommitTopicSnapshot(threadId, snapshot, revision),
            cancellationToken);
    }

    internal Task<ComposerDraftMutationResult> TrySetTopicDraftAsync(
        string threadId,
        TopicComposerSnapshot snapshot,
        long revision,
        Func<bool> shouldPersist,
        CancellationToken cancellationToken = default)
        => ExecuteDurableWriteAsync(
            () =>
            {
                if (!shouldPersist())
                    return ComposerDraftMutationResult.Superseded;
                composerDraftObserver?.Checkpoint(
                    ComposerDraftTransactionCheckpoint.BeforeDraftWrite,
                    threadId,
                    revision);
                return CommitTopicSnapshot(threadId, snapshot, revision);
            },
            cancellationToken);

    internal void StagePendingTopicSnapshot(
        string threadId,
        TopicComposerSnapshot snapshot,
        long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!ValidTopicComposerSnapshot(snapshot, snapshot.Text))
            throw new ArgumentException(
                "The topic composer snapshot is incomplete or inconsistent.",
                nameof(snapshot));
        snapshot = NormalizeTopicComposerSnapshot(snapshot);
        ExecuteDurableWrite(() =>
        {
            using var transaction = conn.BeginTransaction();
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pending_topic_drafts(entity_id, text, revision, snapshot_json)
                VALUES($id, $text, $revision, $snapshot)
                ON CONFLICT(entity_id) DO UPDATE
                SET text = excluded.text,
                    revision = excluded.revision,
                    snapshot_json = excluded.snapshot_json
                WHERE pending_topic_drafts.revision < excluded.revision;
                """;
            command.Parameters.AddWithValue("$id", threadId);
            command.Parameters.AddWithValue("$text", snapshot.Text);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue(
                "$snapshot",
                JsonSerializer.Serialize(snapshot, JsonOpts));
            command.ExecuteNonQuery();
            transaction.Commit();
        });
    }

    internal void ClearPendingTopicSnapshot(string threadId, long persistedRevision)
    {
        ExecuteDurableWrite(() =>
        {
            using var transaction = conn.BeginTransaction();
            DeletePendingTopicSnapshots(threadId, persistedRevision, transaction);
            transaction.Commit();
        });
    }

    private void DeletePendingTopicSnapshots(
        string threadId,
        long persistedRevision,
        SqliteTransaction transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM pending_topic_drafts
            WHERE entity_id = $id AND revision <= $revision;
            """;
        command.Parameters.AddWithValue("$id", threadId);
        command.Parameters.AddWithValue("$revision", persistedRevision);
        command.ExecuteNonQuery();
    }

    private ComposerDraftMutationResult CommitTopicSnapshot(
        string threadId,
        TopicComposerSnapshot snapshot,
        long revision)
    {
        using var transaction = conn.BeginTransaction();
        var persisted = SetComposerDraft(
            TopicDraftKind,
            threadId,
            snapshot.Text,
            revision,
            snapshot,
            transaction);
        DeletePendingTopicSnapshots(threadId, revision, transaction);
        transaction.Commit();
        return persisted;
    }

    internal Task<ComposerDraftClearResult> ResolveTopicDraftCleanupAsync(
        string threadId,
        long expectedRevision,
        ComposerDraft? currentCandidate,
        CancellationToken cancellationToken = default)
        => ExecuteDurableWriteAsync(
            () => ResolveTopicDraftCleanup(
                threadId,
                expectedRevision,
                currentCandidate),
            cancellationToken);

    internal ComposerDraftClearResult ResolveTopicDraftCleanup(
        string threadId,
        long expectedRevision,
        ComposerDraft? currentCandidate)
    {
        if (expectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var transaction = conn.BeginTransaction();
        var stored = ReadComposerDraftState(threadId, transaction);
        var pending = ReadPendingTopicSnapshot(threadId, transaction);
        composerDraftObserver?.Checkpoint(
            ComposerDraftTransactionCheckpoint.CleanupObserved,
            threadId,
            expectedRevision);

        var newer = HighestCompleteSnapshot(expectedRevision, currentCandidate, pending);
        if (newer is not null)
        {
            composerDraftObserver?.Checkpoint(
                ComposerDraftTransactionCheckpoint.BeforeNewerSnapshotWrite,
                threadId,
                expectedRevision);
            _ = SetComposerDraft(
                TopicDraftKind,
                threadId,
                newer.Text,
                newer.Revision,
                newer.TopicSnapshot,
                transaction);
            DeletePendingTopicSnapshots(threadId, newer.Revision, transaction);
            stored = newer;
        }

        ComposerDraftClearResult result;
        if (stored is null)
        {
            result = ComposerDraftClearResult.Missing;
        }
        else if (stored.Revision != expectedRevision)
        {
            result = ComposerDraftClearResult.Superseded;
        }
        else
        {
            using var clear = conn.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM composer_drafts
                WHERE kind = $kind AND entity_id = $id
                  AND typeof(revision) = 'integer' AND revision = $revision;
                """;
            clear.Parameters.AddWithValue("$kind", TopicDraftKind);
            clear.Parameters.AddWithValue("$id", threadId);
            clear.Parameters.AddWithValue("$revision", expectedRevision);
            if (clear.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "The submitted topic draft changed during atomic cleanup.");
            DeletePendingTopicSnapshots(threadId, expectedRevision, transaction);
            result = ComposerDraftClearResult.Cleared;
        }

        transaction.Commit();
        return result;
    }

    private ComposerDraft? ReadComposerDraftState(
        string threadId,
        SqliteTransaction transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT text, revision, typeof(revision), snapshot_json
            FROM composer_drafts
            WHERE kind = $kind AND entity_id = $id;
            """;
        command.Parameters.AddWithValue("$kind", TopicDraftKind);
        command.Parameters.AddWithValue("$id", threadId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var text = reader.GetString(0);
        if (reader.IsDBNull(1)
            || !string.Equals(reader.GetString(2), "integer", StringComparison.Ordinal)
            || reader.GetInt64(1) <= 0)
            return new ComposerDraft(text, 0, IsMalformed: true);
        var revision = reader.GetInt64(1);
        if (reader.IsDBNull(3))
            return new ComposerDraft(
                text,
                revision,
                TopicSnapshot: TopicComposerSnapshot.TextOnly(text));
        try
        {
            var snapshot = JsonSerializer.Deserialize<TopicComposerSnapshot>(
                reader.GetString(3),
                JsonOpts);
            return snapshot is not null && ValidTopicComposerSnapshot(snapshot, text)
                ? new ComposerDraft(
                    text,
                    revision,
                    TopicSnapshot: NormalizeTopicComposerSnapshot(snapshot))
                : new ComposerDraft(text, 0, IsMalformed: true);
        }
        catch (Exception exception) when (
            exception is JsonException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return new ComposerDraft(text, 0, IsMalformed: true);
        }
    }

    private static ComposerDraft? HighestCompleteSnapshot(
        long expectedRevision,
        ComposerDraft? currentCandidate,
        ComposerDraft? pending)
        => new[] { currentCandidate, pending }
            .Where(candidate => candidate is
            {
                IsMalformed: false,
                TopicSnapshot: not null
            } && candidate.Revision > expectedRevision)
            .OrderByDescending(candidate => candidate!.Revision)
            .FirstOrDefault();

    private ComposerDraft? ReadPendingTopicSnapshot(
        string threadId,
        SqliteTransaction transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT text, revision, snapshot_json
            FROM pending_topic_drafts
            WHERE entity_id = $id;
            """;
        command.Parameters.AddWithValue("$id", threadId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var text = reader.GetString(0);
        var revision = reader.GetInt64(1);
        var snapshot = JsonSerializer.Deserialize<TopicComposerSnapshot>(
                           reader.GetString(2),
                           JsonOpts)
                       ?? throw new InvalidDataException(
                           "A pending topic snapshot is missing its payload.");
        if (revision <= 0 || !ValidTopicComposerSnapshot(snapshot, text))
            throw new InvalidDataException("A pending topic snapshot is malformed.");
        return new ComposerDraft(
            text,
            revision,
            TopicSnapshot: NormalizeTopicComposerSnapshot(snapshot));
    }

    public Task<ComposerDraftClearResult> CompareAndClearTopicDraftAsync(
        string threadId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => ResolveTopicDraftCleanupAsync(
            threadId,
            expectedRevision,
            null,
            cancellationToken);

    private void ImportLegacyPendingTopicSnapshots()
    {
        if (!File.Exists(legacyPendingComposerPath)) return;
        using var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacyPendingComposerPath,
            DefaultTimeout = 1
        }.ToString());
        legacy.Open();
        ApplyKey(legacy, key);
        using var exists = legacy.CreateCommand();
        exists.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = 'pending_topic_snapshots';
            """;
        if (exists.ExecuteScalar() is null) return;
        using var read = legacy.CreateCommand();
        read.CommandText = """
            SELECT entity_id, text, revision, snapshot_json
            FROM pending_topic_snapshots
            ORDER BY revision;
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var entityId = reader.GetString(0);
            var text = reader.GetString(1);
            var revision = reader.GetInt64(2);
            var snapshot = JsonSerializer.Deserialize<TopicComposerSnapshot>(
                               reader.GetString(3),
                               JsonOpts)
                           ?? throw new InvalidDataException(
                               "A legacy pending topic snapshot is missing its payload.");
            if (!ValidTopicComposerSnapshot(snapshot, text))
                throw new InvalidDataException(
                    "A legacy pending topic snapshot is malformed.");
            StagePendingTopicSnapshot(entityId, snapshot, revision);
        }
        legacy.Close();
        TryDeleteLegacyPendingDatabase();
    }

    private void TryDeleteLegacyPendingDatabase()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(legacyPendingComposerPath + suffix); }
            catch { }
        }
    }

    private void ReplayPendingTopicSnapshots()
    {
        ImportLegacyPendingTopicSnapshots();
        ExecuteDurableWrite(() =>
        {
            using var transaction = conn.BeginTransaction();
            using var read = conn.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT entity_id, text, revision, snapshot_json
                FROM pending_topic_drafts
                ORDER BY revision;
                """;
            using var reader = read.ExecuteReader();
            var staged =
                new List<(string EntityId, string Text, long Revision, TopicComposerSnapshot Snapshot)>();
            while (reader.Read())
            {
                var entityId = reader.GetString(0);
                var text = reader.GetString(1);
                var revision = reader.GetInt64(2);
                var snapshot = JsonSerializer.Deserialize<TopicComposerSnapshot>(
                                   reader.GetString(3),
                                   JsonOpts)
                               ?? throw new InvalidDataException(
                                   "A pending topic snapshot is missing its payload.");
                if (revision <= 0 || !ValidTopicComposerSnapshot(snapshot, text))
                    throw new InvalidDataException(
                        "A pending topic snapshot is malformed.");
                staged.Add((
                    entityId,
                    text,
                    revision,
                    NormalizeTopicComposerSnapshot(snapshot)));
            }
            reader.Close();
            foreach (var item in staged)
            {
                _ = SetComposerDraft(
                    TopicDraftKind,
                    item.EntityId,
                    item.Text,
                    item.Revision,
                    item.Snapshot,
                    transaction);
                DeletePendingTopicSnapshots(item.EntityId, item.Revision, transaction);
            }
            transaction.Commit();
        });
    }

    private ComposerDraft? GetComposerDraftState(string kind, string entityId)
    {
        using (var read = conn.CreateCommand())
        {
            read.CommandText = """
                SELECT text, revision, typeof(revision), snapshot_json
                FROM composer_drafts
                WHERE kind = $kind AND entity_id = $id;
                """;
            read.Parameters.AddWithValue("$kind", kind);
            read.Parameters.AddWithValue("$id", entityId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                return null;

            var text = reader.GetString(0);
            var revisionType = reader.GetString(2);
            if (!reader.IsDBNull(1)
                && string.Equals(revisionType, "integer", StringComparison.Ordinal)
                && reader.GetInt64(1) > 0)
            {
                var revision = reader.GetInt64(1);
                if (!string.Equals(kind, TopicDraftKind, StringComparison.Ordinal))
                    return new ComposerDraft(text, revision);
                if (reader.IsDBNull(3))
                    return new ComposerDraft(
                        text,
                        revision,
                        TopicSnapshot: TopicComposerSnapshot.TextOnly(text));
                try
                {
                    var snapshot = JsonSerializer.Deserialize<TopicComposerSnapshot>(
                        reader.GetString(3),
                        JsonOpts);
                    if (snapshot is null || !ValidTopicComposerSnapshot(snapshot, text))
                        return new ComposerDraft(text, 0, IsMalformed: true);
                    return new ComposerDraft(
                        text,
                        revision,
                        TopicSnapshot: NormalizeTopicComposerSnapshot(snapshot));
                }
                catch (Exception exception) when (
                    exception is JsonException
                    or ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    return new ComposerDraft(text, 0, IsMalformed: true);
                }
            }

            if (!reader.IsDBNull(1))
                return new ComposerDraft(text, 0, IsMalformed: true);
        }

        return ExecuteDurableWrite(() =>
        {
            using var transaction = conn.BeginTransaction();
            var migratedRevision = ComposerDraftRevision.New();
            using var migrate = conn.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                UPDATE composer_drafts
                SET revision = $revision
                WHERE kind = $kind AND entity_id = $id AND revision IS NULL;
                """;
            migrate.Parameters.AddWithValue("$revision", migratedRevision);
            migrate.Parameters.AddWithValue("$kind", kind);
            migrate.Parameters.AddWithValue("$id", entityId);
            if (migrate.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Legacy composer draft migration lost its revision fence.");
            transaction.Commit();
            var migratedText = GetComposerDraftText(kind, entityId);
            return new ComposerDraft(
                migratedText,
                migratedRevision,
                TopicSnapshot: string.Equals(
                    kind,
                    TopicDraftKind,
                    StringComparison.Ordinal)
                    ? TopicComposerSnapshot.TextOnly(migratedText)
                    : null);
        });
    }

    private string GetComposerDraftText(string kind, string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT text FROM composer_drafts
            WHERE kind = $kind AND entity_id = $id;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    private ComposerDraftMutationResult SetComposerDraft(
        string kind,
        string entityId,
        string text,
        long revision,
        TopicComposerSnapshot? topicSnapshot,
        SqliteTransaction? transaction = null)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        string? snapshotJson = null;
        if (string.Equals(kind, TopicDraftKind, StringComparison.Ordinal))
        {
            topicSnapshot ??= TopicComposerSnapshot.TextOnly(text);
            if (!ValidTopicComposerSnapshot(topicSnapshot, text))
                throw new ArgumentException(
                    "The topic composer snapshot is incomplete or inconsistent.",
                    nameof(topicSnapshot));
            topicSnapshot = NormalizeTopicComposerSnapshot(topicSnapshot);
            snapshotJson = JsonSerializer.Serialize(topicSnapshot, JsonOpts);
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO composer_drafts(kind, entity_id, text, revision, snapshot_json)
            VALUES($kind, $id, $text, $revision, $snapshot)
            ON CONFLICT(kind, entity_id) DO UPDATE
            SET text = excluded.text,
                revision = excluded.revision,
                snapshot_json = excluded.snapshot_json
            WHERE typeof(composer_drafts.revision) <> 'integer'
               OR composer_drafts.revision IS NULL
               OR composer_drafts.revision < excluded.revision;
            """;
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$revision", revision);
        cmd.Parameters.AddWithValue("$snapshot", (object?)snapshotJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        if (cmd.ExecuteNonQuery() == 1)
            return ComposerDraftMutationResult.Persisted;

        using var existing = conn.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT text, revision, typeof(revision), snapshot_json
            FROM composer_drafts
            WHERE kind = $kind AND entity_id = $id;
            """;
        existing.Parameters.AddWithValue("$kind", kind);
        existing.Parameters.AddWithValue("$id", entityId);
        using var reader = existing.ExecuteReader();
        if (!reader.Read()
            || reader.IsDBNull(1)
            || !string.Equals(reader.GetString(2), "integer", StringComparison.Ordinal)
            || reader.GetInt64(1) != revision
            || !string.Equals(reader.GetString(0), text, StringComparison.Ordinal))
            return ComposerDraftMutationResult.Superseded;

        if (!string.Equals(kind, TopicDraftKind, StringComparison.Ordinal))
            return ComposerDraftMutationResult.AlreadyPersisted;

        var storedSnapshot = reader.IsDBNull(3)
            ? TopicComposerSnapshot.TextOnly(text)
            : JsonSerializer.Deserialize<TopicComposerSnapshot>(
                reader.GetString(3),
                JsonOpts);
        return storedSnapshot is not null
               && ValidTopicComposerSnapshot(storedSnapshot, text)
               && TopicComposerSnapshotsEqual(storedSnapshot, topicSnapshot!)
            ? ComposerDraftMutationResult.AlreadyPersisted
            : ComposerDraftMutationResult.Superseded;
    }

    internal static bool TopicComposerSnapshotsEqual(
        TopicComposerSnapshot left,
        TopicComposerSnapshot right)
        => string.Equals(
            JsonSerializer.Serialize(
                NormalizeTopicComposerSnapshot(left),
                JsonOpts),
            JsonSerializer.Serialize(
                NormalizeTopicComposerSnapshot(right),
                JsonOpts),
            StringComparison.Ordinal);

    private static string ComputeTopicComposerFingerprint(TopicComposerSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(
            NormalizeTopicComposerSnapshot(snapshot),
            JsonOpts);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static bool ValidTopicComposerSnapshot(
        TopicComposerSnapshot snapshot,
        string expectedText)
        => snapshot.Text is not null
           && string.Equals(snapshot.Text, expectedText, StringComparison.Ordinal)
           && snapshot.Attachments is not null
           && snapshot.Attachments.All(attachment =>
               attachment is not null
               && !string.IsNullOrWhiteSpace(attachment.Id)
               && !string.IsNullOrWhiteSpace(attachment.Name)
               && !string.IsNullOrWhiteSpace(attachment.Path)
              && attachment.Size >= 0)
           && !(snapshot.WidgetMode && !string.IsNullOrWhiteSpace(snapshot.WidgetId))
           && !(snapshot.WidgetMode && snapshot.Widget is not null)
           && (snapshot.Widget is null
               || !string.IsNullOrWhiteSpace(snapshot.Widget.Id)
               && snapshot.Widget.Name is not null
               && snapshot.Widget.Prompt is not null
               && snapshot.Widget.Html is not null
               && string.Equals(
                   snapshot.WidgetId,
                   snapshot.Widget.Id,
                   StringComparison.Ordinal))
           && snapshot.TargetDeviceId is not null;

    private static TopicComposerSnapshot NormalizeTopicComposerSnapshot(
        TopicComposerSnapshot snapshot)
        => snapshot with
        {
            Attachments = snapshot.Attachments
                .Select(attachment => attachment with
                {
                    Path = System.IO.Path.GetFullPath(attachment.Path)
                })
                .ToArray(),
            WidgetId = string.IsNullOrWhiteSpace(snapshot.WidgetId)
                ? null
                : snapshot.WidgetId,
            TargetDeviceId = snapshot.TargetDeviceId ?? ""
        };


    private void DeleteComposerDraft(SqliteTransaction transaction, string kind, string entityId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM composer_drafts WHERE kind = $kind AND entity_id = $id;";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.ExecuteNonQuery();
    }

    // ---- local UI state ----------------------------------------------------

    public string? GetLastDesktopTopicId()
        => Volatile.Read(ref lastDesktopTopicId);

    public void SetLastDesktopTopicId(string? threadId)
    {
        SetMetaValue(LastDesktopTopicMetaKey, threadId);
        Volatile.Write(ref lastDesktopTopicId, threadId);
    }

    internal void StageLastDesktopTopicId(string? threadId)
        => Volatile.Write(ref lastDesktopTopicId, threadId);

    public string? GetLastDesktopConversationKey()
        => Volatile.Read(ref lastDesktopConversationKey);

    public void SetLastDesktopConversationKey(string? conversationKey)
    {
        SetMetaValue(LastDesktopConversationMetaKey, conversationKey);
        Volatile.Write(ref lastDesktopConversationKey, conversationKey);
    }

    internal void StageLastDesktopConversationKey(string? conversationKey)
        => Volatile.Write(ref lastDesktopConversationKey, conversationKey);

    private string? GetMetaValue(string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMetaValue(string key, string? value)
    {
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(value))
        {
            cmd.CommandText = "DELETE FROM meta WHERE k = $key;";
        }
        else
        {
            cmd.CommandText = "INSERT INTO meta(k, v) VALUES($key, $value) ON CONFLICT(k) DO UPDATE SET v = excluded.v;";
            cmd.Parameters.AddWithValue("$value", value);
        }
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
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
        profile.Memories = LoadMemories();
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
                       group_id, group_name, group_owner_handle, group_members_json, group_version,
                       created_at, last_activity_at, is_pinned
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
                c.CreatedAt = r.IsDBNull(9) ? (DateTimeOffset?)null : ParseAt(r.GetString(9));
                c.LastActivityAt = r.IsDBNull(10) ? null : ParseAt(r.GetString(10));
                c.IsPinned = !r.IsDBNull(11) && r.GetInt64(11) != 0;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT handle, role, text, via, at, line_id, status, sender_handle, internal, reasoning, model_id
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
                    Reasoning = r.IsDBNull(9) ? null : r.GetString(9),
                    ModelId = r.IsDBNull(10) ? null : r.GetString(10)
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
            DateTimeOffset? first = null;
            DateTimeOffset? last = null;
            using (var timestamps = conn.CreateCommand())
            {
                timestamps.CommandText = """
                    SELECT at FROM own_chat
                    WHERE thread_id IS NULL
                    ORDER BY julianday(at), id;
                    """;
                using var reader = timestamps.ExecuteReader();
                while (reader.Read())
                {
                    var at = ParseAt(reader.GetString(0));
                    first ??= at;
                    last = at;
                }
            }
            var defaultId = Guid.NewGuid().ToString("n");
            var createdAt = first ?? DateTimeOffset.UnixEpoch;
            EnsureOwnThread(defaultId, "General", createdAt);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE own_chat SET thread_id = $tid WHERE thread_id IS NULL;";
            cmd.Parameters.AddWithValue("$tid", defaultId);
            cmd.ExecuteNonQuery();
            if (last.HasValue) SetOwnThreadActivity(defaultId, last.Value);
        }

        var threads = new List<OwnThread>();
        var byId = new Dictionary<string, OwnThread>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, title, created_at, last_activity_at, is_pinned,
                       execution_device_id, execution_device_name, execution_device_platform,
                       execution_at, execution_run_id
                FROM own_threads ORDER BY sort_order, created_at, id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = new OwnThread
                {
                    Id = r.GetString(0),
                    Title = r.GetString(1),
                    CreatedAt = ParseAt(r.GetString(2)),
                    LastActivityAt = r.IsDBNull(3) ? null : ParseAt(r.GetString(3)),
                    IsPinned = !r.IsDBNull(4) && r.GetInt64(4) != 0,
                    ExecutionDeviceId = r.IsDBNull(5) ? null : r.GetString(5),
                    ExecutionDeviceName = r.IsDBNull(6) ? null : r.GetString(6),
                    ExecutionDevicePlatform = r.IsDBNull(7) ? null : r.GetString(7),
                    ExecutionAt = r.IsDBNull(8) ? null : ParseAt(r.GetString(8)),
                    ExecutionRunId = r.IsDBNull(9) ? null : r.GetString(9)
                };
                threads.Add(t);
                byId[t.Id] = t;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT thread_id, role, text, via, at, line_id, status, internal, reasoning, sender_handle, reply_to_line_id, model_id
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
                    SenderHandle = r.IsDBNull(9) ? null : r.GetString(9),
                    ReplyToLineId = r.IsDBNull(10) ? null : r.GetString(10),
                    ModelId = r.IsDBNull(11) ? null : r.GetString(11)
                });
            }
        }
        return threads;
    }

    private List<MemoryItem> LoadMemories()
    {
        var memories = new List<MemoryItem>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, content, category, origin, importance, confidence, stability,
                   reinforcement_count, source_thread_id, source_line_id, created_at, updated_at,
                   last_reinforced_at, recall_count, last_recalled_at
            FROM memories ORDER BY updated_at DESC, id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            memories.Add(new MemoryItem
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Content = reader.GetString(2),
                Category = reader.GetString(3),
                Origin = reader.GetString(4),
                Importance = reader.GetDouble(5),
                Confidence = reader.GetDouble(6),
                Stability = reader.GetDouble(7),
                ReinforcementCount = reader.GetInt32(8),
                SourceThreadId = reader.IsDBNull(9) ? null : reader.GetString(9),
                SourceLineId = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = ParseAt(reader.GetString(11)),
                UpdatedAt = ParseAt(reader.GetString(12)),
                LastReinforcedAt = ParseAt(reader.GetString(13)),
                RecallCount = reader.GetInt32(14),
                LastRecalledAt = reader.IsDBNull(15) ? null : ParseAt(reader.GetString(15))
            });
        }
        return memories;
    }

    public void UpsertMemory(MemoryItem memory)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        UpsertMemory(transaction, memory, preserveLocalUsage: false);
        transaction.Commit();
    }

    public void DeleteMemory(string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    internal bool TryApplyMemoryUpsert(
        MemoryItem memory,
        string versionKey,
        string version,
        string deleteKind)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        var newest = Newest(
            GetSyncVersion(transaction, versionKey),
            GetSyncTombstoneVersion(transaction, deleteKind, memory.Id));
        if (!ProjectionVersion.IsNewer(version, newest))
        {
            transaction.Rollback();
            return false;
        }

        UpsertMemory(transaction, memory, preserveLocalUsage: true);
        UpsertSyncVersion(transaction, versionKey, version);
        transaction.Commit();
        return true;
    }

    internal bool TryApplyMemoryDelete(
        string id,
        string tombstoneKind,
        string version,
        string upsertKey)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        var newest = Newest(
            GetSyncTombstoneVersion(transaction, tombstoneKind, id),
            GetSyncVersion(transaction, upsertKey));
        if (!ProjectionVersion.IsNewer(version, newest))
        {
            transaction.Rollback();
            return false;
        }

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM memories WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
        UpsertSyncTombstone(transaction, tombstoneKind, id, version);
        transaction.Commit();
        return true;
    }

    public void TouchMemories(IReadOnlyCollection<string> ids, DateTimeOffset at)
    {
        if (ids.Count == 0) return;
        using var transaction = conn.BeginTransaction(deferred: false);
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE memories
                SET recall_count = recall_count + 1, last_recalled_at = $at
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$at", at.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void UpsertMemory(
        SqliteTransaction transaction,
        MemoryItem memory,
        bool preserveLocalUsage)
    {
        var localUpdates = preserveLocalUsage
            ? ""
            : ", recall_count = excluded.recall_count, last_recalled_at = excluded.last_recalled_at";
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT INTO memories(
                id, title, content, category, origin, importance, confidence, stability,
                reinforcement_count, source_thread_id, source_line_id, created_at, updated_at,
                last_reinforced_at, recall_count, last_recalled_at)
            VALUES(
                $id, $title, $content, $category, $origin, $importance, $confidence, $stability,
                $reinforcement, $sourceThread, $sourceLine, $created, $updated,
                $reinforced, $recallCount, $lastRecalled)
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
                last_reinforced_at = excluded.last_reinforced_at{localUpdates};
            """;
        cmd.Parameters.AddWithValue("$id", memory.Id);
        cmd.Parameters.AddWithValue("$title", memory.Title);
        cmd.Parameters.AddWithValue("$content", memory.Content);
        cmd.Parameters.AddWithValue("$category", memory.Category);
        cmd.Parameters.AddWithValue("$origin", memory.Origin);
        cmd.Parameters.AddWithValue("$importance", memory.Importance);
        cmd.Parameters.AddWithValue("$confidence", memory.Confidence);
        cmd.Parameters.AddWithValue("$stability", memory.Stability);
        cmd.Parameters.AddWithValue("$reinforcement", memory.ReinforcementCount);
        cmd.Parameters.AddWithValue("$sourceThread", (object?)memory.SourceThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sourceLine", (object?)memory.SourceLineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", memory.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", memory.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$reinforced", memory.LastReinforcedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$recallCount", memory.RecallCount);
        cmd.Parameters.AddWithValue("$lastRecalled", memory.LastRecalledAt.HasValue
            ? memory.LastRecalledAt.Value.ToString("O")
            : DBNull.Value);
        cmd.ExecuteNonQuery();
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
        => SaveProfileAndSyncState(
            SerializeProfileForStorage(profile), versions, tombstones, beforeCommit, circleRenames);

    /// <summary>
    /// Atomic sync-transaction variant that accepts a pre-serialized, already-bounded profile blob
    /// (see <see cref="SerializeProfileForStorage"/>). Used by the asynchronous persistence worker so
    /// the profile is serialized off the transaction.
    /// </summary>
    internal bool SaveProfileAndSyncState(
        string profileJson,
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
            if (!ProjectionVersion.IsNewer(version.Version, Newest(current, opposing)))
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
            if (!ProjectionVersion.IsNewer(tombstone.Version, Newest(current, opposing)))
            {
                transaction.Rollback();
                return false;
            }
            acceptedTombstones[key] = tombstone.Version;
        }
        using (var profileCommand = conn.CreateCommand())
        {
            profileCommand.Transaction = transaction;
            SaveProfileJson(profileCommand, profileJson);
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
            DomainProjectionKinds.ContactUpsert => DomainProjectionKinds.ContactDelete,
            DomainProjectionKinds.CircleUpsert => DomainProjectionKinds.CircleDelete,
            DomainProjectionKinds.MemoryUpsert => DomainProjectionKinds.MemoryDelete,
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
            DomainProjectionKinds.ContactDelete => DomainProjectionKinds.ContactUpsert,
            DomainProjectionKinds.CircleDelete => DomainProjectionKinds.CircleUpsert,
            DomainProjectionKinds.MemoryDelete => DomainProjectionKinds.MemoryUpsert,
            _ => ""
        };
        entityKey = upsertKind.Length == 0 ? "" : upsertKind + "\u001f" + entityId;
        return entityKey.Length > 0;
    }

    private static void SaveProfile(SqliteCommand cmd, MeshProfile profile)
        => SaveProfileJson(cmd, SerializeProfileForStorage(profile));

    /// <summary>
    /// Serializes a profile into the bounded on-disk blob. The scalable, append-only collections
    /// (conversations, own-chat, own-threads, memories) live in their own row tables, and the
    /// capability assets (skills, knowledge, widgets) live in the Mesh 1.17 asset tables; all of
    /// them are stripped here so the profile row stays small no matter how much data the identity
    /// accumulates. Marketplace configuration (skillMarketplaces) is bounded and is retained.
    /// </summary>
    public static string SerializeProfileForStorage(MeshProfile profile)
    {
        var node = JsonSerializer.SerializeToNode(profile, JsonOpts)!.AsObject();
        node.Remove("conversations");
        node.Remove("ownChat");
        node.Remove("ownThreads");
        node.Remove("memories");
        node.Remove("skills");
        node.Remove("knowledge");
        node.Remove("widgets");
        return node.ToJsonString(JsonOpts);
    }

    private static void SaveProfileJson(SqliteCommand cmd, string json)
    {
        cmd.CommandText = "INSERT INTO profile(id, json) VALUES(1, $j) ON CONFLICT(id) DO UPDATE SET json = $j;";
        cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Persists a pre-serialized, already-bounded profile blob (produced by
    /// <see cref="SerializeProfileForStorage"/>). Used by the asynchronous persistence coordinator
    /// so the potentially expensive serialization happens off the database worker.
    /// </summary>
    public void SaveProfileJson(string json)
    {
        using var cmd = conn.CreateCommand();
        SaveProfileJson(cmd, json);
    }

    // ---- projection merge state -------------------------------------------

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

    internal IReadOnlyList<CircleRenameProjection> GetSyncCircleRenames(string entityId)
    {
        var result = new List<CircleRenameProjection>();
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
            result.Add(new CircleRenameProjection(reader.GetString(0), reader.GetString(1)));
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

    public void DeleteOwnChatLine(string threadId, string lineId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM own_chat
            WHERE thread_id = $thread AND (line_id = $line OR reply_to_line_id = $line);
            """;
        cmd.Parameters.AddWithValue("$thread", threadId);
        cmd.Parameters.AddWithValue("$line", lineId);
        cmd.ExecuteNonQuery();
    }

    public void ApplyTopicLineDelete(
        string threadId,
        string lineId,
        string entityId,
        string kind,
        string version)
    {
        using var tx = conn.BeginTransaction();
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM own_chat
                WHERE thread_id = $thread AND (line_id = $line OR reply_to_line_id = $line);
                """;
            delete.Parameters.AddWithValue("$thread", threadId);
            delete.Parameters.AddWithValue("$line", lineId);
            delete.ExecuteNonQuery();
        }
        UpsertSyncTombstone(tx, kind, entityId, version);
        tx.Commit();
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
        DeleteComposerDraft(tx, TopicDraftKind, id);
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
        DeleteComposerDraft(tx, ConversationDraftKind, handle);
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
                ProfileProjection.CircleEntityId(ancestor.PreviousName));
            insert.Parameters.AddWithValue("$name", ancestor.PreviousName);
            insert.Parameters.AddWithValue("$version", ancestor.DeleteVersion);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Records that a conversation thread exists so an empty thread survives a reload.</summary>
    public void EnsureConversation(string handle, DateTimeOffset? createdAt = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO conversations(handle, created_at, sort_order, last_activity_at)
            VALUES($h, $t, (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM conversations), $t);
            """;
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue("$t", (createdAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O"));
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
        int groupVersion,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? lastActivityAt = null,
        bool isPinned = false,
        bool replaceCreatedAt = false)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations(
                handle, created_at, sort_order, service_id, service_name, provider_handle,
                group_id, group_name, group_owner_handle, group_members_json, group_version,
                last_activity_at, is_pinned)
            VALUES($h, $created, $sort, $sid, $sname, $provider, $gid, $gname, $owner, $members, $gversion,
                $activity, $pinned)
            ON CONFLICT(handle) DO UPDATE SET
                created_at = CASE WHEN $replaceCreated = 1
                    THEN excluded.created_at ELSE created_at END,
                sort_order = excluded.sort_order,
                service_id = excluded.service_id,
                service_name = excluded.service_name,
                provider_handle = excluded.provider_handle,
                group_id = excluded.group_id,
                group_name = excluded.group_name,
                group_owner_handle = excluded.group_owner_handle,
                group_members_json = excluded.group_members_json,
                group_version = excluded.group_version,
                last_activity_at = CASE
                    WHEN excluded.last_activity_at IS NOT NULL
                         AND (last_activity_at IS NULL
                              OR julianday(excluded.last_activity_at) > julianday(last_activity_at))
                    THEN excluded.last_activity_at
                    ELSE last_activity_at
                END,
                is_pinned = excluded.is_pinned;
            """;
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.Parameters.AddWithValue(
            "$created",
            (createdAt ?? DateTimeOffset.UnixEpoch).UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$replaceCreated", replaceCreatedAt ? 1 : 0);
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
        cmd.Parameters.AddWithValue("$activity",
            lastActivityAt.HasValue ? lastActivityAt.Value.UtcDateTime.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$pinned", isPinned ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void SetConversationActivity(string handle, DateTimeOffset at)
        => AdvanceConversationActivity(handle, at);

    private void AdvanceConversationActivity(string handle, DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        AdvanceConversationActivity(cmd, handle, at);
    }

    private void AdvanceConversationActivity(
        SqliteTransaction transaction,
        string handle,
        DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        AdvanceConversationActivity(cmd, handle, at);
    }

    private static void AdvanceConversationActivity(
        SqliteCommand cmd,
        string handle,
        DateTimeOffset at)
    {
        cmd.CommandText = """
            UPDATE conversations
            SET last_activity_at = $at
            WHERE handle = $h
              AND (last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at));
            """;
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    public void SetConversationPin(string handle, bool pinned)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET is_pinned = $p WHERE handle = $h;";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$h", handle);
        cmd.ExecuteNonQuery();
    }

    public void SetConversationPinAndActivity(string handle, bool pinned, DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE conversations
            SET is_pinned = $p,
                last_activity_at = CASE
                    WHEN last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at)
                    THEN $at ELSE last_activity_at
                END
            WHERE handle = $h;
            """;
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$h", handle);
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
        => ReorderConversations(orderedHandles, null, null);

    public void ReorderConversations(
        IReadOnlyList<string> orderedHandles,
        string? activityHandle,
        DateTimeOffset? activityAt)
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
                line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning, model_id)
            VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning, $modelId);
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
        cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        AdvanceConversationActivity(handle, line.At);
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
                    sender_handle = $sender, internal = $internal, reasoning = $reasoning, model_id = COALESCE($modelId, model_id)
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
                    line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning, model_id)
                VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning, $modelId);
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
        cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
    }

    /// <summary>Appends a single line to a "Me" topic thread.</summary>
    public void AppendOwnChat(string threadId, ChatLine line)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO own_chat(
                line_id, thread_id, role, text, reply_to_line_id, via, status, at, internal, reasoning, sender_handle, model_id)
            VALUES($lid, $tid, $r, $x, $replyTo, $v, $s, $a, $i, $rz, $sender, $modelId);
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
        cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replyTo", (object?)line.ReplyToLineId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        AdvanceOwnThreadActivity(threadId, line.At);
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
                SET role = $r, text = $x, reply_to_line_id = $replyTo,
                    via = $v, status = $s, at = $a,
                    internal = $internal, reasoning = $reasoning, sender_handle = $sender, model_id = COALESCE($modelId, model_id)
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
                    line_id, thread_id, role, text, reply_to_line_id, via, status, at, internal, reasoning, sender_handle, model_id)
                VALUES($lid, $tid, $r, $x, $replyTo, $v, $s, $a, $internal, $reasoning, $sender, $modelId);
                """;
            AddOwnChatParameters(insert, threadId, line);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal bool TryApplyOwnSyncLine(
        string threadId,
        ChatLine line,
        string versionKey,
        string version,
        string? lineDeleteKind = null)
    {
        if (line.At == default) return false;
        using var tx = conn.BeginTransaction();
        if (!ProjectionVersion.IsNewer(version, GetSyncVersion(tx, versionKey)))
            return false;
        if (lineDeleteKind is not null
            && (GetSyncTombstoneVersion(
                    tx,
                    lineDeleteKind,
                    DomainProjectionEntityIds.TopicLine(threadId, line.Id)) is not null
                || line.ReplyToLineId is not null
                && GetSyncTombstoneVersion(
                    tx,
                    lineDeleteKind,
                    DomainProjectionEntityIds.TopicLine(threadId, line.ReplyToLineId)) is not null))
            return false;

        using (var parent = conn.CreateCommand())
        {
            parent.Transaction = tx;
            parent.CommandText = """
                INSERT OR IGNORE INTO own_threads(
                    id, title, created_at, sort_order, last_activity_at)
                VALUES(
                    $id, '', $at,
                    (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM own_threads),
                    $at);
                """;
            parent.Parameters.AddWithValue("$id", threadId);
            parent.Parameters.AddWithValue("$at", line.At.UtcDateTime.ToString("O"));
            parent.ExecuteNonQuery();
        }
        int updated;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE own_chat
                SET role = $r, text = $x, reply_to_line_id = $replyTo,
                    via = $v, status = $s, at = $a,
                    internal = $internal, reasoning = $reasoning, sender_handle = $sender, model_id = COALESCE($modelId, model_id)
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
                    line_id, thread_id, role, text, reply_to_line_id, via, status, at, internal, reasoning, sender_handle, model_id)
                VALUES($lid, $tid, $r, $x, $replyTo, $v, $s, $a, $internal, $reasoning, $sender, $modelId);
                """;
            AddOwnChatParameters(insert, threadId, line);
            insert.ExecuteNonQuery();
        }
        AdvanceOwnThreadActivity(tx, threadId, line.At);
        UpsertSyncVersion(tx, versionKey, version);
        tx.Commit();
        return true;
    }

    internal bool TryApplyConversationSyncLine(
        string handle,
        ChatLine line,
        string versionKey,
        string version)
    {
        if (line.At == default) return false;
        using var tx = conn.BeginTransaction();
        if (!ProjectionVersion.IsNewer(version, GetSyncVersion(tx, versionKey)))
            return false;

        using (var parent = conn.CreateCommand())
        {
            parent.Transaction = tx;
            parent.CommandText = """
                INSERT OR IGNORE INTO conversations(
                    handle, created_at, sort_order, last_activity_at)
                VALUES(
                    $h, $at,
                    (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM conversations),
                    $at);
                """;
            parent.Parameters.AddWithValue("$h", handle);
            parent.Parameters.AddWithValue("$at", line.At.UtcDateTime.ToString("O"));
            parent.ExecuteNonQuery();
        }
        int updated;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE chat_lines
                SET role = $r, text = $x, via = $v, status = $s, at = $a,
                    sender_handle = $sender, internal = $internal, reasoning = $reasoning, model_id = COALESCE($modelId, model_id)
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
                    line_id, handle, role, text, via, status, at, sender_handle, internal, reasoning, model_id)
                VALUES($lid, $h, $r, $x, $v, $s, $a, $sender, $internal, $reasoning, $modelId);
                """;
            AddChatLineParameters(insert, handle, line);
            insert.ExecuteNonQuery();
        }
        AdvanceConversationActivity(tx, handle, line.At);
        UpsertSyncVersion(tx, versionKey, version);
        tx.Commit();
        return true;
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
        cmd.Parameters.AddWithValue("$replyTo", (object?)line.ReplyToLineId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modelId", (object?)line.ModelId ?? DBNull.Value);
    }

    /// <summary>Records that a "Me" thread exists so an empty thread survives a reload.</summary>
    public void EnsureOwnThread(string id, string title, DateTimeOffset createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO own_threads(id, title, created_at, last_activity_at) VALUES($id, $t, $c, $c);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", createdAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upserts complete topic metadata and its explicit order.</summary>
    public void UpsertOwnThread(string id, string title, DateTimeOffset createdAt, int sortOrder,
        DateTimeOffset? lastActivityAt = null, bool isPinned = false,
        string? executionDeviceId = null, DateTimeOffset? executionAt = null,
        string? executionRunId = null, bool replaceExecutionMetadata = false,
        string? executionDeviceName = null, string? executionDevicePlatform = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO own_threads(id, title, created_at, sort_order, last_activity_at, is_pinned,
                execution_device_id, execution_device_name, execution_device_platform,
                execution_at, execution_run_id)
            VALUES($id, $title, $created, $sort, $activity, $pinned,
                $execDevice, $execName, $execPlatform, $execAt, $execRun)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                created_at = excluded.created_at,
                sort_order = excluded.sort_order,
                last_activity_at = CASE
                    WHEN excluded.last_activity_at IS NOT NULL
                         AND (last_activity_at IS NULL
                              OR julianday(excluded.last_activity_at) > julianday(last_activity_at))
                    THEN excluded.last_activity_at
                    ELSE last_activity_at
                END,
                is_pinned = excluded.is_pinned,
                execution_device_id = CASE WHEN $replaceExecution = 1
                    THEN excluded.execution_device_id
                    ELSE COALESCE(excluded.execution_device_id, execution_device_id) END,
                execution_device_name = CASE WHEN $replaceExecution = 1
                    THEN excluded.execution_device_name
                    ELSE COALESCE(excluded.execution_device_name, execution_device_name) END,
                execution_device_platform = CASE WHEN $replaceExecution = 1
                    THEN excluded.execution_device_platform
                    ELSE COALESCE(excluded.execution_device_platform, execution_device_platform) END,
                execution_at = CASE WHEN $replaceExecution = 1
                    THEN excluded.execution_at
                    ELSE COALESCE(excluded.execution_at, execution_at) END,
                execution_run_id = CASE WHEN $replaceExecution = 1
                    THEN excluded.execution_run_id
                    ELSE COALESCE(excluded.execution_run_id, execution_run_id) END;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$created", createdAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$sort", sortOrder);
        cmd.Parameters.AddWithValue("$activity", lastActivityAt.HasValue
            ? lastActivityAt.Value.UtcDateTime.ToString("O")
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$pinned", isPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$execDevice", (object?)executionDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$execName", (object?)executionDeviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$execPlatform", (object?)executionDevicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$execAt", executionAt.HasValue
            ? executionAt.Value.UtcDateTime.ToString("O")
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$execRun", (object?)executionRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$replaceExecution", replaceExecutionMetadata ? 1 : 0);
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
        => ReorderOwnThreads(orderedIds, null, null);

    public void ReorderOwnThreads(
        IReadOnlyList<string> orderedIds,
        string? activityThreadId,
        DateTimeOffset? activityAt)
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

    public void SetOwnThreadActivity(string id, DateTimeOffset at)
        => AdvanceOwnThreadActivity(id, at);

    private void AdvanceOwnThreadActivity(string id, DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        AdvanceOwnThreadActivity(cmd, id, at);
    }

    private void AdvanceOwnThreadActivity(
        SqliteTransaction transaction,
        string id,
        DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        AdvanceOwnThreadActivity(cmd, id, at);
    }

    private static void AdvanceOwnThreadActivity(
        SqliteCommand cmd,
        string id,
        DateTimeOffset at)
    {
        cmd.CommandText = """
            UPDATE own_threads
            SET last_activity_at = $at
            WHERE id = $id
              AND (last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at));
            """;
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetOwnThreadPin(string id, bool pinned)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE own_threads SET is_pinned = $p WHERE id = $id;";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetOwnThreadPinAndActivity(string id, bool pinned, DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET is_pinned = $p,
                last_activity_at = CASE
                    WHEN last_activity_at IS NULL OR julianday($at) > julianday(last_activity_at)
                    THEN $at ELSE last_activity_at
                END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetOwnThreadExecution(
        string id,
        string? deviceId,
        DateTimeOffset? at,
        string? runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_at = $at,
                execution_run_id = $rid
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$did", (object?)deviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$at",
            at.HasValue ? at.Value.UtcDateTime.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$rid", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetOwnThreadExecution(
        string id,
        string? deviceId,
        DateTimeOffset? at,
        string? runId,
        string? deviceName,
        string? devicePlatform)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_device_name = $dname,
                execution_device_platform = $dplatform,
                execution_at = $at,
                execution_run_id = $rid
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$did", (object?)deviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dname", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dplatform", (object?)devicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", at.HasValue ? (object)at.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$rid", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public bool TryBindOwnThreadDevice(
        string id,
        string deviceId,
        string? deviceName = null,
        string? devicePlatform = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_device_name = $dname,
                execution_device_platform = $dplatform
            WHERE id = $id
              AND (execution_device_id IS NULL OR trim(execution_device_id) = '');
            """;
        cmd.Parameters.AddWithValue("$did", deviceId);
        cmd.Parameters.AddWithValue("$dname", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dplatform", (object?)devicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool MoveOwnThreadToDevice(
        string id,
        string deviceId,
        string? deviceName,
        string? devicePlatform,
        DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_device_name = $dname,
                execution_device_platform = $dplatform,
                execution_at = NULL,
                execution_run_id = NULL,
                last_activity_at = CASE
                    WHEN last_activity_at IS NULL
                         OR julianday($activity) > julianday(last_activity_at)
                    THEN $activity ELSE last_activity_at
                END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$did", deviceId);
        cmd.Parameters.AddWithValue("$dname", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dplatform", (object?)devicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity", at.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool CompleteOwnThreadRunAndDeleteTopicOutbox(
        string id,
        string runId,
        string? triggerLineId,
        string? deviceId,
        string? deviceName,
        string? devicePlatform,
        DateTimeOffset? executionAt,
        DateTimeOffset activityAt)
    {
        using var transaction = conn.BeginTransaction();
        using (var correlation = conn.CreateCommand())
        {
            correlation.Transaction = transaction;
            var hasDurableIdentity = TopicRunProtocol.IsValidIdentifier(triggerLineId);
            correlation.CommandText = hasDurableIdentity
                ? """
                  SELECT EXISTS(
                      SELECT 1
                      FROM topic_run_correlations
                      WHERE run_id = $run
                        AND thread_id = $thread
                        AND trigger_line_id = $trigger);
                  """
                : """
                  SELECT NOT EXISTS(
                      SELECT 1 FROM topic_run_correlations WHERE thread_id = $thread)
                     AND NOT EXISTS(
                      SELECT 1 FROM topic_outbox WHERE thread_id = $thread);
                  """;
            correlation.Parameters.AddWithValue("$run", runId);
            correlation.Parameters.AddWithValue("$thread", id);
            correlation.Parameters.AddWithValue(
                "$trigger", hasDurableIdentity ? triggerLineId! : DBNull.Value);
            if (Convert.ToInt64(correlation.ExecuteScalar()) != 1)
                return false;
        }
        using (var current = conn.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT execution_run_id FROM own_threads WHERE id = $id;";
            current.Parameters.AddWithValue("$id", id);
            var activeRun = current.ExecuteScalar();
            if (activeRun is string active
                && !string.Equals(active, runId, StringComparison.Ordinal))
                return false;
        }
        using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_device_name = $dname,
                execution_device_platform = $dplatform,
                execution_at = $at,
                execution_run_id = NULL,
                last_activity_at = CASE
                    WHEN last_activity_at IS NULL
                         OR julianday($activity) > julianday(last_activity_at)
                    THEN $activity ELSE last_activity_at
                END
            WHERE id = $id
              AND (execution_run_id = $run OR execution_run_id IS NULL);
            """;
        update.Parameters.AddWithValue("$did", (object?)deviceId ?? DBNull.Value);
        update.Parameters.AddWithValue("$dname", (object?)deviceName ?? DBNull.Value);
        update.Parameters.AddWithValue("$dplatform", (object?)devicePlatform ?? DBNull.Value);
        update.Parameters.AddWithValue(
            "$at",
            executionAt.HasValue ? executionAt.Value.UtcDateTime.ToString("O") : DBNull.Value);
        update.Parameters.AddWithValue("$activity", activityAt.UtcDateTime.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$run", runId);
        if (update.ExecuteNonQuery() != 1) return false;

        using var delete = conn.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM topic_outbox WHERE run_id = $run;";
        delete.Parameters.AddWithValue("$run", runId);
        delete.ExecuteNonQuery();
        MarkTopicRunCorrelationTerminal(
            transaction, runId, timeProvider.GetUtcNow(), activityAt);
        transaction.Commit();
        return true;
    }
    public bool SetOwnThreadExecutionAndActivity(
        string id,
        string? deviceId,
        string? deviceName,
        string? devicePlatform,
        DateTimeOffset? executionAt,
        string? runId,
        DateTimeOffset activityAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE own_threads
            SET execution_device_id = $did,
                execution_device_name = $dname,
                execution_device_platform = $dplatform,
                execution_at = $at,
                execution_run_id = $rid,
                last_activity_at = CASE
                    WHEN last_activity_at IS NULL
                         OR julianday($activity) > julianday(last_activity_at)
                    THEN $activity ELSE last_activity_at
                END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$did", (object?)deviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dname", (object?)deviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dplatform", (object?)devicePlatform ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "$at",
            executionAt.HasValue ? executionAt.Value.UtcDateTime.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$rid", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$activity", activityAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
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
        DeleteComposerDraft(tx, TopicDraftKind, id);
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
        DeleteComposerDraft(tx, ConversationDraftKind, handle);
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

    private void MigrateOwnThreadActivity()
    {
        Exec("""
            UPDATE own_threads
            SET last_activity_at = COALESCE(
                (SELECT c.at FROM own_chat c
                 WHERE c.thread_id = own_threads.id
                 ORDER BY julianday(c.at) DESC, c.id DESC LIMIT 1),
                own_threads.created_at
            )
            WHERE last_activity_at IS NULL;
            """);
    }

    private void MigrateConversationActivity()
    {
        Exec("""
            UPDATE conversations
            SET last_activity_at = COALESCE(
                (SELECT l.at FROM chat_lines l
                 WHERE l.handle = conversations.handle
                 ORDER BY julianday(l.at) DESC, l.id DESC LIMIT 1),
                conversations.created_at
            )
            WHERE last_activity_at IS NULL;
            """);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var connection in connections.Values)
        {
            try { connection.Close(); } catch { }
            connection.Dispose();
        }
        connections.Dispose();
        durableWriteGate.Dispose();
        CryptographicOperations.ZeroMemory(key);
    }
}
