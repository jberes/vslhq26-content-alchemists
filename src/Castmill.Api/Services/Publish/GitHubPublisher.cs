using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Castmill.Api.Data;
using Castmill.Api.Services.Blob;
using Castmill.Api.Services.Export;
using Castmill.Api.Tenancy;
using Castmill.Core;
using Castmill.Core.Content;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Publish;

public sealed record GitPublishRequestOptions(bool IncludeImages, string? Mode, bool? Draft);

public sealed record GitPublishPreview(
    string ContentPath, string FrontMatter, IReadOnlyList<string> ImagePaths, string Branch);

public sealed record GitPublishOutcome(
    string Branch, string CommitSha, int? PullRequestNumber, string? PullRequestUrl,
    IReadOnlyList<string> Files);

public interface IGitHubPublisher
{
    /// <summary>What WOULD be written. No GitHub call — the point is to see it before committing.</summary>
    Task<GitPublishPreview> PreviewAsync(GitRepoProfile profile, Artifact artifact, CancellationToken ct);

    Task<GitPublishOutcome> PublishAsync(
        string token, GitRepoProfile profile, Artifact artifact,
        GitPublishRequestOptions options, CancellationToken ct);
}

public sealed partial class GitHubPublisher(
    IGitHubClient github,
    IPublicContentStore publicStore,
    CastmillDbContext db,
    ITenantProvider tenant,
    TimeProvider clock) : IGitHubPublisher
{
    public async Task<GitPublishPreview> PreviewAsync(
        GitRepoProfile profile, Artifact artifact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(artifact);

        var layout = GitRepoLayout.Parse(profile.LayoutJson, profile.Preset);
        var slug = ExportService.Slug(artifact.Title);
        var images = await CollectImagesAsync(artifact, layout, slug, includeImages: true, ct);

        return new GitPublishPreview(
            layout.ContentFilePath(slug, artifact.CreatedAt),
            FrontMatter(layout, artifact, slug, images),
            [.. images.Select(i => i.RepoPath)],
            Branch(slug));
    }

    public async Task<GitPublishOutcome> PublishAsync(
        string token, GitRepoProfile profile, Artifact artifact,
        GitPublishRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);

        var layout = GitRepoLayout.Parse(profile.LayoutJson, profile.Preset);
        var slug = ExportService.Slug(artifact.Title);
        var branch = Branch(slug);
        var mode = options.Mode ?? profile.Mode;
        var direct = mode.Equals("direct-commit", StringComparison.OrdinalIgnoreCase);

        var repository = await github.GetRepositoryAsync(token, profile.Owner, profile.Repo, ct);
        var baseBranch = profile.BaseBranch ?? repository.DefaultBranch;

        var images = await CollectImagesAsync(artifact, layout, slug, options.IncludeImages, ct);

        var body = ArtifactMarkdown.Body(artifact.ContentJson)
                   ?? ArtifactMarkdown.ForExport(artifact.Kind, artifact.Title, artifact.ContentJson);

        // The markdown carries absolute blob URLs; the repo needs its own paths. Missing this
        // is how a PR merges green and the published page shows broken images.
        foreach (var image in images)
        {
            body = body.Replace(image.SourceUrl, layout.ImageReference(slug, image.FileName), StringComparison.Ordinal);
        }

        var contentPath = layout.ContentFilePath(slug, artifact.CreatedAt);
        var files = new List<GitFile>
        {
            // Explicit \n and no BOM: \r\n fills the diff with ^M and a BOM makes Hugo and
            // Jekyll fail to see the front matter at all.
            new(contentPath,
                new UTF8Encoding(false).GetBytes(
                    FrontMatter(layout, artifact, slug, images) + body.Replace("\r\n", "\n", StringComparison.Ordinal)),
                IsText: true),
        };
        files.AddRange(images.Select(i => new GitFile(i.RepoPath, i.Bytes, IsText: false)));

        var existing = await db.GitPublications
            .FirstOrDefaultAsync(p => p.ArtifactId == artifact.Id && p.RepoProfileId == profile.Id, ct);
        var verb = existing is null ? "Add" : "Update";

        var targetBranch = direct ? baseBranch : branch;
        var commitSha = await github.CommitAsync(
            token, profile.Owner, profile.Repo, baseBranch, targetBranch,
            $"{verb} post: {artifact.Title}", files, ct);

        GitHubPullRequest? pull = null;
        if (!direct)
        {
            // Reuse the branch's open PR rather than opening #47. This is the detail naive
            // implementations get wrong, and the reason branch names are deterministic.
            pull = await github.FindOpenPullRequestAsync(token, profile.Owner, profile.Repo, branch, ct)
                   ?? await github.CreatePullRequestAsync(
                       token, profile.Owner, profile.Repo, branch, baseBranch,
                       $"{verb} post: {artifact.Title}",
                       PullRequestBody(artifact, files),
                       options.Draft ?? profile.OpenAsDraftPr, ct);
        }

        await RecordAsync(existing, profile, artifact, targetBranch, commitSha, contentPath, pull, direct, ct);

        return new GitPublishOutcome(
            targetBranch, commitSha, pull?.Number, pull?.Url, [.. files.Select(f => f.Path)]);
    }

    /// <summary>Deterministic, so a re-publish lands on the same branch and the same PR.</summary>
    private static string Branch(string slug) => $"castmill/{slug}";

    private static string FrontMatter(
        GitRepoLayout layout, Artifact artifact, string slug, IReadOnlyList<PublishImage> images) =>
        layout.FrontMatter(
            artifact.Title,
            ArtifactMarkdown.MetaDescription(artifact.ContentJson),
            artifact.CreatedAt,
            slug,
            // Anything not yet marked reviewed goes out as a draft: publishing something
            // mid-review as live is not a mistake the tool should be able to make.
            draft: artifact.Status != ArtifactStatus.Queued && artifact.Status != ArtifactStatus.Published,
            images.FirstOrDefault(i => i.Kind.Contains("header", StringComparison.OrdinalIgnoreCase)) is { } hero
                ? layout.ImageReference(slug, hero.FileName)
                : null,
            tags: []);

    private static string PullRequestBody(Artifact artifact, IReadOnlyList<GitFile> files)
    {
        var text = new StringBuilder();
        text.AppendLine("Generated by Castmill.").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"**{artifact.Kind}** · status **{artifact.Status}**").AppendLine();

        text.AppendLine("Files:");
        foreach (var file in files)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{file.Path}`");
        }

        // Provenance in the review surface: the reviewer can see which transcript moments
        // the post is built on without opening Castmill.
        var citations = ArtifactMarkdown.Citations(artifact.ContentJson);
        if (citations.Count > 0)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"Sources: {string.Join(", ", citations)}");
        }

        return text.ToString();
    }

    private sealed record PublishImage(string Kind, string FileName, string RepoPath, string SourceUrl, byte[] Bytes);

    /// <summary>
    /// Reads the campaign's filled image slots for this artifact straight out of blob storage
    /// rather than over their public URLs — going out over the internet for our own bytes
    /// would make an internal operation depend on egress.
    /// </summary>
    private async Task<List<PublishImage>> CollectImagesAsync(
        Artifact artifact, GitRepoLayout layout, string slug, bool includeImages, CancellationToken ct)
    {
        var images = new List<PublishImage>();
        if (!includeImages || !publicStore.IsConfigured)
        {
            return images;
        }

        var slots = await db.ImageSlots
            .Where(s => s.CampaignId == artifact.CampaignId
                        && (s.ArtifactId == artifact.Id || s.ArtifactId == null)
                        && s.State == "Filled"
                        && s.PublishedUrl != null)
            .ToListAsync(ct);

        var directory = layout.ImageDirectory(slug);
        foreach (var slot in slots.Where(s => s.Kind.StartsWith("blog-", StringComparison.Ordinal)))
        {
            var path = PathFromUrl(slot.PublishedUrl!) ?? slot.BaseImagePath;
            if (path is null || await publicStore.ReadAsync(path, ct) is not { } bytes)
            {
                continue;
            }

            // Blob names carry GUIDs, which read badly in a repository. Content type is
            // always WebP on this path, so the extension is deterministic.
            var fileName = $"{slug}-{slot.Kind}.webp";
            images.Add(new PublishImage(
                slot.Kind, fileName, $"{directory}/{fileName}", slot.PublishedUrl!, bytes));
        }

        return images;
    }

    /// <summary>Public URL → container-relative blob path.</summary>
    private static string? PathFromUrl(string url)
    {
        var match = BlobPath().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/(campaigns/[^?]+)")]
    private static partial Regex BlobPath();

    private async Task RecordAsync(
        GitPublication? existing, GitRepoProfile profile, Artifact artifact, string branch,
        string commitSha, string contentPath, GitHubPullRequest? pull, bool direct, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var status = direct ? "Committed" : "Open";

        if (existing is null)
        {
            db.GitPublications.Add(new GitPublication
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId ?? throw new InvalidOperationException("Publishing requires a tenant."),
                ArtifactId = artifact.Id,
                RepoProfileId = profile.Id,
                Branch = branch,
                CommitSha = commitSha,
                PullRequestNumber = pull?.Number,
                PullRequestUrl = pull?.Url,
                Status = status,
                ContentPath = contentPath,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Branch = branch;
            existing.CommitSha = commitSha;
            existing.PullRequestNumber = pull?.Number ?? existing.PullRequestNumber;
            existing.PullRequestUrl = pull?.Url ?? existing.PullRequestUrl;
            existing.Status = status;
            existing.ContentPath = contentPath;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
