namespace Castmill.UI.Http;

/// <summary>
/// The typed error envelope every API call funnels into, so features handle named cases
/// rather than status codes (Frontend-Architecture.md §4, Reliability).
/// </summary>
public class ApiException(string message, int statusCode, string? correlationId = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>The correlation ID the server echoed, for matching a client report to a server log.</summary>
    public string? CorrelationId { get; } = correlationId;
}

/// <summary>Authentication failed and a silent refresh could not rescue it — the user must sign in.</summary>
public sealed class UnauthorizedApiException(string? correlationId = null)
    : ApiException("Your session has expired. Please sign in again.", 401, correlationId);

/// <summary>
/// A conditional write lost a race (412), or was sent without an ETag (428). Features turn
/// this into the designed reload-or-merge prompt rather than a generic failure.
/// </summary>
public sealed class ConflictApiException(string message, int statusCode, string? correlationId = null)
    : ApiException(message, statusCode, correlationId);

/// <summary>Server-side validation failed; <see cref="Errors"/> is field name to messages.</summary>
public sealed class ValidationApiException(
    IReadOnlyDictionary<string, string[]> errors,
    string? correlationId = null)
    : ApiException("Some of that isn't valid yet.", 400, correlationId)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
