using Bunit;
using Bunit.TestDoubles;
using Castmill.Core.Ai;
using Castmill.Core.Auth;
using Castmill.UI.Auth;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.Layout;
using Castmill.UI.Platform;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class AccountSecurityTests : CastmillUiTestContext
{
    [Fact]
    public void Security_renders_password_and_provider_status_without_identifiers()
    {
        ConfigureSettings();
        Http.OnGet("api/v1/auth/external/links", Links(
            hasPassword: false,
            microsoft: new(true, false),
            google: new(false, true)));
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/settings/security");

        var view = Render<Castmill.UI.Pages.Settings>();

        view.WaitForAssertion(() =>
        {
            Assert.Contains("No password is set", view.Markup, StringComparison.Ordinal);
            Assert.Contains("Microsoft", view.Markup, StringComparison.Ordinal);
            Assert.Contains("NOT LINKED", view.Markup, StringComparison.Ordinal);
            Assert.Contains("Google", view.Markup, StringComparison.Ordinal);
            Assert.Contains("LINKED", view.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("providerKey", view.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("externalId", view.Markup, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Link_button_completes_desktop_poll_without_replacing_the_session()
    {
        ConfigureSettings();
        Tokens.SignIn();
        var status = Links(true, new(true, false), new(true, false));
        Http.OnAsync(HttpMethod.Get, "api/v1/auth/external/links",
            () => Task.FromResult(StubHttpHandler.Json(status)));
        var attemptId = Guid.NewGuid();
        ExternalBrowser.CallbackResult = new(attemptId, new string('e', 43), null);
        Http.OnPost("api/v1/auth/external/link/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            new string('p', 43),
            DateTimeOffset.UtcNow.AddMinutes(10)));
        Http.OnStatus(HttpMethod.Post, "api/v1/auth/external/link/exchange", System.Net.HttpStatusCode.NoContent);
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/settings/security");
        var view = Render<Castmill.UI.Pages.Settings>();
        view.WaitForAssertion(() => Assert.Contains("Microsoft", view.Markup, StringComparison.Ordinal));
        status = Links(true, new(true, true), new(true, false));

        await ProviderButton(view, "Microsoft", "Link").ClickAsync();

        view.WaitForAssertion(() => Assert.Contains(
            "LINKED",
            view.FindAll("li")
                .Single(row => row.TextContent.Contains("Microsoft", StringComparison.Ordinal))
                .TextContent,
            StringComparison.Ordinal));
        Assert.True(Tokens.IsSignedIn);
        Assert.DoesNotContain(Http.Requests, request =>
            request.RequestUri?.AbsolutePath == "/api/v1/auth/external/exchange");
    }

    [Fact]
    public async Task Unlink_refuses_the_only_sign_in_method_before_calling_the_api()
    {
        ConfigureSettings();
        Http.OnGet("api/v1/auth/external/links", Links(
            false,
            new(true, false),
            new(true, true)));
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/settings/security");
        var view = Render<Castmill.UI.Pages.Settings>();
        view.WaitForAssertion(() => Assert.Contains("Google", view.Markup, StringComparison.Ordinal));

        await ProviderButton(view, "Google", "Unlink").ClickAsync();

        Assert.Contains("Add another sign-in method", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Http.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Confirmed_unlink_calls_api_and_refreshes_status()
    {
        ConfigureSettings();
        Services.AddSingleton<IConfirmService>(new AcceptingConfirmService());
        var status = Links(true, new(true, false), new(true, true));
        Http.OnAsync(HttpMethod.Get, "api/v1/auth/external/links",
            () => Task.FromResult(StubHttpHandler.Json(status)));
        Http.OnStatus(
            HttpMethod.Delete,
            "api/v1/auth/external/link/google",
            System.Net.HttpStatusCode.NoContent);
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/settings/security");
        var view = Render<Castmill.UI.Pages.Settings>();
        view.WaitForAssertion(() => Assert.Contains("Google", view.Markup, StringComparison.Ordinal));
        status = Links(true, new(true, false), new(true, false));

        await ProviderButton(view, "Google", "Unlink").ClickAsync();

        view.WaitForAssertion(() => Assert.Contains(Http.Requests, request =>
            request.Method == HttpMethod.Delete
            && request.RequestUri?.AbsolutePath == "/api/v1/auth/external/link/google"));
        Assert.Contains("NOT LINKED", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_callback_resumes_the_stored_link_flow()
    {
        ConfigureSettings();
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = new ExternalAuthPendingState(
            Guid.NewGuid(),
            new string('p', 43),
            new string('v', 43),
            DateTimeOffset.UtcNow.AddMinutes(10),
            string.Empty,
            ExternalAuthFlowKinds.Link);
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            new string('e', 43),
            null);
        Http.OnStatus(HttpMethod.Post, "api/v1/auth/external/link/exchange", System.Net.HttpStatusCode.NoContent);
        Http.OnGet("api/v1/auth/external/links", Links(
            true,
            new(true, true),
            new(true, false)));
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/settings/security#external=complete&attemptId={ExternalBrowser.PendingState.AttemptId:D}&code={new string('e', 43)}");

        var view = Render<Castmill.UI.Pages.Settings>();

        view.WaitForAssertion(() =>
        {
            Assert.Null(ExternalBrowser.PendingState);
            Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
            Assert.Contains("Microsoft", view.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(Http.Requests, request =>
                request.RequestUri?.AbsolutePath == "/api/v1/auth/external/exchange");
        });
    }

    [Fact]
    public void Settings_callback_failure_keeps_the_session_and_surfaces_the_reason()
    {
        ConfigureSettings();
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = new ExternalAuthPendingState(
            Guid.NewGuid(),
            new string('p', 43),
            new string('v', 43),
            DateTimeOffset.UtcNow.AddMinutes(10),
            string.Empty,
            ExternalAuthFlowKinds.Link);
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            null,
            ExternalAuthErrors.LoginAlreadyAssociated);
        Http.OnGet("api/v1/auth/external/links", Links(
            true,
            new(true, false),
            new(true, false)));
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/settings/security#external=complete&attemptId={ExternalBrowser.PendingState.AttemptId:D}&error={ExternalAuthErrors.LoginAlreadyAssociated}");

        var view = Render<Castmill.UI.Pages.Settings>();

        view.WaitForAssertion(() =>
        {
            Assert.True(Tokens.IsSignedIn);
            Assert.Contains("another Castmill account", view.Markup, StringComparison.Ordinal);
            Assert.Null(ExternalBrowser.PendingState);
        });
    }

    [Fact]
    public void Settings_retry_button_resumes_link_without_navigation_or_reload()
    {
        ConfigureSettings();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = new ExternalAuthPendingState(
            Guid.NewGuid(),
            new string('p', 43),
            new string('v', 43),
            DateTimeOffset.UtcNow.AddMinutes(10),
            string.Empty,
            ExternalAuthFlowKinds.Link);
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            new string('e', 43),
            null);
        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/link/exchange",
            System.Net.HttpStatusCode.ServiceUnavailable);
        Http.OnGet("api/v1/auth/external/links", Links(
            true,
            new(true, false),
            new(true, false)));
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/settings/security#external=complete&attemptId={ExternalBrowser.PendingState.AttemptId:D}&code={new string('e', 43)}");
        var view = Render<Castmill.UI.Pages.Settings>();
        view.WaitForAssertion(() => Assert.Contains(
            view.FindAll("button"),
            button => button.TextContent.Trim() == "Retry"));
        var uriBeforeRetry = Services.GetRequiredService<NavigationManager>().Uri;
        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/link/exchange",
            System.Net.HttpStatusCode.NoContent);

        view.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();

        view.WaitForAssertion(() => Assert.Null(ExternalBrowser.PendingState));
        Assert.Equal(uriBeforeRetry, Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Change_password_replaces_the_form_for_an_external_only_account()
    {
        Http.OnGet("api/v1/auth/external/links", Links(
            false,
            new(true, true),
            new(true, false)));

        var view = Render<Castmill.UI.Pages.ChangePassword>();

        view.WaitForAssertion(() =>
        {
            Assert.Contains("No password is set", view.Markup, StringComparison.Ordinal);
            Assert.NotNull(view.Find("a[href='settings/security']"));
            Assert.Empty(view.FindAll("input[type=password]"));
        });
    }

    [Fact]
    public void Change_password_keeps_the_form_for_a_password_account()
    {
        Http.OnGet("api/v1/auth/external/links", Links(
            true,
            new(true, false),
            new(true, false)));

        var view = Render<Castmill.UI.Pages.ChangePassword>();

        view.WaitForAssertion(() =>
        {
            Assert.Equal(2, view.FindAll("input[type=password]").Count);
            Assert.Contains("Change password", view.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("No password is set", view.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Omnibox_routes_external_only_accounts_to_sign_in_methods()
    {
        SignInTestUser();
        Http.OnGet("api/v1/auth/external/links", Links(
            false,
            new(true, true),
            new(true, false)));
        await Services.GetRequiredService<AuthState>().InitializeAsync();
        var view = Render<Omnibox>();

        await view.InvokeAsync(() => view.Instance.NotifyChordAsync("omnibox"));

        Assert.Contains("Manage sign-in methods", view.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Change password", view.Markup, StringComparison.Ordinal);
    }

    private void ConfigureSettings()
    {
        SignInTestUser();
        Http.OnGet("api/v1/settings/secrets", new List<SecretStatus>());
        Http.OnGet("api/v1/settings", new List<SettingRow>());
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "none",
            false,
            new Dictionary<string, string>(),
            false,
            null,
            []));
    }

    private static ExternalAuthLinksResponse Links(
        bool hasPassword,
        (bool Enabled, bool Linked) microsoft,
        (bool Enabled, bool Linked) google) => new(
        hasPassword,
        [
            new(ExternalAuthProviders.Microsoft, microsoft.Enabled, microsoft.Linked),
            new(ExternalAuthProviders.Google, google.Enabled, google.Linked),
        ]);

    private static AngleSharp.Dom.IElement ProviderButton(
        IRenderedComponent<Castmill.UI.Pages.Settings> view,
        string provider,
        string label) => view.FindAll("li")
        .Single(row => row.TextContent.Contains(provider, StringComparison.Ordinal))
        .QuerySelectorAll("button")
        .Single(button => button.TextContent.Contains(label, StringComparison.Ordinal));

    private sealed class AcceptingConfirmService : IConfirmService
    {
        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);
    }
}