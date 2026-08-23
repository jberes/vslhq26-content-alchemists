using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Castmill.Api.Services.Ai;
using Castmill.Api.Services.Blob;
using Castmill.Core;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class MediaUploadTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Interrupted_upload_resumes_commits_and_short_transcription_creates_timed_evidence()
    {
        var blobs = new BlockBlobStore();
        var transcription = new FakeTranscriptionService();
        await using var app = WithServices(blobs, transcription);
        var (client, campaignId) = await SignedInCampaignAsync(app, "media-resume");
        var bytes = new byte[(4 * 1024 * 1024) + 3];
        RandomNumberGenerator.Fill(bytes);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest("launch.wav", "audio/wav", bytes.Length));
        created.EnsureSuccessStatusCode();
        var upload = (await created.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        Assert.Equal(0, upload.UploadedBytes);

        var first = bytes.AsMemory(0, upload.BlockSize).ToArray();
        var firstResponse = await PutBlockAsync(client, campaignId, upload.Id, 0, first);
        firstResponse.EnsureSuccessStatusCode();
        var afterFirst = (await firstResponse.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        Assert.Equal(first.Length, afterFirst.UploadedBytes);
        Assert.Equal(1, afterFirst.NextBlockIndex);

        var resumed = await client.GetFromJsonAsync<MediaUploadResponse>(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}");
        Assert.Equal(afterFirst.UploadedBytes, resumed!.UploadedBytes);
        Assert.Equal(afterFirst.NextBlockIndex, resumed.NextBlockIndex);

        var retry = await PutBlockAsync(client, campaignId, upload.Id, 0, first);
        retry.EnsureSuccessStatusCode();
        var afterRetry = (await retry.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        Assert.Equal(afterFirst.UploadedBytes, afterRetry.UploadedBytes);
        Assert.Equal(1, afterRetry.NextBlockIndex);

        var last = bytes.AsMemory(upload.BlockSize).ToArray();
        (await PutBlockAsync(client, campaignId, upload.Id, 1, last)).EnsureSuccessStatusCode();
        var committed = await client.PostAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/commit",
            JsonContent.Create(new { }));
        committed.EnsureSuccessStatusCode();
        Assert.Equal(bytes, blobs.LastCommitted);

        var transcribed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/transcribe",
            new MediaUploadTranscribeRequest());
        transcribed.EnsureSuccessStatusCode();
        var complete = (await transcribed.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        Assert.Equal(MediaUploadStatus.Completed, complete.Status);
        Assert.NotNull(complete.TranscriptArtifactId);
        Assert.Equal(1, transcription.ShortCalls);
        Assert.Equal(0, transcription.LongCalls);

        var sources = await client.GetFromJsonAsync<List<SourceAssetResponse>>(
            $"/api/v1/campaigns/{campaignId}/sources");
        var source = Assert.Single(sources!, item => item.LegacyArtifactId == complete.TranscriptArtifactId);
        Assert.Equal(SourceModalities.Media, source.Modality);
        Assert.Equal("audio/wav", source.ContentType);
        Assert.Equal(bytes.Length, source.SizeBytes);
        var evidence = await client.GetFromJsonAsync<EvidenceRevisionResponse>(
            $"/api/v1/campaigns/{campaignId}/sources/{source.Id}/evidence");
        Assert.All(evidence!.Blocks, block =>
            Assert.Equal(EvidenceLocatorKinds.MediaTimeRange, block.LocatorKind));
        Assert.Equal("Host", evidence.Blocks[0].Locator.GetProperty("speaker").GetString());
        Assert.Equal("launch.wav", evidence.Blocks[0].Locator.GetProperty("sourceLabel").GetString());

        var duplicate = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/transcribe",
            new MediaUploadTranscribeRequest());
        duplicate.EnsureSuccessStatusCode();
        Assert.Equal(1, transcription.ShortCalls);
    }

    [Fact]
    public async Task Forced_speech_route_and_cancel_are_durable()
    {
        var blobs = new BlockBlobStore();
        var transcription = new FakeTranscriptionService();
        await using var app = WithServices(blobs, transcription);
        var (client, campaignId) = await SignedInCampaignAsync(app, "media-speech");
        var bytes = "voice note bytes"u8.ToArray();
        var upload = await CreateAndCommitAsync(
            client, campaignId, "voice.webm", "audio/webm;codecs=opus", bytes);

        var transcribed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/transcribe",
            new MediaUploadTranscribeRequest(UseSpeech: true));
        transcribed.EnsureSuccessStatusCode();
        Assert.Equal(0, transcription.ShortCalls);
        Assert.Equal(1, transcription.LongCalls);

        var pending = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest("cancel.mp4", "video/mp4", 12));
        pending.EnsureSuccessStatusCode();
        var pendingUpload = (await pending.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        var cancelled = await client.DeleteAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{pendingUpload.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Single(blobs.DeletedPaths);
    }

    [Fact]
    public async Task Upload_is_size_checksum_order_and_tenant_bounded()
    {
        var blobs = new BlockBlobStore();
        await using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:MaxMediaBytes", "8");
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IBlobSasService>(blobs));
                services.Replace(ServiceDescriptor.Scoped<ITranscriptionService>(
                    _ => new FakeTranscriptionService()));
            });
        });
        var (alice, campaignId) = await SignedInCampaignAsync(app, "media-alice");

        var oversized = await alice.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest("large.mp4", "video/mp4", 9));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        var wrongType = await alice.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest("payload.bin", "application/octet-stream", 8));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        var created = await alice.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest("bounded.wav", "audio/wav", 8));
        created.EnsureSuccessStatusCode();
        var upload = (await created.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        var outOfOrder = await PutBlockAsync(alice, campaignId, upload.Id, 1, "12345678"u8.ToArray());
        Assert.Equal(HttpStatusCode.Conflict, outOfOrder.StatusCode);
        var badHash = await PutBlockAsync(
            alice, campaignId, upload.Id, 0, "12345678"u8.ToArray(), new string('0', 64));
        Assert.Equal(HttpStatusCode.BadRequest, badHash.StatusCode);

        var (bob, _) = await SignedInCampaignAsync(app, "media-bob");
        var hidden = await bob.GetAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var hiddenBlock = await PutBlockAsync(
            bob, campaignId, upload.Id, 0, "12345678"u8.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, hiddenBlock.StatusCode);
    }

    [Fact]
    public async Task Transcription_failure_returns_to_committed_and_retries_without_uploading_again()
    {
        var blobs = new BlockBlobStore();
        var transcription = new FakeTranscriptionService { FailNext = true };
        await using var app = WithServices(blobs, transcription);
        var (client, campaignId) = await SignedInCampaignAsync(app, "media-retry");
        var upload = await CreateAndCommitAsync(
            client, campaignId, "retry.wav", "audio/wav", "retry bytes"u8.ToArray());

        var failed = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/transcribe",
            new MediaUploadTranscribeRequest());
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var resumable = await client.GetFromJsonAsync<MediaUploadResponse>(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}");
        Assert.Equal(MediaUploadStatus.Committed, resumable!.Status);
        Assert.Contains("rejected this media", resumable.Error, StringComparison.Ordinal);

        var retried = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/transcribe",
            new MediaUploadTranscribeRequest());
        retried.EnsureSuccessStatusCode();
        var completed = (await retried.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        Assert.Equal(MediaUploadStatus.Completed, completed.Status);
        Assert.Equal(2, transcription.ShortCalls);
        Assert.Equal(1, blobs.CommitCalls);
    }

    private WebApplicationFactory<Program> WithServices(
        IBlobSasService blobs, ITranscriptionService transcription) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(blobs));
            services.Replace(ServiceDescriptor.Scoped(_ => transcription));
        }));

    private static async Task<MediaUploadResponse> CreateAndCommitAsync(
        HttpClient client,
        Guid campaignId,
        string fileName,
        string contentType,
        byte[] bytes)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads",
            new MediaUploadCreateRequest(fileName, contentType, bytes.Length));
        created.EnsureSuccessStatusCode();
        var upload = (await created.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
        (await PutBlockAsync(client, campaignId, upload.Id, 0, bytes)).EnsureSuccessStatusCode();
        var committed = await client.PostAsync(
            $"/api/v1/campaigns/{campaignId}/media-uploads/{upload.Id}/commit",
            JsonContent.Create(new { }));
        committed.EnsureSuccessStatusCode();
        return (await committed.Content.ReadFromJsonAsync<MediaUploadResponse>())!;
    }

    private static async Task<HttpResponseMessage> PutBlockAsync(
        HttpClient client,
        Guid campaignId,
        Guid uploadId,
        int index,
        byte[] bytes,
        string? checksum = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/campaigns/{campaignId}/media-uploads/{uploadId}/blocks/{index}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.TryAddWithoutValidation(
            "X-Content-SHA256",
            checksum ?? Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return await client.SendAsync(request);
    }

    private static async Task<(HttpClient Client, Guid CampaignId)> SignedInCampaignAsync(
        WebApplicationFactory<Program> app, string prefix)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest(
                $"{prefix}-{Guid.NewGuid():N}@example.com",
                "correct-horse-battery-staple",
                "Media Upload Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        var campaign = await client.PostAsJsonAsync(
            "/api/v1/campaigns",
            new CampaignCreateRequest("Media upload", null));
        campaign.EnsureSuccessStatusCode();
        return (client, (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id);
    }

    private sealed class FakeTranscriptionService : ITranscriptionService
    {
        public int ShortCalls { get; private set; }
        public int LongCalls { get; private set; }
        public bool SpeechConfigured => true;
        public bool FailNext { get; set; }

        public Task<TranscriptContent> TranscribeShortAsync(
            Guid userId, Stream audio, string fileName, CancellationToken ct)
        {
            ShortCalls++;
            if (FailNext)
            {
                FailNext = false;
                throw new FormatException("Unsupported provider response.");
            }
            return Task.FromResult(Transcript(fileName));
        }

        public Task<TranscriptContent> TranscribeLongAsync(
            Stream audio, string fileName, CancellationToken ct)
        {
            LongCalls++;
            return Task.FromResult(Transcript(fileName));
        }

        private static TranscriptContent Transcript(string fileName) => new(
            fileName,
            [
                new TranscriptSegment("provider-1", 0, 2.5, "Host", "First measured claim.", fileName),
                new TranscriptSegment("provider-2", 2.5, 5, "Guest", "Second measured claim.", fileName),
            ]);
    }

    private sealed class BlockBlobStore : IBlobSasService
    {
        private readonly ConcurrentDictionary<string, byte[]> _blocks = new();
        private readonly ConcurrentDictionary<string, byte[]> _committed = new();
        public bool IsConfigured => true;
        public ConcurrentBag<string> DeletedPaths { get; } = [];
        public byte[] LastCommitted { get; private set; } = [];
        public int CommitCalls { get; private set; }

        public Task<Uri> MintAsync(
            string blobPath, Azure.Storage.Sas.BlobSasPermissions permission,
            int? minutes, CancellationToken ct) =>
            Task.FromResult(new Uri($"https://blob.test/{blobPath}"));

        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<(Stream Stream, long Length)?> OpenReadAsync(
            string blobPath, CancellationToken ct) =>
            Task.FromResult(_committed.TryGetValue(blobPath, out var bytes)
                ? ((Stream)new MemoryStream(bytes, writable: false), bytes.LongLength)
                : ((Stream Stream, long Length)?)null);

        public async Task WriteAsync(
            string blobPath, Stream content, string contentType, CancellationToken ct)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            _committed[blobPath] = memory.ToArray();
        }

        public Task<bool> ExistsAsync(string blobPath, CancellationToken ct) =>
            Task.FromResult(_committed.ContainsKey(blobPath));

        public async Task StageBlockAsync(
            string blobPath, string blockId, Stream content, CancellationToken ct)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            _blocks[$"{blobPath}#{blockId}"] = memory.ToArray();
        }

        public Task CommitBlocksAsync(
            string blobPath, IReadOnlyList<string> blockIds, string contentType, CancellationToken ct)
        {
            CommitCalls++;
            _committed[blobPath] = blockIds
                .SelectMany(blockId => _blocks[$"{blobPath}#{blockId}"])
                .ToArray();
            LastCommitted = _committed[blobPath];
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string blobPath, CancellationToken ct)
        {
            _committed.TryRemove(blobPath, out _);
            DeletedPaths.Add(blobPath);
            return Task.CompletedTask;
        }
    }
}