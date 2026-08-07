using System.Net;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// A campaign that cannot load must say so. A pending migration made /preview return a 500
/// whose body was an HTML developer error page; interpreting that threw something the store
/// did not name, the exception escaped into the component lifecycle, and the page died
/// instead of showing a message — the user saw a flashing screen stuck on "Loading campaign…"
/// with nothing to act on.
/// </summary>
public sealed class CampaignLoadFailureTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("e1111111-1111-1111-1111-111111111111");

    public CampaignLoadFailureTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse>
        {
            new(CampaignId, Guid.NewGuid(), "Webinar campaign", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        });
    }

    [Fact]
    public async Task A_server_error_shows_a_message_rather_than_loading_forever()
    {
        Http.OnStatus(HttpMethod.Get, $"api/v1/campaigns/{CampaignId}/preview",
            HttpStatusCode.InternalServerError);

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForStateAsync(
            () => view.Markup.Contains("couldn't be loaded", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("Loading campaign", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact shape that broke it: a 500 carrying HTML rather than problem-details JSON,
    /// which is what ASP.NET's developer exception page returns.
    /// </summary>
    [Fact]
    public async Task A_five_hundred_carrying_html_still_ends_in_a_message()
    {
        Http.OnHtml($"api/v1/campaigns/{CampaignId}/preview", HttpStatusCode.InternalServerError,
            "<!DOCTYPE html><html><body>SqlException: Invalid column name 'ArtifactId'.</body></html>");

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForStateAsync(
            () => view.Markup.Contains("couldn't be loaded", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("Loading campaign", view.Markup, StringComparison.Ordinal);
    }

    /// <summary>An unreachable API is a message too, not a blank page.</summary>
    [Fact]
    public async Task An_unreachable_api_says_so()
    {
        Http.OnThrow(HttpMethod.Get, $"api/v1/campaigns/{CampaignId}/preview",
            () => new HttpRequestException("connection refused"));

        var view = Render<MillFloorView>(p => p.Add(c => c.CampaignId, CampaignId));

        await view.WaitForStateAsync(
            () => view.Markup.Contains("Couldn't reach the Castmill API", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }
}
