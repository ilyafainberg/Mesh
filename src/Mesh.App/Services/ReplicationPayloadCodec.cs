using System.Text.Json;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Protocol-9 replication payload codec. Owns the two concerns that sit between the
/// replication engine and the local domain:
///
///  1. End-to-end confidentiality. Every durable event body and every control-frame body
///     is opaque ciphertext produced by <see cref="MessageCrypto"/> (ECIES over the same
///     P-256 device keys already published to the relay directory). The relay only ever
///     sees ciphertext.
///  2. A single, unified mapping from the ten replication op kinds onto neutral domain
///     envelopes. The engine never interprets a domain body; it hands the decrypted
///     envelope to an <c>IReplicationDomainApplier</c> seam inside the same database
///     transaction that appends the inbound event. Asset and skill-package operations are
///     desktop-only and are refused on mobile before any mutation runs.
///
/// This type is deterministic and free of storage / MAUI / relay dependencies so it can be
/// exercised in isolation.
/// </summary>
public static class ReplicationPayloadCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // -----------------------------------------------------------------------
    // Unified op-kind -> domain-action mapping (spec item 7).
    // -----------------------------------------------------------------------

    /// <summary>The concrete domain action a replicated envelope requests.</summary>
    public enum DomainAction
    {
        Upsert = 0,
        Delete = 1,
        AppendLine = 2,
        AskUserPrompt = 3,
        AskUserResolve = 4,
        ReadWatermark = 5,
        AssetUpsert = 6,
        AssetDelete = 7,
        PackageTransfer = 8,
    }

    /// <summary>
    /// A neutral, self-describing domain envelope carried (encrypted) as an event's
    /// ciphertext. <see cref="BodyJson"/> is the domain-specific payload the applier
    /// projects; the codec never inspects it.
    /// </summary>
    public sealed record DomainEnvelope(
        string Kind,
        DomainAction Action,
        string EntityId,
        string? ConversationId,
        string CausalVersion,
        string BodyJson,
        NotificationIntent? NotificationIntent = null);

    /// <summary>
    /// The canonical operation-mapping table. Each unified op kind is projected onto the
    /// domain actions it may carry. Exposed so tests and callers can assert the mapping is
    /// exhaustive and stable.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<DomainAction>> OperationMap =
        new Dictionary<string, IReadOnlyList<DomainAction>>(StringComparer.Ordinal)
        {
            [ReplicationOpKinds.Message] = new[] { DomainAction.Upsert, DomainAction.Delete, DomainAction.AppendLine },
            [ReplicationOpKinds.Conversation] = new[] { DomainAction.Upsert, DomainAction.Delete },
            [ReplicationOpKinds.Topic] = new[] { DomainAction.Upsert, DomainAction.Delete, DomainAction.AppendLine },
            [ReplicationOpKinds.Contact] = new[] { DomainAction.Upsert, DomainAction.Delete },
            [ReplicationOpKinds.Circle] = new[] { DomainAction.Upsert, DomainAction.Delete },
            [ReplicationOpKinds.Memory] = new[] { DomainAction.Upsert, DomainAction.Delete },
            [ReplicationOpKinds.Asset] = new[] { DomainAction.AssetUpsert, DomainAction.AssetDelete, DomainAction.PackageTransfer },
            [ReplicationOpKinds.AskUser] = new[] { DomainAction.AskUserPrompt, DomainAction.AskUserResolve },
            [ReplicationOpKinds.ReadWatermark] = new[] { DomainAction.ReadWatermark },
        };

    /// <summary>True when <paramref name="action"/> is a legal action for <paramref name="kind"/>.</summary>
    public static bool IsMappedAction(string kind, DomainAction action)
        => OperationMap.TryGetValue(kind, out var actions) && actions.Contains(action);

    /// <summary>
    /// Asset upserts and skill-package transfers persist device-local bytes; they run on
    /// desktop only and must never be applied on a mobile (or LocalOnly) device.
    /// </summary>
    public static bool RequiresDesktop(string kind, DomainAction action)
        => string.Equals(kind, ReplicationOpKinds.Asset, StringComparison.Ordinal)
            && action is DomainAction.AssetUpsert or DomainAction.PackageTransfer;

    // -----------------------------------------------------------------------
    // Domain envelope encode / decode (the plaintext inside an event ciphertext).
    // -----------------------------------------------------------------------

    /// <summary>Serialises a domain envelope to its canonical plaintext JSON.</summary>
    public static string EncodeEnvelope(DomainEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsMappedAction(envelope.Kind, envelope.Action))
            throw new ArgumentException($"Action {envelope.Action} is not valid for kind '{envelope.Kind}'.", nameof(envelope));
        return JsonSerializer.Serialize(envelope, Json);
    }

    /// <summary>Parses a domain envelope from plaintext JSON. Returns null on malformed input.</summary>
    public static DomainEnvelope? DecodeEnvelope(string plaintextJson)
    {
        if (string.IsNullOrWhiteSpace(plaintextJson)) return null;
        try
        {
            var env = JsonSerializer.Deserialize<DomainEnvelope>(plaintextJson, Json);
            if (env is null || string.IsNullOrWhiteSpace(env.Kind) || string.IsNullOrWhiteSpace(env.EntityId))
                return null;
            return IsMappedAction(env.Kind, env.Action) ? env : null;
        }
        catch (JsonException) { return null; }
    }

    // -----------------------------------------------------------------------
    // End-to-end encryption of a payload string (event body or control frame body).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> to every supplied recipient device public key so
    /// any of those devices can unwrap it. Throws when no usable recipient key was supplied
    /// (fail closed - the online-only protocol never sends payloads in the clear).
    /// </summary>
    public static string Encrypt(string plaintext, IReadOnlyCollection<string> recipientDeviceKeysB64)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(recipientDeviceKeysB64);
        var cipher = MessageCrypto.Encrypt(plaintext, recipientDeviceKeysB64);
        if (cipher is null)
            throw new InvalidOperationException("No authorised recipient device key was available to encrypt to.");
        return cipher;
    }

    /// <summary>
    /// Attempts to decrypt an end-to-end payload with this device's key pair. Returns
    /// (true, plaintext) when the payload is addressed to this device and authenticates.
    /// </summary>
    public static (bool ok, string? plaintext) TryDecrypt(string ciphertext, string myPrivateKeyB64, string myPublicKeyB64)
        => MessageCrypto.TryDecrypt(ciphertext, myPrivateKeyB64, myPublicKeyB64);

    /// <summary>The device ids an encrypted body was wrapped for (key-slot metadata only).</summary>
    public static IReadOnlyList<string> RecipientDeviceIds(string ciphertext)
        => MessageCrypto.EncryptedDeviceIds(ciphertext);

    // -----------------------------------------------------------------------
    // Control-frame body serialisation (session / offer / request / batch / ...).
    // -----------------------------------------------------------------------

    /// <summary>Serialises a control payload record to compact JSON.</summary>
    public static string SerializeControl<T>(T value)
        => JsonSerializer.Serialize(value, Json);

    /// <summary>Deserialises a control payload record from JSON. Returns null on malformed input.</summary>
    public static T? DeserializeControl<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    // -----------------------------------------------------------------------
    // Concrete projection (spec items 3, 4, 6).
    //
    // The engine hands every decrypted inbound envelope to this seam inside the same transaction
    // that appends the event and advances the cursor, and the local emit path hands its own
    // envelope to the same seam inside the transaction that allocates the local sequence and
    // writes the outbox references. Both therefore converge through identical code.
    //
    // The actual materialisation lives in ReplicationDomainMaterializer, which writes the real
    // Mesh domain tables (conversations / chat_lines, own_threads / own_chat, memories, the
    // profile blob carrying contacts and circles, assets / asset_content, ask_user_prompts, the
    // read watermark table and skill-package blob staging) gated on the generic convergence
    // index's causal decision. An unmodelled kind/action or an invalid payload throws
    // ReplicationProjectionException, which rolls the transaction back so the cursor never
    // advances past an event the device could not project.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The concrete projection registered with the engine and used by the local emit path.
    /// Delegates to <see cref="ReplicationDomainMaterializer.Apply"/>.
    /// </summary>
    public static void Project(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        ReplicationEvent evt,
        DomainEnvelope envelope,
        bool deviceIsDesktop)
        => ReplicationDomainMaterializer.Apply(conn, tx, evt, envelope, deviceIsDesktop);

    /// <summary>Serialises an outer <see cref="E2EFrame"/> for opaque relay carriage.</summary>
    public static string EncodeFrame(E2EFrame frame)
        => JsonSerializer.Serialize(frame, Json);

    /// <summary>Parses an outer <see cref="E2EFrame"/> from a relay delivery. Returns null on malformed input.</summary>
    public static E2EFrame? DecodeFrame(string frameJson)
    {
        if (string.IsNullOrWhiteSpace(frameJson)) return null;
        try
        {
            var frame = JsonSerializer.Deserialize<E2EFrame>(frameJson, Json);
            return frame is null || string.IsNullOrWhiteSpace(frame.SessionId) ? null : frame;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Thrown by <see cref="ReplicationPayloadCodec.Project"/> when an inbound envelope cannot be
/// projected (an unmodelled kind/action or a malformed envelope). The engine runs the projection
/// inside the inbound apply transaction, so throwing rolls that transaction back and the cursor
/// does not advance: the failure is permanent for that event rather than a silent skip.
/// </summary>
public sealed class ReplicationProjectionException : Exception
{
    public ReplicationProjectionException(string message) : base(message) { }
}
