namespace Mesh.Relay.Storage;

/// <summary>
/// A persisted public service (capability bundle) in the relay's capability directory, plus its
/// reputation state. Only public metadata is stored here: the actual capabilities run on the
/// provider's client on invocation, and are never uploaded to the relay.
///
/// Reputation has two parts:
/// <list type="bullet">
///   <item><see cref="Votes"/>: one updatable up/down vote per voter handle (voterHandle -> +1/-1).
///   A voter is only allowed to vote after an attested usage event (see <see cref="Users"/>).</item>
///   <item><see cref="Users"/>: the set of handles that have invoked the service (attested usage),
///   used both for the "unique users" reputation signal and to gate voting.</item>
/// </list>
/// Serializable so it can live in a durable store (Cosmos) or in memory.
/// </summary>
public sealed class StoredService
{
    public string ServiceId { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Voter handle -> vote in {-1, +1}. A cleared vote (0) is removed from the map.</summary>
    public Dictionary<string, int> Votes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Handles that have invoked the service (attested, signed usage). Drives UniqueUsers and vote-gating.</summary>
    public HashSet<string> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
