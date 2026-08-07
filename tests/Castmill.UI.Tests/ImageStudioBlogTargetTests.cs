using Bunit;
using Castmill.Core;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// A campaign can hold more than one blog — every regenerate prints a new take as its own
/// artifact, and "add another" makes that routine. Placing an image rewrites the target blog's
/// <c>![stub:kind]()</c> marker, so it has to rewrite the blog the user meant. It used to take
/// the first artifact of kind "blog", which is the OLDEST one because the preview is ordered by
/// kind then creation: placing an image for the second blog silently patched the first.
/// </summary>
public sealed class ImageStudioBlogTargetTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("81111111-1111-1111-1111-111111111111");
    private static readonly Guid SlotId = Guid.Parse("81111111-1111-1111-1111-222222222222");
    private static readonly Guid TakeId = Guid.Parse("81111111-1111-1111-1111-333333333333");
    private static readonly Guid FirstBlogId = Guid.Parse("81111111-1111-1111-1111-444444444444");
    private static readonly Guid SecondBlogId = Guid.Parse("81111111-1111-1111-1111-555555555555");

    public ImageStudioBlogTargetTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });

        Http.OnGet("api/v1/ai/status", new Castmill.Core.Ai.AiStatusResponse(
            "config", true, new Dictionary<string, string>(), false, null,
            [new Castmill.Core.Ai.ImageProviderReadiness("foundry", true, null)]));

        Http.OnGet($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/variants",
            new List<ImageVariantResponse> { Take() });

        Http.OnPost($"api/v1/campaigns/{CampaignId}/image-slots/{SlotId}/place",
            new PlaceResult(Slot("Filled"), null, null));
    }

    /// <summary>One blog is unambiguous: no picker, and the placement targets it.</summary>
    [Fact]
    public async Task With_one_blog_no_picker_is_shown_and_that_blog_is_the_target()
    {
        StubPreview(Blog(FirstBlogId, "The first blog"));

        var view = await OpenTakeAsync();

        Assert.Empty(view.FindAll("select[aria-label='Blog to place this image into']"));

        await PlaceAsync(view);
        Assert.Contains(FirstBlogId.ToString(), LastPlaceBody(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The regression. Two blogs, the user picks the second, and the second is what gets
    /// patched — not whichever one happens to be oldest.
    /// </summary>
    [Fact]
    public async Task With_two_blogs_the_chosen_blog_is_the_one_that_gets_patched()
    {
        StubPreview(Blog(FirstBlogId, "The first blog"), Blog(SecondBlogId, "The second blog"));

        var view = await OpenTakeAsync();

        var picker = view.Find("select[aria-label='Blog to place this image into']");
        await picker.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
        {
            Value = SecondBlogId.ToString(),
        });

        await PlaceAsync(view);

        var body = LastPlaceBody();
        Assert.Contains(SecondBlogId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FirstBlogId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A thumbnail has no stub in any blog, so it never asks which blog to patch.</summary>
    [Fact]
    public async Task A_non_blog_slot_never_asks_which_blog()
    {
        _slotKind = "youtube-thumbnail";
        StubPreview(Blog(FirstBlogId, "The first blog"), Blog(SecondBlogId, "The second blog"));

        var view = await OpenTakeAsync();

        Assert.Empty(view.FindAll("select[aria-label='Blog to place this image into']"));
    }

    /// <summary>
    /// Slots reserved for a specific blog carry its id, so they answer for themselves and no
    /// picker is needed — even with several blogs in the campaign. That is the durable fix;
    /// the picker only covers legacy slots reserved before slots were artifact-scoped.
    /// </summary>
    [Fact]
    public async Task A_slot_that_belongs_to_a_blog_places_into_that_blog_with_no_picker()
    {
        _slotArtifactId = SecondBlogId;
        StubPreview(Blog(FirstBlogId, "The first blog"), Blog(SecondBlogId, "The second blog"));

        var view = await OpenTakeAsync();

        Assert.Empty(view.FindAll("select[aria-label='Blog to place this image into']"));

        await PlaceAsync(view);

        var body = LastPlaceBody();
        Assert.Contains(SecondBlogId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FirstBlogId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<IRenderedComponent<ImageStudioView>> OpenTakeAsync()
    {
        var view = Render<ImageStudioView>(p => p.Add(c => c.CampaignId, CampaignId));
        await view.WaitForStateAsync(
            () => view.FindAll(".cm-gallery__tile").Count == 1, TimeSpan.FromSeconds(5));
        await view.Find(".cm-gallery__tile").ClickAsync();
        return view;
    }

    private static async Task PlaceAsync(IRenderedComponent<ImageStudioView> view)
    {
        var place = view.FindAll(".cm-lightbox button")
            .First(b => b.TextContent.Contains("Place in slot", StringComparison.Ordinal));
        await place.ClickAsync();
    }

    private string LastPlaceBody() =>
        Http.Bodies.Last(b => b.Method == HttpMethod.Post
                              && b.Path.EndsWith("/place", StringComparison.Ordinal)).Body;

    private string _slotKind = "blog-header";
    private Guid? _slotArtifactId;

    private void StubPreview(params ArtifactPreviewResponse[] artifacts) =>
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [.. artifacts], [Slot("Empty")], 0, 6));

    private ImageSlotResponse Slot(string state) => new(
        SlotId, CampaignId, _slotKind, 1600, 900, "a hero image", "gpt-image-2",
        null, null, true, state,
        state == "Filled" ? "https://public.example/x.webp" : null,
        state == "Filled" ? "https://public.example/x.webp" : null,
        DateTimeOffset.UtcNow,
        HeadlineBackground: null,
        ArtifactId: _slotArtifactId);

    private static ImageVariantResponse Take() => new(
        TakeId, SlotId,
        "https://public.example/campaigns/x/images/blog-header/variants/1-full.webp",
        "https://public.example/campaigns/x/images/blog-header/variants/thumbs/1.webp",
        "gpt-image-2", "Candidate", null, null, 1600, 900, DateTimeOffset.UtcNow);

    /// <summary>Created times are staggered so "oldest first" is unambiguous in the test.</summary>
    private static ArtifactPreviewResponse Blog(Guid id, string title) =>
        new(id, CampaignId, "blog", title, ArtifactStatus.Draft, 1,
            id == FirstBlogId ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow);

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
