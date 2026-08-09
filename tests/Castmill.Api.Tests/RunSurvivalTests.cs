using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Api.Services.Ai;
using Castmill.Core.Auth;
using Castmill.Core.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

/// <summary>
/// "This said 13 items created, it did not create 13 items."
///
/// The run used to execute inside its HTTP request, so anything that severed the request —
/// the client's timeout, a closed app, a navigation, a dropped connection — cancelled the
/// remaining generators mid-run, with the completed items' model spend already paid. Nothing
/// reported the truncation; the run row just stopped moving.
///
/// Two guarantees are pinned here: a run outlives its request, and a run orphaned by a dead
/// PROCESS (the one thing that genuinely kills it) is marked Interrupted at the next startup
/// instead of saying Running forever.
/// </summary>
[Collection("api")]
public sealed class RunSurvivalTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task A_run_survives_its_request_being_aborted()
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"run-{Guid.NewGuid():N}@example.com", "correct-horse-battery-staple", "Run Tester"));
        register.EnsureSuccessStatusCode();
        var tokens = await register.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        var campaign = (await (await client.PostAsJsonAsync("/api/v1/campaigns",
            new CampaignCreateRequest("Severed", null))).Content.ReadFromJsonAsync<CampaignResponse>())!;
        var ingest = await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/transcripts",
            new { text = "We launched. It cut deploy time in half. Everyone was pleased with the result.", source = "test" });
        ingest.EnsureSuccessStatusCode();
        using var ingestDoc = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync());
        var transcriptId = ingestDoc.RootElement.GetProperty("transcriptArtifactId").GetGuid();

        // Abort the request shortly after it starts — the closed-laptop scenario. The token
        // cancels the CLIENT call; the question is what happens server-side.
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await client.PostAsJsonAsync($"/api/v1/ai/campaigns/{campaign.Id}/generate",
                new { transcriptArtifactId = transcriptId, kinds = new[] { "social-x", "newsletter" } },
                abort.Token);
        }
        catch (OperationCanceledException)
        {
            // The abort itself — expected.
        }

        // The run must reach a terminal state with every item accounted for, despite the
        // caller being long gone. Generators fail fast here (no credentials), which is fine:
        // failure IS an accounted outcome; truncation is not.
        RunProgressShape? run = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            run = await LatestRunAsync(client, campaign.Id);
            if (run is { Status: not "Running" })
            {
                break;
            }
        }

        Assert.NotNull(run);
        Assert.Equal("Completed", run!.Status);
        Assert.Equal(2, run.Items.Count);
    }

    [Fact]
    public async Task A_run_orphaned_by_a_dead_process_is_marked_interrupted_at_startup()
    {
        using var scope = factory.CreateDbScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<CastmillDbContext>>();
        await using var db = new CastmillDbContext(options, new NullTenant());

        var orphan = new Castmill.Core.GenerationRun
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            Status = "Running",
            TotalKinds = 13,
            ItemsJson = "[]",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        db.GenerationRuns.Add(orphan);
        await db.SaveChangesAsync();

        var swept = await InterruptedRunSweeper.SweepAsync(db, CancellationToken.None);
        Assert.True(swept >= 1);

        // A fresh context for the read: ExecuteUpdate bypasses the change tracker, and the
        // identity map would otherwise hand back the stale tracked instance.
        await using var reader = new CastmillDbContext(options, new NullTenant());
        var row = await reader.GenerationRuns.IgnoreQueryFilters().SingleAsync(r => r.Id == orphan.Id);
        // "Interrupted", not "Running" and not "Completed": the honest label for work a dead
        // process can no longer finish and never finished.
        Assert.Equal("Interrupted", row.Status);
    }

    private static async Task<RunProgressShape?> LatestRunAsync(HttpClient client, Guid campaignId)
    {
        var response = await client.GetAsync($"/api/v1/ai/campaigns/{campaignId}/runs/latest?kind=content");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new RunProgressShape(
            root.GetProperty("status").GetString()!,
            [.. root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()!)]);
    }

    private sealed record RunProgressShape(string Status, List<string> Items);

    private sealed class NullTenant : Castmill.Api.Tenancy.ITenantProvider
    {
        public Guid? TenantId => null;
    }
}
