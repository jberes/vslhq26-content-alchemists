using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Castmill.Core.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Castmill.Api.Auth;

public sealed record ExternalIdentity(
    string Provider,
    string ProviderKey,
    string Email,
    string DisplayName);

public interface IExternalIdentityResolver
{
    ExternalIdentity Resolve(string provider, ClaimsPrincipal syntheticPrincipal);
}

public sealed class ExternalIdentityResolver : IExternalIdentityResolver
{
    internal const string ValidatedIssuerClaimType = "castmill:validated_issuer";

    public ExternalIdentity Resolve(string provider, ClaimsPrincipal syntheticPrincipal) => provider switch
    {
        ExternalAuthProviders.Microsoft => ResolveMicrosoft(syntheticPrincipal),
        ExternalAuthProviders.Google => ResolveGoogle(syntheticPrincipal),
        _ => throw new ExternalIdentityException(ExternalAuthErrors.InvalidProviderIdentity),
    };

    public static string ValidateMicrosoftIssuer(
        string issuer,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        _ = securityToken;
        _ = validationParameters;
        ParseMicrosoftIssuerTenant(issuer);
        return issuer;
    }

    public static string ValidateGoogleIssuer(
        string issuer,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        _ = securityToken;
        _ = validationParameters;
        if (issuer is not "accounts.google.com" and not "https://accounts.google.com")
        {
            throw new SecurityTokenInvalidIssuerException("The Google issuer is not trusted.");
        }

        return issuer;
    }

    internal static void AddValidatedIssuerClaim(ClaimsPrincipal? principal, string issuer)
    {
        if (principal?.Identity is not ClaimsIdentity identity || string.IsNullOrWhiteSpace(issuer))
        {
            throw new SecurityTokenInvalidIssuerException("The validated issuer is unavailable.");
        }

        foreach (var existing in identity.FindAll(ValidatedIssuerClaimType).ToArray())
        {
            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(ValidatedIssuerClaimType, issuer));
    }

    private static ExternalIdentity ResolveMicrosoft(ClaimsPrincipal principal)
    {
        var issuerTenant = ParseMicrosoftIssuerTenant(
            RequiredClaim(principal, ValidatedIssuerClaimType));
        if (!Guid.TryParse(RequiredClaim(principal, "tid"), out var tenantId)
            || !Guid.TryParse(RequiredClaim(principal, "oid"), out var objectId)
            || issuerTenant != tenantId)
        {
            throw new ExternalIdentityException(ExternalAuthErrors.InvalidProviderIdentity);
        }

        // Work-account ID tokens commonly omit `email` and return `preferred_username`.
        // It is contact metadata only; identity remains the immutable tid+oid key above.
        var email = RequiredUsableEmail(principal, "email", "preferred_username");
        return new ExternalIdentity(
            ExternalAuthProviders.Microsoft,
            $"{tenantId:N}:{objectId:N}",
            email,
            DisplayName(principal, email));
    }

    private static ExternalIdentity ResolveGoogle(ClaimsPrincipal principal)
    {
        try
        {
            ValidateGoogleIssuer(
                RequiredClaim(principal, ValidatedIssuerClaimType),
                null!,
                null!);
        }
        catch (SecurityTokenInvalidIssuerException exception)
        {
            throw new ExternalIdentityException(
                ExternalAuthErrors.InvalidProviderIdentity,
                exception);
        }

        var subject = RequiredClaim(principal, "sub");
        if (!string.Equals(RequiredClaim(principal, "email_verified"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalIdentityException(ExternalAuthErrors.ExternalEmailRequired);
        }

        var email = RequiredUsableEmail(principal, "email");
        return new ExternalIdentity(
            ExternalAuthProviders.Google,
            subject,
            email,
            DisplayName(principal, email));
    }

    private static Guid ParseMicrosoftIssuerTenant(string issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new SecurityTokenInvalidIssuerException("The Microsoft issuer is not trusted.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2
            || !Guid.TryParse(segments[0], out var tenantId)
            || !string.Equals(segments[1], "v2.0", StringComparison.Ordinal))
        {
            throw new SecurityTokenInvalidIssuerException("The Microsoft issuer tenant is invalid.");
        }

        return tenantId;
    }

    private static string RequiredClaim(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirstValue(claimType);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ExternalIdentityException(ExternalAuthErrors.InvalidProviderIdentity);
        }

        return value;
    }

    private static string RequiredUsableEmail(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirstValue(claimType)?.Trim();
            if (!string.IsNullOrWhiteSpace(value)
                && value.Length <= 320
                && new EmailAddressAttribute().IsValid(value))
            {
                return value;
            }
        }

        throw new ExternalIdentityException(ExternalAuthErrors.ExternalEmailRequired);
    }

    private static string DisplayName(ClaimsPrincipal principal, string email)
    {
        var value = principal.FindFirstValue("name")?.Trim() is { Length: > 0 } name
            ? name
            : email.Split('@')[0];
        return value[..Math.Min(value.Length, 200)];
    }
}

public sealed class ExternalIdentityException : Exception
{
    public ExternalIdentityException(string errorCode, Exception? innerException = null)
        : base("The external identity is invalid.", innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}