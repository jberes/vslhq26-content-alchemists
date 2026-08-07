using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.Layout;
using Castmill.UI.State;
using Microsoft.AspNetCore.Components;

namespace Castmill.UI.Tests;

/// <summary>
/// A campaign view that throws must show a message. Blazor tears down the entire render tree
/// on an unhandled component exception, so without a boundary the user gets a blank screen
/// with no error, no explanation and no way back — which is exactly what was reported.
/// </summary>
public sealed class CampaignErrorBoundaryTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("b3333333-3333-3333-3333-333333333333");

    public CampaignErrorBoundaryTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign() });
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview",
            new CampaignPreview(Campaign(), [], [], 0, 0));
    }

    [Fact]
    public async Task A_view_that_throws_renders_an_error_instead_of_nothing()
    {
        var view = Render<CampaignShell>(p => p
            .Add(c => c.CampaignId, CampaignId)
            .Add(c => c.View, CampaignView.MillFloor)
            .Add(c => c.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<Exploding>(0);
                builder.CloseComponent();
            })));

        await view.WaitForStateAsync(
            () => view.Markup.Contains("hit an error", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));

        // The failure is named rather than swallowed, and there is a way forward.
        Assert.Contains("the view blew up", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(view.FindAll("button"),
            b => b.TextContent.Contains("Try again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_healthy_view_renders_normally_through_the_boundary()
    {
        var view = Render<CampaignShell>(p => p
            .Add(c => c.CampaignId, CampaignId)
            .Add(c => c.View, CampaignView.MillFloor)
            .Add(c => c.ChildContent, (RenderFragment)(builder =>
                builder.AddMarkupContent(0, "<p>the real view</p>"))));

        await view.WaitForStateAsync(
            () => view.Markup.Contains("the real view", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("hit an error", view.Markup, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Exploding : ComponentBase
    {
        protected override void OnParametersSet() =>
            throw new InvalidOperationException("the view blew up");
    }

    private static CampaignResponse Campaign() =>
        new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);
}
