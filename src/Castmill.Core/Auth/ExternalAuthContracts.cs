using System.ComponentModel.DataAnnotations;

namespace Castmill.Core.Auth;

public static class ExternalAuthProviders
{
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public const string Pattern = "^(microsoft|google)$";
}

public static class ExternalAuthClientKinds
{
    public const string Web = "web";
    public const string Desktop = "desktop";
    public const string Pattern = "^(web|desktop)$";
}

public static class ExternalAuthReturnRoutes
{
    public const string SignIn = "sign-in";
    public const string AccountSettings = "account-settings";
    public const string Pattern = "^(sign-in|account-settings)$";
}

public static class ExternalAuthCodeChallengeMethods
{
    public const string S256 = "S256";
    public const string Pattern = "^S256$";
}

public static class ExternalAuthStatuses
{
    public const string Pending = "pending";
    public const string Completing = "completing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Consumed = "consumed";
}

public static class ExternalAuthErrors
{
    public const string InvalidRequest = "external_auth_invalid_request";
    public const string InvalidProvider = "external_auth_invalid_provider";
    public const string AttemptNotFound = "external_auth_attempt_not_found";
    public const string AttemptExpired = "external_auth_attempt_expired";
    public const string AttemptFailed = "external_auth_attempt_failed";
    public const string AttemptPending = "external_auth_attempt_pending";
    public const string InvalidPollSecret = "external_auth_invalid_poll_secret";
    public const string InvalidExchangeCode = "external_auth_invalid_exchange_code";
    public const string InvalidCodeVerifier = "external_auth_invalid_code_verifier";
    public const string LoginAlreadyAssociated = "external_auth_login_already_associated";
    public const string EmailAlreadyExists = "external_auth_email_already_exists";
    public const string PasswordNotConfigured = "external_auth_password_not_configured";
    public const string AccountLinkRequired = "external_auth_account_link_required";
    public const string ExternalEmailRequired = "external_auth_email_required";
    public const string ProviderUnavailable = "external_auth_provider_unavailable";
    public const string CodeConsumed = "external_auth_code_consumed";
    public const string ExchangeNotAllowed = "external_auth_exchange_not_allowed";
    public const string LastLoginMethod = "external_auth_last_login_method";
    public const string LoginNotLinked = "external_auth_login_not_linked";
    public const string InvalidProviderIdentity = "external_auth_invalid_provider_identity";
}

public sealed record ExternalAuthProviderStatus(string Provider, bool Enabled);

public sealed record ExternalAuthProviderStatusResponse(
    IReadOnlyList<ExternalAuthProviderStatus> Providers);

public sealed record ExternalAuthLinkStatus(string Provider, bool Enabled, bool Linked);

public sealed record ExternalAuthLinksResponse(
    bool HasPassword,
    IReadOnlyList<ExternalAuthLinkStatus> Providers);

public sealed record ExternalAuthStartRequest(
    [property: Required, RegularExpression(ExternalAuthProviders.Pattern)] string Provider,
    [property: Required, RegularExpression(ExternalAuthClientKinds.Pattern)] string ClientKind,
    [property: Required, RegularExpression(ExternalAuthReturnRoutes.Pattern)] string ReturnRouteKey,
    [property: Required, RegularExpression("^[A-Za-z0-9_-]{43}$")] string CodeChallenge,
    [property: Required, RegularExpression(ExternalAuthCodeChallengeMethods.Pattern)] string CodeChallengeMethod,
    [property: MaxLength(512)] string? LoopbackReturnUri = null);

public sealed record ExternalAuthStartResponse(
    Guid AttemptId,
    string BrowserUrl,
    string PollSecret,
    DateTimeOffset ExpiresAt);

public sealed record ExternalAuthPollRequest(
    Guid AttemptId,
    [property: Required, MinLength(32), MaxLength(128)] string PollSecret);

public sealed record ExternalAuthPollResponse(
    string Status,
    DateTimeOffset ExpiresAt,
    string? ErrorCode = null);

public sealed record ExternalAuthExchangeRequest(
    Guid AttemptId,
    [property: Required, MinLength(32), MaxLength(128)] string ExchangeCode,
    [property: Required, MinLength(43), MaxLength(128)] string CodeVerifier);

public sealed class ExternalAuthOptions
{
    public const string SectionName = "ExternalAuth";

    [Range(1, 30)]
    public int AttemptLifetimeMinutes { get; set; } = 10;

    [Range(1, 168)]
    public int RetentionHours { get; set; } = 24;

    [Range(1, 1440)]
    public int CleanupIntervalMinutes { get; set; } = 15;

    public ExternalAuthProviderOptions Providers { get; set; } = new();
    public ExternalAuthClientOptions Clients { get; set; } = new();
}

public sealed class ExternalAuthProviderOptions
{
    public ExternalAuthProviderCredentials Microsoft { get; set; } = new();
    public ExternalAuthProviderCredentials Google { get; set; } = new();
}

public sealed class ExternalAuthProviderCredentials
{
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public sealed class ExternalAuthClientOptions
{
    public ExternalAuthClientReturnRoute Web { get; set; } = new();
    public ExternalAuthClientReturnRoute Desktop { get; set; } = new();
}

public sealed class ExternalAuthClientReturnRoute
{
    public string SignInReturnUri { get; set; } = string.Empty;

    public string AccountSettingsReturnUri { get; set; } = string.Empty;
}