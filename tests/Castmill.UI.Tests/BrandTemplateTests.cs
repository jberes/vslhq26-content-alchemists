using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

public sealed class BrandTemplateTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("73333333-1111-1111-1111-111111111111");
    private static readonly Guid TemplateId = Guid.Parse("73333333-1111-1111-1111-222222222222");

    public BrandTemplateTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Northwind", null, null, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/templates",
            new List<BrandTemplateResponse>
            {
                new(TemplateId, BrandId, "youtube", "YouTube strategy",
                    "Use the approved semantic topic cluster.", true, DateTimeOffset.UtcNow),
            });
    }

    [Fact]
    public async Task Youtube_is_a_first_class_full_height_primary_template_editor()
    {
        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 5, TimeSpan.FromSeconds(5));

        await view.FindAll("[role=tab]")[3].ClickAsync();

        var option = Assert.Single(view.FindAll(".cm-brand__kind option"),
            item => item.GetAttribute("value") == "youtube");
        Assert.Equal("YouTube package", option.TextContent.Trim());
        var optionKinds = view.FindAll(".cm-brand__kind option")
            .Select(item => item.GetAttribute("value"))
            .ToList();
        Assert.Equal(ArtifactDisplay.BrandTemplateKinds.Select(kind => kind.Kind), optionKinds);
        Assert.Contains("clip-suggestions", optionKinds);
        Assert.DoesNotContain("campaign-summary", optionKinds);
        Assert.DoesNotContain("seo-brief", optionKinds);
        Assert.DoesNotContain("image-prompts", optionKinds);
        Assert.NotNull(view.Find(".cm-page--brand-template"));
        Assert.NotNull(view.Find(".cm-brand__section--template"));

        var editor = view.Find(".cm-brand__template-body");
        Assert.Equal("Template for YouTube package", editor.GetAttribute("aria-label"));
        Assert.Equal("20000", editor.GetAttribute("maxlength"));
        Assert.Contains("approved semantic topic cluster", editor.GetAttribute("value"), StringComparison.Ordinal);
        Assert.Contains("primary content brief sent to AI", view.Markup, StringComparison.Ordinal);
    }
}
