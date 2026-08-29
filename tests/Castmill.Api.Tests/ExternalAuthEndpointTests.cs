using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ExternalAuthEndpointTests(CastmillApiFactory factory)
{
    [Fact]
    public async Task External_endpoints_use_dedicated_rate_limit_partitions()
    {
        await using var app = EnabledApp();
        _ = app.CreateClient();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertPolicy(endpoints, "/api/v1/auth/login", "auth");
        AssertPolicy(endpoints, "/api/v1/auth/external/start", "external-start");
        AssertPolicy(endpoints, "/api/v1/auth/external/browser/{attemptId:guid}", "external-flow");
        AssertPolicy(endpoints, "/api/v1/auth/external/exchange", "external-flow");
        AssertPolicy(endpoints, "/api/v1/auth/external/link/exchange", "external-flow");
        AssertPolicy(endpoints, "/api/v1/auth/external/poll", "external-poll");
        Assert.DoesNotContain(endpoints, endpoint =>
            endpoint.RoutePattern.RawText is "/signin-microsoft" or "/signin-google");
    }

    [Fact]
    public async Task Provider_middleware_uses_required_code_flow_without_persistent_sign_in()
    {
        await using var app = EnabledApp();
        _ = app.CreateClient();
        var authentication = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, authentication.DefaultAuthenticateScheme);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, authentication.DefaultChallengeScheme);

        var oidc = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var microsoft = oidc.Get(ExternalAuthSchemes.Microsoft);
        var google = oidc.Get(ExternalAuthSchemes.Google);
        Assert.Equal("https://login.microsoftonline.com/common/v2.0", microsoft.Authority);
        Assert.Equal("https://accounts.google.com", google.Authority);
        Assert.Equal("/signin-microsoft", microsoft.CallbackPath);
        Assert.Equal("/signin-google", google.CallbackPath);
        Assert.Null(microsoft.SignInScheme);
        Assert.Null(google.SignInScheme);
        Assert.All(new[] { microsoft, google }, options =>
        {
            Assert.Equal("code", options.ResponseType);
            Assert.True(options.UsePkce);
            Assert.False(options.SaveTokens);
            Assert.False(options.MapInboundClaims);
            Assert.True(options.TokenValidationParameters.ValidateIssuer);
            Assert.NotNull(options.TokenValidationParameters.IssuerValidator);
            Assert.Equal(
                new[] { "email", "openid", "profile" },
                options.Scope.Order(StringComparer.Ordinal));
            Assert.Equal(CookieSecurePolicy.Always, options.CorrelationCookie.SecurePolicy);
            Assert.Equal(SameSiteMode.None, options.CorrelationCookie.SameSite);
            Assert.Equal(CookieSecurePolicy.Always, options.NonceCookie.SecurePolicy);
            Assert.Equal(SameSiteMode.None, options.NonceCookie.SameSite);
            Assert.NotNull(options.Events.OnTicketReceived);
        });

        var schemes = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync("Castmill.External.Microsoft"));
        Assert.Null(await schemes.GetSchemeAsync("Castmill.External.Google"));
    }

    [Fact]
    public async Task Provider_remote_failure_marks_web_attempt_failed_and_returns_fixed_callback()
    {
        await using var app = EnabledApp();
        var start = await StartAsync(app.CreateClient(), StartRequest() with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        await using var scope = app.Services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        var options = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(ExternalAuthSchemes.Google);
        var properties = new AuthenticationProperties();
        properties.Items[ExternalAuthSchemes.AttemptIdProperty] = start.AttemptId.ToString("D");
        properties.Items[ExternalAuthSchemes.ProviderProperty] = ExternalAuthProviders.Google;
        var failure = new RemoteFailureContext(
            httpContext,
            new AuthenticationScheme(
                ExternalAuthSchemes.Google,
                ExternalAuthSchemes.Google,
                typeof(OpenIdConnectHandler)),
            options,
            new InvalidOperationException("Synthetic provider failure"))
        {
            Properties = properties,
        };

        await options.Events.RemoteFailure(failure);

        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Equal(
            $"https://localhost:7124/sign-in#external=complete&attemptId={start.AttemptId:D}&error={ExternalAuthErrors.AttemptFailed}",
            httpContext.Response.Headers.Location);
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == start.AttemptId);
        Assert.Equal(ExternalAuthStatuses.Failed, attempt.Status);
        Assert.Equal(ExternalAuthErrors.AttemptFailed, attempt.ErrorCode);
    }

    [Fact]
    public async Task Validated_provider_ticket_completes_only_its_bound_attempt_without_cookie()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var (verifier, startRequest) = StartRequestWithVerifier();
        var start = await StartAsync(client, startRequest with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        var email = $"callback-{Guid.NewGuid():N}@example.com";
        var providerKey = $"subject-{Guid.NewGuid():N}";

        var location = await CompleteProviderTicketAsync(
            app.Services,
            start.AttemptId,
            providerKey,
            email);

        Assert.StartsWith(
            $"https://localhost:7124/sign-in#external=complete&attemptId={start.AttemptId:D}&code=",
            location,
            StringComparison.Ordinal);
        await using var verificationScope = app.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var attempt = await db.ExternalAuthAttempts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == start.AttemptId);
        Assert.Equal(ExternalAuthStatuses.Completed, attempt.Status);
        Assert.Null(attempt.UserId);
        Assert.Equal(email, attempt.CandidateEmail);
        Assert.NotNull(attempt.ExchangeCodeHash);
        Assert.False(await db.Users.AnyAsync(user => user.Email == email));
        var exchangeCode = CallbackCode(location);
        Assert.Equal(ExternalAuthEndpoints.HashSecret(exchangeCode), attempt.ExchangeCodeHash);

        var exchange = await client.PostAsJsonAsync(
            "/api/v1/auth/external/exchange",
            new ExternalAuthExchangeRequest(start.AttemptId, exchangeCode, verifier));
        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(candidate => candidate.Email == email);
        Assert.Equal(user.Id, (await db.UserLogins.SingleAsync(login =>
            login.LoginProvider == ExternalAuthProviders.Google
            && login.ProviderKey == providerKey)).UserId);
        Assert.Equal(1, await db.Tenants.CountAsync(tenant => tenant.Id == user.TenantId));
    }

    [Fact]
    public async Task Same_provider_interleaving_completes_each_properties_bound_attempt()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var first = await StartAsync(client, StartRequest() with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        var second = await StartAsync(client, StartRequest() with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        var firstEmail = $"overlap-first-{Guid.NewGuid():N}@example.com";
        var secondEmail = $"overlap-second-{Guid.NewGuid():N}@example.com";
        var secondLocation = await CompleteProviderTicketAsync(
            app.Services, second.AttemptId, $"second-{Guid.NewGuid():N}", secondEmail);
        var firstLocation = await CompleteProviderTicketAsync(
            app.Services, first.AttemptId, $"first-{Guid.NewGuid():N}", firstEmail);

        Assert.Contains($"attemptId={second.AttemptId:D}", secondLocation, StringComparison.Ordinal);
        Assert.Contains($"attemptId={first.AttemptId:D}", firstLocation, StringComparison.Ordinal);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var attempts = await db.ExternalAuthAttempts
            .Where(candidate => candidate.Id == first.AttemptId || candidate.Id == second.AttemptId)
            .ToDictionaryAsync(candidate => candidate.Id);
        Assert.Equal(ExternalAuthStatuses.Completed, attempts[first.AttemptId].Status);
        Assert.Equal(firstEmail, attempts[first.AttemptId].CandidateEmail);
        Assert.Equal(ExternalAuthStatuses.Completed, attempts[second.AttemptId].Status);
        Assert.Equal(secondEmail, attempts[second.AttemptId].CandidateEmail);
        Assert.NotEqual(attempts[first.AttemptId].ExchangeCodeHash, attempts[second.AttemptId].ExchangeCodeHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://127.0.0.1:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("http://localhost:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("http://127.0.0.1:80/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("http://user@127.0.0.1:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("http://127.0.0.1:49152/castmill/auth/short/")]
    [InlineData("http://127.0.0.1:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("http://127.0.0.1:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/?query=1")]
    [InlineData("http://127.0.0.1:49152/castmill/auth/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/#fragment")]
    public void Desktop_loopback_validation_rejects_non_exact_returns(string? value)
    {
        Assert.False(ExternalAuthEndpoints.TryValidateLoopbackReturnUri(value, out _));
    }

    [Fact]
    public void Desktop_loopback_validation_accepts_exact_ipv4_nonce_path()
    {
        var value = $"http://127.0.0.1:49152/castmill/auth/{new string('a', 43)}/";

        Assert.True(ExternalAuthEndpoints.TryValidateLoopbackReturnUri(value, out var normalized));
        Assert.Equal(value, normalized);
    }

    [Fact]
    public async Task Intermediary_complete_endpoint_is_not_routable()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var response = await client.GetAsync(
            $"/api/v1/auth/external/complete?attemptId={Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/sign-in")]
    [InlineData("ftp://localhost/sign-in")]
    [InlineData("https://localhost/sign-in#fragment")]
    [InlineData("https://localhost/sign-in?attemptId=secret")]
    [InlineData("https://localhost/settings/security")]
    [InlineData("http://example.com/sign-in")]
    public void Web_return_uri_validation_rejects_malformed_or_unsafe_values(string value)
    {
        Assert.False(ExternalAuthEndpoints.IsValidWebSignInReturnUri(value, isProduction: false));
    }

    [Fact]
    public void Web_return_uri_validation_requires_https_in_production()
    {
        Assert.False(ExternalAuthEndpoints.IsValidWebSignInReturnUri(
            "http://localhost:7124/sign-in",
            isProduction: true));
        Assert.False(ExternalAuthEndpoints.IsValidWebSignInReturnUri(
            "https://localhost:7124/sign-in",
            isProduction: true));
        Assert.True(ExternalAuthEndpoints.IsValidWebSignInReturnUri(
            "https://castmill.example/sign-in",
            isProduction: true));
        Assert.True(ExternalAuthEndpoints.IsValidWebAccountSettingsReturnUri(
            "https://castmill.example/settings/security",
            isProduction: true));
        Assert.False(ExternalAuthEndpoints.IsValidWebAccountSettingsReturnUri(
            "https://castmill.example/sign-in",
            isProduction: true));
    }

    [Fact]
    public async Task Poll_budget_exceeds_the_former_shared_120_per_minute_ceiling()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var start = await StartAsync(client);

        for (var index = 0; index < 121; index++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/external/poll",
                new ExternalAuthPollRequest(start.AttemptId, start.PollSecret));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public async Task Providers_report_readiness_from_server_configuration()
    {
        var disabled = await factory.CreateClient().GetFromJsonAsync<ExternalAuthProviderStatusResponse>(
            "/api/v1/auth/external/providers");
        Assert.NotNull(disabled);
        Assert.All(disabled.Providers, provider => Assert.False(provider.Enabled));

        await using var app = EnabledApp();
        var enabled = await app.CreateClient().GetFromJsonAsync<ExternalAuthProviderStatusResponse>(
            "/api/v1/auth/external/providers");
        Assert.NotNull(enabled);
        Assert.All(enabled.Providers, provider => Assert.True(provider.Enabled));
    }

    [Fact]
    public void Provider_configuration_requires_explicit_enablement_and_complete_credentials()
    {
        Assert.True(ExternalAuthSchemes.IsValidConfiguration(new ExternalAuthProviderCredentials()));
        Assert.False(ExternalAuthSchemes.IsConfigured(new ExternalAuthProviderCredentials
        {
            ClientId = "ignored-while-disabled",
            ClientSecret = "ignored-while-disabled",
        }));
        Assert.False(ExternalAuthSchemes.IsValidConfiguration(new ExternalAuthProviderCredentials
        {
            Enabled = true,
            ClientId = "partial",
        }));
        Assert.True(ExternalAuthSchemes.IsConfigured(new ExternalAuthProviderCredentials
        {
            Enabled = true,
            ClientId = "client",
            ClientSecret = "secret",
        }));
    }

    [Fact]
    public async Task Start_rejects_invalid_provider_client_return_challenge_and_method()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var valid = StartRequest();
        var invalid = new[]
        {
            valid with { Provider = "github" },
            valid with { ClientKind = "mobile" },
            valid with { ReturnRouteKey = "arbitrary-return" },
            valid with { CodeChallenge = "short" },
            valid with { CodeChallenge = new string('+', 43) },
            valid with { CodeChallengeMethod = "plain" },
        };

        foreach (var request in invalid)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/external/start", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var wrongMethod = await client.GetAsync("/api/v1/auth/external/start");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
    }

    [Fact]
    public async Task Disabled_provider_returns_stable_unavailable_error()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/external/start",
            StartRequest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            ExternalAuthErrors.ProviderUnavailable,
            await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Start_persists_only_hashes_and_browser_url_contains_only_attempt_id()
    {
        await using var app = EnabledApp();
        var response = await app.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/external/start",
            StartRequest());
        response.EnsureSuccessStatusCode();
        var start = await response.Content.ReadFromJsonAsync<ExternalAuthStartResponse>();
        Assert.NotNull(start);

        Assert.Equal(
            $"/api/v1/auth/external/browser/{start.AttemptId:D}",
            start.BrowserUrl);
        Assert.DoesNotContain(start.PollSecret, start.BrowserUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("exchangeCode", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var scope = app.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == start.AttemptId);
        Assert.Equal(ExternalAuthEndpoints.HashSecret(start.PollSecret), attempt.PollSecretHash);
        Assert.Null(attempt.ExchangeCodeHash);
        Assert.DoesNotContain(start.PollSecret, attempt.PollSecretHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_rejects_wrong_secret_and_reports_pending_without_exchange_code()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var start = await StartAsync(client);

        var wrong = await client.PostAsJsonAsync(
            "/api/v1/auth/external/poll",
            new ExternalAuthPollRequest(start.AttemptId, "wrong-secret-that-is-long-enough-to-submit"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var pending = await client.PostAsJsonAsync(
            "/api/v1/auth/external/poll",
            new ExternalAuthPollRequest(start.AttemptId, start.PollSecret));
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var body = await pending.Content.ReadAsStringAsync();
        Assert.DoesNotContain("exchangeCode", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ExternalAuthStatuses.Pending,
            JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Poll_marks_an_expired_attempt_failed_and_reports_expired()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var start = await StartAsync(client);
        await SetExpiryAsync(app.Services, start.AttemptId, DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/external/poll",
            new ExternalAuthPollRequest(start.AttemptId, start.PollSecret));
        response.EnsureSuccessStatusCode();
        var poll = await response.Content.ReadFromJsonAsync<ExternalAuthPollResponse>();
        Assert.NotNull(poll);
        Assert.Equal(ExternalAuthStatuses.Expired, poll.Status);
        Assert.Equal(ExternalAuthErrors.AttemptExpired, poll.ErrorCode);

        await using var scope = app.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == start.AttemptId);
        Assert.Equal(ExternalAuthStatuses.Failed, attempt.Status);
        Assert.Equal(ExternalAuthErrors.AttemptExpired, attempt.ErrorCode);
    }

    [Fact]
    public async Task Exchange_rejects_pending_wrong_code_and_wrong_verifier()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var (verifier, request) = StartRequestWithVerifier();
        var start = await StartAsync(client, request);

        var pending = await client.PostAsJsonAsync(
            "/api/v1/auth/external/exchange",
            new ExternalAuthExchangeRequest(
                start.AttemptId,
                WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                verifier));
        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);

        var user = await CreateUserAsync(app.Services, password: true);
        var exchangeCode = await CompleteAttemptAsync(app.Services, start.AttemptId, user.Id);

        var wrongCode = await client.PostAsJsonAsync(
            "/api/v1/auth/external/exchange",
            new ExternalAuthExchangeRequest(
                start.AttemptId,
                WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
                verifier));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCode.StatusCode);

        var wrongVerifier = await client.PostAsJsonAsync(
            "/api/v1/auth/external/exchange",
            new ExternalAuthExchangeRequest(
                start.AttemptId,
                exchangeCode,
                WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongVerifier.StatusCode);
    }

    [Fact]
    public async Task Exchange_is_single_use_and_issues_standard_tokens()
    {
        await using var app = EnabledApp();
        var client = app.CreateClient();
        var (verifier, request) = StartRequestWithVerifier();
        var start = await StartAsync(client, request);
        var user = await CreateUserAsync(app.Services, password: true);
        var exchangeCode = await CompleteAttemptAsync(app.Services, start.AttemptId, user.Id);
        var exchange = new ExternalAuthExchangeRequest(start.AttemptId, exchangeCode, verifier);

        var first = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", exchange);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(await first.Content.ReadFromJsonAsync<AuthResponse>());

        var replay = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", exchange);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(ExternalAuthErrors.CodeConsumed, await ErrorCodeAsync(replay));
    }

    [Fact]
    public async Task Concurrent_exchange_allows_exactly_one_success()
    {
        await using var app = EnabledApp();
        var firstClient = app.CreateClient();
        var secondClient = app.CreateClient();
        var (verifier, request) = StartRequestWithVerifier();
        var start = await StartAsync(firstClient, request);
        var user = await CreateUserAsync(app.Services, password: true);
        var exchangeCode = await CompleteAttemptAsync(app.Services, start.AttemptId, user.Id);
        var exchange = new ExternalAuthExchangeRequest(start.AttemptId, exchangeCode, verifier);

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/v1/auth/external/exchange", exchange),
            secondClient.PostAsJsonAsync("/api/v1/auth/external/exchange", exchange));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Execution_strategy_returns_only_the_last_transaction_attempt_outcome()
    {
        var invocation = 0;

        var outcome = await ExternalAuthEndpoints.ExecuteOutcomeAsync(
            async operation =>
            {
                var ambiguous = await operation();
                Assert.Equal("stale-token", ambiguous);
                return await operation();
            },
            () => Task.FromResult(++invocation == 1 ? "stale-token" : "code-consumed"));

        Assert.Equal(2, invocation);
        Assert.Equal("code-consumed", outcome);
    }

    [Fact]
    public async Task Link_start_uses_authenticated_user_and_ignores_client_user_field()
    {
        await using var app = EnabledApp();
        var user = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, user);
        var clientSuppliedUser = Guid.NewGuid();
        var request = LinkStartRequest();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/link/start")
        {
            Content = JsonContent.Create(new
            {
                request.Provider,
                request.ClientKind,
                request.ReturnRouteKey,
                request.CodeChallenge,
                request.CodeChallengeMethod,
                request.LoopbackReturnUri,
                linkUserId = clientSuppliedUser,
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await app.CreateClient().SendAsync(message);
        response.EnsureSuccessStatusCode();
        var start = await response.Content.ReadFromJsonAsync<ExternalAuthStartResponse>();
        Assert.NotNull(start);

        await using var scope = app.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == start.AttemptId);
        Assert.Equal(user.Id, attempt.LinkUserId);
        Assert.NotEqual(clientSuppliedUser, attempt.LinkUserId);

        using var wrongRoute = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/link/start")
        {
            Content = JsonContent.Create(request with
            {
                ReturnRouteKey = ExternalAuthReturnRoutes.SignIn,
            }),
        };
        wrongRoute.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var wrongRouteResponse = await app.CreateClient().SendAsync(wrongRoute);
        Assert.Equal(HttpStatusCode.BadRequest, wrongRouteResponse.StatusCode);
        Assert.Equal(ExternalAuthErrors.InvalidRequest, await ErrorCodeAsync(wrongRouteResponse));
    }

    [Fact]
    public async Task Web_callback_only_stores_candidate_then_authenticated_exchange_links_user()
    {
        await using var app = EnabledApp();
        var user = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, user);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var (verifier, linkRequest) = LinkStartRequestWithVerifier();
        var start = await LinkStartAsync(client, tokens, linkRequest with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        var providerKey = $"linked-{Guid.NewGuid():N}";
        var callbackLocation = await CompleteProviderTicketAsync(
            app.Services,
            start.AttemptId,
            providerKey,
            user.Email!);
        var exchangeCode = CallbackCode(callbackLocation);
        var pollResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/external/poll",
            new ExternalAuthPollRequest(start.AttemptId, start.PollSecret));
        var poll = await pollResponse.Content.ReadFromJsonAsync<ExternalAuthPollResponse>();
        Assert.Equal(ExternalAuthStatuses.Completed, poll?.Status);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(meRequest)).StatusCode);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>();
        var trackedUser = await users.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(trackedUser);
        Assert.DoesNotContain(await users.GetLoginsAsync(trackedUser), login =>
            login.LoginProvider == ExternalAuthProviders.Google);
        Assert.False(await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .AuditEvents.IgnoreQueryFilters()
            .AnyAsync(audit => audit.UserId == user.Id && audit.Action == "auth.external.linked"));

        using var exchangeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/external/link/exchange")
        {
            Content = JsonContent.Create(new ExternalAuthExchangeRequest(
                start.AttemptId,
                exchangeCode,
                verifier)),
        };
        exchangeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var exchangeResponse = await client.SendAsync(exchangeRequest);
        Assert.Equal(HttpStatusCode.NoContent, exchangeResponse.StatusCode);
        db.ChangeTracker.Clear();
        trackedUser = await users.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(trackedUser);
        Assert.Contains(await users.GetLoginsAsync(trackedUser), login =>
            login.LoginProvider == ExternalAuthProviders.Google
            && login.ProviderKey == providerKey);
        Assert.True(await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .AuditEvents.IgnoreQueryFilters()
            .AnyAsync(audit => audit.UserId == user.Id && audit.Action == "auth.external.linked"));
    }

    [Fact]
    public async Task Web_link_exchange_rejects_a_provider_identity_linked_to_another_user()
    {
        await using var app = EnabledApp();
        var providerKey = $"owned-{Guid.NewGuid():N}";
        var owner = await CreateUserAsync(
            app.Services,
            password: false,
            new ExternalLoginMapping(ExternalAuthProviders.Google, providerKey, "Google"));
        var currentUser = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, currentUser);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var (verifier, linkRequest) = LinkStartRequestWithVerifier();
        var start = await LinkStartAsync(client, tokens, linkRequest with
        {
            ClientKind = ExternalAuthClientKinds.Web,
            LoopbackReturnUri = null,
        });
        var callbackLocation = await CompleteProviderTicketAsync(
            app.Services,
            start.AttemptId,
            providerKey,
            owner.Email!);
        using var exchangeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/external/link/exchange")
        {
            Content = JsonContent.Create(new ExternalAuthExchangeRequest(
                start.AttemptId,
                CallbackCode(callbackLocation),
                verifier)),
        };
        exchangeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var exchangeResponse = await client.SendAsync(exchangeRequest);
        Assert.Equal(HttpStatusCode.Conflict, exchangeResponse.StatusCode);
        Assert.Equal(ExternalAuthErrors.LoginAlreadyAssociated, await ErrorCodeAsync(exchangeResponse));
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>();
        var trackedCurrentUser = await users.FindByIdAsync(currentUser.Id.ToString());
        Assert.NotNull(trackedCurrentUser);
        Assert.DoesNotContain(await users.GetLoginsAsync(trackedCurrentUser), login =>
            login.LoginProvider == ExternalAuthProviders.Google);
    }

    [Fact]
    public async Task Links_status_is_configured_scoped_and_never_exposes_provider_keys()
    {
        await using var app = EnabledApp();
        var currentUser = await CreateUserAsync(app.Services, password: false);
        var otherUser = await CreateUserAsync(app.Services, password: true);
        await AddLoginAsync(app.Services, currentUser, ExternalAuthProviders.Google);
        await AddLoginAsync(app.Services, otherUser, ExternalAuthProviders.Microsoft);
        var tokens = await IssueTokensAsync(app.Services, currentUser);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/external/links");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await app.CreateClient().SendAsync(request);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var links = JsonSerializer.Deserialize<ExternalAuthLinksResponse>(
            json,
            JsonSerializerOptions.Web);
        Assert.NotNull(links);
        Assert.False(links.HasPassword);
        Assert.Equal(2, links.Providers.Count);
        Assert.All(links.Providers, provider => Assert.True(provider.Enabled));
        Assert.True(links.Providers.Single(provider =>
            provider.Provider == ExternalAuthProviders.Google).Linked);
        Assert.False(links.Providers.Single(provider =>
            provider.Provider == ExternalAuthProviders.Microsoft).Linked);
        Assert.DoesNotContain("providerKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Links_status_reports_disabled_providers()
    {
        var user = await CreateUserAsync(factory.Services, password: true);
        var tokens = await IssueTokensAsync(factory.Services, user);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/external/links");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await factory.CreateClient().SendAsync(request);

        response.EnsureSuccessStatusCode();
        var links = await response.Content.ReadFromJsonAsync<ExternalAuthLinksResponse>();
        Assert.NotNull(links);
        Assert.True(links.HasPassword);
        Assert.All(links.Providers, provider => Assert.False(provider.Enabled));
    }

    [Fact]
    public async Task Unlink_rejects_removing_the_last_usable_method()
    {
        await using var app = EnabledApp();
        var mapping = new ExternalLoginMapping(
            ExternalAuthProviders.Google,
            $"subject-{Guid.NewGuid():N}",
            "Google");
        var user = await CreateUserAsync(app.Services, password: false, mapping);
        var tokens = await IssueTokensAsync(app.Services, user);

        var response = await DeleteLinkAsync(app.CreateClient(), tokens, ExternalAuthProviders.Google);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ExternalAuthErrors.LastLoginMethod, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Unlink_missing_login_returns_stable_error()
    {
        await using var app = EnabledApp();
        var user = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, user);

        var response = await DeleteLinkAsync(
            app.CreateClient(), tokens, ExternalAuthProviders.Google);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ExternalAuthErrors.LoginNotLinked, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Unlink_invalid_provider_returns_stable_error()
    {
        await using var app = EnabledApp();
        var user = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, user);

        var response = await DeleteLinkAsync(app.CreateClient(), tokens, "github");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ExternalAuthErrors.InvalidProvider, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Link_attempt_cannot_be_exchanged_for_a_new_session()
    {
        await using var app = EnabledApp();
        var user = await CreateUserAsync(app.Services, password: true);
        var tokens = await IssueTokensAsync(app.Services, user);
        var (verifier, request) = LinkStartRequestWithVerifier();
        using var startMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/link/start")
        {
            Content = JsonContent.Create(request),
        };
        startMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var startResponse = await app.CreateClient().SendAsync(startMessage);
        startResponse.EnsureSuccessStatusCode();
        var start = await startResponse.Content.ReadFromJsonAsync<ExternalAuthStartResponse>();
        Assert.NotNull(start);
        var exchangeCode = await CompleteAttemptAsync(app.Services, start.AttemptId, user.Id);

        var response = await app.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/external/exchange",
            new ExternalAuthExchangeRequest(start.AttemptId, exchangeCode, verifier));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ExternalAuthErrors.ExchangeNotAllowed, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Unlink_is_allowed_with_a_password_or_a_second_external_login()
    {
        await using var app = EnabledApp();
        var passwordUser = await CreateUserAsync(app.Services, password: true);
        await AddLoginAsync(app.Services, passwordUser, ExternalAuthProviders.Google);
        var passwordTokens = await IssueTokensAsync(app.Services, passwordUser);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await DeleteLinkAsync(app.CreateClient(), passwordTokens, ExternalAuthProviders.Google)).StatusCode);

        var externalUser = await CreateUserAsync(
            app.Services,
            password: false,
            new ExternalLoginMapping(
                ExternalAuthProviders.Google,
                $"google-{Guid.NewGuid():N}",
                "Google"));
        await AddLoginAsync(app.Services, externalUser, ExternalAuthProviders.Microsoft);
        var externalTokens = await IssueTokensAsync(app.Services, externalUser);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await DeleteLinkAsync(app.CreateClient(), externalTokens, ExternalAuthProviders.Google)).StatusCode);
    }

    private WebApplicationFactory<Program> EnabledApp() => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ExternalAuth:Providers:Microsoft:ClientId", "test-microsoft-client");
        builder.UseSetting("ExternalAuth:Providers:Microsoft:ClientSecret", "test-microsoft-secret");
        builder.UseSetting("ExternalAuth:Providers:Microsoft:Enabled", "true");
        builder.UseSetting("ExternalAuth:Providers:Google:ClientId", "test-google-client");
        builder.UseSetting("ExternalAuth:Providers:Google:ClientSecret", "test-google-secret");
        builder.UseSetting("ExternalAuth:Providers:Google:Enabled", "true");
    });

    private static ExternalAuthStartRequest StartRequest(string? challenge = null) => new(
        ExternalAuthProviders.Google,
        ExternalAuthClientKinds.Desktop,
        ExternalAuthReturnRoutes.SignIn,
        challenge ?? new string('a', 43),
        ExternalAuthCodeChallengeMethods.S256,
        $"http://127.0.0.1:49152/castmill/auth/{new string('a', 43)}/");

    private static ExternalAuthStartRequest LinkStartRequest(string? challenge = null) => new(
        ExternalAuthProviders.Google,
        ExternalAuthClientKinds.Desktop,
        ExternalAuthReturnRoutes.AccountSettings,
        challenge ?? new string('a', 43),
        ExternalAuthCodeChallengeMethods.S256,
        $"http://127.0.0.1:49153/castmill/auth/{new string('b', 43)}/");

    private static (string Verifier, ExternalAuthStartRequest Request) StartRequestWithVerifier()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, StartRequest(challenge));
    }

    private static (string Verifier, ExternalAuthStartRequest Request) LinkStartRequestWithVerifier()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, LinkStartRequest(challenge));
    }

    private static async Task<ExternalAuthStartResponse> StartAsync(
        HttpClient client,
        ExternalAuthStartRequest? request = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/external/start",
            request ?? StartRequest());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExternalAuthStartResponse>())!;
    }

    private static async Task<ExternalAuthStartResponse> LinkStartAsync(
        HttpClient client,
        AuthResponse tokens,
        ExternalAuthStartRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/external/link/start")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExternalAuthStartResponse>())!;
    }

    private static async Task<string> CompleteProviderTicketAsync(
        IServiceProvider services,
        Guid attemptId,
        string providerKey,
        string email)
    {
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        var properties = new AuthenticationProperties();
        properties.Items[ExternalAuthSchemes.AttemptIdProperty] = attemptId.ToString("D");
        properties.Items[ExternalAuthSchemes.ProviderProperty] = ExternalAuthProviders.Google;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(
                ExternalIdentityResolver.ValidatedIssuerClaimType,
                "https://accounts.google.com"),
            new Claim("sub", providerKey),
            new Claim("email", email),
            new Claim("email_verified", "true"),
            new Claim("name", "Link Tester"),
        ], "synthetic-provider-callback"));
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(ExternalAuthSchemes.Google);
        var scheme = new AuthenticationScheme(
            ExternalAuthSchemes.Google,
            ExternalAuthSchemes.Google,
            typeof(OpenIdConnectHandler));
        var ticket = new AuthenticationTicket(principal, properties, ExternalAuthSchemes.Google);
        var received = new TicketReceivedContext(context, scheme, options, ticket);

        await options.Events.TicketReceived(received);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Headers.SetCookie.Count);
        return Assert.IsType<string>(context.Response.Headers.Location.ToString());
    }

    private static async Task SetExpiryAsync(
        IServiceProvider services,
        Guid attemptId,
        DateTimeOffset expiresAt)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.Where(attempt => attempt.Id == attemptId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(attempt => attempt.ExpiresAt, expiresAt));
    }

    private static async Task<string> CompleteAttemptAsync(
        IServiceProvider services,
        Guid attemptId,
        Guid userId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        var providerKey = $"completed-{Guid.NewGuid():N}";
        var login = await users.AddLoginAsync(
            user,
            new UserLoginInfo(ExternalAuthProviders.Google, providerKey, "Google"));
        Assert.True(login.Succeeded);
        var exchangeCode = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await db
            .ExternalAuthAttempts.Where(attempt => attempt.Id == attemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Status, ExternalAuthStatuses.Completed)
                .SetProperty(attempt => attempt.ExchangeCodeHash, ExternalAuthEndpoints.HashSecret(exchangeCode))
                .SetProperty(attempt => attempt.CandidateProviderKey, providerKey)
                .SetProperty(attempt => attempt.CandidateEmail, user.Email)
                .SetProperty(attempt => attempt.CandidateDisplayName, user.DisplayName)
                .SetProperty(attempt => attempt.CompletedAt, DateTimeOffset.UtcNow));
        return exchangeCode;
    }

    private static async Task<CastmillUser> CreateUserAsync(
        IServiceProvider services,
        bool password,
        ExternalLoginMapping? mapping = null)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAccountService>().CreateAsync(
            $"external-endpoint-{Guid.NewGuid():N}@example.com",
            "External Endpoint Tester",
            password ? "correct-horse-battery-staple" : null,
            mapping);
        Assert.True(result.Succeeded);
        return result.User!;
    }

    private static async Task AddLoginAsync(
        IServiceProvider services,
        CastmillUser user,
        string provider)
    {
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CastmillUser>>();
        var trackedUser = await users.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(trackedUser);
        var result = await scope.ServiceProvider.GetRequiredService<IAccountService>()
            .LinkExternalLoginAsync(
                trackedUser,
                new ExternalLoginMapping(provider, $"key-{Guid.NewGuid():N}", provider));
        Assert.True(result.Succeeded);
    }

    private static async Task<AuthResponse> IssueTokensAsync(
        IServiceProvider services,
        CastmillUser user)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthTokenIssuer>()
            .IssueAsync(user, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    private static async Task<HttpResponseMessage> DeleteLinkAsync(
        HttpClient client,
        AuthResponse tokens,
        string provider)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/auth/external/link/{provider}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("errorCode").GetString();
    }

    private static void AssertPolicy(
        IReadOnlyCollection<RouteEndpoint> endpoints,
        string route,
        string expectedPolicy)
    {
        var matches = endpoints.Where(candidate => candidate.RoutePattern.RawText == route).ToArray();
        Assert.NotEmpty(matches);
        Assert.All(matches, endpoint =>
        {
            var metadata = Assert.Single(
                endpoint.Metadata.GetOrderedMetadata<EnableRateLimitingAttribute>());
            Assert.Equal(expectedPolicy, metadata.PolicyName);
        });
    }

    private static string CallbackCode(string callbackLocation)
    {
        var fragment = new Uri(callbackLocation).Fragment.TrimStart('#');
        Assert.False(string.IsNullOrWhiteSpace(fragment));
        var values = QueryHelpers.ParseQuery("?" + fragment);
        Assert.Single(values["code"]);
        var code = values["code"].ToString();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", code);
        return code;
    }
}