using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;

namespace Castmill.Api.Services.Blob;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Storage account name (e.g. "castmill") — used with Entra auth (user-delegation SAS).</summary>
    public string? AccountName { get; set; }
    /// <summary>Optional fallback: full connection string (shared-key SAS). Lives only in gitignored/dev config.</summary>
    public string? ConnectionString { get; set; }
    public string PrivateContainer { get; set; } = "private";
    public string PublicContainer { get; set; } = "public";
    public int DefaultSasMinutes { get; set; } = 10;
    public int MaxSasMinutes { get; set; } = 60;
    public long MaxMediaBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountName) || !string.IsNullOrWhiteSpace(ConnectionString);
}

public interface IBlobSasService
{
    bool IsConfigured { get; }
    /// <summary>Mints a single-blob, single-operation SAS URL (G2: least privilege, minutes-scale expiry).</summary>
    Task<Uri> MintAsync(string blobPath, BlobSasPermissions permission, int? minutes, CancellationToken ct);
    Task<bool> ProbeAsync(CancellationToken ct);
    /// <summary>Server-side read for processing (e.g. transcription). Returns null if the blob doesn't exist.</summary>
    Task<(Stream Stream, long Length)?> OpenReadAsync(string blobPath, CancellationToken ct);

    /// <summary>
    /// Writes bytes to a private blob from the SERVER. The SAS path puts the browser in direct
    /// contact with storage, which drags in cross-origin rules that differ per shell; this does
    /// not, so it works identically from the web client, the desktop shell and a test.
    /// </summary>
    Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct);

    /// <summary>
    /// Cheap existence check — a HEAD, not a download. Used by the derived-thumbnail path,
    /// which must not pull a multi-megabyte original just to discover it already has a thumb.
    /// </summary>
    Task<bool> ExistsAsync(string blobPath, CancellationToken ct);
    Task StageBlockAsync(
        string blobPath, string blockId, Stream content, CancellationToken ct);
    Task CommitBlocksAsync(
        string blobPath, IReadOnlyList<string> blockIds, string contentType, CancellationToken ct);
    Task DeleteAsync(string blobPath, CancellationToken ct);
}

public sealed class BlobSasService : IBlobSasService
{
    private readonly StorageOptions _options;
    private readonly BlobServiceClient? _client;
    private readonly StorageSharedKeyCredential? _sharedKey;

    public BlobSasService(Microsoft.Extensions.Options.IOptions<StorageOptions> options)
    {
        _options = options.Value;
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _client = new BlobServiceClient(_options.ConnectionString);
            _sharedKey = ParseSharedKey(_options.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccountName))
        {
            // Passwordless: az login locally, managed identity in Azure.
            // SAS tokens are user-delegation SAS — no account key exists anywhere.
            _client = new BlobServiceClient(
                new Uri($"https://{_options.AccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<Uri> MintAsync(string blobPath, BlobSasPermissions permission, int? minutes, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }

        var effectiveMinutes = Math.Clamp(minutes ?? _options.DefaultSasMinutes, 1, _options.MaxSasMinutes);
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(effectiveMinutes);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _options.PrivateContainer,
            BlobName = blobPath,
            Resource = "b", // single blob — never a container-wide grant
            ExpiresOn = expiresOn,
        };
        builder.SetPermissions(permission);

        var blobUri = _client.GetBlobContainerClient(_options.PrivateContainer).GetBlobClient(blobPath).Uri;

        if (_sharedKey is not null)
        {
            var query = builder.ToSasQueryParameters(_sharedKey);
            return new UriBuilder(blobUri) { Query = query.ToString() }.Uri;
        }

        var delegationKey = await _client.GetUserDelegationKeyAsync(
            startsOn: DateTimeOffset.UtcNow.AddMinutes(-1), expiresOn, ct);
        var delegatedQuery = builder.ToSasQueryParameters(delegationKey.Value, _client.AccountName);
        return new UriBuilder(blobUri) { Query = delegatedQuery.ToString() }.Uri;
    }

    public async Task<(Stream Stream, long Length)?> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        var blob = _client.GetBlobContainerClient(_options.PrivateContainer).GetBlobClient(blobPath);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }
        var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
        var stream = await blob.OpenReadAsync(cancellationToken: ct);
        return (stream, properties.Value.ContentLength);
    }

    public async Task WriteAsync(string blobPath, Stream content, string contentType, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        var blob = _client.GetBlobContainerClient(_options.PrivateContainer).GetBlobClient(blobPath);
        await blob.UploadAsync(
            content,
            new Azure.Storage.Blobs.Models.BlobUploadOptions
            {
                HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType },
            },
            ct);
    }

    public async Task<bool> ExistsAsync(string blobPath, CancellationToken ct)
    {
        if (_client is null)
        {
            return false;
        }
        return await _client.GetBlobContainerClient(_options.PrivateContainer)
            .GetBlobClient(blobPath).ExistsAsync(ct);
    }

    public async Task StageBlockAsync(
        string blobPath, string blockId, Stream content, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        var blockBlob = _client.GetBlobContainerClient(_options.PrivateContainer)
            .GetBlockBlobClient(blobPath);
        await blockBlob.StageBlockAsync(blockId, content, cancellationToken: ct);
    }

    public async Task CommitBlocksAsync(
        string blobPath, IReadOnlyList<string> blockIds, string contentType, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        var blockBlob = _client.GetBlobContainerClient(_options.PrivateContainer)
            .GetBlockBlobClient(blobPath);
        await blockBlob.CommitBlockListAsync(
            blockIds,
            new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            },
            ct);
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct)
    {
        if (_client is null)
        {
            return;
        }
        await _client.GetBlobContainerClient(_options.PrivateContainer)
            .GetBlobClient(blobPath)
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        if (_client is null)
        {
            return false;
        }
        var container = _client.GetBlobContainerClient(_options.PrivateContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        return await container.ExistsAsync(ct);
    }

    private static StorageSharedKeyCredential? ParseSharedKey(string connectionString)
    {
        string? name = null, key = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }
            var k = part[..idx];
            if (k.Equals("AccountName", StringComparison.OrdinalIgnoreCase))
            {
                name = part[(idx + 1)..];
            }
            else if (k.Equals("AccountKey", StringComparison.OrdinalIgnoreCase))
            {
                key = part[(idx + 1)..];
            }
        }
        return name is not null && key is not null ? new StorageSharedKeyCredential(name, key) : null;
    }
}
