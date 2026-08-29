namespace Castmill.Api.Auth;

public sealed class ExternalAuthAttempt
{
    public Guid Id { get; set; }
    public required string Provider { get; set; }
    public required string ClientKind { get; set; }
    public required string ReturnRouteKey { get; set; }
    public required string CodeChallenge { get; set; }
    public required string PollSecretHash { get; set; }
    public string? ExchangeCodeHash { get; set; }
    public string? CandidateProviderKey { get; set; }
    public string? CandidateEmail { get; set; }
    public string? CandidateDisplayName { get; set; }
    public string? LoopbackReturnUri { get; set; }
    public required string Status { get; set; }
    public string? ErrorCode { get; set; }
    public Guid? UserId { get; set; }
    public Guid? LinkUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}