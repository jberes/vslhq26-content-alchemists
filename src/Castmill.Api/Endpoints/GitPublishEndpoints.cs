using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Api.Services.Publish;
using Castmill.Api.Services.Secrets;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

public sealed record GitRepoRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(100)] string Owner,
    [property: Required, MaxLength(100)] string Repo,
    [property: Required, MaxLength(20)] string Preset,
    [property: MaxLength(255)] string? BaseBranch,
    Guid? BrandId,
    [property: MaxLength(20)] string? Mode,
    bool OpenAsDraftPr = true,
    bool IsDefault = false,
    [property: MaxLength(8000)] string? LayoutJson = null);

public sealed record GitRepoResponse(
    Guid Id, Guid? BrandId, string Name, string Owner, string Repo, string? BaseBranch,
    string Preset, string Mode, bool OpenAsDraftPr, bool IsDefault, string LayoutJson);

public sealed record GitPublishRequest(
    [property: Required] Guid RepoProfileId,
    bool IncludeImages = true,
    [property: MaxLength(20)] string? Mode = null,
    bool? Draft = null);

public sealed record GitPublicationResponse(
    Guid Id, Guid ArtifactId, Guid RepoProfileId, string Branch, string CommitSha,
    int? PullRequestNumber, string? PullRequestUrl, string Status, string ContentPath,
    DateTimeOffset UpdatedAt);

public sealed record GitConnectionResponse(bool Ok, string? DefaultBranch, bool CanPush, string? Reason);

/// <summary>
/// Optional git publishing (ADR-021). The content lands as plain markdown in the customer's
/// own repository, which is the point: it stops being locked inside Castmill's database.
/// </summary>
public static class GitPublishEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapGitPublishEndpoints(this IEndpointRouteBuilder routes)
    {
        var repos = routes.MapGroup("/api/v1/git/repos").RequireAuthorization("TenantAllowed");
        repos.MapGet("/", ListAsync);
        repos.MapPost("/", CreateAsync).Validate<GitRepoRequest>().RequireRateLimiting("writes");
        repos.MapPut("/{id:guid}", UpdateAsync).Validate<GitRepoRequest>().RequireRateLimiting("writes");
        repos.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting("writes");
        repos.MapPost("/{id:guid}/test", TestAsync).RequireRateLimiting("writes");

        var publish = routes.MapGroup("/api/v1/campaigns").RequireAuthorization("TenantAllowed");
        publish.MapPost("/{campaignId:guid}/artifacts/{artifactId:guid}/publish/github/preview", PreviewAsync)
            .Validate<GitPublishRequest>().RequireRateLimiting("writes");
        publish.MapPost("/{campaignId:guid}/artifacts/{artifactId:guid}/publish/github", PublishAsync)
            .Validate<GitPublishRequest>().RequireRateLimiting("writes");
        publish.MapGet("/{campaignId:guid}/artifacts/{artifactId:guid}/publish/github", HistoryAsync);
        return routes;
    }

    // ---- repo profiles ----------------------------------------------------------

    private static async Task<IResult> ListAsync(CastmillDbContext db, CancellationToken ct) =>
        Results.Ok(await db.GitRepoProfiles
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .Select(p => new GitRepoResponse(
                p.Id, p.BrandId, p.Name, p.Owner, p.Repo, p.BaseBranch, p.Preset, p.Mode,
                p.OpenAsDraftPr, p.IsDefault, p.LayoutJson))
            .ToListAsync(ct));

    private static async Task<IResult> CreateAsync(
        GitRepoRequest request, ITenantProvider tenant, CastmillDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var now = clock.GetUtcNow();
        var profile = new GitRepoProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("A tenant is required."),
            BrandId = request.BrandId,
            Name = request.Name,
            Owner = request.Owner,
            Repo = request.Repo,
            BaseBranch = request.BaseBranch,
            Preset = request.Preset,
            Mode = request.Mode ?? "pull-request",
            OpenAsDraftPr = request.OpenAsDraftPr,
            IsDefault = request.IsDefault,
            LayoutJson = LayoutJson(request),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.GitRepoProfiles.Add(profile);
        await ClearOtherDefaultsAsync(db, profile, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/git/repos/{profile.Id}", ToResponse(profile));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, GitRepoRequest request, CastmillDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var profile = await db.GitRepoProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        profile.BrandId = request.BrandId;
        profile.Name = request.Name;
        profile.Owner = request.Owner;
        profile.Repo = request.Repo;
        profile.BaseBranch = request.BaseBranch;
        profile.Preset = request.Preset;
        profile.Mode = request.Mode ?? profile.Mode;
        profile.OpenAsDraftPr = request.OpenAsDraftPr;
        profile.IsDefault = request.IsDefault;
        profile.LayoutJson = LayoutJson(request);
        profile.UpdatedAt = clock.GetUtcNow();

        await ClearOtherDefaultsAsync(db, profile, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(profile));
    }

    private static async Task<IResult> DeleteAsync(Guid id, CastmillDbContext db, CancellationToken ct)
    {
        var profile = await db.GitRepoProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        db.GitRepoProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Proves the token can actually write here, before the user discovers otherwise
    /// mid-publish. A fine-grained PAT needs Contents: read and write (which covers the whole
    /// Git Database API), Pull requests: read and write, and Metadata: read-only.
    /// </summary>
    private static async Task<IResult> TestAsync(
        Guid id, ClaimsPrincipal principal, CastmillDbContext db,
        IGitHubClient github, IUserSecretsService secrets, CancellationToken ct)
    {
        var profile = await db.GitRepoProfiles.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var (token, error) = await ResolveTokenAsync(principal, secrets, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var repository = await github.GetRepositoryAsync(token, profile.Owner, profile.Repo, ct);
            return Results.Ok(new GitConnectionResponse(
                repository.CanPush, repository.DefaultBranch, repository.CanPush,
                repository.CanPush
                    ? null
                    : "The token can read this repository but not write to it — it needs Contents: read and write."));
        }
        catch (GitHubApiException ex)
        {
            return Results.Ok(new GitConnectionResponse(false, null, false, ex.Message));
        }
    }

    // ---- publishing -------------------------------------------------------------

    private static async Task<IResult> PreviewAsync(
        Guid campaignId, Guid artifactId, GitPublishRequest request,
        CastmillDbContext db, IGitHubPublisher publisher, CancellationToken ct)
    {
        var loaded = await LoadAsync(campaignId, artifactId, request.RepoProfileId, db, ct);
        if (loaded.Error is not null)
        {
            return loaded.Error;
        }

        return Results.Ok(await publisher.PreviewAsync(loaded.Profile!, loaded.Artifact!, ct));
    }

    private static async Task<IResult> PublishAsync(
        Guid campaignId, Guid artifactId, GitPublishRequest request, ClaimsPrincipal principal,
        CastmillDbContext db, IGitHubPublisher publisher, IUserSecretsService secrets,
        TimeProvider clock, ITenantProvider tenant, CancellationToken ct)
    {
        var loaded = await LoadAsync(campaignId, artifactId, request.RepoProfileId, db, ct);
        if (loaded.Error is not null)
        {
            return loaded.Error;
        }

        var (token, error) = await ResolveTokenAsync(principal, secrets, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var outcome = await publisher.PublishAsync(
                token, loaded.Profile!, loaded.Artifact!,
                new GitPublishRequestOptions(request.IncludeImages, request.Mode, request.Draft), ct);

            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                UserId = AuthEndpoints.GetUserId(principal),
                Action = "publish.github",
                Detail = $"{loaded.Profile!.Owner}/{loaded.Profile.Repo}"
                         + (outcome.PullRequestNumber is { } n ? $"#{n}" : $"@{outcome.Branch}"),
                OccurredAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(outcome);
        }
        catch (GitHubApiException ex)
        {
            // GitHub's own message is the actionable part ("Resource not accessible by
            // personal access token"), and it never contains the credential.
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, detail: ex.Message);
        }
    }

    private static async Task<IResult> HistoryAsync(
        Guid campaignId, Guid artifactId, CastmillDbContext db, CancellationToken ct) =>
        Results.Ok(await db.GitPublications
            .Where(p => p.ArtifactId == artifactId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new GitPublicationResponse(
                p.Id, p.ArtifactId, p.RepoProfileId, p.Branch, p.CommitSha,
                p.PullRequestNumber, p.PullRequestUrl, p.Status, p.ContentPath, p.UpdatedAt))
            .ToListAsync(ct));

    // ---- shared -----------------------------------------------------------------

    private static async Task<(GitRepoProfile? Profile, Artifact? Artifact, IResult? Error)> LoadAsync(
        Guid campaignId, Guid artifactId, Guid profileId, CastmillDbContext db, CancellationToken ct)
    {
        var profile = await db.GitRepoProfiles.SingleOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile is null)
        {
            return (null, null, Results.NotFound());
        }

        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == artifactId && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return (null, null, Results.NotFound());
        }

        if (artifact.Kind.Equals("transcript", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "A transcript is source material, not something to publish."));
        }

        return (profile, artifact, null);
    }

    private static async Task<(string Token, IResult? Error)> ResolveTokenAsync(
        ClaimsPrincipal principal, IUserSecretsService secrets, CancellationToken ct)
    {
        var token = await secrets.GetAsync(AuthEndpoints.GetUserId(principal), SecretKind.GitHubToken, ct);
        return string.IsNullOrWhiteSpace(token)
            ? (string.Empty, Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                detail: "No GitHub token stored. Set one via PUT /api/v1/settings/secrets/GitHubToken."))
            : (token, null);
    }

    private static GitRepoResponse ToResponse(GitRepoProfile p) =>
        new(p.Id, p.BrandId, p.Name, p.Owner, p.Repo, p.BaseBranch, p.Preset, p.Mode,
            p.OpenAsDraftPr, p.IsDefault, p.LayoutJson);

    /// <summary>At most one default per brand, so "which repo?" always has an answer.</summary>
    private static async Task ClearOtherDefaultsAsync(
        CastmillDbContext db, GitRepoProfile profile, CancellationToken ct)
    {
        if (!profile.IsDefault)
        {
            return;
        }

        var others = await db.GitRepoProfiles
            .Where(p => p.Id != profile.Id && p.BrandId == profile.BrandId && p.IsDefault)
            .ToListAsync(ct);
        foreach (var other in others)
        {
            other.IsDefault = false;
        }
    }

    private static string LayoutJson(GitRepoRequest request)
    {
        var layout = GitRepoLayout.Parse(request.LayoutJson, request.Preset);
        return JsonSerializer.Serialize(layout, Json);
    }

    /// <summary>
    /// Refuses paths that would escape the repository or reach somewhere a publish has no
    /// business writing. <c>.github/workflows</c> specifically: writing there needs a
    /// Workflows permission we deliberately do not ask for, and it is the obvious
    /// privilege-escalation target if a token happened to have it.
    /// </summary>
    private static IResult? Validate(GitRepoRequest request)
    {
        var layout = GitRepoLayout.Parse(request.LayoutJson, request.Preset);

        foreach (var path in new[] { layout.ContentPath, layout.ImagePath, layout.ContentFileTemplate })
        {
            if (path.Contains("..", StringComparison.Ordinal)
                || path.StartsWith('/')
                || path.StartsWith('\\')
                || path.Replace('\\', '/').StartsWith(".github/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    detail: $"'{path}' is not a publishable path. Paths are repository-relative and "
                            + "may not traverse upward or write into .github/.");
            }
        }

        if (!request.Owner.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            || !request.Repo.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Owner and repository names may only contain letters, digits, '-', '_' and '.'.");
        }

        return null;
    }
}
