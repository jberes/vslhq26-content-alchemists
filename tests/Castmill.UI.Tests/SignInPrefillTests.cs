using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// The sign-in form prefills the seeded demo account in development so nobody types the
/// credentials on every run. The endpoint shape here mirrors the real Development-only
/// <c>/api/v1/dev/demo-credentials</c> response (camelCase, anonymous).
/// </summary>
public sealed class SignInPrefillTests : CastmillUiTestContext
{
    [Fact]
    public void Sign_in_prefills_the_demo_credentials_in_a_development_shell()
    {
        Http.OnGet("api/v1/dev/demo-credentials", new { email = "demo@castmill.local", password = "local-dev-pass" });

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/sign-in");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            Assert.Equal("demo@castmill.local", app.Find("input[type=email]").GetAttribute("value"));
            Assert.Equal("local-dev-pass", app.Find("input[type=password]").GetAttribute("value"));
        });
    }

    [Fact]
    public void Sign_in_stays_empty_when_the_credentials_endpoint_is_absent()
    {
        // No stubbed route: the handler answers 404, like an API with Dev:SeedDemoUser off.
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/sign-in");

        var app = Render<App>();

        Assert.Equal(string.Empty, app.Find("input[type=email]").GetAttribute("value") ?? string.Empty);
        Assert.Equal(string.Empty, app.Find("input[type=password]").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void Sign_in_never_asks_for_demo_credentials_outside_development()
    {
        Shell.IsDevelopment = false;
        Http.OnGet("api/v1/dev/demo-credentials", new { email = "demo@castmill.local", password = "local-dev-pass" });

        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        navigation.NavigateTo("/sign-in");

        var app = Render<App>();

        Assert.DoesNotContain(
            Http.Requests,
            r => r.RequestUri!.AbsolutePath.Contains("demo-credentials", StringComparison.Ordinal));
        Assert.Equal(string.Empty, app.Find("input[type=email]").GetAttribute("value") ?? string.Empty);
    }
}
