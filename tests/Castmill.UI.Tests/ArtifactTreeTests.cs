using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Editor;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Focus mode's left rail is a vertical projection of the Mill Floor: the same lane names in
/// the same order, with non-interactive category bands and interactive content rows.
/// </summary>
public sealed class ArtifactTreeTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("41111111-1111-1111-1111-111111111111");
    private static readonly Guid BlogId = Guid.Parse("41111111-1111-1111-1111-222222222222");
    private static readonly Guid SocialId = Guid.Parse("41111111-1111-1111-1111-333333333333");
    private static readonly Guid YouTubeId = Guid.Parse("41111111-1111-1111-1111-444444444444");

    public ArtifactTreeTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });

        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview());

        // Focus auto-opens the first editable artifact, so its full fetch must answer.
        StubFullArtifact(BlogId, "blog", "Launch-day blog post");
        StubFullArtifact(SocialId, "social-x", "Launch thread", BlogId, ArtifactStatus.InReview);
        StubFullArtifact(YouTubeId, "youtube", "Launch video package", status: ArtifactStatus.Queued);
    }

    [Fact]
    public async Task The_rail_mirrors_mill_floor_lanes_with_clean_noninteractive_headers()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var categories = view.FindAll(".cm-focus__category").Select(header => header.TextContent).ToList();
        Assert.Collection(categories,
            category => Assert.Contains("YouTube", category, StringComparison.Ordinal),
            category => Assert.Contains("Blog", category, StringComparison.Ordinal),
            category => Assert.Contains("Social", category, StringComparison.Ordinal));
        Assert.DoesNotContain("Social set availability", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy SEO brief", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyword plan", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Deep SEO analysis", view.Markup, StringComparison.Ordinal);
        Assert.Empty(view.FindAll(".cm-focus__category button"));

        // Parenthesised, so the number reads as a category count, not part of its title.
        Assert.All(view.FindAll(".cm-tree__count"), c => Assert.Matches(@"^\(\d+\)$", c.TextContent.Trim()));
    }

    [Fact]
    public async Task Document_outline_lives_beside_the_editor_and_scrolls_independently()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.FindComponent<RichEditor>()));

        var headings = Enumerable.Range(1, 18)
            .Select(index => new EditorHeading((index % 3) + 1, $"Section {index}", index * 10))
            .ToArray();
        await view.InvokeAsync(() =>
            view.FindComponent<RichEditor>().Instance.NotifyHeadingsAsync(headings));

        await view.WaitForAssertionAsync(() =>
        {
            var outline = view.Find(".cm-focus > .cm-focus__document-outline");
            Assert.Equal("NAV", outline.QuerySelector(".cm-document-outline")!.TagName);
            Assert.NotNull(outline.QuerySelector(".cm-document-outline__list[data-cm-scroll]"));
            Assert.Equal(headings.Length, outline.QuerySelectorAll(".cm-document-outline__mark").Length);
            Assert.Equal(headings.Length, outline.QuerySelectorAll(".cm-document-outline__label").Length);
            Assert.Null(view.Find(".cm-focus__outline").QuerySelector(".cm-document-outline"));
            Assert.DoesNotContain("On this page", view.Find(".cm-focus__outline").TextContent,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Entering_focus_without_a_deep_link_selects_the_first_displayed_item()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch video package", view.Find(".cm-focus__manuscript h1").TextContent));

        var firstRow = view.FindAll(".cm-focus__list-item")[0];
        Assert.Contains("Launch video package", firstRow.TextContent, StringComparison.Ordinal);
        Assert.Equal("true", firstRow.GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Producer_can_expand_and_restore_without_hiding_the_manuscript()
    {
        var view = Render<FocusView>(parameters => parameters.Add(component => component.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-focus__producer")));

        var expand = view.Find("button[aria-label='Expand Producer']");
        await expand.ClickAsync();

        Assert.Contains("cm-focus--producer-wide", view.Find(".cm-focus").ClassList);
        Assert.NotNull(view.Find(".cm-focus__manuscript"));
        var restore = view.Find("button[aria-label='Restore Producer width']");
        await restore.ClickAsync();
        Assert.DoesNotContain("cm-focus--producer-wide", view.Find(".cm-focus").ClassList);
    }

    [Fact]
    public async Task Document_actions_are_grouped_at_the_content_edge_with_a_centered_download_caret()
    {
        var view = Render<FocusView>(parameters =>
            parameters.Add(component => component.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() =>
            Assert.NotNull(view.Find(".cm-focus__actions")));

        var status = view.Find(".cm-focus__status");
        var actions = status.QuerySelector(":scope > .cm-focus__actions");
        Assert.NotNull(actions);
        Assert.NotNull(actions.QuerySelector(".cm-focus__copy"));
        Assert.NotNull(actions.QuerySelector(".cm-focus__download-toggle"));
        Assert.NotNull(actions.QuerySelector(".cm-focus__download-caret"));
    }

    [Fact]
    public async Task Artifact_rows_show_their_review_state_with_text_and_color_modifier()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var blog = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch-day blog post", StringComparison.Ordinal));
        var social = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        var youtube = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch video package", StringComparison.Ordinal));

        Assert.Equal("Draft", blog.QuerySelector(".cm-status")!.TextContent.Trim());
        Assert.Equal("In review", social.QuerySelector(".cm-status--review")!.TextContent.Trim());
        Assert.Equal("Reviewed", youtube.QuerySelector(".cm-status--queued")!.TextContent.Trim());
        Assert.Contains("cm-status-edge--draft", blog.ClassList);
        Assert.Contains("cm-status-edge--review", social.ClassList);
        Assert.Contains("cm-status-edge--approved", youtube.ClassList);
    }

    [Fact]
    public async Task Marking_an_artifact_reviewed_updates_its_left_rail_state()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");
        await view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal))
            .ClickAsync();
        await view.WaitForAssertionAsync(() =>
            Assert.NotNull(view.FindAll("button")
                .SingleOrDefault(button => button.TextContent.Contains("Mark reviewed", StringComparison.Ordinal))));

        Http.OnPatch($"api/v1/campaigns/{CampaignId}/artifacts/{SocialId}/status",
            FullArtifact(SocialId, "social-x", "Launch thread", BlogId, ArtifactStatus.Queued) with { Version = 2 });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", Preview(ArtifactStatus.Queued));

        await view.FindAll("button")
            .Single(button => button.TextContent.Contains("Mark reviewed", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var social = view.FindAll(".cm-focus__list-item")
                .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
            Assert.Equal("Reviewed", social.QuerySelector(".cm-status--queued")!.TextContent.Trim());
        });
    }

    [Fact]
    public async Task Reviewed_content_can_be_scheduled_to_the_wire_with_a_date_and_time()
    {
        var scheduledAt = DateTimeOffset.Now.AddDays(2);
        Http.OnPost("api/v1/schedule", new ScheduleEntryResponse(
            Guid.NewGuid(), CampaignId, YouTubeId, "youtube", null,
            "Launch video package", null, scheduledAt, "Draft", null, DateTimeOffset.UtcNow));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() =>
            Assert.NotNull(view.Find("button[aria-controls='cm-focus-schedule-picker']")));

        await view.Find("button[aria-controls='cm-focus-schedule-picker']").ClickAsync();
        Assert.NotNull(view.Find(".cm-focus__schedule-picker input[type='datetime-local']"));
        await view.Find(".cm-focus__schedule-picker input[type='datetime-local']")
            .ChangeAsync(scheduledAt.LocalDateTime.ToString(
                "yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        await view.FindAll("button")
            .Single(button => button.TextContent.Contains("Schedule to The Wire", StringComparison.Ordinal))
            .ClickAsync();

        await view.WaitForAssertionAsync(() => Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Post
            && body.Path == "api/v1/schedule"
            && body.Body.Contains($"\"artifactId\":\"{YouTubeId}\"", StringComparison.Ordinal)
            && body.Body.Contains("\"pushToBroker\":false", StringComparison.Ordinal)));

        var scheduledButton = view.Find("button[aria-controls='cm-focus-schedule-picker']");
        Assert.Contains("Scheduled", scheduledButton.TextContent, StringComparison.Ordinal);
        Assert.Contains("cm-focus__schedule-button--scheduled", scheduledButton.ClassList);
        Assert.Equal("Scheduled", view.Find(".cm-focus__status > .cm-status--queued").TextContent.Trim());

        await scheduledButton.ClickAsync();
        Assert.Contains("Scheduled for", view.Find(".cm-focus__schedule-picker").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(scheduledAt.ToLocalTime().ToString(
                "MMMM d", System.Globalization.CultureInfo.CurrentCulture),
            view.Find(".cm-focus__scheduled-at").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Focus_rejects_an_ambiguous_local_schedule_time()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), eastern));
        var view = Render<FocusView>(parameters => parameters.Add(component => component.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() =>
            Assert.NotNull(view.Find("button[aria-controls='cm-focus-schedule-picker']")));

        await view.Find("button[aria-controls='cm-focus-schedule-picker']").ClickAsync();
        await view.Find(".cm-focus__schedule-picker input[type='datetime-local']")
            .ChangeAsync("2026-11-01T01:30");

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Choose an unambiguous local time.", view.Markup, StringComparison.Ordinal);
            var schedule = view.FindAll("button")
                .Single(button => button.TextContent.Contains("Schedule to The Wire", StringComparison.Ordinal));
            Assert.True(schedule.HasAttribute("disabled"));
        });
        Assert.DoesNotContain(Http.Bodies,
            body => body.Method == HttpMethod.Post && body.Path == "api/v1/schedule");
    }

    [Fact]
    public async Task Existing_wire_entry_restores_the_scheduled_focus_state()
    {
        var scheduledAt = DateTimeOffset.Now.AddDays(3).AddHours(2);
        Http.OnGet("api/v1/schedule", new List<ScheduleEntryResponse>
        {
            new(Guid.NewGuid(), CampaignId, YouTubeId, "youtube", null,
                "Launch video package", null, scheduledAt, "Draft", null, DateTimeOffset.UtcNow),
        });

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Equal("Scheduled", view.Find(".cm-focus__status > .cm-status--queued").TextContent.Trim());
            var button = view.Find("button[aria-controls='cm-focus-schedule-picker']");
            Assert.Contains("Scheduled", button.TextContent, StringComparison.Ordinal);
            Assert.Contains("cm-focus__schedule-button--scheduled", button.ClassList);
        });

        await view.Find("button[aria-controls='cm-focus-schedule-picker']").ClickAsync();
        Assert.Contains(scheduledAt.ToLocalTime().ToString(
                "MMMM d", System.Globalization.CultureInfo.CurrentCulture),
            view.Find(".cm-focus__scheduled-at").TextContent, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task Entering_focus_with_an_artifact_deep_link_opens_that_exact_item()
    {
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"campaigns/{CampaignId}/focus?artifact={SocialId}");
        var view = Render<FocusView>(parameters => parameters
            .Add(component => component.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch thread", view.Find(".cm-focus__manuscript h1").TextContent));

        var selected = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        Assert.Equal("true", selected.GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Opening_a_new_placeholder_starts_generation_after_the_editor_renders()
    {
        var placeholderId = Guid.Parse("41111111-1111-1111-1111-555555555555");
        var transcriptId = Guid.Parse("41111111-1111-1111-1111-666666666666");
        var placeholder = Artifact(placeholderId, "newsletter", "New newsletter") with
        {
            IsPlaceholder = true,
        };
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [Artifact(transcriptId, "transcript", "Source transcript"), placeholder],
            [], 0, 0));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{transcriptId}",
            FullArtifact(transcriptId, "transcript", "Source transcript"));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{placeholderId}",
            new ArtifactResponse(
                placeholderId, CampaignId, "newsletter", "New newsletter",
                """{"markdown":"","placeholder":true,"seedAngle":"Support the launch"}""",
                ArtifactStatus.Draft, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{placeholderId}/revisions",
            new List<ArtifactRevisionResponse>());
        var generation = Http.Gate(
            HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate/newsletter");
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"campaigns/{CampaignId}/focus?artifact={placeholderId}&generate=true");

        var view = Render<FocusView>(parameters => parameters
            .Add(component => component.CampaignId, CampaignId));

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Generating newsletter", view.Find(".cm-focus__regenerating").TextContent,
                StringComparison.Ordinal);
            var body = Http.Bodies.Single(item =>
                item.Path.EndsWith("generate/newsletter", StringComparison.Ordinal)).Body;
            Assert.Contains(placeholderId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Support the launch", body, StringComparison.Ordinal);
        });

        generation.SetResult(StubHttpHandler.Json(
            new RunItem("newsletter", false, placeholderId, "Generation stopped.", [], 100)));
        await view.WaitForAssertionAsync(() =>
            Assert.Empty(view.FindAll(".cm-focus__regenerating")));
    }

    [Fact]
    public async Task Clicking_a_content_row_changes_the_main_manuscript()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var social = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        await social.ClickAsync();

        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch thread", view.Find(".cm-focus__manuscript h1").TextContent));
        Assert.Equal("true", view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal))
            .GetAttribute("aria-current"));
    }

    [Fact]
    public async Task Slow_selection_keeps_the_current_document_and_announces_loading()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch video package", view.Find(".cm-focus__manuscript h1").TextContent));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Http.OnAsync(HttpMethod.Get,
            $"api/v1/campaigns/{CampaignId}/artifacts/{SocialId}", async () =>
            {
                await gate.Task;
                return StubHttpHandler.Json(FullArtifact(
                    SocialId, "social-x", "Launch thread", BlogId));
            });
        var social = view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        var click = social.ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains("Loading Launch thread", view.Find(".cm-focus__loading").TextContent,
                StringComparison.Ordinal);
            Assert.Equal("true", view.FindAll(".cm-focus__list-item")
                .Single(item => item.TextContent.Contains("Launch thread", StringComparison.Ordinal))
                .GetAttribute("aria-busy"));
            Assert.Equal("Launch video package", view.Find(".cm-focus__manuscript h1").TextContent);
        });

        gate.SetResult();
        await click;

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Empty(view.FindAll(".cm-focus__loading"));
            Assert.Equal("Launch thread", view.Find(".cm-focus__manuscript h1").TextContent);
        });
    }

    [Fact]
    public async Task Deleting_from_the_tree_confirms_calls_the_endpoint_and_reloads()
    {
        var confirm = new AutoConfirm(accept: true);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/campaigns/{CampaignId}/artifacts/{SocialId}", System.Net.HttpStatusCode.NoContent);

        var socialRow = view.FindAll(".cm-tree__row")
            .First(r => r.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        Assert.NotNull(socialRow.QuerySelector(".cm-tree__delete svg.cm-icon"));
        Assert.DoesNotContain("🗑", socialRow.QuerySelector(".cm-tree__delete")!.TextContent,
            StringComparison.Ordinal);
        await socialRow.QuerySelector(".cm-tree__delete")!.ClickAsync();

        Assert.Single(confirm.Requests);
        Assert.Contains(Http.Requests, r =>
            r.Method == HttpMethod.Delete
            && r.RequestUri!.AbsolutePath.EndsWith($"artifacts/{SocialId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_the_tree_delete_leaves_the_artifact_alone()
    {
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: false));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Launch thread");

        var socialRow = view.FindAll(".cm-tree__row")
            .First(r => r.TextContent.Contains("Launch thread", StringComparison.Ordinal));
        await socialRow.QuerySelector(".cm-tree__delete")!.ClickAsync();

        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Contains("Launch thread", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Producer_only_shows_images_owned_by_the_open_content_item()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(),
            [Artifact(BlogId, "blog", "Launch-day blog post"), Artifact(SocialId, "social-x", "Launch thread", BlogId)],
            [Slot("blog-hero", BlogId), Slot("social-card", SocialId)], 0, 2));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await WaitForTextAsync(view, "Images for this content item");

        var producer = view.Find(".cm-focus__producer").TextContent;
        Assert.Contains("Blog header", producer, StringComparison.Ordinal);
        Assert.DoesNotContain("Social card", producer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_icon_writes_plain_text_and_formatted_html()
    {
        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.FindAll(".cm-focus__list-item")
            .Single(item => item.TextContent.Contains("Launch-day blog post", StringComparison.Ordinal))
            .ClickAsync();
        await view.WaitForAssertionAsync(() =>
            Assert.Equal("Launch-day blog post", view.Find(".cm-focus__manuscript h1").TextContent));

        await view.Find("button[aria-label='Copy formatted']").ClickAsync();

        var copied = Assert.Single(Clipboard.FormattedCopies);
        Assert.Equal("Hello.", copied.Text);
        Assert.Contains("<p>Hello.</p>", copied.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Producer_shows_the_keeper_for_the_open_content_item_before_placement()
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [Artifact(BlogId, "blog", "Launch-day blog post")],
            [Slot("blog-hero", BlogId, keeperUrl: "https://public.example/keeper.webp")], 0, 1));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-plan__slot-image").Count == 1,
            TimeSpan.FromSeconds(5));

        Assert.StartsWith("https://public.example/keeper.webp",
            view.Find(".cm-plan__slot-image").GetAttribute("src"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_youtube_keeper_replaces_an_empty_artifact_owned_slot()
    {
        var keeperId = Guid.NewGuid();
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [Artifact(YouTubeId, "youtube", "Launch video package")],
            [
                Slot("youtube-thumbnail", YouTubeId),
                Slot("youtube-thumbnail", null, keeperUrl: "https://public.example/legacy-keeper.webp",
                    keeperVariantId: keeperId),
            ], 0, 2));

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-plan__slot-image").Count == 1,
            TimeSpan.FromSeconds(5));

        Assert.StartsWith("https://public.example/legacy-keeper.webp",
            view.Find(".cm-plan__slot-image").GetAttribute("src"), StringComparison.Ordinal);
        Assert.Contains("KEEPER", view.Find(".cm-plan__slot").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeper_hover_download_saves_the_full_resolution_image()
    {
        var slotId = Guid.NewGuid();
        var keeperId = Guid.NewGuid();
        var downloader = new RecordingDownloader();
        Services.AddSingleton<IFileDownloader>(downloader);
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Campaign(), [Artifact(YouTubeId, "youtube", "Launch video package")],
            [Slot("youtube-thumbnail", YouTubeId,
                keeperUrl: "https://public.example/keeper.webp",
                slotId: slotId, keeperVariantId: keeperId)], 0, 1));
        Http.OnFile(
            $"api/v1/campaigns/{CampaignId}/image-slots/{slotId}/variants/{keeperId}/download",
            "castmill-keeper.webp", "image/webp", [4, 5, 6]);

        var view = Render<FocusView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(() => view.FindAll(".cm-plan__slot-download").Count == 1,
            TimeSpan.FromSeconds(5));
        await view.Find("button[aria-label='Download YouTube thumbnail']").ClickAsync();

        var saved = Assert.Single(downloader.Saved);
        Assert.Equal("castmill-keeper.webp", saved.FileName);
        Assert.Equal([4, 5, 6], saved.Bytes);
    }

    // ---- helpers ---------------------------------------------------------------

    private sealed class AutoConfirm(bool accept) : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(accept);
        }
    }

    private void StubFullArtifact(
        Guid id, string kind, string title, Guid? parentArtifactId = null,
        string status = ArtifactStatus.Draft)
    {
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{id}",
            FullArtifact(id, kind, title, parentArtifactId, status));
        Http.OnGet($"api/v1/campaigns/{CampaignId}/artifacts/{id}/revisions",
            new List<ArtifactRevisionResponse>());
    }

    private static ArtifactResponse FullArtifact(
        Guid id, string kind, string title, Guid? parentArtifactId = null,
        string status = ArtifactStatus.Draft) =>
        new(id, CampaignId, kind, title, """{"content":{"markdown":"Hello."}}""",
            status, 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            ParentArtifactId: parentArtifactId);

    private static CampaignPreview Preview(string socialStatus = ArtifactStatus.InReview) => new(
        Campaign(),
        [
            Artifact(BlogId, "blog", "Launch-day blog post", status: ArtifactStatus.Draft),
            Artifact(SocialId, "social-x", "Launch thread", BlogId, socialStatus),
            Artifact(YouTubeId, "youtube", "Launch video package", status: ArtifactStatus.Queued),
            Artifact(Guid.NewGuid(), "seo-brief", "Legacy SEO brief"),
            Artifact(Guid.NewGuid(), "seo-keyword-plan", "Keyword plan"),
            Artifact(Guid.NewGuid(), "seo-report", "Deep SEO analysis"),
        ],
        [], 0, 0);

    private static async Task WaitForTextAsync(IRenderedComponent<FocusView> view, string text)
    {
        try
        {
            await view.WaitForStateAsync(
                () => view.Markup.Contains(text, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"'{text}' never rendered ({ex.GetType().Name}). Markup was:{Environment.NewLine}{view.Markup}");
        }
    }

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private static ArtifactPreviewResponse Artifact(
        Guid id, string kind, string title, Guid? parentArtifactId = null,
        string status = ArtifactStatus.Draft) =>
        new(id, CampaignId, kind, title, status, 1,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
            ParentArtifactId: parentArtifactId);

    private static ImageSlotResponse Slot(
        string kind, Guid? artifactId, string? publishedUrl = null, string? keeperUrl = null,
        Guid? slotId = null, Guid? keeperVariantId = null) => new(
        slotId ?? Guid.NewGuid(), CampaignId, kind, 1200, 675, null, "foundry", null, null, true,
        publishedUrl is null ? "Empty" : "Filled", publishedUrl, publishedUrl,
        DateTimeOffset.UtcNow, ArtifactId: artifactId, KeeperThumbUrl: keeperUrl,
        KeeperVariantId: keeperVariantId);

    private sealed class RecordingDownloader : IFileDownloader
    {
        public List<DownloadedFile> Saved { get; } = [];

        public Task SaveAsync(DownloadedFile file)
        {
            Saved.Add(file);
            return Task.CompletedTask;
        }
    }
}
