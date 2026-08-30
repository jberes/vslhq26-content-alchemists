using Castmill.Core.Auth;
using Castmill.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Auth;

public static class ExternalAuthSchemes
{
    public const string Microsoft = "MicrosoftExternal";
    public const string Google = "GoogleExternal";
    public const string AttemptIdProperty = "castmill:attempt_id";
    public const string ProviderProperty = "castmill:provider";

    public static AuthenticationBuilder AddExternalProviders(
        this AuthenticationBuilder authentication,
        IConfiguration configuration)
    {
        var external = configuration.GetSection(ExternalAuthOptions.SectionName)
            .Get<ExternalAuthOptions>() ?? new ExternalAuthOptions();
        var remoteTimeout = TimeSpan.FromMinutes(
            Math.Clamp(external.AttemptLifetimeMinutes, 1, 30));

        if (IsConfigured(external.Providers.Microsoft))
        {
            authentication.AddOpenIdConnect(Microsoft, options =>
            {
                ConfigureCommon(
                    options,
                    Microsoft,
                    ExternalAuthProviders.Microsoft,
                    "/signin-microsoft",
                    remoteTimeout);
                options.Authority = "https://login.microsoftonline.com/common/v2.0";
                options.ClientId = external.Providers.Microsoft.ClientId;
                options.ClientSecret = external.Providers.Microsoft.ClientSecret;
                options.Scope.Add("User.Read");
                options.TokenValidationParameters.IssuerValidator =
                    ExternalIdentityResolver.ValidateMicrosoftIssuer;
            });
        }

        if (IsConfigured(external.Providers.Google))
        {
            authentication.AddOpenIdConnect(Google, options =>
            {
                ConfigureCommon(
                    options,
                    Google,
                    ExternalAuthProviders.Google,
                    "/signin-google",
                    remoteTimeout);
                options.Authority = "https://accounts.google.com";
                options.ClientId = external.Providers.Google.ClientId;
                options.ClientSecret = external.Providers.Google.ClientSecret;
                options.TokenValidationParameters.IssuerValidator =
                    ExternalIdentityResolver.ValidateGoogleIssuer;
            });
        }

        return authentication;
    }

    public static bool IsConfigured(ExternalAuthProviderCredentials credentials) =>
        credentials.Enabled
        && !string.IsNullOrWhiteSpace(credentials.ClientId)
        && !string.IsNullOrWhiteSpace(credentials.ClientSecret);

    public static bool IsValidConfiguration(ExternalAuthProviderCredentials credentials) =>
        !credentials.Enabled || IsConfigured(credentials);

    public static string SchemeFor(string provider) => provider switch
    {
        ExternalAuthProviders.Microsoft => Microsoft,
        ExternalAuthProviders.Google => Google,
        _ => throw new InvalidOperationException("Unsupported external authentication provider."),
    };

    private static void ConfigureCommon(
        OpenIdConnectOptions options,
        string scheme,
        string provider,
        string callbackPath,
        TimeSpan remoteTimeout)
    {
        options.CallbackPath = callbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.RemoteAuthenticationTimeout = remoteTimeout;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.NameClaimType = "name";

        options.CorrelationCookie.Name = $"Castmill.Correlation.{scheme}.";
        options.CorrelationCookie.HttpOnly = true;
        options.CorrelationCookie.IsEssential = true;
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.Name = $"Castmill.Nonce.{scheme}.";
        options.NonceCookie.HttpOnly = true;
        options.NonceCookie.IsEssential = true;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnTokenValidated = async context =>
        {
            ExternalIdentityResolver.AddValidatedIssuerClaim(
                context.Principal,
                context.SecurityToken.Issuer);
            if (context.Properties is { } properties
                && properties.Items.TryGetValue(AttemptIdProperty, out var value)
                && Guid.TryParse(value, out var attemptId)
                && context.TokenEndpointResponse?.AccessToken is { Length: > 0 } accessToken)
            {
                var avatars = context.HttpContext.RequestServices
                    .GetRequiredService<IExternalAvatarCaptureService>();
                await avatars.CaptureAsync(
                    attemptId,
                    provider,
                    accessToken,
                    context.HttpContext.RequestAborted);
            }
        };
        options.Events.OnTicketReceived = context => CompleteTicketAsync(context, provider);

        options.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            var returnUri = ExternalAuthEndpoints.FinishedPath;
            if (context.Properties?.Items.TryGetValue(AttemptIdProperty, out var value) == true
                && Guid.TryParse(value, out var attemptId)
                && context.Properties.Items.TryGetValue(ProviderProperty, out var propertyProvider)
                && string.Equals(propertyProvider, provider, StringComparison.Ordinal))
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<CastmillDbContext>();
                var attemptProvider = await db.ExternalAuthAttempts
                    .AsNoTracking()
                    .Where(attempt => attempt.Id == attemptId)
                    .Select(attempt => attempt.Provider)
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                if (!string.Equals(attemptProvider, provider, StringComparison.Ordinal))
                {
                    context.Response.Redirect(returnUri);
                    return;
                }

                var externalOptions = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<ExternalAuthOptions>>();
                returnUri = await ExternalAuthEndpoints.ReturnUriForAttemptAsync(
                    attemptId,
                    db,
                    externalOptions.Value,
                    context.HttpContext.RequestAborted,
                    errorCode: ExternalAuthErrors.AttemptFailed);
                var completion = context.HttpContext.RequestServices
                    .GetRequiredService<IExternalAuthCompletionService>();
                await completion.FailAsync(
                    attemptId,
                    ExternalAuthErrors.AttemptFailed,
                    context.HttpContext.RequestAborted);
            }

            context.Response.Redirect(returnUri);
        };
    }

    internal static async Task CompleteTicketAsync(
        TicketReceivedContext context,
        string provider)
    {
        context.HandleResponse();
        var returnUri = ExternalAuthEndpoints.FinishedPath;
        if (context.Principal is not null
            && context.Properties is { } properties
            && properties.Items.TryGetValue(AttemptIdProperty, out var value)
            && Guid.TryParse(value, out var attemptId)
            && properties.Items.TryGetValue(ProviderProperty, out var propertyProvider)
            && string.Equals(propertyProvider, provider, StringComparison.Ordinal))
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<CastmillDbContext>();
            var attemptProvider = await db.ExternalAuthAttempts
                .AsNoTracking()
                .Where(attempt => attempt.Id == attemptId)
                .Select(attempt => attempt.Provider)
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
            if (string.Equals(attemptProvider, provider, StringComparison.Ordinal))
            {
                var completion = context.HttpContext.RequestServices
                    .GetRequiredService<IExternalAuthCompletionService>();
                var result = await completion.CompleteAsync(
                    attemptId,
                    context.Principal,
                    context.HttpContext.RequestAborted);
                var externalOptions = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<ExternalAuthOptions>>();
                returnUri = await ExternalAuthEndpoints.ReturnUriForAttemptAsync(
                    attemptId,
                    db,
                    externalOptions.Value,
                    context.HttpContext.RequestAborted,
                    result.ExchangeCode,
                    result.ErrorCode);
            }
        }

        context.Response.Redirect(returnUri);
    }
}