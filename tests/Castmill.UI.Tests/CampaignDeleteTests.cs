using System.Net;
using Bunit;
using Castmill.Core.Resources;
using Castmill.UI.Design;
using Castmill.UI.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Deleting a campaign from the index destroys every artifact, image and the uploaded source
/// media irreversibly, and a card grid is exactly where a stray click lands. The page must
/// therefore ask for a STRONG confirm — one carrying the campaign's name as the phrase the
/// producer has to type — and must not call the API unless it is accepted.
///
/// The dialog's own phrase gate is covered by <see cref="ConfirmHostPhraseTests"/>; here the
/// confirm service is stubbed, because <c>ConfirmHost</c> is mounted by the shell layout and
/// never renders inside a page-only render.
/// </summary>
public sealed class CampaignDeleteTests : CastmillUiTestContext
{
    private static readonly Guid Doomed = Guid.Parse("c1111111-1111-1111-1111-111111111111");

    public CampaignDeleteTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<CampaignResponse> { Campaign(Doomed, "Reveal - LDX3 Demo") });
        Http.OnGet("api/v1/campaigns/dashboard", new DashboardResponse(
            [], [],
            [new CampaignCounts(Doomed, 11, 2, 14, 14, null, Draft: 5, Reviewed: 3, Published: 0)],
            EmptySlots: 12, CampaignsWithEmptySlots: 1, EmptySlotModels: [], FirstEmptySlotCampaign: Doomed));
    }

    [Fact]
    public async Task The_delete_control_is_a_sibling_of_the_card_not_a_nested_button()
    {
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: false));
        var view = Render<CampaignsIndex>();
        await view.WaitForAssertionAsync(() =>
            Assert.NotNull(view.Find("button.cm-campaign-card__delete")));

        // A button inside a button is invalid HTML and swallows the inner click.
        Assert.Empty(view.FindAll(".cm-campaign-card button.cm-campaign-card__delete"));
    }

    [Fact]
    public async Task The_prompt_requires_the_campaign_name_and_names_the_real_counts()
    {
        var confirm = new AutoConfirm(accept: false);
        Services.AddScoped<IConfirmService>(_ => confirm);

        var view = Render<CampaignsIndex>();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find("button.cm-campaign-card__delete")));
        await view.Find("button.cm-campaign-card__delete").ClickAsync(new());

        await view.WaitForAssertionAsync(() => Assert.Single(confirm.Requests));
        var request = confirm.Requests[0];
        Assert.True(request.Destructive);
        // The phrase gate is the whole point: without it this is a one-click data loss.
        Assert.Equal("Reveal - LDX3 Demo", request.RequirePhrase);
        Assert.Contains("11 artifacts", request.Message, StringComparison.Ordinal);
        Assert.Contains("source media", request.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_prompt_deletes_nothing()
    {
        var deleted = false;
        Http.OnAsync(HttpMethod.Delete, $"api/v1/campaigns/{Doomed}", () =>
        {
            deleted = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: false));

        var view = Render<CampaignsIndex>();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find("button.cm-campaign-card__delete")));
        await view.Find("button.cm-campaign-card__delete").ClickAsync(new());

        Assert.False(deleted);
        Assert.NotEmpty(view.FindAll(".cm-campaign-card"));
    }

    [Fact]
    public async Task Accepting_deletes_the_campaign_and_it_leaves_the_grid()
    {
        var deleted = false;
        Http.OnAsync(HttpMethod.Delete, $"api/v1/campaigns/{Doomed}", () =>
        {
            deleted = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        Services.AddScoped<IConfirmService>(_ => new AutoConfirm(accept: true));

        var view = Render<CampaignsIndex>();
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find("button.cm-campaign-card__delete")));
        await view.Find("button.cm-campaign-card__delete").ClickAsync(new());

        await view.WaitForAssertionAsync(() => Assert.True(deleted));
        // Removed from the list without a reload, so the grid cannot show a campaign that is gone.
        await view.WaitForAssertionAsync(() => Assert.Empty(view.FindAll(".cm-campaign-card")));
    }

    private static CampaignResponse Campaign(Guid id, string name) =>
        new(id, Guid.NewGuid(), name, null,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow);

    private sealed class AutoConfirm(bool accept) : IConfirmService
    {
        public List<ConfirmRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(ConfirmRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(accept);
        }
    }
}

/// <summary>
/// The strong-confirm gate itself, rendered directly. A destructive prompt whose accept button
/// is live on open is one reflex click from deleting a campaign, so the button stays disabled
/// until the required phrase is typed exactly.
/// </summary>
public sealed class ConfirmHostPhraseTests : CastmillUiTestContext
{
    [Fact]
    public async Task Accept_stays_disabled_until_the_phrase_matches_exactly()
    {
        var confirm = new ConfirmService();
        Services.AddScoped(_ => confirm);
        var view = Render<ConfirmHost>();

        var answer = confirm.ConfirmAsync(new ConfirmRequest(
            "Delete Reveal - LDX3 Demo?", "This cannot be undone.",
            AcceptLabel: "Delete campaign", Destructive: true,
            RequirePhrase: "Reveal - LDX3 Demo"));

        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-modal__phrase")));
        Assert.True(view.Find("button.cm-button--danger").HasAttribute("disabled"));

        // A prefix is not enough.
        await view.Find(".cm-modal__phrase input").InputAsync(new() { Value = "Reveal - LDX3" });
        Assert.True(view.Find("button.cm-button--danger").HasAttribute("disabled"));

        // Case matters — the point is deliberate transcription.
        await view.Find(".cm-modal__phrase input").InputAsync(new() { Value = "reveal - ldx3 demo" });
        Assert.True(view.Find("button.cm-button--danger").HasAttribute("disabled"));

        await view.Find(".cm-modal__phrase input").InputAsync(new() { Value = "Reveal - LDX3 Demo" });
        var armed = view.Find("button.cm-button--danger");
        Assert.False(armed.HasAttribute("disabled"));
        await armed.ClickAsync(new());

        Assert.True(await answer);
    }

    /// <summary>An ordinary prompt has no phrase field and accepts immediately.</summary>
    [Fact]
    public async Task A_prompt_with_no_phrase_is_unchanged()
    {
        var confirm = new ConfirmService();
        Services.AddScoped(_ => confirm);
        var view = Render<ConfirmHost>();

        var answer = confirm.ConfirmAsync(new ConfirmRequest("Discard?", "Unsaved edits are lost."));

        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-modal__panel")));
        Assert.Empty(view.FindAll(".cm-modal__phrase"));
        var buttons = view.FindAll("button.cm-button");
        await buttons[^1].ClickAsync(new());

        Assert.True(await answer);
    }

    /// <summary>
    /// A typed phrase must never carry into the next prompt, or the second delete would open
    /// already armed.
    /// </summary>
    [Fact]
    public async Task The_typed_phrase_does_not_survive_into_the_next_prompt()
    {
        var confirm = new ConfirmService();
        Services.AddScoped(_ => confirm);
        var view = Render<ConfirmHost>();

        var first = confirm.ConfirmAsync(new ConfirmRequest(
            "Delete A?", "Gone.", Destructive: true, RequirePhrase: "A"));
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-modal__phrase")));
        await view.Find(".cm-modal__phrase input").InputAsync(new() { Value = "A" });
        await view.Find("button.cm-button--danger").ClickAsync(new());
        Assert.True(await first);

        var second = confirm.ConfirmAsync(new ConfirmRequest(
            "Delete A again?", "Gone.", Destructive: true, RequirePhrase: "A"));
        await view.WaitForAssertionAsync(() => Assert.NotNull(view.Find(".cm-modal__phrase")));
        Assert.True(view.Find("button.cm-button--danger").HasAttribute("disabled"));

        await view.Find("button.cm-button--quiet").ClickAsync(new());
        Assert.False(await second);
    }
}
