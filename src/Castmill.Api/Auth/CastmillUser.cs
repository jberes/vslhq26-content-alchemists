using Microsoft.AspNetCore.Identity;

namespace Castmill.Api.Auth;

public sealed class CastmillUser : IdentityUser<Guid>
{
    /// <summary>Permanent tenant binding, set once at registration (ADR-011).</summary>
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public byte[]? AvatarImage { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
