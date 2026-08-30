using System.Security.Claims;

namespace Castmill.Api.Tenancy;

public interface ITenantProvider
{
    /// <summary>Tenant of the current request, or null when unauthenticated.</summary>
    Guid? TenantId { get; }

    /// <summary>Authenticated actor, used only for ownership checks.</summary>
    Guid? UserId => null;

    /// <summary>Uppercase Identity-normalized email used for exact campaign grants.</summary>
    string? NormalizedEmail => null;

    /// <summary>Uppercase domain from the validated JWT email claim.</summary>
    string? EmailDomain => null;
}

/// <summary>Resolves the tenant from the validated JWT's "tenant" claim — never from client-supplied headers or route values.</summary>
public sealed class HttpContextTenantProvider(IHttpContextAccessor accessor) : ITenantProvider
{
    public const string TenantClaim = "tenant";

    public Guid? TenantId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(TenantClaim);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public Guid? UserId
    {
        get
        {
            var value = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? accessor.HttpContext?.User.FindFirstValue("sub");
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public string? NormalizedEmail
    {
        get
        {
            var email = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
                ?? accessor.HttpContext?.User.FindFirstValue("email");
            return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();
        }
    }

    public string? EmailDomain
    {
        get
        {
            var email = NormalizedEmail;
            var separator = email?.LastIndexOf('@') ?? -1;
            return separator >= 0 && separator < email!.Length - 1
                ? email[(separator + 1)..]
                : null;
        }
    }
}
