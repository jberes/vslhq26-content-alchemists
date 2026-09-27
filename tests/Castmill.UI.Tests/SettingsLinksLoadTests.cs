using Bunit;
using Castmill.Core.Ai;
using Castmill.UI.Http;

namespace Castmill.UI.Tests;

/// <summary>
/// Links can be edited only once the saved links have arrived. A row added before then was
/// replaced by the load while still showing on screen, and Save wrote an empty list over the
/// workspace's links (seen in WebKit when the model status read was slow).
/// </summary>
public sealed class SettingsLinksLoadTests : CastmillUiTestContext
{
    public SettingsLinksLoadTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/settings/secrets", new List<SecretStatus>());
    }

    [Fact]
    public async Task Links_cannot_be_edited_until_the_saved_links_arrive_and_show_without_waiting_for_model_status()
    {
        var settings = Http.Gate(HttpMethod.Get, "api/v1/settings");
        var status = Http.Gate(HttpMethod.Get, "api/v1/ai/status");
        var view = Render<Castmill.UI.Pages.Settings>();
        await view.WaitForStateAsync(() => view.FindAll("button[role='tab']").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("button[role='tab']").Single(tab => tab.TextContent.StartsWith("Links", StringComparison.Ordinal)).ClickAsync();

        view.WaitForAssertion(() =>
        {
            Assert.True(Button(view, "+ Add a link").HasAttribute("disabled"));
            Assert.True(Button(view, "Save links").HasAttribute("disabled"));
            Assert.Contains("Loading your links…", view.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing yet", view.Markup, StringComparison.Ordinal);
        });

        settings.SetResult(StubHttpHandler.Json(new List<SettingRow>
        {
            new(SettingsClient.LinksKey, """[{"label":"Website","url":"https://example.com"}]"""),
        }));

        // The model status is still outstanding: the links must already be there and editable.
        view.WaitForAssertion(() =>
        {
            Assert.Equal("https://example.com", view.Find("input[aria-label='URL']").GetAttribute("value"));
            Assert.False(Button(view, "+ Add a link").HasAttribute("disabled"));
            Assert.False(Button(view, "Save links").HasAttribute("disabled"));
        });

        status.SetResult(StubHttpHandler.Json(new AiStatusResponse("config", true, new Dictionary<string, string>(), false, null, [])));
    }

    [Fact]
    public async Task Links_that_fail_to_load_stay_locked_so_save_cannot_overwrite_them()
    {
        Http.OnStatus(HttpMethod.Get, "api/v1/settings", System.Net.HttpStatusCode.InternalServerError);
        Http.OnGet("api/v1/ai/status", new AiStatusResponse("config", true, new Dictionary<string, string>(), false, null, []));
        var view = Render<Castmill.UI.Pages.Settings>();
        await view.WaitForStateAsync(() => view.FindAll("button[role='tab']").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("button[role='tab']").Single(tab => tab.TextContent.StartsWith("Links", StringComparison.Ordinal)).ClickAsync();

        view.WaitForAssertion(() =>
        {
            Assert.True(Button(view, "Save links").HasAttribute("disabled"));
            Assert.Contains("couldn't be loaded", view.Markup, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Put);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Castmill.UI.Pages.Settings> view, string text) =>
        view.FindAll("button").Single(b => b.TextContent.Trim() == text);
}
