using System.Net;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Castmill.Core.Auth;
using Castmill.UI.Auth;
using Castmill.UI.Http;
using Castmill.UI.Platform;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Castmill.UI.Tests;

public sealed class ExternalAuthSignInTests : CastmillUiTestContext
{
    [Fact]
    public void Sign_in_renders_provider_readiness_without_hiding_password_sign_in()
    {
        Http.OnGet("api/v1/auth/external/providers", new ExternalAuthProviderStatusResponse(
        [
            new(ExternalAuthProviders.Microsoft, true),
            new(ExternalAuthProviders.Google, false),
        ]));
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/sign-in");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            var microsoft = app.FindAll("button").Single(button => button.TextContent.Contains("Microsoft"));
            var google = app.FindAll("button").Single(button => button.TextContent.Contains("Google"));
            Assert.False(microsoft.HasAttribute("disabled"));
            Assert.True(google.HasAttribute("disabled"));
            Assert.Contains("Google sign-in isn't configured", app.Markup, StringComparison.Ordinal);
            Assert.Equal("1", app.Find("input[type=email]").GetAttribute("tabindex"));
            Assert.Equal("2", app.Find("input[type=password]").GetAttribute("tabindex"));
        });
    }

    [Fact]
    public void Sign_in_disables_external_controls_when_the_shell_has_no_launcher()
    {
        ExternalBrowser.IsAvailable = false;
        ExternalBrowser.UnavailableReason = "External sign-in is currently available in the desktop app.";
        Http.OnGet("api/v1/auth/external/providers", new ExternalAuthProviderStatusResponse(
        [
            new(ExternalAuthProviders.Microsoft, true),
            new(ExternalAuthProviders.Google, true),
        ]));
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo("/sign-in");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            Assert.All(
                app.FindAll(".cm-external-auth__button"),
                button => Assert.True(button.HasAttribute("disabled")));
            Assert.Contains("currently available in the desktop app", app.Markup, StringComparison.Ordinal);
            Assert.NotNull(app.Find("input[type=email]"));
        });
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("https://example.invalid/steal", "")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("data:text/html,hello", "")]
    [InlineData("//example.invalid/steal", "")]
    [InlineData("\\\\example.invalid\\steal", "")]
    [InlineData("/campaigns/123?tab=focus", "/campaigns/123?tab=focus")]
    [InlineData("campaigns", "campaigns")]
    public void Return_url_accepts_only_local_relative_uris(string? value, string expected) =>
        Assert.Equal(expected, Pages.SignIn.LocalReturnUrl(value));

    [Fact]
    public async Task Failed_external_launch_does_not_clear_an_existing_session()
    {
        Tokens.SignIn();
        ExternalBrowser.Succeeds = false;
        var attemptId = Guid.NewGuid();
        Http.OnPost("api/v1/auth/external/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            new string('p', 43),
            DateTimeOffset.UtcNow.AddMinutes(10)));
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        var result = await service.SignInAsync(ExternalAuthProviders.Microsoft);

        Assert.False(result.Succeeded);
        Assert.True(Tokens.IsSignedIn);
        Assert.Single(ExternalBrowser.OpenedUris);
    }

    [Fact]
    public async Task Web_start_persists_pending_state_before_top_level_navigation_without_url_leakage()
    {
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        var attemptId = Guid.NewGuid();
        var pollSecret = new string('p', 43);
        Http.OnPost("api/v1/auth/external/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            pollSecret,
            DateTimeOffset.UtcNow.AddMinutes(10)));
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        var result = await service.SignInAsync(
            ExternalAuthProviders.Microsoft,
            "/campaigns/123?tab=focus");

        Assert.True(result.NavigationStarted);
        Assert.False(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(ExternalBrowser.PendingState);
        Assert.Equal(attemptId, ExternalBrowser.PendingState.AttemptId);
        Assert.Equal("/campaigns/123?tab=focus", ExternalBrowser.PendingState.ReturnUrl);
        Assert.Equal(0, ExternalBrowser.ClearPendingCalls);
        var opened = Assert.Single(ExternalBrowser.OpenedUris).AbsoluteUri;
        Assert.DoesNotContain(pollSecret, opened, StringComparison.Ordinal);
        Assert.DoesNotContain("exchangeCode", Http.Bodies.Single(request =>
            request.Path == "api/v1/auth/external/start").Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Http.Bodies, request =>
            request.Path == "api/v1/auth/external/start"
            && request.Body.Contains("\"clientKind\":\"web\"", StringComparison.Ordinal));

        await service.CancelAsync();
        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.ClearPendingCalls);
        Assert.Equal(ExternalAuthSignInPhase.Cancelled, service.Snapshot.Phase);
    }

    [Fact]
    public async Task Web_launcher_serializes_one_session_entry_and_enforces_api_and_callback_origins()
    {
        const string modulePath = "./_content/Castmill.UI/js/castmill-external-auth.js";
        var module = JSInterop.SetupModule(modulePath);
        module.Setup<bool>("writePending", _ => true).SetResult(true);
        module.SetupVoid("navigate", _ => true).SetVoidResult();
        module.SetupVoid("clearCallback", _ => true).SetVoidResult();
        var launcher = new WebExternalBrowserLauncher(
            Services.GetRequiredService<IJSRuntime>(),
            Services.GetRequiredService<HttpClient>());
        var state = new ExternalAuthPendingState(
            Guid.NewGuid(),
            new string('p', 43),
            new string('v', 43),
            DateTimeOffset.UtcNow.AddMinutes(10),
            "/campaigns/123?tab=focus");

        Assert.True(await launcher.StorePendingAsync(state));
        var write = Assert.Single(module.Invocations, invocation => invocation.Identifier == "writePending");
        var json = Assert.IsType<string>(Assert.Single(write.Arguments));
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(state.AttemptId, payload.RootElement.GetProperty("AttemptId").GetGuid());
        Assert.Equal(state.PollSecret, payload.RootElement.GetProperty("PollSecret").GetString());
        Assert.Equal(state.CodeVerifier, payload.RootElement.GetProperty("CodeVerifier").GetString());
        Assert.False(payload.RootElement.TryGetProperty("ExchangeCode", out _));
        Assert.False(payload.RootElement.TryGetProperty("BrowserUrl", out _));
        Assert.DoesNotContain("api.test", json, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            ExternalBrowserLaunchStatus.NavigationStarted,
            await launcher.OpenAsync(new Uri("https://api.test/api/v1/auth/external/browser/"
                + state.AttemptId.ToString("D"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.OpenAsync(
            new Uri("https://example.invalid/api/v1/auth/external/browser/" + state.AttemptId.ToString("D"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.StorePendingAsync(
            state with { ReturnUrl = "https://example.invalid/steal" }));

        await launcher.RemoveCallbackMarkerAsync();
        Assert.Single(
            module.Invocations,
            invocation => invocation.Identifier == "clearCallback");
    }

    [Fact]
    public void Web_callback_resume_signs_in_clears_state_and_restores_local_return_url()
    {
        ConfigureWebResume(new ExternalAuthPollResponse(
            ExternalAuthStatuses.Completed,
            DateTimeOffset.UtcNow.AddMinutes(10)));
        Http.OnPost("api/v1/auth/external/exchange", new AuthResponse(
            "web-access",
            DateTimeOffset.UtcNow.AddMinutes(15),
            "web-refresh",
            DateTimeOffset.UtcNow.AddDays(30)));
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(), Guid.NewGuid(), "web@example.com", "Web user"));
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/sign-in#external=complete&attemptId={ExternalBrowser.PendingState!.AttemptId:D}&code={new string('e', 43)}");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            Assert.True(Tokens.IsSignedIn);
            Assert.EndsWith(
                "/campaigns/123?tab=focus",
                Services.GetRequiredService<NavigationManager>().Uri,
                StringComparison.Ordinal);
            Assert.Null(ExternalBrowser.PendingState);
            Assert.Equal(1, ExternalBrowser.ClearPendingCalls);
            Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
        });
    }

    [Fact]
    public void Web_callback_failure_keeps_existing_session_and_surfaces_safe_error()
    {
        Tokens.SignIn();
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(), Guid.NewGuid(), "existing@example.com", "Existing user"));
        ConfigureWebResume(new ExternalAuthPollResponse(
            ExternalAuthStatuses.Failed,
            DateTimeOffset.UtcNow.AddMinutes(10),
            ExternalAuthErrors.AttemptFailed));
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/sign-in#external=complete&attemptId={ExternalBrowser.PendingState!.AttemptId:D}&error={ExternalAuthErrors.AttemptFailed}");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            Assert.True(Tokens.IsSignedIn);
            Assert.Contains("provider couldn't complete sign-in", app.Markup, StringComparison.Ordinal);
            Assert.Null(ExternalBrowser.PendingState);
            Assert.Equal(1, ExternalBrowser.ClearPendingCalls);
            Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
        });
    }

    [Fact]
    public async Task Expired_web_callback_cleans_pending_state_without_calling_the_api()
    {
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(DateTimeOffset.UtcNow.AddMinutes(-1));
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        var result = await service.ResumeAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("took too long", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(Http.Requests);
        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
    }

    [Fact]
    public async Task Ordinary_sign_in_visit_clears_expired_web_pending_state()
    {
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(DateTimeOffset.UtcNow.AddMinutes(-1));
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        await service.ClearExpiredPendingAsync();

        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.ClearPendingCalls);
        Assert.Empty(Http.Requests);
    }

    [Fact]
    public async Task Web_exchange_server_failure_retains_callback_and_pending_state_for_retry()
    {
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(DateTimeOffset.UtcNow.AddMinutes(10));
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            new string('e', 43),
            null);
        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/exchange",
            HttpStatusCode.ServiceUnavailable);
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        var result = await service.ResumeAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(ExternalBrowser.PendingState);
        Assert.NotNull(ExternalBrowser.CallbackResult);
        Assert.Equal(0, ExternalBrowser.ClearPendingCalls);
        Assert.Equal(0, ExternalBrowser.RemoveCallbackMarkerCalls);
        Assert.True(service.Snapshot.CanRetry);
        Assert.Equal(3, Http.Requests.Count(request =>
            request.RequestUri?.AbsolutePath == "/api/v1/auth/external/exchange"));

        Http.OnPost("api/v1/auth/external/exchange", new AuthResponse(
            "retry-access",
            DateTimeOffset.UtcNow.AddMinutes(15),
            "retry-refresh",
            DateTimeOffset.UtcNow.AddDays(30)));
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(), Guid.NewGuid(), "retry@example.com", "Retry user"));

        var retried = await service.ResumeAsync();

        Assert.True(retried.Succeeded);
        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
    }

    [Fact]
    public void Sign_in_retry_button_resumes_without_navigation_or_reload()
    {
        ConfigureWebResume(new ExternalAuthPollResponse(
            ExternalAuthStatuses.Completed,
            DateTimeOffset.UtcNow.AddMinutes(10)));
        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/exchange",
            HttpStatusCode.TooManyRequests);
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/sign-in#external=complete&attemptId={ExternalBrowser.PendingState!.AttemptId:D}&code={new string('e', 43)}");
        var app = Render<App>();
        app.WaitForAssertion(() => Assert.Contains(
            app.FindAll("button"),
            button => button.TextContent.Trim() == "Retry"));
        Http.OnPost("api/v1/auth/external/exchange", new AuthResponse(
            "button-access",
            DateTimeOffset.UtcNow.AddMinutes(15),
            "button-refresh",
            DateTimeOffset.UtcNow.AddDays(30)));
        Http.OnGet("api/v1/me", new MeResponse(
            Guid.NewGuid(), Guid.NewGuid(), "button@example.com", "Button user"));

        app.FindAll("button").Single(button => button.TextContent.Trim() == "Retry").Click();

        app.WaitForAssertion(() => Assert.True(Tokens.IsSignedIn));
        Assert.Equal("/campaigns/123?tab=focus", new Uri(
            Services.GetRequiredService<NavigationManager>().Uri).PathAndQuery);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(409, false)]
    public void Exchange_retry_classifies_only_429_and_server_errors(int statusCode, bool expected) =>
        Assert.Equal(
            expected,
            ExternalAuthExchangeRetry.IsRetryable(new ApiException("failure", statusCode)));

    [Fact]
    public async Task Exchange_retry_recovers_from_bounded_network_failures()
    {
        var attempts = 0;

        var result = await ExternalAuthExchangeRetry.ExecuteAsync(
            async _ =>
            {
                await Task.Yield();
                if (++attempts < 3)
                {
                    throw new HttpRequestException("Synthetic transport failure.");
                }
                return "recovered";
            },
            TimeProvider.System,
            CancellationToken.None);

        Assert.Equal(3, attempts);
        Assert.Equal("recovered", result);
    }

    [Fact]
    public async Task Desktop_loopback_receiver_accepts_only_the_expected_attempt_over_real_http()
    {
        var expectedAttemptId = Guid.NewGuid();
        var code = new string('e', 43);
        await using var receiver = DesktopLoopbackReceiver.Start();
        using var client = new HttpClient();
        var callback = new UriBuilder(receiver.ReturnUri)
        {
            Query = $"external=complete&attemptId={expectedAttemptId:D}&code={code}",
        }.Uri;

        var responseTask = client.GetAsync(callback);
        var result = await receiver.ReceiveAsync(
            expectedAttemptId,
            DateTimeOffset.UtcNow.AddMinutes(1),
            TimeProvider.System);
        var response = await responseTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ExternalAuthCallbackResult(expectedAttemptId, code, null), result);
    }

    [Fact]
    public async Task Desktop_loopback_receiver_rejects_a_different_attempt()
    {
        await using var receiver = DesktopLoopbackReceiver.Start();
        var expectedAttemptId = Guid.NewGuid();
        using var client = new HttpClient();
        var callback = new UriBuilder(receiver.ReturnUri)
        {
            Query = $"external=complete&attemptId={Guid.NewGuid():D}&code={new string('e', 43)}",
        }.Uri;

        var responseTask = client.GetAsync(callback);
        var result = await receiver.ReceiveAsync(
            expectedAttemptId,
            DateTimeOffset.UtcNow.AddMinutes(1),
            TimeProvider.System);
        var response = await responseTask;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(result);
    }

    [Fact]
    public void Every_server_error_has_stable_friendly_copy()
    {
        var errorCodes = typeof(ExternalAuthErrors)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()));

        foreach (var errorCode in errorCodes)
        {
            Assert.True(ExternalAuthFailureMessages.IsKnown(errorCode));
            var message = ExternalAuthFailureMessages.For(errorCode);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("external_auth_", message, StringComparison.Ordinal);
        }
    }

    private void ConfigureWebResume(ExternalAuthPollResponse poll)
    {
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(DateTimeOffset.UtcNow.AddMinutes(10));
        ExternalBrowser.CallbackResult = poll.Status == ExternalAuthStatuses.Completed
            ? new(ExternalBrowser.PendingState.AttemptId, new string('e', 43), null)
            : new(ExternalBrowser.PendingState.AttemptId, null,
                poll.ErrorCode ?? ExternalAuthErrors.AttemptFailed);
        Http.OnGet("api/v1/auth/external/providers", new ExternalAuthProviderStatusResponse(
        [
            new(ExternalAuthProviders.Microsoft, true),
            new(ExternalAuthProviders.Google, true),
        ]));
        Http.OnPost("api/v1/auth/external/poll", poll);
    }

    private static ExternalAuthPendingState PendingState(DateTimeOffset expiresAt) => new(
        Guid.NewGuid(),
        new string('p', 43),
        new string('v', 43),
        expiresAt,
        "/campaigns/123?tab=focus");
}