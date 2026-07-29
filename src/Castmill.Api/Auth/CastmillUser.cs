using Microsoft.AspNetCore.Identity;

namespace Castmill.Api.Auth;

public sealed class CastmillUser : IdentityUser<Guid>
{
    /// <summary>Permanent tenant binding, set once at registration (ADR-011).</summary>
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
