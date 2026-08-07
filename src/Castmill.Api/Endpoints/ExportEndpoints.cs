using Castmill.Api.Data;
using Castmill.Api.Services.Export;
using Castmill.Core.Content;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Endpoints;

/// <summary>
/// Roadmap 5.6 — getting the work back out. Downloads rather than JSON: the browser saves
/// these, and a base64 payload in a JSON envelope would only make the client re-decode what
/// the transport already knows how to stream.
/// </summary>
public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/campaigns").RequireAuthorization("TenantAllowed");

        group.MapGet("/{campaignId:guid}/artifacts/{artifactId:guid}/export", ArtifactAsync);
        group.MapGet("/{campaignId:guid}/export", CampaignAsync);
        return routes;
    }

    private static async Task<IResult> ArtifactAsync(
        Guid campaignId,
        Guid artifactId,
        string? format,
        CastmillDbContext db,
        IExportService export,
        CancellationToken ct)
    {
        // Tenant-filtered by the global query filter, so another tenant's id is a 404.
        var artifact = await db.Artifacts
            .SingleOrDefaultAsync(a => a.Id == artifactId && a.CampaignId == campaignId, ct);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        var slug = ExportService.Slug(artifact.Title);

        return (format ?? "md").ToLowerInvariant() switch
        {
            "md" or "markdown" => Results.File(
                System.Text.Encoding.UTF8.GetBytes(export.Markdown(artifact)),
                "text/markdown; charset=utf-8", $"{slug}.md"),

            "docx" => Results.File(
                export.Docx(artifact),
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"{slug}.docx"),

            _ => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                detail: "Supported formats are 'md' and 'docx'."),
        };
    }

    private static async Task<IResult> CampaignAsync(
        Guid campaignId,
        CastmillDbContext db,
        IExportService export,
        CancellationToken ct)
    {
        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            return Results.NotFound();
        }

        // The transcript is source material rather than output: it would dominate the archive
        // and it is not something anyone publishes.
        var artifacts = await db.Artifacts
            .Where(a => a.CampaignId == campaignId && a.Kind != "transcript")
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return Results.File(
            export.Zip(campaign, artifacts),
            "application/zip",
            $"{ExportService.Slug(campaign.Name)}.zip");
    }
}
