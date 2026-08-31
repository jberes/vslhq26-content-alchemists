using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Castmill.UI.Scheduling;
using IgniteUI.Blazor.Controls;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class WirePageTests : CastmillUiTestContext
{
    private readonly Guid _campaignId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private readonly Guid _artifactId = Guid.Parse("a2000000-0000-0000-0000-000000000001");

    public WirePageTests() => SignInTestUser();

    [Fact]
    public async Task The_wire_loads_one_live_data_set_inside_the_workspace_shell()
    {
        StubWire();
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/wire");

        var app = Render<App>();
        await app.WaitForAssertionAsync(() =>
            Assert.NotNull(app.Find(".cm-run-show")));

        Assert.Contains("Front page", app.Markup, StringComparison.Ordinal);
        Assert.Contains("Ready story", app.Markup, StringComparison.Ordinal);
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/api/v1/campaigns/dashboard");
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/api/v1/schedule"
            && request.RequestUri.Query.Contains("from=", StringComparison.Ordinal)
            && request.RequestUri.Query.Contains("to=", StringComparison.Ordinal));
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath == "/api/v1/publish/readiness");
    }

    [Fact]
    public void Queue_actions_are_fixed_and_slot_opens_the_ignite_dialog()
    {
        var page = Render<RunOfShowView>(parameters => parameters.Add(component => component.Data, Board()));
        var queueCard = page.Find(".cm-run-show__queue-card");

        Assert.Equal(2, queueCard.Children.Length);
        // Card actions are icon-only: the accessible name carries the verb, not a word.
        Assert.Equal(new[] { "Edit", "Slot" }, queueCard.QuerySelectorAll(".cm-wire-icon-btn")
            .Select(button => button.GetAttribute("aria-label")));

        queueCard.QuerySelector("[aria-label='Slot']")!.Click();

        Assert.NotNull(page.Find("igc-dialog[open]"));
        Assert.NotNull(page.FindComponent<IgbDatePicker>());
        Assert.NotNull(page.FindComponent<IgbDateTimeInput>());

        var css = ReadWorkspaceFile("src/Castmill.UI/wwwroot/css/views.css");
        Assert.Contains("inline-size: 26px", Rule(css, ".cm-wire-icon-btn"), StringComparison.Ordinal);
        Assert.Contains(":focus-within", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyboard_slot_stages_locally_and_uses_the_schedule_client()
    {
        var scheduledAt = DateTimeOffset.UtcNow.AddDays(2);
        StubWire();
        Http.OnPost("api/v1/schedule", ScheduleResponse(
            Guid.Parse("a3000000-0000-0000-0000-000000000001"), scheduledAt, "Draft"));
        var page = Render<Wire>();
        await page.WaitForAssertionAsync(() => Assert.Single(page.FindAll(".cm-run-show__queue-card")));

        page.Find("[aria-label='Slot']").Click();
        var date = DateTime.Today.AddDays(2);
        await page.InvokeAsync(() =>
            page.FindComponent<IgbDatePicker>().Instance.ValueChanged.InvokeAsync(date));
        await page.InvokeAsync(() =>
            page.FindComponent<IgbDateTimeInput>().Instance.ValueChanged.InvokeAsync(date.AddHours(9)));
        page.FindAll("igc-button").Single(button => button.TextContent.Trim() == "Schedule").Click();

        await page.WaitForAssertionAsync(() =>
            Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule"));
        var request = Http.Bodies.Single(body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule");
        using var json = JsonDocument.Parse(request.Body);
        Assert.False(json.RootElement.GetProperty("pushToBroker").GetBoolean());
        Assert.Contains("staged locally", Services.GetRequiredService<Notifier>().Current.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Past_keyboard_slot_is_rejected_before_the_schedule_client()
    {
        StubWire();
        var page = Render<Wire>();
        await page.WaitForAssertionAsync(() => Assert.Single(page.FindAll(".cm-run-show__queue-card")));

        page.Find("[aria-label='Slot']").Click();
        var past = DateTime.Today.AddDays(-1).AddHours(9);
        await page.InvokeAsync(() =>
            page.FindComponent<IgbDatePicker>().Instance.ValueChanged.InvokeAsync(past.Date));
        await page.InvokeAsync(() =>
            page.FindComponent<IgbDateTimeInput>().Instance.ValueChanged.InvokeAsync(past));
        page.FindAll("igc-button").Single(button => button.TextContent.Trim() == "Schedule").Click();

        await page.WaitForAssertionAsync(() => Assert.Contains(
            Services.GetRequiredService<Notifier>().Current,
            message => message.Message == "Choose a future time."));
        Assert.DoesNotContain(Http.Bodies,
            body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule");
    }

    [Fact]
    public void Empty_days_collapse_titles_clamp_and_timeline_can_shrink()
    {
        var page = Render<RunOfShowView>(parameters => parameters.Add(component => component.Data, Board()));

        Assert.Contains("nothing scheduled — drop here", page.Find(".cm-run-show__day--empty").TextContent, StringComparison.Ordinal);
        Assert.Contains("SAT–SUN", page.Find(".cm-run-show__day--weekend").TextContent, StringComparison.Ordinal);
        Assert.All(page.FindAll(".cm-run-show__queue-title"), title =>
            Assert.Contains("cm-wire-clamp-2", title.ClassList));

        var css = ReadWorkspaceFile("src/Castmill.UI/wwwroot/css/views.css");
        Assert.Contains("display: -webkit-box", Rule(css, ".cm-wire-clamp-2"), StringComparison.Ordinal);
        Assert.Contains("-webkit-line-clamp: 2", Rule(css, ".cm-wire-clamp-2"), StringComparison.Ordinal);
        Assert.Contains("min-inline-size: 0", Rule(css, ".cm-run-show__timeline"), StringComparison.Ordinal);
        Assert.Contains("block-size: 36px", Rule(css, ".cm-run-show__day--empty"), StringComparison.Ordinal);
    }

    [Fact]
    public void Time_mapping_snaps_to_fifteen_minutes_and_overlap_stacks()
    {
        Assert.Equal(6 * 60, WireTime.Snap(0, new TimeOnly(6, 0), new TimeOnly(22, 0)));
        Assert.Equal(14 * 60, WireTime.Snap(0.5, new TimeOnly(6, 0), new TimeOnly(22, 0)));
        Assert.Equal(22 * 60, WireTime.Snap(1, new TimeOnly(6, 0), new TimeOnly(22, 0)));

        var items = new[]
        {
            Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000001"), 9, 0),
            Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000002"), 10, 0),
            Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000003"), 10, 30),
        };
        var levels = WireTime.StackLevels(items);

        Assert.Equal(0, levels[items[0].Id]);
        Assert.Equal(1, levels[items[1].Id]);
        Assert.Equal(0, levels[items[2].Id]);
        Assert.True(WireTime.IsSameSlot(items[0] with
        {
            ScheduledAtUtc = items[0].ScheduledAtUtc.AddSeconds(20),
        }, new DateOnly(2026, 8, 31), 9 * 60));

        var board = Board(items);
        var page = Render<RunOfShowView>(parameters => parameters.Add(component => component.Data, board));
        Assert.Equal(6, page.FindAll(".cm-run-show__day").Count);
        Assert.Contains("--cm-wire-rows: 2", page.Find("[data-wire-lane='2026-08-31']").GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pointer_drop_on_a_lane_maps_x_to_a_snapped_time()
    {
        WireSlotRequest? received = null;
        var page = Render<RunOfShowView>(parameters => parameters
            .Add(component => component.Data, Board())
            .Add(component => component.Slot, request => { received = request; }));

        await page.InvokeAsync(() => page.Instance.DropFromPointerAsync(
            $"q:{_artifactId}:linkedin", "lane", "2026-09-01", 0.5));

        Assert.NotNull(received);
        Assert.Equal(new DateOnly(2026, 9, 1), received!.Date);
        Assert.Equal(14 * 60, received.Minutes);
        Assert.NotNull(received.QueueItem);

        var script = ReadWorkspaceFile("src/Castmill.UI/wwwroot/js/castmill-wire.js");
        Assert.Contains("armPointerDrag", script, StringComparison.Ordinal);
        Assert.Contains("data-wire-lane", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_uses_delivery_contract_and_no_wire_view_renders_metrics()
    {
        var dashboard = Dashboard();
        var sentAt = new DateTimeOffset(2026, 8, 31, 9, 3, 12, TimeSpan.Zero);
        var sent = ScheduleResponse(
            Guid.Parse("a3000000-0000-0000-0000-000000000001"),
            new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero),
            "Sent",
            brokerRef: "broker-42",
            sentAtUtc: sentAt,
            permalink: "https://social.example/posts/42",
            metrics: new ScheduleMetricsResponse(
                Reach: 984321,
                Engagement: 87654,
                OpenRate: 0.375m,
                CompletionRate: 0.625m));
        var staged = ScheduleResponse(
            Guid.Parse("a3000000-0000-0000-0000-000000000002"),
            new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            "Draft");
        var data = WireBoardMapper.Create(
            new DateOnly(2026, 8, 31), 7, dashboard, [sent, staged],
            Readiness(false), [], new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        Assert.Equal(WireDeliveryStatus.Staged, data.Items.Single(item => item.Id == staged.Id).Status);
        var sentItem = data.Items.Single(item => item.Id == sent.Id);
        Assert.Equal(sentAt, sentItem.SentAtUtc);
        Assert.Equal("https://social.example/posts/42", sentItem.Permalink);
        Assert.Equal(984321, sentItem.Metrics!.Reach);
        Assert.Equal(0.625m, sentItem.Metrics.CompletionRate);

        var runOfShow = Render<RunOfShowView>(parameters => parameters.Add(component => component.Data, data));
        var pipeline = Render<PipelineView>(parameters => parameters.Add(component => component.Data, data));
        Assert.Contains("STAGED", runOfShow.Markup, StringComparison.Ordinal);
        Assert.Contains("No broker", runOfShow.Markup, StringComparison.Ordinal);
        Assert.Contains("Delivery receipt", runOfShow.Markup, StringComparison.Ordinal);
        Assert.Contains("Live post", runOfShow.Markup, StringComparison.Ordinal);
        foreach (var markup in new[] { runOfShow.Markup, pipeline.Markup })
        {
            Assert.DoesNotContain("reach", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("engagement", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("984321", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("87654", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pipeline_projects_four_status_columns_with_only_sent_delivery_facts()
    {
        var queued = Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000020"), 10, 15);
        var sentAt = new DateTimeOffset(2026, 8, 31, 12, 5, 0, TimeSpan.Zero);
        var sent = Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000021"), 12, 0) with
        {
            Status = WireDeliveryStatus.Sent,
            SentAtUtc = sentAt,
            BrokerRef = "broker-42",
            Permalink = "https://broker.example/posts/42",
        };
        var blocked = Scheduled(Guid.Parse("a3000000-0000-0000-0000-000000000022"), 14, 0) with
        {
            Status = WireDeliveryStatus.Blocked,
            BlockedReason = "A durable published URL is required.",
        };
        var page = Render<PipelineView>(parameters => parameters
            .Add(component => component.Data, Board([queued, sent, blocked])));

        Assert.Equal(
            new[] { "Ready", "Staged", "Sent", "Needs attention" },
            page.FindAll(".cm-pipeline__column-head h2").Select(heading => heading.TextContent.Trim()));
        Assert.Single(page.FindAll(".cm-pipeline__column--ready .cm-wire-queue-card"));
        Assert.Contains("NO DATE", page.Find(".cm-pipeline__column--ready").TextContent, StringComparison.Ordinal);
        Assert.Contains("MON 10:15", page.Find(".cm-pipeline__column--queued").TextContent, StringComparison.Ordinal);
        Assert.Contains("A durable published URL is required.", page.Find(".cm-pipeline__column--attention").TextContent, StringComparison.Ordinal);
        Assert.NotNull(page.Find(".cm-pipeline__column--attention [aria-label='Export clip']"));
        Assert.Contains("broker-42", page.Find(".cm-pipeline__column--sent").TextContent, StringComparison.Ordinal);
        Assert.Contains("Live post", page.Find(".cm-pipeline__column--sent").TextContent, StringComparison.Ordinal);
        Assert.Null(page.Find(".cm-pipeline__column--sent .cm-pipeline__body").GetAttribute("data-wire-col"));
        Assert.Equal(2, page.FindAll("[data-wire-col]").Count);
        Assert.All(page.FindAll(".cm-pipeline__title"), title => Assert.Contains("cm-wire-clamp-2", title.ClassList));
        Assert.DoesNotContain("reach", page.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("engagement", page.Markup, StringComparison.OrdinalIgnoreCase);

        var css = ReadWorkspaceFile("src/Castmill.UI/wwwroot/css/views.css");
        Assert.Contains("min-block-size: 120px", Rule(css, ".cm-pipeline__body"), StringComparison.Ordinal);
        Assert.Contains("position: absolute", Rule(css, ".cm-pipeline__actions"), StringComparison.Ordinal);
        Assert.Contains(".cm-pipeline__card:focus-within .cm-pipeline__actions", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task View_switch_uses_one_loaded_data_set_without_refetching()
    {
        StubWire();
        var page = Render<Wire>();
        await page.WaitForAssertionAsync(() => Assert.NotNull(page.Find(".cm-run-show__timeline")));

        // The page header matches the other workspace pages; the week strip carries the range.
        Assert.Equal("The Wire", page.Find("#cm-wire-title").TextContent.Trim());
        Assert.StartsWith("Week of", page.Find("#cm-wire-range").TextContent.Trim(), StringComparison.Ordinal);
        var requestsAfterLoad = Http.Requests.Count;

        page.FindAll("igc-toggle-button").Single(button => button.TextContent.Trim() == "Pipeline").Click();
        await page.WaitForAssertionAsync(() => Assert.NotNull(page.Find(".cm-pipeline")));
        Assert.Empty(page.FindAll(".cm-run-show__range"));

        page.FindAll("igc-toggle-button").Single(button => button.TextContent.Trim() == "Run of show").Click();
        await page.WaitForAssertionAsync(() => Assert.NotNull(page.Find(".cm-run-show__timeline")));
        Assert.NotNull(page.Find(".cm-run-show__range"));
        Assert.Equal(requestsAfterLoad, Http.Requests.Count);
    }

    [Fact]
    public async Task Pipeline_pointer_drop_on_queued_opens_the_shared_slot_dialog()
    {
        var page = Render<PipelineView>(parameters => parameters
            .Add(component => component.Data, Board()));

        await page.InvokeAsync(() => page.Instance.DropFromPointerAsync(
            $"q:{_artifactId}:linkedin", "column", "queued", 0));

        Assert.NotNull(page.Find("igc-dialog[open]"));
        Assert.NotNull(page.FindComponent<IgbDatePicker>());
        Assert.NotNull(page.FindComponent<IgbDateTimeInput>());
    }

    private void StubWire(bool brokerReady = false)
    {
        Http.OnGet("api/v1/campaigns/dashboard", Dashboard());
        Http.OnGet("api/v1/schedule", Array.Empty<ScheduleEntryResponse>());
        Http.OnGet("api/v1/publish/readiness", Readiness(brokerReady));
        if (brokerReady)
        {
            Http.OnGet("api/v1/publish/channels", new[] { new PublishChannel("linkedin", "LinkedIn", "LinkedIn") });
        }
    }

    private DashboardResponse Dashboard() => new(
        ReviewQueue: [],
        AgingDrafts: [],
        Campaigns: [new CampaignCounts(_campaignId, 1, 0, 0, 0)],
        EmptySlots: 0,
        CampaignsWithEmptySlots: 0,
        EmptySlotModels: [],
        FirstEmptySlotCampaign: null,
        ReadyToSchedule:
        [
            new DashboardArtifact(
                _campaignId, "Launch campaign", _artifactId, "linkedin", "Ready story", "Queued",
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)),
        ]);

    private static PublishReadinessResponse Readiness(bool ready) => new(
        BrokerConfigured: ready,
        CredentialStored: ready,
        Ready: ready,
        Detail: ready ? "Ready." : "No broker configured.",
        CanStageLocally: true,
        CanSchedule: ready);

    private ScheduleEntryResponse ScheduleResponse(
        Guid id,
        DateTimeOffset scheduledAt,
        string status,
        string? brokerRef = null,
        DateTimeOffset? sentAtUtc = null,
        string? permalink = null,
        ScheduleMetricsResponse? metrics = null) => new(
        id,
        _campaignId,
        _artifactId,
        "linkedin",
        brokerRef,
        "Ready story",
        null,
        scheduledAt,
        status,
        null,
        scheduledAt.AddMinutes(1),
        sentAtUtc,
        permalink,
        metrics);

    private WireBoardData Board(IReadOnlyList<WireScheduleItem>? items = null, int rangeDays = 7)
    {
        items ??= [];
        var start = new DateOnly(2026, 8, 31);
        var days = Enumerable.Range(0, rangeDays)
            .Select(offset => start.AddDays(offset))
            .Select(date => new WireDay(
                date,
                date == start,
                date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
                date == start ? items : []))
            .ToList();
        return new WireBoardData(
            start,
            rangeDays,
            TimeZoneInfo.Utc.Id,
            new TimeOnly(6, 0),
            new TimeOnly(22, 0),
            BrokerConfigured: false,
            [new WireQueueItem(_artifactId, _campaignId, "linkedin", "LinkedIn", "Ready story", "validators passed")],
            days);
    }

    private WireScheduleItem Scheduled(Guid id, int hour, int minute) => new(
        id,
        Guid.NewGuid(),
        _campaignId,
        "linkedin",
        "LinkedIn",
        $"Story at {hour}:{minute:00}",
        new DateTimeOffset(2026, 8, 31, hour, minute, 0, TimeSpan.Zero),
        TimeZoneInfo.Utc.Id,
        WireDeliveryStatus.Queued);

    private static string ReadWorkspaceFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Castmill.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing CSS rule {selector}");
        var end = css.IndexOf('}', start);
        Assert.True(end > start, $"Unclosed CSS rule {selector}");
        return css[start..end];
    }
}