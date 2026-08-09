using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

public sealed class BrandAssetTypeTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("72222222-1111-1111-1111-111111111111");
    private static readonly Guid LinkId = Guid.Parse("72222222-1111-1111-1111-222222222222");
    private static readonly Guid AssetId = Guid.Parse("72222222-1111-1111-1111-333333333333");
    private static readonly Guid FaceLinkId = Guid.Parse("72222222-1111-1111-1111-444444444444");
    private static readonly Guid FaceAssetId = Guid.Parse("72222222-1111-1111-1111-555555555555");

    public BrandAssetTypeTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Northwind", null, null, DateTimeOffset.UtcNow));
        Http.OnGet($"api/v1/brands/{BrandId}/assets",
            new List<BrandAssetResponse>
            {
                new(LinkId, BrandId, AssetId, "background", "Studio wall",
                    "wall.png", "image/png", DateTimeOffset.UtcNow),
                new(FaceLinkId, BrandId, FaceAssetId, "face", "Host portrait",
                    "host.png", "image/png", DateTimeOffset.UtcNow),
            });
        Http.OnGet($"api/v1/brands/{BrandId}/templates", new List<BrandTemplateResponse>());
        Http.OnGet($"api/v1/blob/assets/{AssetId}/read-sas",
            new ReadSas("https://public.example/wall.png"));
        Http.OnGet($"api/v1/blob/assets/{FaceAssetId}/read-sas",
            new ReadSas("https://public.example/host.png"));
        Http.OnPatch($"api/v1/brands/{BrandId}/assets/{LinkId}/kind",
            new BrandAssetResponse(LinkId, BrandId, AssetId, "face", "Studio wall",
                "wall.png", "image/png", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Asset_type_can_be_changed_in_place_and_the_card_moves_groups()
    {
        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 4, TimeSpan.FromSeconds(5));

        await view.FindAll("[role=tab]")[2].ClickAsync();
        var type = view.Find("select[aria-label='Type for Studio wall']");
        Assert.Equal("background", type.GetAttribute("value"));

        await type.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "face" });

        Assert.Contains(Http.Bodies, body =>
            body.Method == HttpMethod.Patch
            && body.Path.EndsWith($"brands/{BrandId}/assets/{LinkId}/kind", StringComparison.Ordinal)
            && body.Body.Contains("\"kind\":\"face\"", StringComparison.Ordinal));
        Assert.Contains("Face · 2", view.Markup, StringComparison.Ordinal);
        Assert.Equal("face", view.Find("select[aria-label='Type for Studio wall']").GetAttribute("value"));
        Assert.Equal("face", view.Find("select[aria-label='Type for Host portrait']").GetAttribute("value"));
    }
}
