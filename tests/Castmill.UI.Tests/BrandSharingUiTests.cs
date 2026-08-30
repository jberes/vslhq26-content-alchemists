using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages;

namespace Castmill.UI.Tests;

public sealed class BrandSharingUiTests : CastmillUiTestContext
{
    private static readonly Guid BrandId = Guid.Parse("74444444-1111-1111-1111-111111111111");

    [Fact]
    public async Task Owner_sees_collaborator_management()
    {
        SignInTestUser();
        StubBrand(isOwner: true);
        Http.OnGet($"api/v1/brands/{BrandId}/collaborators",
            new List<BrandCollaboratorResponse>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), "collaborator@example.com",
                    "Collaborator", DateTimeOffset.UtcNow),
            });

        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));
        await view.WaitForStateAsync(
            () => view.FindAll("[role=tab]").Count == 5, TimeSpan.FromSeconds(5));

        await Assert.Single(view.FindAll("[role=tab]"), tab => tab.TextContent.Trim() == "Sharing")
            .ClickAsync();

        view.WaitForAssertion(() =>
        {
            Assert.Contains("collaborator@example.com", view.Markup, StringComparison.Ordinal);
            Assert.NotNull(view.Find("input[type=email]"));
            Assert.NotNull(view.Find("button[aria-label='Remove collaborator@example.com']"));
        });
    }

    [Fact]
    public void Collaborator_can_edit_but_cannot_manage_shares()
    {
        SignInTestUser();
        StubBrand(isOwner: false);

        var view = Render<BrandEditor>(parameters => parameters.Add(page => page.BrandId, BrandId));

        view.WaitForAssertion(() =>
        {
            Assert.Equal(4, view.FindAll("[role=tab]").Count);
            Assert.DoesNotContain(view.FindAll("[role=tab]"),
                tab => tab.TextContent.Trim() == "Sharing");
            Assert.Contains("Shared with you", view.Markup, StringComparison.Ordinal);
            Assert.NotNull(view.Find("button.cm-button"));
        });
    }

    private void StubBrand(bool isOwner)
    {
        Http.OnGet($"api/v1/brands/{BrandId}",
            new BrandProfileDetailResponse(
                BrandId, "Northwind", null, null, DateTimeOffset.UtcNow, isOwner));
        Http.OnGet($"api/v1/brands/{BrandId}/assets", new List<BrandAssetResponse>());
        Http.OnGet($"api/v1/brands/{BrandId}/templates", new List<BrandTemplateResponse>());
    }
}