using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace Mesh.Relay.Storage;

/// <summary>
/// Issues short-lived SAS URLs for uploading and downloading end-to-end-encrypted attachment blobs. The
/// relay never reads attachment content: clients upload ciphertext and download it back, and the envelope
/// carries only a pointer. Blobs auto-expire via a storage lifecycle rule (see the deployment guide), so an
/// attachment never outlives the 14-day inbox retention of its message.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>True when blob storage is configured; false leaves the attachment endpoints reporting "not configured".</summary>
    bool Enabled { get; }

    /// <summary>Max attachment size (ciphertext bytes) the relay will issue an upload SAS for.</summary>
    long MaxBytes { get; }

    /// <summary>Mints a new blob id and a write-only SAS URL the client PUTs ciphertext to.</summary>
    (string blobId, string uploadUrl, DateTimeOffset expiresAt) IssueUpload(long size);

    /// <summary>Mints a read-only SAS URL the client GETs ciphertext from.</summary>
    (string downloadUrl, DateTimeOffset expiresAt) IssueDownload(string blobId);
}

/// <summary>No blob storage configured: attachment endpoints report "not configured".</summary>
public sealed class DisabledAttachmentStore : IAttachmentStore
{
    public bool Enabled => false;
    public long MaxBytes => 0;

    public (string blobId, string uploadUrl, DateTimeOffset expiresAt) IssueUpload(long size)
        => throw new InvalidOperationException("attachment storage is not configured");

    public (string downloadUrl, DateTimeOffset expiresAt) IssueDownload(string blobId)
        => throw new InvalidOperationException("attachment storage is not configured");
}

/// <summary>
/// Azure Blob Storage backed attachment store. The container is private (no public access); every read and
/// write is authorized by a per-blob, short-lived, account-key-signed SAS URL the relay mints on demand.
/// </summary>
public sealed class AzureBlobAttachmentStore : IAttachmentStore
{
    private readonly BlobContainerClient container;
    private readonly TimeSpan uploadTtl;
    private readonly TimeSpan downloadTtl;

    public bool Enabled => true;
    public long MaxBytes { get; }

    public AzureBlobAttachmentStore(
        string connectionString, string containerName, long maxBytes, TimeSpan uploadTtl, TimeSpan downloadTtl)
    {
        MaxBytes = maxBytes;
        this.uploadTtl = uploadTtl;
        this.downloadTtl = downloadTtl;

        var service = new BlobServiceClient(connectionString);
        container = service.GetBlobContainerClient(containerName);
        // Private container: only holders of a relay-issued SAS can read or write. Never public.
        container.CreateIfNotExists(PublicAccessType.None);

        // GenerateSasUri needs a shared-key credential, i.e. the connection string must carry an account key.
        if (!container.CanGenerateSasUri)
            throw new InvalidOperationException(
                "BLOB_CONNECTION must include an account key so the relay can issue SAS URLs (a SAS-only or "
                + "token-credential connection string cannot mint SAS URLs).");
    }

    public (string blobId, string uploadUrl, DateTimeOffset expiresAt) IssueUpload(long size)
    {
        var blobId = Guid.NewGuid().ToString("N");
        var blob = container.GetBlobClient(blobId);
        var expiresAt = DateTimeOffset.UtcNow.Add(uploadTtl);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobId,
            Resource = "b",
            ExpiresOn = expiresAt,
        };
        sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);
        return (blobId, blob.GenerateSasUri(sas).ToString(), expiresAt);
    }

    public (string downloadUrl, DateTimeOffset expiresAt) IssueDownload(string blobId)
    {
        var blob = container.GetBlobClient(blobId);
        var expiresAt = DateTimeOffset.UtcNow.Add(downloadTtl);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            BlobName = blobId,
            Resource = "b",
            ExpiresOn = expiresAt,
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return (blob.GenerateSasUri(sas).ToString(), expiresAt);
    }
}
