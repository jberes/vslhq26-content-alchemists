using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class BrandAssetTypeTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("72222222-1111-1111-1111-111111111111");
    private static readonly Guid LinkId = Guid.Parse("72222222-1111-1111-1111-222222222222");
    private static readonly Guid AssetId = Guid.Parse("72222222-1111-1111-1111-333333333333");
    private static readonly Guid FaceLinkId = Guid.Parse("72222222-1111-1111-1111-444444444444");
    private static readonly Guid FaceAssetId = Guid.Parse("72222222-1111-1111-1111-555555555555");
    private static readonly Guid DestinationBrandId = Guid.Parse("72222222-1111-1111-1111-666666666666");

    public BrandAssetTypeTests()
    {
        SignInTestUser();
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(BrandId, "Northwind", null, null, DateTimeOffset.UtcNow));
        Http.OnGet("api/v1/brands", new List<BrandProfileDetailResponse>
        {
            new(BrandId, "Northwind", null, null, DateTimeOffset.UtcNow),
            new(DestinationBrandId, "Contoso", null, null, DateTimeOffset.UtcNow),
        });
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
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));

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

    [Fact]
    public async Task Clicking_an_asset_opens_the_original_in_a_full_size_viewer()
    {
        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));

        await view.FindAll("[role=tab]")[2].ClickAsync();
        await view.Find("button[aria-label='View Studio wall full size']").ClickAsync();

        var dialog = view.Find("[role=dialog][aria-labelledby='cm-brand-asset-viewer-title']");
        Assert.Contains("Studio wall", dialog.TextContent, StringComparison.Ordinal);
        Assert.Equal("https://public.example/wall.png", dialog.QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal("https://public.example/wall.png", dialog.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath.EndsWith(
                $"/blob/assets/{AssetId}/read-sas", StringComparison.Ordinal));

        await dialog.QuerySelector("button[aria-pressed='false']")!.ClickAsync();
        Assert.NotNull(view.Find(".cm-brand-asset-viewer__stage--actual"));
        Assert.Equal("Fit to window", view.Find("button[aria-pressed='true']").TextContent.Trim());

        await dialog.KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(view.FindAll(".cm-brand-asset-viewer"));
    }

    [Fact]
    public async Task Every_asset_has_copy_to_and_delete_actions_and_single_copy_works()
    {
        Http.OnPost($"api/v1/brands/{DestinationBrandId}/assets/copy",
            new BrandAssetCopyResult(1, 0));
        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]")[2].ClickAsync();

        Assert.Equal(2, view.FindAll("button[aria-label^='Copy '][aria-label$=' to another brand']").Count);
        Assert.Equal(2, view.FindAll("button[aria-label^='Delete '][aria-label$=' from this brand']").Count);
        Assert.Equal(2, view.FindAll(".cm-asset-card__media > .cm-asset-card__select").Count);
        Assert.Empty(view.FindAll(".cm-brand__asset-tools"));

        await view.Find("button[aria-label='Copy Studio wall to another brand']").ClickAsync();
        var dialog = view.Find("[role=dialog][aria-labelledby='cm-asset-copy-title']");
        Assert.Contains("Studio wall", dialog.TextContent, StringComparison.Ordinal);
        Assert.Null(dialog.QuerySelector(".cm-dialog-close"));
        await dialog.QuerySelector("select[aria-label='Copy Studio wall to brand']")!
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
            {
                Value = DestinationBrandId.ToString(),
            });
        await dialog.QuerySelector("button:last-child")!.ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(body => body.Method == HttpMethod.Post
                && body.Path.EndsWith($"brands/{DestinationBrandId}/assets/copy", StringComparison.Ordinal));
            Assert.Contains(LinkId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Copied Studio wall to Contoso", view.Markup, StringComparison.Ordinal);
            Assert.Empty(view.FindAll("[role=dialog][aria-labelledby='cm-asset-copy-title']"));
        });
    }

    [Fact]
    public async Task Assets_can_be_multi_selected_and_copied_to_another_brand()
    {
        Http.OnPost($"api/v1/brands/{DestinationBrandId}/assets/copy",
            new BrandAssetCopyResult(2, 0));
        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]")[2].ClickAsync();

        view.Find("input[aria-label='Select Studio wall']").Change(true);
        view.Find("input[aria-label='Select Host portrait']").Change(true);

        Assert.Equal(2, view.FindAll(".cm-asset-card--selected").Count);
        var toolbar = view.Find("[aria-label='Selected asset actions']");
        Assert.Contains("2 selected", toolbar.TextContent, StringComparison.Ordinal);
        await toolbar.QuerySelector("button[aria-label='Copy selected assets to another brand']")!.ClickAsync();
        var dialog = view.Find("[role=dialog][aria-labelledby='cm-bulk-asset-copy-title']");
        Assert.Null(dialog.QuerySelector(".cm-dialog-close"));
        await dialog.QuerySelector("select[aria-label='Destination brand for selected assets']")!
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
            {
                Value = DestinationBrandId.ToString(),
            });
        await view.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Copy 2 assets")
            .ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            var request = Http.Bodies.Single(body => body.Method == HttpMethod.Post
                && body.Path.EndsWith($"brands/{DestinationBrandId}/assets/copy", StringComparison.Ordinal));
            Assert.Contains($"\"sourceBrandId\":\"{BrandId}\"", request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LinkId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(FaceLinkId.ToString(), request.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Copied 2 assets to Contoso", view.Markup, StringComparison.Ordinal);
            Assert.Empty(view.FindAll(".cm-asset-card--selected"));
        });
    }

    [Fact]
    public async Task Copy_falls_back_to_the_existing_link_endpoint_during_server_rollout()
    {
        Http.OnStatus(HttpMethod.Post,
            $"api/v1/brands/{DestinationBrandId}/assets/copy", System.Net.HttpStatusCode.NotFound);
        Http.OnGet($"api/v1/brands/{DestinationBrandId}/assets", new List<BrandAssetResponse>());
        Http.OnPost($"api/v1/brands/{DestinationBrandId}/assets",
            new BrandAssetResponse(Guid.NewGuid(), DestinationBrandId, AssetId, "background",
                "Studio wall", "wall.png", "image/png", DateTimeOffset.UtcNow));

        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]")[2].ClickAsync();
        view.Find("input[aria-label='Select Studio wall']").Change(true);
        await view.Find("button[aria-label='Copy selected assets to another brand']").ClickAsync();
        view.Find("select[aria-label='Destination brand for selected assets']")
            .Change(DestinationBrandId.ToString());
        await view.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Copy 1 asset")
            .ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains(Http.Bodies, body => body.Method == HttpMethod.Post
                && body.Path.EndsWith($"brands/{DestinationBrandId}/assets", StringComparison.Ordinal)
                && body.Body.Contains(AssetId.ToString(), StringComparison.OrdinalIgnoreCase)
                && body.Body.Contains("\"kind\":\"background\"", StringComparison.Ordinal));
            Assert.Contains("Copied 1 asset to Contoso", view.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Selected_assets_can_be_deleted_from_the_contextual_toolbar()
    {
        var confirm = new AutoConfirm();
        Services.AddScoped<IConfirmService>(_ => confirm);
        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/brands/{BrandId}/assets/{LinkId}", System.Net.HttpStatusCode.NoContent);
        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/brands/{BrandId}/assets/{FaceLinkId}", System.Net.HttpStatusCode.NoContent);

        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]")[2].ClickAsync();
        view.Find("input[aria-label='Select Studio wall']").Change(true);
        view.Find("input[aria-label='Select Host portrait']").Change(true);

        await view.Find("button[aria-label='Delete selected assets from this brand']").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains(confirm.Requests, request => request.Destructive
                && request.Title.Contains("2 selected assets", StringComparison.Ordinal));
            Assert.Contains(Http.Requests, request => request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith(
                    $"brands/{BrandId}/assets/{LinkId}", StringComparison.Ordinal));
            Assert.Contains(Http.Requests, request => request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith(
                    $"brands/{BrandId}/assets/{FaceLinkId}", StringComparison.Ordinal));
            Assert.Empty(view.FindAll(".cm-asset-card"));
            Assert.Empty(view.FindAll(".cm-brand__asset-tools"));
        });
    }

    [Fact]
    public async Task Delete_is_explicit_confirmed_and_removes_the_asset_card()
    {
        var confirm = new AutoConfirm();
        Services.AddScoped<IConfirmService>(_ => confirm);
        Http.OnStatus(HttpMethod.Delete,
            $"api/v1/brands/{BrandId}/assets/{LinkId}", System.Net.HttpStatusCode.NoContent);

        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("[role=tab]")[2].ClickAsync();
        await view.Find("button[aria-label='Delete Studio wall from this brand']").ClickAsync();

        await view.WaitForAssertionAsync(() =>
        {
            Assert.Contains(confirm.Requests, request => request.Destructive
                && request.AcceptLabel == "Delete"
                && request.Message.Contains("uploaded file", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Http.Requests, request => request.Method == HttpMethod.Delete
                && request.RequestUri!.AbsolutePath.EndsWith(
                    $"brands/{BrandId}/assets/{LinkId}", StringComparison.Ordinal));
            Assert.Empty(view.FindAll("input[aria-label='Select Studio wall']"));
            Assert.Single(view.FindAll(".cm-asset-card"));
        });
    }

    private sealed class AutoConfirm : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }
}
