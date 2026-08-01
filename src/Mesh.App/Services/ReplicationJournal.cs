using Microsoft.Data.Sqlite;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Thrown when a local domain operation is asked to replicate but this device has no usable
/// replication identity / local authority (missing signing keys or an uninitialised custody
/// chain). Greenfield onboarding initialises genesis custody; an account that predates it must
/// be re-onboarded. The local domain operation fails rather than falsely reporting success.
/// </summary>
public sealed class ReplicationIdentityMissingException : Exception
{
    public ReplicationIdentityMissingException(string message) : base(message) { }
}

/// <summary>
/// The offline-capable local replication emitter (spec item 1). It writes a signed
/// <see cref="ReplicationEvent"/>, its target-account outbox references and the domain change
/// into the active account database in a single transaction <b>whenever the local account is
/// open, regardless of any relay connection or engine session</b>. It never silently no-ops:
/// every call either appends a durable event (and returns its id) or throws. The online engine
/// only drains and sessions this journal; it is not required for a local change to be recorded.
///
/// This type has no MAUI / relay dependency, so it is exercised directly in tests and shared
/// by the online engine's local-emit path.
/// </summary>
public sealed class ReplicationJournal
{
    private readonly MeshDb db;
    private readonly ReplicationIdentity identity;
    private readonly IReplicationRoster roster;
    private readonly bool deviceIsDesktop;

    public ReplicationJournal(MeshDb db, ReplicationIdentity identity, IReplicationRoster roster, bool deviceIsDesktop)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.roster = roster ?? throw new ArgumentNullException(nameof(roster));
        this.deviceIsDesktop = deviceIsDesktop;
        ValidateIdentity(identity);
    }

    /// <summary>This device's replication identity.</summary>
    public ReplicationIdentity Identity => identity;

    /// <summary>Registers this device's local origin log if absent (idempotent).</summary>
    public void EnsureLocalOrigin()
        => db.EnsureLocalOrigin(identity.DeviceId, identity.LogEpoch, identity.AuthGeneration);

    /// <summary>
    /// Records a local domain change: encrypts the envelope to every currently-authorised
    /// recipient device, allocates the next sequence, signs the event and commits the event,
    /// its outbox references and the domain projection in one transaction. Returns the new
    /// event id. Never no-ops.
    /// </summary>
    /// <param name="domainWork">
    /// Optional in-transaction domain write. When null, the envelope is projected onto the
    /// local convergence store so local and inbound state converge identically.
    /// </param>
    public string EmitLocal(
        ReplicationPayloadCodec.DomainEnvelope envelope,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, ReplicationEvent>? domainWork = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(targetAccounts);
        ValidateIdentity(identity);

        // Asset upserts and skill-package transfers persist device-local bytes and are
        // desktop-only; a mobile / LocalOnly device never emits them (spec item 3).
        if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
            throw new InvalidOperationException(
                $"Asset/package replication ({envelope.Kind}/{envelope.Action}) is desktop-only and must not be emitted on this device.");

        EnsureLocalOrigin();

        var recipientKeys = BuildRecipientKeys(targetAccounts);
        var plaintext = ReplicationPayloadCodec.EncodeEnvelope(envelope);
        var ciphertext = ReplicationPayloadCodec.Encrypt(plaintext, recipientKeys);

        var normalizedTargets = NormalizeTargets(targetAccounts);
        var evt = db.AllocateAndAppendLocalEvent(
            identity.DeviceId,
            (epoch, seq) => OnlineReplicationProtocol.CreateEvent(
                identity.DeviceId,
                epoch,
                seq,
                identity.Handle,
                identity.AuthGeneration,
                envelope.Kind,
                envelope.EntityId,
                envelope.ConversationId,
                envelope.CausalVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ciphertext,
                identity.PrivateKeyB64),
            normalizedTargets,
            (conn, tx, created) =>
            {
                if (domainWork is not null)
                    domainWork(conn, tx, created);
                else
                    ReplicationPayloadCodec.Project(conn, tx, created, envelope, deviceIsDesktop);
            });
        return evt.EventId;
    }

    /// <summary>
    /// Records several local domain changes that belong to ONE logical operation (for example a
    /// chunked skill-package or attachment transfer plus the asset body it installs). All events,
    /// their outbox references, the sequence allocation and every domain write commit in a single
    /// transaction, so a partially transferred package is never observable. Returns the new event
    /// ids in order.
    /// </summary>
    public IReadOnlyList<string> EmitLocalBatch(
        IReadOnlyList<ReplicationPayloadCodec.DomainEnvelope> envelopes,
        IReadOnlyCollection<string> targetAccounts,
        Action<SqliteConnection, SqliteTransaction, int>? domainWork = null)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(targetAccounts);
        if (envelopes.Count == 0)
            throw new ArgumentException("At least one envelope is required.", nameof(envelopes));
        ValidateIdentity(identity);

        foreach (var envelope in envelopes)
        {
            if (!deviceIsDesktop && ReplicationPayloadCodec.RequiresDesktop(envelope.Kind, envelope.Action))
                throw new InvalidOperationException(
                    $"Asset/package replication ({envelope.Kind}/{envelope.Action}) is desktop-only and must not be emitted on this device.");
        }

        EnsureLocalOrigin();

        var recipientKeys = BuildRecipientKeys(targetAccounts);
        var normalizedTargets = NormalizeTargets(targetAccounts);
        var factories = new List<Func<string, ulong, ReplicationEvent>>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            var ciphertext = ReplicationPayloadCodec.Encrypt(
                ReplicationPayloadCodec.EncodeEnvelope(envelope), recipientKeys);
            var captured = envelope;
            factories.Add((epoch, seq) => OnlineReplicationProtocol.CreateEvent(
                identity.DeviceId, epoch, seq, identity.Handle, identity.AuthGeneration,
                captured.Kind, captured.EntityId, captured.ConversationId, captured.CausalVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ciphertext, identity.PrivateKeyB64));
        }

        var created = db.AllocateAndAppendLocalEvents(
            identity.DeviceId, factories, normalizedTargets,
            (conn, tx, evt, index) =>
            {
                if (domainWork is not null)
                    domainWork(conn, tx, index);
                else
                    ReplicationPayloadCodec.Project(conn, tx, evt, envelopes[index], deviceIsDesktop);
            });
        return created.Select(e => e.EventId).ToList();
    }

    private void ValidateIdentity(ReplicationIdentity id)
    {
        if (string.IsNullOrWhiteSpace(id.Handle)
            || string.IsNullOrWhiteSpace(id.DeviceId)
            || string.IsNullOrWhiteSpace(id.PublicKeyB64)
            || string.IsNullOrWhiteSpace(id.PrivateKeyB64)
            || string.IsNullOrWhiteSpace(id.LogEpoch)
            || string.IsNullOrWhiteSpace(id.CustodyHead))
        {
            throw new ReplicationIdentityMissingException(
                "This device has no usable replication identity / local authority; re-onboard the account.");
        }
    }

    /// <summary>
    /// The set of recipient device public keys for an outbound change: always this device's own
    /// key, plus every non-revoked authorised device of every target account and of the local
    /// account (own devices always receive their account's own changes).
    /// </summary>
    private IReadOnlyCollection<string> BuildRecipientKeys(IReadOnlyCollection<string> targetAccounts)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { identity.PublicKeyB64 };
        var accounts = new HashSet<string>(targetAccounts, StringComparer.Ordinal) { identity.Handle };
        foreach (var account in accounts)
            foreach (var device in roster.AuthorizedDevices(account))
                if (!device.Revoked && !string.IsNullOrWhiteSpace(device.PublicKeyB64))
                    keys.Add(device.PublicKeyB64);
        return keys;
    }

    /// <summary>
    /// Normalises and de-duplicates the outbox target accounts. An own-account target is only
    /// tracked when a sibling authorised device exists to take custody; a sole device has no
    /// one to receipt, so the change is locally complete with no false remote custody
    /// (spec item 5).
    /// </summary>
    private IReadOnlyList<string> NormalizeTargets(IReadOnlyCollection<string> targetAccounts)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in targetAccounts)
        {
            if (string.IsNullOrWhiteSpace(account) || !seen.Add(account)) continue;
            if (string.Equals(account, identity.Handle, StringComparison.Ordinal))
            {
                var siblings = roster.AuthorizedDevices(account)
                    .Count(d => !string.Equals(d.DeviceId, identity.DeviceId, StringComparison.Ordinal));
                if (siblings == 0) continue;
            }
            result.Add(account);
        }
        return result;
    }
}
