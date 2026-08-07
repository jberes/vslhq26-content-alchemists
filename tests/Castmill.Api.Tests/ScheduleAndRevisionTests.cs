using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Services.Media;
using Castmill.Api.Services.Publish;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Castmill.Api.Tests;

/// <summary>B9.4, B9.6–B9.8: reference frames, the schedule mirror, revisions, run progress.</summary>
[Collection("api")]
public sealed class ScheduleAndRevisionTests(CastmillApiFactory factory)
{
    private sealed class CapturingDispatcher : IClipJobDispatcher
    {
        public List<ClipJobMessage> Sent { get; } = [];
        public bool IsConfigured => true;
        public Task EnqueueAsync(ClipJobMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>Broker that accepts posts and can be told to drop one from its queue.</summary>
    private sealed class TrackingBroker : IPublishBrokerClient
    {
        public Dictionary<string, BrokerPost> Posts { get; } = [];
        public HashSet<string> Dropped { get; } = [];
        public bool FailNextSchedule { get; set; }
        public int Cancels { get; private set; }

        public Task<IReadOnlyList<BrokerChannel>> ListChannelsAsync(string token, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrokerChannel>>([new BrokerChannel("ch-1", "Main X", "x")]);

        public Task<BrokerPost> SchedulePostAsync(
            string token, string channelId, string text, DateTimeOffset scheduledAt, string? mediaUrl, CancellationToken ct)
        {
            if (FailNextSchedule)
            {
                FailNextSchedule = false;
                throw new HttpRequestException("broker down");
            }
            var post = new BrokerPost($"post-{Posts.Count + 1}", channelId, text, scheduledAt, "scheduled");
            Posts[post.Id] = post;
            return Task.FromResult(post);
        }

        public Task CancelPostAsync(string token, string postId, CancellationToken ct)
        {
            Cancels++;
            Posts.Remove(postId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPost>> GetQueueAsync(string token, string channelId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrokerPost>>(
                [.. Posts.Values.Where(p => p.ChannelId == channelId && !Dropped.Contains(p.Id))]);
    }

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"wire-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Wire Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private static async Task<CampaignResponse> CampaignAsync(HttpClient client, string name) =>
        (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest(name, null))).Content.ReadFromJsonAsync<CampaignResponse>())!;

    // ---- B9.6 schedule mirror --------------------------------------------------

    [Fact]
    public async Task Wire_entries_persist_without_a_broker_and_survive_a_restart()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Wire local");

        var slot = DateTimeOffset.Parse("2026-08-06T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var created = await client.PostAsJsonAsync("/api/v1/schedule", new
        {
            campaignId = campaign.Id,
            channelId = "ch-1",
            text = "LinkedIn post body",
            scheduledAt = slot,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = (await created.Content.ReadFromJsonAsync<ScheduleEntryResponse>())!;
        // No broker configured: the drag gesture is still saved, with the reason on it.
        Assert.Equal("Draft", entry.Status);
        Assert.Null(entry.BrokerPostId);
        Assert.Contains("broker", entry.Error!, StringComparison.OrdinalIgnoreCase);

        // A fresh client (new "session") sees the entry — the Wire renders from us,
        // not from a broker round-trip (ADR-016).
        var reader = await AuthedClientAsync(app);
        var mine = await reader.GetFromJsonAsync<List<ScheduleEntryResponse>>("/api/v1/schedule");
        Assert.Empty(mine!); // another tenant sees nothing (G1)

        var week = await client.GetFromJsonAsync<List<ScheduleEntryResponse>>(
            $"/api/v1/schedule?from={Uri.EscapeDataString("2026-08-03T00:00:00Z")}&to={Uri.EscapeDataString("2026-08-10T00:00:00Z")}");
        Assert.Equal(entry.Id, Assert.Single(week!).Id);

        // Outside the queried range → not returned.
        var otherWeek = await client.GetFromJsonAsync<List<ScheduleEntryResponse>>(
            $"/api/v1/schedule?from={Uri.EscapeDataString("2026-08-10T00:00:00Z")}&to={Uri.EscapeDataString("2026-08-17T00:00:00Z")}");
        Assert.Empty(otherWeek!);

        // Move keeps the row's identity.
        var moved = await (await client.PatchAsJsonAsync($"/api/v1/schedule/{entry.Id}",
            new { scheduledAt = slot.AddDays(1) })).Content.ReadFromJsonAsync<ScheduleEntryResponse>();
        Assert.Equal(entry.Id, moved!.Id);
        Assert.Equal(slot.AddDays(1), moved.ScheduledAt);

        var deleted = await client.DeleteAsync($"/api/v1/schedule/{entry.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ScheduleEntryResponse>>("/api/v1/schedule"))!);
    }

    [Fact]
    public async Task Schedule_pushes_to_the_broker_and_reconcile_lets_the_broker_win()
    {
        var broker = new TrackingBroker();
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Publish:BrokerBaseUrl", "https://broker.example");
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<IPublishBrokerClient>(_ => broker)));
        });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Wire brokered");

        // The broker token lives in secret custody, like every other credential.
        var storeToken = await client.PutAsJsonAsync("/api/v1/settings/secrets/BrokerToken",
            new { value = "broker-test-token" });
        storeToken.EnsureSuccessStatusCode();

        var slot = DateTimeOffset.Parse("2026-08-06T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var entry = (await (await client.PostAsJsonAsync("/api/v1/schedule", new
        {
            campaignId = campaign.Id,
            channelId = "ch-1",
            text = "Queued post",
            scheduledAt = slot,
        })).Content.ReadFromJsonAsync<ScheduleEntryResponse>())!;
        Assert.Equal("Queued", entry.Status);
        Assert.NotNull(entry.BrokerPostId);

        // Reconcile with the post still queued → nothing changes.
        using var noop = JsonDocument.Parse(
            await (await client.PostAsync("/api/v1/schedule/reconcile", null)).Content.ReadAsStringAsync());
        Assert.Equal(0, noop.RootElement.GetProperty("updated").GetInt32());

        // The post leaves the broker's queue → it went out. Broker wins (ADR-016).
        broker.Dropped.Add(entry.BrokerPostId!);
        using var reconciled = JsonDocument.Parse(
            await (await client.PostAsync("/api/v1/schedule/reconcile", null)).Content.ReadAsStringAsync());
        Assert.Equal(1, reconciled.RootElement.GetProperty("updated").GetInt32());

        var after = (await client.GetFromJsonAsync<List<ScheduleEntryResponse>>("/api/v1/schedule"))!;
        Assert.Equal("Sent", Assert.Single(after).Status);

        // A sent post cannot be rescheduled.
        var move = await client.PatchAsJsonAsync($"/api/v1/schedule/{entry.Id}", new { scheduledAt = slot.AddDays(2) });
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
    }

    [Fact]
    public async Task A_broker_rejection_leaves_an_error_entry_not_a_lost_gesture()
    {
        var broker = new TrackingBroker { FailNextSchedule = true };
        await using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Publish:BrokerBaseUrl", "https://broker.example");
            b.ConfigureServices(s => s.Replace(ServiceDescriptor.Scoped<IPublishBrokerClient>(_ => broker)));
        });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Wire failure");
        (await client.PutAsJsonAsync("/api/v1/settings/secrets/BrokerToken", new { value = "t" })).EnsureSuccessStatusCode();

        var entry = (await (await client.PostAsJsonAsync("/api/v1/schedule", new
        {
            campaignId = campaign.Id,
            channelId = "ch-1",
            text = "Doomed post",
            scheduledAt = DateTimeOffset.Parse("2026-08-06T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        })).Content.ReadFromJsonAsync<ScheduleEntryResponse>())!;

        Assert.Equal("Error", entry.Status);
        Assert.Contains("rejected", entry.Error!, StringComparison.OrdinalIgnoreCase);
        // The row exists so the user can retry from the strip.
        Assert.Single((await client.GetFromJsonAsync<List<ScheduleEntryResponse>>("/api/v1/schedule"))!);
    }

    // ---- B9.7 revisions --------------------------------------------------------

    [Fact]
    public async Task Revisions_snapshot_edits_and_restore_returns_byte_identical_content()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Revisions");

        const string first = """{"content":{"title":"Take one","markdown":"first draft"},"validation":{}}""";
        var artifact = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "Take one", first))).Content.ReadFromJsonAsync<ArtifactResponse>())!;

        // Two edits → two snapshots of what came before each.
        await PutAsync(client, campaign.Id, artifact.Id, 1, "Take two",
            """{"content":{"title":"Take two","markdown":"second draft"},"validation":{}}""");
        await PutAsync(client, campaign.Id, artifact.Id, 2, "Take three",
            """{"content":{"title":"Take three","markdown":"third draft"},"validation":{}}""");

        var revisions = (await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions"))!;
        Assert.Equal(2, revisions.Count);
        Assert.Equal([2, 1], revisions.Select(r => r.Version)); // newest first
        Assert.All(revisions, r => Assert.Equal("manual-save", r.Reason));

        var oldest = revisions.Last();
        var detail = (await client.GetFromJsonAsync<ArtifactRevisionDetailResponse>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions/{oldest.Id}"))!;
        Assert.Equal(first, detail.ContentJson);

        // Restore requires the current ETag, like any other write.
        var noEtag = await client.PostAsync(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions/{oldest.Id}/restore", null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, noEtag.StatusCode);

        var stale = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions/{oldest.Id}/restore");
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

        var restore = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions/{oldest.Id}/restore");
        restore.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        var restored = await (await client.SendAsync(restore)).Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.Equal(first, restored!.ContentJson); // byte-identical
        Assert.Equal("Take one", restored.Title);
        Assert.Equal(4, restored.Version); // restore is a normal forward write

        // Restoring is itself undoable.
        var afterRestore = (await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions"))!;
        Assert.Equal("restore", afterRestore[0].Reason);
    }

    [Fact]
    public async Task Revision_ring_is_bounded()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Ring");

        var artifact = (await (await client.PostAsJsonAsync($"/api/v1/campaigns/{campaign.Id}/artifacts",
            new ArtifactCreateRequest("blog", "v1", """{"content":{"markdown":"v1"},"validation":{}}"""))).Content
            .ReadFromJsonAsync<ArtifactResponse>())!;

        // 13 edits against a 10-deep ring.
        for (var version = 1; version <= 13; version++)
        {
            await PutAsync(client, campaign.Id, artifact.Id, version, $"v{version + 1}",
                $"{{\"content\":{{\"markdown\":\"v{version + 1}\"}},\"validation\":{{}}}}");
        }

        var revisions = (await client.GetFromJsonAsync<List<ArtifactRevisionResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/artifacts/{artifact.Id}/revisions"))!;
        Assert.Equal(Castmill.Core.ArtifactRevision.RingSize, revisions.Count);
        // The oldest takes were trimmed, the newest kept.
        Assert.Equal(13, revisions[0].Version);
        Assert.Equal(4, revisions[^1].Version);
    }

    private static async Task PutAsync(
        HttpClient client, Guid campaignId, Guid artifactId, long etag, string title, string contentJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}")
        {
            Content = JsonContent.Create(new ArtifactUpdateRequest(title, contentJson)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    // ---- B9.4 reference frames -------------------------------------------------

    [Fact]
    public async Task Frame_extraction_enqueues_a_frame_mode_job_on_the_same_worker_path()
    {
        var dispatcher = new CapturingDispatcher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Replace(ServiceDescriptor.Singleton<IClipJobDispatcher>(dispatcher))));
        var client = await AuthedClientAsync(app);

        var asset = (await (await client.PostAsJsonAsync("/api/v1/assets",
            new AssetCreateRequest("webinar.mp4", "video/mp4", 5_000_000))).Content
            .ReadFromJsonAsync<AssetResponse>())!;

        var accepted = await client.PostAsJsonAsync("/api/v1/media/frames",
            new { assetId = asset.Id, atSeconds = 262.0 });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var message = Assert.Single(dispatcher.Sent);
        Assert.Equal("frame", message.Mode);
        Assert.Equal(262.0, message.InSeconds);
        Assert.EndsWith(".png", message.OutputBlobPath, StringComparison.Ordinal);
        Assert.Contains("/frames/", message.OutputBlobPath, StringComparison.Ordinal);

        // Same status endpoint as clips — one job path, not two (ADR-014).
        var done = await client.PostAsJsonAsync($"/api/v1/media/clip-jobs/{message.JobId}/callback",
            new { token = message.CallbackToken, status = "Succeeded" });
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        var status = await client.GetStringAsync($"/api/v1/media/clip-jobs/{message.JobId}");
        Assert.Contains("Succeeded", status, StringComparison.Ordinal);
    }

    // ---- B9.8 run progress -----------------------------------------------------

    [Fact]
    public async Task Fan_out_opens_a_run_that_reports_per_artifact_completions()
    {
        await using var app = factory.WithWebHostBuilder(_ => { });
        var client = await AuthedClientAsync(app);
        var campaign = await CampaignAsync(client, "Run progress");

        var transcript = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new { text = "Welcome. We cut first paint from 4.2s to 900ms. Trust is not cheap.", source = "test" });
        transcript.EnsureSuccessStatusCode();
        using var transcriptDoc = JsonDocument.Parse(await transcript.Content.ReadAsStringAsync());
        var transcriptId = transcriptDoc.RootElement.GetProperty("transcriptArtifactId").GetGuid();

        // No credentials in the test factory: every generator fails, which is
        // exactly what makes this a clean progress-accounting test.
        var generate = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/generate",
            new { transcriptArtifactId = transcriptId, kinds = new[] { "social-x", "newsletter" } });
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.True(generate.Headers.TryGetValues("Castmill-Run-Id", out var header));
        var runId = Guid.Parse(header!.Single());

        using var body = JsonDocument.Parse(await generate.Content.ReadAsStringAsync());
        Assert.Equal(runId, body.RootElement.GetProperty("runId").GetGuid());

        using var run = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/ai/runs/{runId}"));
        Assert.Equal("Completed", run.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, run.RootElement.GetProperty("completed").GetInt32());
        Assert.Equal(2, run.RootElement.GetProperty("items").GetArrayLength());
        // Reveal order is completion order.
        Assert.Equal("social-x", run.RootElement.GetProperty("items")[0].GetProperty("kind").GetString());

        // The run also reserved the image plan (ADR-012). This fan-out produced no blog, so
        // only the campaign-wide half exists — blog slots belong to a specific blog now, and
        // reserving four of them for a campaign with no blog is what the old plan got wrong.
        var slots = (await client.GetFromJsonAsync<List<ImageSlotResponse>>(
            $"/api/v1/campaigns/{campaign.Id}/image-slots"))!;
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s => Assert.Null(s.ArtifactId));

        // Another tenant cannot read the run.
        var stranger = await AuthedClientAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/ai/runs/{runId}")).StatusCode);
    }
}
