using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Renders <see cref="App"/> — the root component both shells mount — rather than a page
/// in isolation, so the Router, the layout, the auth guard and the NotFound wiring are all
/// exercised. Added because rendering only the page let a broken Router configuration reach
/// the browser: NotFoundPage requires a routable component, and a page missing @page throws
/// on first render in a way no page-level test can see.
/// </summary>
public sealed class AppRootTests : CastmillUiTestContext
{
    [Fact]
    public void Root_component_routes_the_default_url_to_the_front_page_inside_the_shell_layout()
    {
        SignInTestUser();
        Http.OnGet("api/v1/campaigns", new List<Castmill.Core.Resources.CampaignResponse>());

        var app = Render<App>();

        // Layout chrome plus page content: proves the Router resolved a route AND that the
        // DefaultLayout wrapped it. Phase F3 took "/" over from the F0 skeleton.
        Assert.Contains("Castmill", app.Markup, StringComparison.Ordinal);
        Assert.Contains("What needs you today", app.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_routes_render_the_not_found_page_rather_than_throwing()
    {
        SignInTestUser();

        var app = Render<App>();
        var navigation = Services.GetRequiredService<BunitNavigationManager>();

        // Navigation must happen on the renderer's synchronisation context.
        await app.InvokeAsync(() => navigation.NavigateTo("/no-such-route"));

        Assert.Contains("Nothing on this plate", app.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Style_guide_refuses_to_render_outside_a_development_build()
    {
        SignInTestUser();
        Shell.IsDevelopment = false;

        var app = Render<App>();
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        await app.InvokeAsync(() => navigation.NavigateTo("/dev/style-guide"));

        // The route resolves — it is not a 404 — but the page refuses to show itself.
        Assert.Contains("development-only surface", app.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Type scale", app.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signed_out_visitor_is_sent_to_sign_in_with_a_return_url()
    {
        // No SignInTestUser(): the token provider has nothing to restore.
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/settings/password");

        Render<App>();

        // The deep link survives the detour (ADR-F07), so signing in lands where the user
        // was headed rather than dumping them on the front page.
        Assert.Contains("sign-in", navigation.Uri, StringComparison.Ordinal);
        Assert.Contains("returnUrl=settings%2Fpassword", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sign_in_screen_renders_without_a_session()
    {
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/sign-in");

        var app = Render<App>();

        Assert.Contains("Sign in", app.Markup, StringComparison.Ordinal);
        Assert.Contains("Create an account", app.Markup, StringComparison.Ordinal);
    }
}
