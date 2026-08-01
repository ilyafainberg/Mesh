using System.Globalization;
using System.Text;

namespace Mesh.Relay.Hub;

/// <summary>
/// The Protocol 9 relay connect challenge. The relay issues a fresh nonce at connect and the client
/// signs the canonical string below with its device private key. Binding the handle, device id,
/// protocol version, auth generation and custody head into the signed message makes the challenge
/// replay-resistant AND proves the client is operating against current custody: a signature made for
/// a stale auth generation or custody head will not verify against the value the relay reconstructs.
///
/// The canonical form is domain-separated and length-prefixed so no field boundary is ambiguous.
/// It is deterministic and public so both the relay and its clients (and tests) derive the exact
/// same bytes to sign and verify.
/// </summary>
public static class RelayConnectChallenge
{
    /// <summary>Domain tag pinning this canonical to the Protocol 9 relay connect challenge.</summary>
    public const string Domain = "mesh.relay.connect.v9";

    public static string Canonical(
        string nonce,
        string handle,
        string deviceId,
        int protocolVersion,
        long authGeneration,
        string custodyHead)
    {
        var sb = new StringBuilder(Domain);
        Append(sb, nonce);
        Append(sb, handle);
        Append(sb, deviceId);
        Append(sb, protocolVersion.ToString(CultureInfo.InvariantCulture));
        Append(sb, authGeneration.ToString(CultureInfo.InvariantCulture));
        Append(sb, custodyHead ?? "");
        return sb.ToString();

        static void Append(StringBuilder b, string field)
        {
            field ??= "";
            b.Append('|').Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field);
        }
    }
}
