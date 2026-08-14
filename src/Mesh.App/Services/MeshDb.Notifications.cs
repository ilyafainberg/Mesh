namespace Mesh.App.Services;

public sealed partial class MeshDb
{
    internal sealed record PendingNotification(string StableId, string EntityId, string Route);
    private const string NotificationStatePending = "pending";
    private const string NotificationStateScheduled = "scheduled";
    private const string NotificationStateSuppressed = "suppressed";
    private const string NotificationStateRead = "read";

    private void CreateNotificationSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS notification_ledger(
                stable_id TEXT PRIMARY KEY,
                source_event_id TEXT NOT NULL,
                kind INTEGER NOT NULL,
                entity_id TEXT NOT NULL,
                conversation_id TEXT,
                route TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL,
                committed_at TEXT NOT NULL,
                historical INTEGER NOT NULL DEFAULT 0,
                notify_requested INTEGER NOT NULL DEFAULT 1,
                origin_account TEXT,
                suppress_on_origin INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL CHECK(state IN ('pending', 'scheduled', 'suppressed', 'read')),
                requires_attention INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL,
                read_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_notification_ledger_entity
                ON notification_ledger(entity_id, conversation_id, state, committed_at);
            CREATE INDEX IF NOT EXISTS ix_notification_ledger_kind
                ON notification_ledger(kind, state, committed_at);
            CREATE INDEX IF NOT EXISTS ix_notification_ledger_attention
                ON notification_ledger(requires_attention, state, committed_at);
            """);

        if (!TableExists("notification_activity")) return;
        Exec("""
            INSERT OR IGNORE INTO notification_ledger(
                stable_id, source_event_id, kind, entity_id, conversation_id, route, title, body,
                created_at, committed_at, historical, notify_requested, origin_account,
                suppress_on_origin, state, requires_attention, updated_at, read_at)
            SELECT
                stable_id, source_event_id, kind, entity_id, conversation_id, route, title, body,
                created_at, committed_at, historical, notify_requested, origin_account,
                suppress_on_origin,
                CASE
                    WHEN is_read = 1 THEN 'read'
                    WHEN banner_shown = 1 THEN 'scheduled'
                    WHEN historical = 1 OR notify_requested = 0 THEN 'suppressed'
                    ELSE 'pending'
                END,
                CASE WHEN is_read = 0 AND historical = 0 AND notify_requested = 1 THEN 1 ELSE 0 END,
                committed_at,
                CASE WHEN is_read = 1 THEN committed_at ELSE NULL END
            FROM notification_activity;
            """);
    }

    internal bool RecordNotificationActivity(CommittedActivity activity)
    {
        var requiresAttention = !activity.IsHistorical && activity.NotifyRequested;
        var initialState = requiresAttention ? NotificationStatePending : NotificationStateSuppressed;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notification_ledger(
                stable_id, source_event_id, kind, entity_id, conversation_id, route, title, body,
                created_at, committed_at, historical, notify_requested, origin_account,
                suppress_on_origin, state, requires_attention, updated_at, read_at)
            VALUES(
                $stable, $source, $kind, $entity, $conversation, $route, $title, $body,
                $created, $committed, $historical, $notify, $origin,
                $suppress, $state, $attention, $committed, NULL)
            ON CONFLICT(stable_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$stable", activity.StableId);
        cmd.Parameters.AddWithValue("$source", activity.SourceEventId);
        cmd.Parameters.AddWithValue("$kind", (int)activity.Kind);
        cmd.Parameters.AddWithValue("$entity", activity.EntityId);
        cmd.Parameters.AddWithValue("$conversation", (object?)activity.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$route", activity.Route);
        cmd.Parameters.AddWithValue("$title", activity.Title);
        cmd.Parameters.AddWithValue("$body", activity.Body);
        cmd.Parameters.AddWithValue("$created", activity.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$committed", activity.CommittedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$historical", activity.IsHistorical ? 1 : 0);
        cmd.Parameters.AddWithValue("$notify", activity.NotifyRequested ? 1 : 0);
        cmd.Parameters.AddWithValue("$origin", (object?)activity.OriginAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$suppress", activity.SuppressOnOriginAccount ? 1 : 0);
        cmd.Parameters.AddWithValue("$state", initialState);
        cmd.Parameters.AddWithValue("$attention", requiresAttention ? 1 : 0);
        return cmd.ExecuteNonQuery() == 1;
    }

    internal void MarkNotificationBannerShown(string stableId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE notification_ledger
            SET state = 'scheduled', updated_at = $at
            WHERE stable_id = $id AND state = 'pending';
            """;
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", stableId);
        cmd.ExecuteNonQuery();
    }

    internal void MarkNotificationSuppressed(string stableId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE notification_ledger
            SET state = 'suppressed', updated_at = $at
            WHERE stable_id = $id AND state != 'read';
            """;
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", stableId);
        cmd.ExecuteNonQuery();
    }

    internal void MarkNotificationRead(string stableId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE notification_ledger
            SET state = 'read', requires_attention = 0, updated_at = $at, read_at = $at
            WHERE stable_id = $id;
            """;
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", stableId);
        cmd.ExecuteNonQuery();
    }

    internal IReadOnlyList<string> MarkNotificationEntityRead(string entityId)
    {
        const string predicate = "(entity_id = $value OR conversation_id = $value)";
        var ids = NotificationIds(predicate, entityId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE notification_ledger
            SET state = 'read', requires_attention = 0, updated_at = $at, read_at = $at
            WHERE {predicate} AND requires_attention = 1;
            """;
        cmd.Parameters.AddWithValue("$value", entityId);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return ids;
    }

    internal IReadOnlyList<string> MarkNotificationKindRead(NotificationKind kind)
    {
        var ids = NotificationIds("kind = $value", (int)kind);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE notification_ledger
            SET state = 'read', requires_attention = 0, updated_at = $at, read_at = $at
            WHERE kind = $value AND requires_attention = 1;
            """;
        cmd.Parameters.AddWithValue("$value", (int)kind);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return ids;
    }

    internal CommittedActivity? GetPendingNotificationActivity(string stableId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT stable_id, source_event_id, kind, entity_id, conversation_id, route,
                   title, body, created_at, committed_at, historical, notify_requested,
                   origin_account, suppress_on_origin
            FROM notification_ledger
            WHERE stable_id = $id AND state = 'pending'
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", stableId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadCommittedActivity(reader) : null;
    }

    internal IReadOnlyList<CommittedActivity> ListPendingNotificationActivities(int limit)
    {
        if (limit is <= 0 or > 512) throw new ArgumentOutOfRangeException(nameof(limit));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT stable_id, source_event_id, kind, entity_id, conversation_id, route,
                   title, body, created_at, committed_at, historical, notify_requested,
                   origin_account, suppress_on_origin
            FROM notification_ledger
            WHERE state = 'pending'
            ORDER BY committed_at, stable_id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<CommittedActivity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadCommittedActivity(reader));
        return result;
    }

    private static CommittedActivity ReadCommittedActivity(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            (NotificationKind)reader.GetInt32(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(10) != 0,
            reader.GetInt32(11) != 0,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetInt32(13) != 0);

    internal int GetUnreadNotificationCount()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notification_ledger WHERE requires_attention = 1 AND state != 'read';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    internal PendingNotification? GetHighestPriorityPendingNotification()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT stable_id, entity_id, route
            FROM notification_ledger
            WHERE requires_attention = 1 AND state != 'read'
            ORDER BY CASE kind
                WHEN 4 THEN 0
                WHEN 5 THEN 1
                WHEN 2 THEN 2
                WHEN 1 THEN 3
                WHEN 6 THEN 4
                ELSE 5
            END, committed_at DESC
            LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new PendingNotification(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private IReadOnlyList<string> NotificationIds(string predicate, object value)
    {
        var ids = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT stable_id FROM notification_ledger WHERE {predicate} AND requires_attention = 1 AND state != 'read';";
        cmd.Parameters.AddWithValue("$value", value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private bool TableExists(string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }
}
