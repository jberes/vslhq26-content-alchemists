using Bunit;
using Bunit.TestDoubles;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The rail's Wire item used to point at a route no page served, which dead-ended the
/// user with no way back to the workspace. These tests pin that /wire is a real routed
/// page inside the shell, renders the week from the schedule mirror, and states what it
/// cannot do yet instead of offering a dead control (G3).
/// </summary>
public sealed class WirePageTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("81111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtifactId = Guid.Parse("81111111-1111-1111-1111-222222222222");

    public WirePageTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
                DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow),
        });

        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [], [], [], 0, 0, [], null,
            ReadyToSchedule:
            [
                new DashboardArtifact(CampaignId, "Webinar campaign", ArtifactId,
                    "social-x", "Launch thread", ArtifactStatus.Queued, DateTimeOffset.UtcNow),
            ]));

        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>());
        Http.OnGet("api/v1/publish/readiness", new PublishReadinessResponse(
            false, false, false,
            "No publishing broker has been selected or configured. Posts can be staged in Castmill only."));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{ArtifactId}", new ArtifactResponse(
            ArtifactId, CampaignId, "social-x", "Launch thread",
            """{"content":{"text":"Launch day","hashtags":["Castmill"]},"validation":{}}""",
            ArtifactStatus.Queued, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview());
    }

    [Fact]
    public async Task The_wire_route_resolves_to_a_real_page_inside_the_shell()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/wire");

        var app = Render<App>();
        await app.WaitForStateAsync(
            () => app.Markup.Contains("Ready to schedule", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        // Not the not-found dead end...
        Assert.DoesNotContain("Nothing on this plate", app.Markup, StringComparison.Ordinal);
        // ...and the workspace rail is present, so there is always a way back.
        Assert.Contains("Front page", app.Markup, StringComparison.Ordinal);
        Assert.Contains("Campaigns", app.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_week_renders_seven_days_and_the_ready_queue()
    {
        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll(".cm-wire__day").Count == 7, TimeSpan.FromSeconds(5));

        Assert.Contains("Launch thread", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Local mirror", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Exported clips", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scheduled_entry_lands_in_its_day_column()
    {
        var when = DateTimeOffset.Now.Date.AddHours(9);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>
        {
            new(Guid.NewGuid(), CampaignId, ArtifactId, "linkedin", null,
                "Shipping the new dashboard today.", null, when, "Queued", null, DateTimeOffset.UtcNow),
        });

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Shipping the new dashboard today.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        var today = page.FindAll(".cm-wire__day")
            .First(d => d.TextContent.Contains("Shipping the new dashboard", StringComparison.Ordinal));
        Assert.Contains("linkedin", today.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composer_warns_with_the_exact_overage_and_blocks_scheduling()
    {
        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer__text").Count == 1, TimeSpan.FromSeconds(5));

        page.Find(".cm-composer__text").Input(new string('x', 281));

        Assert.Contains("1 over limit. Remove exactly 1 character", page.Markup, StringComparison.Ordinal);
        var stage = page.FindAll("button").Single(button => button.TextContent.Contains("Add to Queue", StringComparison.Ordinal));
        Assert.True(stage.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Composer_counts_unicode_characters_instead_of_utf16_code_units()
    {
        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)
                && !button.HasAttribute("disabled")),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer__text").Count == 1, TimeSpan.FromSeconds(5));

        page.Find(".cm-composer__text").Input(string.Concat(Enumerable.Repeat("😀", 280)));

        Assert.Contains("280 / 280", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("over limit", page.Markup, StringComparison.Ordinal);
        var stage = page.FindAll("button").Single(button => button.TextContent.Contains("Add to Queue", StringComparison.Ordinal));
        Assert.False(stage.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Composer_excludes_images_owned_by_a_sibling_artifact()
    {
        var siblingId = Guid.NewGuid();
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", PreviewWithSlots(
            Slot("https://cdn.example/owned.webp", ArtifactId),
            Slot("https://cdn.example/sibling.webp", siblingId)));

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)
                && !button.HasAttribute("disabled")),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer select option").Count > 1, TimeSpan.FromSeconds(5));

        Assert.Contains("https://cdn.example/owned.webp", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://cdn.example/sibling.webp", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composer_stages_an_existing_published_image_then_writes_a_local_schedule_row()
    {
        var scheduled = new ScheduleEntryResponse(
            Guid.NewGuid(), CampaignId, ArtifactId, "x", null, "Launch day\n\n#Castmill",
            "https://cdn.example/social.webp", DateTimeOffset.UtcNow.AddHours(1),
            "Draft", "No broker configured; entry saved locally.", DateTimeOffset.UtcNow);
        Http.OnPost("api/v1/schedule", scheduled);

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer").Count == 1, TimeSpan.FromSeconds(5));

        page.Find(".cm-composer select").Change("https://cdn.example/social.webp");
        page.FindAll("button").Single(button => button.TextContent.Contains("Add to Queue", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(button => button.TextContent.Contains("Save local schedule", StringComparison.Ordinal)).Click();

        await page.WaitForStateAsync(
            () => Http.Bodies.Any(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule"),
            TimeSpan.FromSeconds(5));
        var body = Http.Bodies.Single(request => request.Method == HttpMethod.Post && request.Path == "api/v1/schedule").Body;
        Assert.Contains("\"artifactId\":\"81111111-1111-1111-1111-222222222222\"", body, StringComparison.Ordinal);
        Assert.Contains("\"mediaUrl\":\"https://cdn.example/social.webp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"pushToBroker\":false", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_reconciliation_starts_only_after_the_local_mirror_renders()
    {
        var when = DateTimeOffset.Now.Date.AddHours(9);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>
        {
            new(Guid.NewGuid(), CampaignId, ArtifactId, "ch-1", "broker-1",
                "Visible before broker reconciliation.", null, when, "Queued", null, DateTimeOffset.UtcNow),
        });
        Http.OnGet("api/v1/publish/readiness", new PublishReadinessResponse(
            true, true, true, "The publishing broker is ready.", CanSchedule: true));
        var channels = Http.Gate(HttpMethod.Get, "api/v1/publish/channels");
        Http.OnPost("api/v1/schedule/reconcile", new ScheduleReconcileResponse(1, 0, []));

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Visible before broker reconciliation.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await page.WaitForStateAsync(
            () => Http.Requests.Any(request => request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath.EndsWith("/api/v1/publish/channels", StringComparison.Ordinal) == true),
            TimeSpan.FromSeconds(5));

        Assert.Contains("Visible before broker reconciliation.", page.Markup, StringComparison.Ordinal);
        Assert.True(page.FindAll("button").Single(button =>
            button.TextContent.Contains("Compose", StringComparison.Ordinal)).HasAttribute("disabled"));
        channels.SetResult(StubHttpHandler.Json(new List<PublishChannel>
        {
            new("ch-1", "Main X", "x"),
        }));
        await page.WaitForStateAsync(
            () => page.Markup.Contains("Mirror reconciled with the broker", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        Assert.False(page.FindAll("button").Single(button =>
            button.TextContent.Contains("Compose", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    [Fact]
    public async Task Broker_error_row_keeps_the_composer_open_for_correction()
    {
        Http.OnPost("api/v1/schedule", new ScheduleEntryResponse(
            Guid.NewGuid(), CampaignId, ArtifactId, "x", null, "Launch day\n\n#Castmill", null,
            DateTimeOffset.UtcNow.AddHours(1), "Error", "Broker rejected the post.", DateTimeOffset.UtcNow));

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)
                && !button.HasAttribute("disabled")),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer").Count == 1, TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Add to Queue", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(button => button.TextContent.Contains("Save local schedule", StringComparison.Ordinal)).Click();

        await page.WaitForStateAsync(
            () => Http.Bodies.Any(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule"),
            TimeSpan.FromSeconds(5));
        Assert.Single(page.FindAll(".cm-composer"));
    }

    [Fact]
    public async Task Mixed_channel_retry_submits_only_the_channel_that_failed()
    {
        Http.OnGet("api/v1/publish/readiness", new PublishReadinessResponse(
            true, true, true, "The publishing broker is ready.", CanSchedule: true));
        Http.OnGet("api/v1/publish/channels", new List<PublishChannel>
        {
            new("ch-x", "Main X", "x"),
            new("ch-linkedin", "Company LinkedIn", "linkedin"),
        });
        Http.OnPost("api/v1/schedule/reconcile", new ScheduleReconcileResponse(0, 0, []));
        Http.OnPostSequence("api/v1/schedule",
            Scheduled("ch-x", "Queued"),
            Scheduled("ch-linkedin", "Error", "Broker rejected LinkedIn."),
            Scheduled("ch-linkedin", "Queued"));

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)
                && !button.HasAttribute("disabled")),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Compose", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-composer__variant").Count == 2, TimeSpan.FromSeconds(5));

        page.FindAll(".cm-composer__variant input[type=checkbox]")[1].Change(true);
        page.FindAll("button").Single(button => button.TextContent.Contains("Add to Queue", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(button => button.TextContent == "Schedule").Click();
        await page.WaitForStateAsync(
            () => Http.Bodies.Count(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule") == 2,
            TimeSpan.FromSeconds(5));

        Assert.Contains("Main X · scheduled", page.Markup, StringComparison.Ordinal);
        page.FindAll("button").Single(button => button.TextContent == "Schedule").Click();
        await page.WaitForStateAsync(
            () => Http.Bodies.Count(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule") == 3,
            TimeSpan.FromSeconds(5));

        var requests = Http.Bodies
            .Where(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule")
            .Select(body => body.Body)
            .ToList();
        Assert.Single(requests, body => body.Contains("\"channelId\":\"ch-x\"", StringComparison.Ordinal));
        Assert.Equal(2, requests.Count(body => body.Contains("\"channelId\":\"ch-linkedin\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Error_entry_retry_calls_the_guarded_retry_endpoint()
    {
        var id = Guid.NewGuid();
        var when = DateTimeOffset.Now.Date.AddHours(9);
        var error = new ScheduleEntryResponse(
            id, CampaignId, ArtifactId, "ch-1", null, "Retry me", null, when,
            "Error", "Broker rejected the post.", DateTimeOffset.UtcNow);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse> { error });
        Http.OnGet("api/v1/publish/readiness", new PublishReadinessResponse(
            true, true, true, "The publishing broker is ready.", CanSchedule: true));
        Http.OnGet("api/v1/publish/channels", new List<PublishChannel>
        {
            new("ch-1", "Main X", "x"),
        });
        Http.OnPost("api/v1/schedule/reconcile", new ScheduleReconcileResponse(0, 0, []));
        Http.OnPost($"api/v1/schedule/{id}/retry", error with { Status = "Queued", Error = null, BrokerPostId = "post-1" });

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Retry", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Retry", StringComparison.Ordinal)).Click();

        await page.WaitForStateAsync(
            () => Http.Bodies.Any(body => body.Method == HttpMethod.Post
                && body.Path == $"api/v1/schedule/{id}/retry"),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Draft_entry_can_be_moved_and_cancelled_from_the_wire()
    {
        var id = Guid.NewGuid();
        var when = DateTimeOffset.Now.Date.AddHours(9);
        var draft = new ScheduleEntryResponse(
            id, CampaignId, ArtifactId, "x", null, "Move me", null, when,
            "Draft", null, DateTimeOffset.UtcNow);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse> { draft });
        Http.OnPatch($"api/v1/schedule/{id}", draft with { ScheduledAt = when.AddHours(2) });
        Http.OnStatus(HttpMethod.Delete, $"api/v1/schedule/{id}", System.Net.HttpStatusCode.NoContent);

        var page = Render<Wire>();
        await page.WaitForStateAsync(
            () => page.FindAll("button").Any(button => button.TextContent.Contains("Move", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        page.FindAll("button").Single(button => button.TextContent.Contains("Move", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(() => page.FindAll(".cm-wire__move").Count == 1, TimeSpan.FromSeconds(5));
        page.Find(".cm-wire__move input").Change(
            when.AddHours(2).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        page.FindAll("button").Single(button => button.TextContent.Contains("Save move", StringComparison.Ordinal)).Click();

        await page.WaitForStateAsync(
            () => Http.Bodies.Any(body => body.Method == HttpMethod.Patch
                && body.Path == $"api/v1/schedule/{id}"),
            TimeSpan.FromSeconds(5));
        page.FindAll("button").Single(button => button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();
        await page.WaitForStateAsync(
            () => Http.Requests.Any(request => request.Method == HttpMethod.Delete
                && request.RequestUri?.AbsolutePath.EndsWith($"/api/v1/schedule/{id}", StringComparison.Ordinal) == true),
            TimeSpan.FromSeconds(5));
    }

    private static CampaignPreview Preview() => new(
        new CampaignResponse(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        [],
        [
            new ImageSlotResponse(
                Guid.NewGuid(), CampaignId, "social-card", 1200, 1200,
                null, null, null, null, false, "Filled", "https://cdn.example/social.webp", null,
                DateTimeOffset.UtcNow, ArtifactId: ArtifactId),
        ],
        1,
        1);

    private static CampaignPreview PreviewWithSlots(params ImageSlotResponse[] slots) => new(
        new CampaignResponse(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        [], slots, slots.Length, slots.Length);

    private static ImageSlotResponse Slot(string url, Guid artifactId) => new(
        Guid.NewGuid(), CampaignId, "social-card", 1200, 1200,
        null, null, null, null, false, "Filled", url, null,
        DateTimeOffset.UtcNow, ArtifactId: artifactId);

    private static ScheduleEntryResponse Scheduled(string channelId, string status, string? error = null) => new(
        Guid.NewGuid(), CampaignId, ArtifactId, channelId,
        status == "Queued" ? $"post-{channelId}" : null,
        "Launch day\n\n#Castmill", null, DateTimeOffset.UtcNow.AddHours(1),
        status, error, DateTimeOffset.UtcNow);
}
