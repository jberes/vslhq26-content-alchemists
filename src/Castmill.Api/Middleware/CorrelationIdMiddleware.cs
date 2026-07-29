using System.Text.RegularExpressions;

namespace Castmill.Api.Middleware;

/// <summary>
/// Propagates a correlation ID end-to-end (G7). Client-supplied values are
/// validated against a strict pattern — anything else is replaced, so a hostile
/// header can never inject content into logs or response headers.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = supplied is not null && ValidId().IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
