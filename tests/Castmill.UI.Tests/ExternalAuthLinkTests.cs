using Castmill.Core.Auth;
using Castmill.UI.Auth;
using Castmill.UI.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class ExternalAuthLinkTests : CastmillUiTestContext
{
    [Fact]
    public async Task Desktop_link_polls_without_exchange_and_preserves_the_session()
    {
        Tokens.SignIn();
        var attemptId = Guid.NewGuid();
        var exchangeCode = new string('e', 43);
        ExternalBrowser.CallbackResult = new(attemptId, exchangeCode, null);
        Http.OnPost("api/v1/auth/external/link/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            new string('p', 43),
            DateTimeOffset.UtcNow.AddMinutes(10)));
        Http.OnStatus(HttpMethod.Post, "api/v1/auth/external/link/exchange", System.Net.HttpStatusCode.NoContent);
        var service = Services.GetRequiredService<ExternalAuthLinkService>();

        var result = await service.LinkAsync(ExternalAuthProviders.Microsoft);

        Assert.True(result.Succeeded);
        Assert.True(Tokens.IsSignedIn);
        Assert.Contains(Http.Bodies, request =>
            request.Path == "api/v1/auth/external/link/start"
            && request.Body.Contains("\"returnRouteKey\":\"account-settings\"", StringComparison.Ordinal));
        Assert.Contains(Http.Bodies, request =>
            request.Path == "api/v1/auth/external/link/exchange"
            && request.Body.Contains(exchangeCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Web_link_stores_its_flow_kind_before_navigation()
    {
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        var attemptId = Guid.NewGuid();
        Http.OnPost("api/v1/auth/external/link/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            new string('p', 43),
            DateTimeOffset.UtcNow.AddMinutes(10)));
        var service = Services.GetRequiredService<ExternalAuthLinkService>();

        var result = await service.LinkAsync(ExternalAuthProviders.Google);

        Assert.True(result.NavigationStarted);
        Assert.NotNull(ExternalBrowser.PendingState);
        Assert.Equal(ExternalAuthFlowKinds.Link, ExternalBrowser.PendingState.FlowKind);
        Assert.True(Tokens.IsSignedIn);
    }

    [Fact]
    public async Task Failed_link_does_not_clear_the_current_session()
    {
        Tokens.SignIn();
        var attemptId = Guid.NewGuid();
        ExternalBrowser.CallbackResult = new(
            attemptId,
            null,
            ExternalAuthErrors.LoginAlreadyAssociated);
        Http.OnPost("api/v1/auth/external/link/start", new ExternalAuthStartResponse(
            attemptId,
            $"/api/v1/auth/external/browser/{attemptId:D}",
            new string('p', 43),
            DateTimeOffset.UtcNow.AddMinutes(10)));
        var service = Services.GetRequiredService<ExternalAuthLinkService>();

        var result = await service.LinkAsync(ExternalAuthProviders.Google);

        Assert.False(result.Succeeded);
        Assert.Contains("another Castmill account", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(Tokens.IsSignedIn);
        Assert.DoesNotContain(Http.Requests, request =>
            request.RequestUri?.AbsolutePath == "/api/v1/auth/external/exchange");
    }

    [Fact]
    public async Task Web_link_resume_completes_without_exchange_and_clears_owned_state()
    {
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(ExternalAuthFlowKinds.Link);
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            new string('e', 43),
            null);
        Http.OnStatus(HttpMethod.Post, "api/v1/auth/external/link/exchange", System.Net.HttpStatusCode.NoContent);
        var service = Services.GetRequiredService<ExternalAuthLinkService>();

        var result = await service.ResumeAsync();

        Assert.True(result.Succeeded);
        Assert.True(Tokens.IsSignedIn);
        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
        Assert.Contains(Http.Requests, request =>
            request.RequestUri?.AbsolutePath == "/api/v1/auth/external/link/exchange");
    }

    [Fact]
    public async Task Sign_in_resume_does_not_consume_a_link_continuation()
    {
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(ExternalAuthFlowKinds.Link);
        var service = Services.GetRequiredService<ExternalAuthSignInService>();

        var result = await service.ResumeAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(ExternalBrowser.PendingState);
        Assert.True(Tokens.IsSignedIn);
        Assert.Empty(Http.Requests);
        Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
    }

    [Fact]
    public async Task Web_link_exchange_server_failure_retains_callback_and_pending_state_for_retry()
    {
        Tokens.SignIn();
        ExternalBrowser.ClientKind = ExternalAuthClientKinds.Web;
        ExternalBrowser.UsesPersistentNavigation = true;
        ExternalBrowser.PendingState = PendingState(ExternalAuthFlowKinds.Link);
        ExternalBrowser.CallbackResult = new(
            ExternalBrowser.PendingState.AttemptId,
            new string('e', 43),
            null);
        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/link/exchange",
            System.Net.HttpStatusCode.ServiceUnavailable);
        var service = Services.GetRequiredService<ExternalAuthLinkService>();

        var result = await service.ResumeAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(ExternalBrowser.PendingState);
        Assert.NotNull(ExternalBrowser.CallbackResult);
        Assert.Equal(0, ExternalBrowser.ClearPendingCalls);
        Assert.Equal(0, ExternalBrowser.RemoveCallbackMarkerCalls);
        Assert.True(service.Snapshot.CanRetry);
        Assert.Equal(3, Http.Requests.Count(request =>
            request.RequestUri?.AbsolutePath == "/api/v1/auth/external/link/exchange"));

        Http.OnStatus(
            HttpMethod.Post,
            "api/v1/auth/external/link/exchange",
            System.Net.HttpStatusCode.NoContent);

        var retried = await service.ResumeAsync();

        Assert.True(retried.Succeeded);
        Assert.Null(ExternalBrowser.PendingState);
        Assert.Equal(1, ExternalBrowser.RemoveCallbackMarkerCalls);
    }

    private static ExternalAuthPendingState PendingState(string flowKind) => new(
        Guid.NewGuid(),
        new string('p', 43),
        new string('v', 43),
        DateTimeOffset.UtcNow.AddMinutes(10),
        string.Empty,
        flowKind);
}