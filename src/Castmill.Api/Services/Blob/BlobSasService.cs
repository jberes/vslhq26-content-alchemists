using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
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
    public int DefaultSasMinutes { get; set; } = 10;
    public int MaxSasMinutes { get; set; } = 60;

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
