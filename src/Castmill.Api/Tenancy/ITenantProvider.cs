using System.Security.Claims;

namespace Castmill.Api.Tenancy;

public interface ITenantProvider
{
    /// <summary>Tenant of the current request, or null when unauthenticated.</summary>
    Guid? TenantId { get; }
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
}
