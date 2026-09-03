using System.Security.Cryptography;
using System.Text;
using Mesh.Relay.Storage;
using Mesh.Shared;

namespace Mesh.Relay.LiveFaults;

public sealed class LiveFaultAuthorityObserver
{
    private readonly object gate = new();
    private readonly List<LiveFaultAuthorityLookup> lookups = [];
    private long sequence;

    public void Record(StoredHandle authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        lock (gate)
        {
            lookups.Add(new(
                ++sequence,
                DateTimeOffset.UtcNow,
                authority.Handle,
                authority.AuthGeneration,
                authority.CustodyHead,
                authority.DevicePublicKeys.Select(DeviceProtocol.DeviceId).ToArray(),
                authority.DevicePublicKeys.Select(Fingerprint).ToArray()));
        }
    }

    public IReadOnlyList<LiveFaultAuthorityLookup> Snapshot()
    {
        lock (gate) return lookups.ToArray();
    }

    public static string Fingerprint(string publicKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey)))
            .ToLowerInvariant();
}
