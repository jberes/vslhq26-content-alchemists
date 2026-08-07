namespace Castmill.UI.Http;

public sealed record GitRepo(
    Guid Id, Guid? BrandId, string Name, string Owner, string Repo, string? BaseBranch,
    string Preset, string Mode, bool OpenAsDraftPr, bool IsDefault, string LayoutJson);

/// <summary>What a publish WOULD write — shown before anything is committed.</summary>
public sealed record GitPublishPreview(
    string ContentPath, string FrontMatter, IReadOnlyList<string> ImagePaths, string Branch);

public sealed record GitPublishOutcome(
    string Branch, string CommitSha, int? PullRequestNumber, string? PullRequestUrl,
    IReadOnlyList<string> Files);

public sealed record GitPublication(
    Guid Id, Guid ArtifactId, Guid RepoProfileId, string Branch, string CommitSha,
    int? PullRequestNumber, string? PullRequestUrl, string Status, string ContentPath,
    DateTimeOffset UpdatedAt);

public sealed record GitConnection(bool Ok, string? DefaultBranch, bool CanPush, string? Reason);

/// <summary>
/// Optional git publishing (backend ADR-021). The token never reaches this client — it is
/// written once through the secrets endpoint and read server-side thereafter.
/// </summary>
public sealed class GitClient(ApiClient api)
{
    public Task<List<GitRepo>> ListReposAsync(CancellationToken ct = default) =>
        api.GetAsync<List<GitRepo>>("api/v1/git/repos", ct);

    public Task<GitConnection> TestAsync(Guid repoId, CancellationToken ct = default) =>
        api.PostAsync<object, GitConnection>($"api/v1/git/repos/{repoId}/test", new { }, anonymous: false, ct);

    public Task<GitPublishPreview> PreviewAsync(
        Guid campaignId, Guid artifactId, Guid repoProfileId, bool includeImages,
        CancellationToken ct = default) =>
        api.PostAsync<object, GitPublishPreview>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/publish/github/preview",
            new { repoProfileId, includeImages },
            anonymous: false,
            ct);

    public Task<GitPublishOutcome> PublishAsync(
        Guid campaignId, Guid artifactId, Guid repoProfileId, bool includeImages, string mode,
        CancellationToken ct = default) =>
        api.PostAsync<object, GitPublishOutcome>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/publish/github",
            new { repoProfileId, includeImages, mode },
            anonymous: false,
            ct);

    public Task<List<GitPublication>> HistoryAsync(
        Guid campaignId, Guid artifactId, CancellationToken ct = default) =>
        api.GetAsync<List<GitPublication>>(
            $"api/v1/campaigns/{campaignId}/artifacts/{artifactId}/publish/github", ct);
}
