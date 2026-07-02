using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Lists the mail folders (Outlook) or labels (Gmail) of a connected source, so the
/// user can grant specific folders to specific circles. Read-only, live, on demand.
/// </summary>
public sealed class SourceBrowser(MsalAuthService msal, GoogleAuthService google, IHttpClientFactory httpFactory)
{
    public async Task<(IReadOnlyList<FolderRef> folders, string? error)> ListFoldersAsync(ConnectedSource src, CancellationToken ct = default)
    {
        try
        {
            return src.Provider switch
            {
                SourceProvider.MicrosoftGraph => await GraphFoldersAsync(src, MsalAuthService.WorkScopes, ct),
                SourceProvider.MicrosoftPersonal => await GraphFoldersAsync(src, MsalAuthService.PersonalScopes, ct),
                SourceProvider.Google => await GmailLabelsAsync(src, ct),
                _ => (Array.Empty<FolderRef>(), "This source type has no folders.")
            };
        }
        catch (Exception ex) { return (Array.Empty<FolderRef>(), ex.Message); }
    }

    private async Task<(IReadOnlyList<FolderRef>, string?)> GraphFoldersAsync(ConnectedSource src, string[] scopes, CancellationToken ct)
    {
        var (ok, token, error) = await msal.GetTokenAsync(src.AccountId, scopes, ct);
        if (!ok || token is null) return (Array.Empty<FolderRef>(), error ?? "Not signed in.");

        var http = httpFactory.CreateClient("graph");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var result = new List<FolderRef>();
        // Top-level folders plus one level of children (covers most real mailboxes).
        var url = "https://graph.microsoft.com/v1.0/me/mailFolders?$top=100&$select=id,displayName&$expand=childFolders($select=id,displayName;$top=50)";
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return (Array.Empty<FolderRef>(), $"Graph {(int)resp.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        foreach (var f in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = f.GetProperty("id").GetString();
            var name = f.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            if (id is null || name is null) continue;
            result.Add(new FolderRef(id, name));
            if (f.TryGetProperty("childFolders", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var c in children.EnumerateArray())
                {
                    var cid = c.GetProperty("id").GetString();
                    var cname = c.TryGetProperty("displayName", out var cdn) ? cdn.GetString() : null;
                    if (cid is not null && cname is not null) result.Add(new FolderRef(cid, $"{name} / {cname}"));
                }
        }
        return (result, null);
    }

    private async Task<(IReadOnlyList<FolderRef>, string?)> GmailLabelsAsync(ConnectedSource src, CancellationToken ct)
    {
        var (ok, token, error) = await google.GetTokenAsync(src.AccountId ?? src.ConnectedAs, ct);
        if (!ok || token is null) return (Array.Empty<FolderRef>(), error ?? "Not signed in.");

        var http = httpFactory.CreateClient("google");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.GetAsync("https://gmail.googleapis.com/gmail/v1/users/me/labels", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            return (Array.Empty<FolderRef>(),
                $"Gmail access failed: {GoogleErrorReason(body)}. If you didn't approve the email permission when connecting, disconnect Google and reconnect, ticking the Gmail permission.");
        if (!resp.IsSuccessStatusCode) return (Array.Empty<FolderRef>(), $"Gmail {(int)resp.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var result = new List<FolderRef>();
        if (!doc.RootElement.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return (result, null);
        foreach (var l in labels.EnumerateArray())
        {
            var id = l.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = l.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (id is null || name is null) continue;
            result.Add(new FolderRef(id, name));
        }
        return (result, null);
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;

    /// <summary>
    /// Pulls a human-readable reason out of a Google API error body. Google returns
    /// <c>{ "error": { "message": "...", "status": "PERMISSION_DENIED",
    /// "errors": [ { "reason": "insufficientPermissions" | "accessNotConfigured" } ] } }</c>.
    /// This distinguishes "you didn't grant the scope" from "the API is disabled in the
    /// Google Cloud project", which the old generic message hid.
    /// </summary>
    private static string GoogleErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var err)) return "permission denied";
            string? reason = null, message = null;
            if (err.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0 && errs[0].TryGetProperty("reason", out var r))
                reason = r.GetString();
            if (err.TryGetProperty("message", out var m)) message = m.GetString();

            var hint = reason switch
            {
                "accessNotConfigured" => "the Google Drive/Gmail API is not enabled for this app's Google Cloud project",
                "insufficientPermissions" or "insufficientScopes" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT"
                    => "the required permission wasn't granted",
                _ => null
            };
            if (hint is not null) return message is null ? hint : $"{hint} ({message})";
            return message ?? reason ?? "permission denied";
        }
        catch { return "permission denied"; }
    }

    // ---- drive folders (OneDrive / Google Drive), navigable ----

    /// <summary>
    /// Lists the sub-folders of a drive folder for the folder picker. When
    /// <paramref name="parentRef"/> is null, lists the drive root. Refs are opaque
    /// tokens the caller passes back to navigate deeper, and stores as grants.
    /// </summary>
    public async Task<(IReadOnlyList<FolderRef> folders, string? error)> ListDriveFoldersAsync(
        ConnectedSource src, string? parentRef, CancellationToken ct = default)
    {
        try
        {
            return src.Provider switch
            {
                SourceProvider.MicrosoftGraph => await OneDriveFoldersAsync(src, MsalAuthService.WorkScopes, parentRef, ct),
                SourceProvider.MicrosoftPersonal => await OneDriveFoldersAsync(src, MsalAuthService.PersonalScopes, parentRef, ct),
                SourceProvider.Google => await DriveFoldersAsync(src, parentRef, ct),
                _ => (Array.Empty<FolderRef>(), "This source type has no drive folders.")
            };
        }
        catch (Exception ex) { return (Array.Empty<FolderRef>(), ex.Message); }
    }

    private async Task<(IReadOnlyList<FolderRef>, string?)> OneDriveFoldersAsync(ConnectedSource src, string[] scopes, string? parentRef, CancellationToken ct)
    {
        var (ok, token, error) = await msal.GetTokenAsync(src.AccountId, scopes, ct);
        if (!ok || token is null) return (Array.Empty<FolderRef>(), error ?? "Not signed in.");

        var http = httpFactory.CreateClient("graph");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // parentRef is "driveId:itemId"; null means the user's drive root.
        string url;
        if (string.IsNullOrEmpty(parentRef))
            url = "https://graph.microsoft.com/v1.0/me/drive/root/children?$top=200&$select=id,name,folder,parentReference";
        else
        {
            var (driveId, itemId) = SplitRef(parentRef);
            url = $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/children?$top=200&$select=id,name,folder,parentReference";
        }

        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            return (Array.Empty<FolderRef>(), "OneDrive access wasn't granted. Disconnect and reconnect, approving file access.");
        if (!resp.IsSuccessStatusCode) return (Array.Empty<FolderRef>(), $"Graph {(int)resp.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var result = new List<FolderRef>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!item.TryGetProperty("folder", out _)) continue; // folders only
            var id = item.TryGetProperty("id", out var idv) ? idv.GetString() : null;
            var name = item.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            var driveId = item.TryGetProperty("parentReference", out var pr) && pr.TryGetProperty("driveId", out var dv) ? dv.GetString() : null;
            if (id is null || name is null || driveId is null) continue;
            result.Add(new FolderRef($"{driveId}:{id}", name));
        }
        return (result.OrderBy(f => f.Name).ToList(), null);
    }

    private async Task<(IReadOnlyList<FolderRef>, string?)> DriveFoldersAsync(ConnectedSource src, string? parentRef, CancellationToken ct)
    {
        var (ok, token, error) = await google.GetTokenAsync(src.AccountId ?? src.ConnectedAs, ct);
        if (!ok || token is null) return (Array.Empty<FolderRef>(), error ?? "Not signed in.");

        var http = httpFactory.CreateClient("google");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var parent = string.IsNullOrEmpty(parentRef) ? "root" : parentRef;
        var q = $"'{parent}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&pageSize=200&fields=files(id,name)&orderBy=name&supportsAllDrives=true&includeItemsFromAllDrives=true";

        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            return (Array.Empty<FolderRef>(),
                $"Drive access failed: {GoogleErrorReason(body)}. If you didn't approve Drive when connecting, disconnect Google and reconnect, ticking the Drive permission.");
        if (!resp.IsSuccessStatusCode) return (Array.Empty<FolderRef>(), $"Drive {(int)resp.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        var result = new List<FolderRef>();
        if (doc.RootElement.TryGetProperty("files", out var files))
            foreach (var f in files.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                var name = f.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (id is not null && name is not null) result.Add(new FolderRef(id, name));
            }
        return (result, null);
    }

    /// <summary>Splits a "driveId:itemId" ref (driveId may contain no colon; itemId is after the first).</summary>
    internal static (string driveId, string itemId) SplitRef(string r)
    {
        var i = r.IndexOf(':');
        return i < 0 ? ("", r) : (r[..i], r[(i + 1)..]);
    }
}
