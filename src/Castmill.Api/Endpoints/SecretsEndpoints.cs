using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Castmill.Api.Auth;
using Castmill.Api.Services.Secrets;

namespace Castmill.Api.Endpoints;

public sealed record SecretWriteRequest([property: Required, MinLength(1), MaxLength(4000)] string Value);

/// <summary>
/// Encrypted per-user secrets (Foundry credentials, broker token).
/// Contract: values go IN and are never returned by any endpoint — status
/// reports only which kinds are configured and when they were last updated.
/// </summary>
public static class SecretsEndpoints
{
    public static IEndpointRouteBuilder MapSecretsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/settings/secrets").RequireAuthorization("TenantAllowed");

        group.MapGet("/", StatusAsync);
        group.MapPut("/{kind}", SetAsync).Validate<SecretWriteRequest>().RequireRateLimiting("writes");
        group.MapDelete("/{kind}", RemoveAsync).RequireRateLimiting("writes");
        return routes;
    }

    private static bool TryParseKind(string raw, out SecretKind kind) =>
        Enum.TryParse(raw, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static async Task<IResult> StatusAsync(
        ClaimsPrincipal principal, IUserSecretsService secrets, CancellationToken ct)
    {
        var status = await secrets.StatusAsync(AuthEndpoints.GetUserId(principal), ct);
        return Results.Ok(Enum.GetValues<SecretKind>().Select(k => new
        {
            Kind = k.ToString(),
            Configured = status.ContainsKey(k),
            UpdatedAt = status.TryGetValue(k, out var at) ? at : (DateTimeOffset?)null,
        }));
    }

    private static async Task<IResult> SetAsync(
        string kind,
        SecretWriteRequest request,
        ClaimsPrincipal principal,
        IUserSecretsService secrets,
        CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsed))
        {
            return Results.NotFound();
        }
        await secrets.SetAsync(AuthEndpoints.GetUserId(principal), parsed, request.Value, ct);
        // Deliberately no echo of the value — 204, not the stored secret.
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(
        string kind, ClaimsPrincipal principal, IUserSecretsService secrets, CancellationToken ct)
    {
        if (!TryParseKind(kind, out var parsed))
        {
            return Results.NotFound();
        }
        return await secrets.RemoveAsync(AuthEndpoints.GetUserId(principal), parsed, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }
}
