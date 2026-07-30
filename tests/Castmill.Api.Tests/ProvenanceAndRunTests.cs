using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Core.Auth;
using Microsoft.Extensions.DependencyInjection;
using Castmill.Core.Resources;

namespace Castmill.Api.Tests;

/// <summary>
/// The two backend seams Phase F5 stands on: citations available in list projections
/// (threads draw with no per-card fetch, without violating ADR-003), and the latest-run
/// lookup the Press Run polls (the generate POST is buffered, so its run id arrives too
/// late to poll by id). Plus F7's timed-segment ingest: locally transcribed media must
/// keep its real timestamps.
/// </summary>
[Collection("api")]
public sealed class ProvenanceAndRunTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task Preview_projections_carry_citations_extracted_by_the_database()
    {
        var (client, campaignId) = await SignedInCampaignAsync();

        var create = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest("blog", "Cited post",
                JsonSerializer.Serialize(new { markdown = "# Post", citations = new[] { "s02", "s07" } })));
        create.EnsureSuccessStatusCode();

        // Both list surfaces: the artifacts list and the campaign preview.
        var list = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");
        var cited = Assert.Single(list!, a => a.Kind == "blog");
        Assert.Equal(new[] { "s02", "s07" }, cited.Citations);

        using var preview = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/campaigns/{campaignId}/preview"));
        var previewArtifact = preview.RootElement.GetProperty("artifacts").EnumerateArray()
            .Single(a => a.GetProperty("kind").GetString() == "blog");
        Assert.Equal(2, previewArtifact.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task An_artifact_without_citations_reports_none_rather_than_failing()
    {
        var (client, campaignId) = await SignedInCampaignAsync();

        var create = await client.PostAsJsonAsync(
            $"/api/v1/campaigns/{campaignId}/artifacts",
            new ArtifactCreateRequest("clips", "No citations here",
                JsonSerializer.Serialize(new { items = new[] { new { start = 1.0, end = 2.0 } } })));
        create.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<List<ArtifactPreviewResponse>>(
            $"/api/v1/campaigns/{campaignId}/artifacts");

        Assert.Null(Assert.Single(list!).Citations);
    }

    [Fact]
    public async Task Ingest_accepts_real_timed_segments_and_normalises_their_ids()
    {
        var (client, campaignId) = await SignedInCampaignAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaignId}/transcripts", new
        {
            text = "We shipped the pipeline. It cut deploy time in half. Customers noticed.",
            source = "local-whisper",
            segments = new[]
            {
                // Deliberately out of order with junk ids: the server must sort and rename.
                new { id = "whisper-7", startSeconds = 4.2, endSeconds = 8.9, speaker = (string?)null, text = "It cut deploy time in half." },
                new { id = "", startSeconds = 0.0, endSeconds = 4.2, speaker = (string?)"HOST", text = "We shipped the pipeline." },
            },
        });
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var artifactId = created.RootElement.GetProperty("transcriptArtifactId").GetGuid();
        Assert.Equal(2, created.RootElement.GetProperty("segmentCount").GetInt32());

        var artifact = await client.GetFromJsonAsync<ArtifactResponse>(
            $"/api/v1/campaigns/{campaignId}/artifacts/{artifactId}");
        using var content = JsonDocument.Parse(artifact!.ContentJson);
        var segments = content.RootElement.GetProperty("segments").EnumerateArray().ToList();

        // Sorted by start time, canonical ids, real timestamps preserved.
        Assert.Equal("s01", segments[0].GetProperty("id").GetString());
        Assert.Equal("HOST", segments[0].GetProperty("speaker").GetString());
        Assert.Equal(0.0, segments[0].GetProperty("startSeconds").GetDouble());
        Assert.Equal("s02", segments[1].GetProperty("id").GetString());
        Assert.Equal(4.2, segments[1].GetProperty("startSeconds").GetDouble());
    }

    [Fact]
    public async Task The_latest_run_endpoint_finds_the_most_recent_run_for_a_campaign()
    {
        var (client, campaignId) = await SignedInCampaignAsync();

        // No run yet: 404, which the Press Run treats as "keep polling".
        var missing = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        // Two runs created directly through the orchestrator's run table would need model
        // credentials; instead exercise the read path with rows inserted via the DbContext.
        using var scope = factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<Castmill.Api.Data.CastmillDbContext>();
        var tenantId = (await client.GetFromJsonAsync<MeResponse>("/api/v1/me"))!.TenantId;

        var older = NewRun(tenantId, campaignId, DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = NewRun(tenantId, campaignId, DateTimeOffset.UtcNow);
        db.GenerationRuns.AddRange(older, newer);
        await db.SaveChangesAsync();

        using var latest = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/v1/ai/campaigns/{campaignId}/runs/latest"));

        Assert.Equal(newer.Id, latest.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(1, latest.RootElement.GetProperty("completed").GetInt32());
        Assert.Equal("show-notes",
            latest.RootElement.GetProperty("items")[0].GetProperty("kind").GetString());
    }

    // ---- helpers ---------------------------------------------------------------

    private static Castmill.Core.GenerationRun NewRun(Guid tenantId, Guid campaignId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        CampaignId = campaignId,
        Status = "Running",
        TotalKinds = 2,
        ItemsJson = """[{"kind":"show-notes","success":true,"artifactId":null,"error":null,"validationWarnings":[],"durationMs":812}]""",
        StartedAt = at,
        UpdatedAt = at,
    };

    private async Task<(HttpClient Client, Guid CampaignId)> SignedInCampaignAsync()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"prov-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Prov Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = await client.PostAsJsonAsync(
            "/api/v1/campaigns", new CampaignCreateRequest($"Prov {Guid.NewGuid():N}", null));
        campaign.EnsureSuccessStatusCode();
        return (client, (await campaign.Content.ReadFromJsonAsync<CampaignResponse>())!.Id);
    }
}
