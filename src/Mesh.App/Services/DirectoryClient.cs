using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Mesh.App.Domain;
using Mesh.Shared;

namespace Mesh.App.Services;

/// <summary>
/// Talks to the relay's public capability directory (the Community services list).
/// Browsing is anonymous (public GET endpoints); publishing, unpublishing, voting and usage
/// attestation are authenticated with the owner's device key exactly like the connector broker
/// and hosted-model proxy: the request is signed with the device PRIVATE key over the matching
/// <see cref="ServiceDirectoryProtocol"/> canonical string, and the relay verifies it against the
/// device public keys registered under the handle.
/// <para>
/// All methods are network-best-effort: transport/HTTP failures are swallowed (and logged) and
/// surfaced as an empty result / false, never as a thrown exception, so the UI can call them freely.
/// </para>
/// </summary>
public sealed class DirectoryClient(AppState state, IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public event Action<string>? Log;

    /// <summary>The relay base, resolved from the active profile like the hub/connector code.</summary>
    private string RelayBase => state.Profile.RelayUrl.TrimEnd('/');

    /// <summary>Browse the directory, optionally filtered by a free-text query. Empty on failure.</summary>
    public async Task<IReadOnlyList<ServiceListing>> BrowseAsync(string? query = null, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient("relay");
        var url = $"{RelayBase}/capabilities";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"?q={Uri.EscapeDataString(query)}";
        try
        {
            var list = await http.GetFromJsonAsync<List<ServiceListing>>(url, Json, ct);
            return list ?? new List<ServiceListing>();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"directory browse failed: {ex.Message}");
            return Array.Empty<ServiceListing>();
        }
    }

    /// <summary>Fetch a single listing by id. Null on not-found or failure.</summary>
    public async Task<ServiceListing?> GetAsync(string serviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return null;
        var http = httpFactory.CreateClient("relay");
        try
        {
            var resp = await http.GetAsync($"{RelayBase}/capabilities/{Uri.EscapeDataString(serviceId)}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<ServiceListing>(Json, ct);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"directory get failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Publish (or update) a service in the directory. Signs the publish message with the owner's
    /// device key. Returns false when unauthenticated (no keys) or the relay rejects/is unreachable.
    /// </summary>
    public async Task<bool> PublishAsync(PublishedService svc, CancellationToken ct = default)
    {
        if (svc is null || string.IsNullOrWhiteSpace(svc.Id)) return false;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey)) return false;

        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(p.PrivateKey,
                ServiceDirectoryProtocol.PublishMessage(p.Handle, svc.Id, svc.Name));
            var req = new PublishServiceRequest(p.Handle, p.PublicKey, svc.Id, svc.Name, svc.Description, svc.Category, sig);
            var resp = await http.PostAsJsonAsync($"{RelayBase}/capabilities", req, Json, ct);
            Log?.Invoke($"publish {svc.Id}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"publish failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Unpublish a service. Signs the unpublish message; DELETE carries the signed request body.</summary>
    public async Task<bool> UnpublishAsync(string serviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return false;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey)) return false;

        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(p.PrivateKey,
                ServiceDirectoryProtocol.UnpublishMessage(p.Handle, serviceId));
            var req = new UnpublishServiceRequest(p.Handle, p.PublicKey, serviceId, sig);
            using var msg = new HttpRequestMessage(HttpMethod.Delete,
                $"{RelayBase}/capabilities/{Uri.EscapeDataString(serviceId)}")
            {
                Content = JsonContent.Create(req, options: Json)
            };
            var resp = await http.SendAsync(msg, ct);
            Log?.Invoke($"unpublish {serviceId}: {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"unpublish failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Cast (or change/clear) a usage-gated up/down vote. Signs the vote with the voter's device key.
    /// The relay only accepts it when it has observed this handle invoke the service.
    /// </summary>
    public async Task<bool> VoteAsync(string serviceId, int vote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return false;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey)) return false;

        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(p.PrivateKey,
                ServiceDirectoryProtocol.VoteMessage(p.Handle, serviceId, vote));
            var req = new ServiceVoteRequest(p.Handle, p.PublicKey, serviceId, vote, sig);
            var resp = await http.PostAsJsonAsync(
                $"{RelayBase}/capabilities/{Uri.EscapeDataString(serviceId)}/vote", req, Json, ct);
            Log?.Invoke($"vote {serviceId} ({vote}): {(int)resp.StatusCode}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"vote failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Report that this handle has used a service (attested usage for reputation gating). Best-effort:
    /// signed like a cleared vote (vote == 0) and posted to the usage endpoint; failures are ignored.
    /// </summary>
    public async Task ReportUsedAsync(string serviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) return;
        var p = state.Profile;
        if (string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PublicKey)) return;

        var http = httpFactory.CreateClient("relay");
        try
        {
            var sig = IdentityService.Sign(p.PrivateKey,
                ServiceDirectoryProtocol.VoteMessage(p.Handle, serviceId, 0));
            var req = new ServiceVoteRequest(p.Handle, p.PublicKey, serviceId, 0, sig);
            var resp = await http.PostAsJsonAsync(
                $"{RelayBase}/capabilities/{Uri.EscapeDataString(serviceId)}/used", req, Json, ct);
            Log?.Invoke($"used {serviceId}: {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"report-used failed: {ex.Message}");
        }
    }
}
