using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Queues;
using Castmill.Api.Services.Blob;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Media;

/// <summary>Queue message consumed by the Container Apps ffmpeg worker (/infra/clipjob).</summary>
public sealed record ClipJobMessage(
    Guid JobId,
    /// <summary>"clip" cuts a range; "frame" extracts a single still at InSeconds (ADR-014).</summary>
    string Mode,
    string SourceBlobPath,
    string OutputBlobPath,
    double InSeconds,
    double OutSeconds,
    bool CropVertical,
    bool BurnCaptions,
    string? CaptionsSrt,
    /// <summary>Plaintext callback token — exists only here and in the worker's memory.</summary>
    string CallbackToken,
    string CallbackUrl);

public interface IClipJobDispatcher
{
    bool IsConfigured { get; }
    Task EnqueueAsync(ClipJobMessage message, CancellationToken ct);
}

public sealed class ClipJobDispatcher : IClipJobDispatcher
{
    public const string QueueName = "clip-jobs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly QueueClient? _queue;

    public ClipJobDispatcher(IOptions<StorageOptions> options)
    {
        var storage = options.Value;
        if (!string.IsNullOrWhiteSpace(storage.ConnectionString))
        {
            _queue = new QueueClient(storage.ConnectionString, QueueName,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        }
        else if (!string.IsNullOrWhiteSpace(storage.AccountName))
        {
            _queue = new QueueClient(
                new Uri($"https://{storage.AccountName}.queue.core.windows.net/{QueueName}"),
                new DefaultAzureCredential(),
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        }
    }

    public bool IsConfigured => _queue is not null;

    public async Task EnqueueAsync(ClipJobMessage message, CancellationToken ct)
    {
        if (_queue is null)
        {
            throw new InvalidOperationException("Storage is not configured for the clip-job queue.");
        }
        await _queue.CreateIfNotExistsAsync(cancellationToken: ct);
        await _queue.SendMessageAsync(JsonSerializer.Serialize(message, Json), ct);
    }
}
