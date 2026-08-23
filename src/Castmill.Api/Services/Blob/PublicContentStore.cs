using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Blob;

public sealed record BoundedContentRead(byte[]? Bytes, bool ExceedsLimit);

public interface IPublicContentStore
{
    bool IsConfigured { get; }
    /// <summary>
    /// Publishes bytes to the public container with immutable cache headers and
    /// returns a stable public URL. Used for blog images (WebP) and SEO share
    /// snapshots — content that is public by design once the user publishes.
    /// </summary>
    Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct);

    /// <summary>
    /// Reads published bytes back. Used by the headline compositor, which must
    /// re-composite from the stored base image — going out over the public URL
    /// would make an internal operation depend on internet egress.
    /// </summary>
    Task<byte[]?> ReadAsync(string path, CancellationToken ct);

    /// <summary>Reads at most <paramref name="maxBytes"/> plus one detection byte.</summary>
    async Task<BoundedContentRead> ReadUpToAsync(string path, int maxBytes, CancellationToken ct)
    {
        var bytes = await ReadAsync(path, ct);
        return bytes is { Length: > 0 } && bytes.Length > maxBytes
            ? new BoundedContentRead(null, true)
            : new BoundedContentRead(bytes, false);
    }

    /// <summary>
    /// Removes a published blob. Deleting a blob that is already gone succeeds silently —
    /// the caller's row is the source of truth and blob cleanup must be repeatable.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken ct);
}

public sealed class PublicContentStore : IPublicContentStore
{
    private readonly StorageOptions _options;
    private readonly BlobServiceClient? _client;

    public PublicContentStore(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _client = new BlobServiceClient(_options.ConnectionString);
        }
        else if (!string.IsNullOrWhiteSpace(_options.AccountName))
        {
            _client = new BlobServiceClient(
                new Uri($"https://{_options.AccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<Uri> PublishAsync(string path, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }

        var container = _client.GetBlobContainerClient(_options.PublicContainer);
        // Blob-level public read: URLs work in published content with no SAS.
        // Requires "allow blob public access" on the account — the /blob/test
        // probe surfaces an actionable error when it's disabled.
        await container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

        var blob = container.GetBlobClient(path);
        await blob.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType,
                // Published derivatives are content-addressed by campaign/slot and
                // never mutated — immutable caching is safe and cheap.
                CacheControl = "public, max-age=31536000, immutable",
            },
        }, ct);
        return blob.Uri;
    }

    public async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        var blob = _client.GetBlobContainerClient(_options.PublicContainer).GetBlobClient(path);
        if (!await blob.ExistsAsync(ct))
        {
            return null;
        }
        var download = await blob.DownloadContentAsync(ct);
        return download.Value.Content.ToArray();
    }

    public async Task<BoundedContentRead> ReadUpToAsync(
        string path, int maxBytes, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var blob = _client.GetBlobContainerClient(_options.PublicContainer).GetBlobClient(path);
        if (!await blob.ExistsAsync(ct))
        {
            return new BoundedContentRead(null, false);
        }

        await using var input = await blob.OpenReadAsync(cancellationToken: ct);
        using var output = new MemoryStream(Math.Min(maxBytes + 1, 1024 * 1024));
        var buffer = new byte[81920];
        while (output.Length <= maxBytes)
        {
            var remaining = maxBytes + 1 - (int)output.Length;
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
            {
                return new BoundedContentRead(output.ToArray(), false);
            }
            output.Write(buffer, 0, read);
        }
        return new BoundedContentRead(null, true);
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Storage is not configured.");
        }
        await _client.GetBlobContainerClient(_options.PublicContainer)
            .GetBlobClient(path)
            .DeleteIfExistsAsync(cancellationToken: ct);
    }
}
