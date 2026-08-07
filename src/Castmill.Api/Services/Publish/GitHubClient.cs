using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Castmill.Api.Services.Publish;

/// <summary>A file to write into the repository. Text and binary take different encodings.</summary>
public sealed record GitFile(string Path, byte[] Bytes, bool IsText);

public sealed record GitHubRepoInfo(string DefaultBranch, bool CanPush);

public sealed record GitHubPullRequest(int Number, string Url);

public sealed class GitHubApiException(string message, HttpStatusCode status)
    : InvalidOperationException(message)
{
    public HttpStatusCode Status { get; } = status;
}

public interface IGitHubClient
{
    Task<GitHubRepoInfo> GetRepositoryAsync(string token, string owner, string repo, CancellationToken ct);

    /// <summary>
    /// Writes every file as ONE commit on <paramref name="branch"/>, creating the branch from
    /// <paramref name="baseBranch"/> if it does not exist. Returns the new commit sha.
    /// </summary>
    Task<string> CommitAsync(
        string token, string owner, string repo, string baseBranch, string branch,
        string message, IReadOnlyList<GitFile> files, CancellationToken ct);

    /// <summary>The open PR for a branch, or null — so a re-publish reuses it.</summary>
    Task<GitHubPullRequest?> FindOpenPullRequestAsync(
        string token, string owner, string repo, string branch, CancellationToken ct);

    Task<GitHubPullRequest> CreatePullRequestAsync(
        string token, string owner, string repo, string branch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct);
}

/// <summary>
/// The Git Data API, not the Contents API (ADR-021).
///
/// Contents is one commit and one round trip PER FILE, non-atomic, and needs a
/// read-before-write blob sha to update. A post is a markdown file plus several images: a
/// blip halfway through would leave a branch holding the post without its hero image, and a
/// green pull request the author merges into a broken site. Blobs → tree → commit → ref is
/// one atomic commit and one reviewable diff, and <c>base_tree</c> makes create-vs-update
/// transparent so the whole 409-on-stale-sha class of bugs never arises.
///
/// A typed HttpClient rather than Octokit: this touches seven trivially shaped endpoints,
/// and Octokit's GitHubClient owns its own HttpClient, so it would sit outside the
/// resilience/telemetry/timeout handling every other outbound call here goes through.
/// </summary>
public sealed class GitHubClient(IHttpClientFactory httpClients) : IGitHubClient
{
    public const string HttpClientName = "github";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GitHubRepoInfo> GetRepositoryAsync(
        string token, string owner, string repo, CancellationToken ct)
    {
        using var client = Client(token);
        var repository = await GetAsync<RepoPayload>(client, $"repos/{owner}/{repo}", ct);
        return new GitHubRepoInfo(
            repository.DefaultBranch ?? "main",
            repository.Permissions?.Push ?? false);
    }

    public async Task<string> CommitAsync(
        string token, string owner, string repo, string baseBranch, string branch,
        string message, IReadOnlyList<GitFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var client = Client(token);

        // Branch first if it already exists: a re-publish must build on its own tip rather
        // than on the base branch, or it would silently discard the previous push.
        var existing = await TryGetRefAsync(client, owner, repo, branch, ct);
        var startRef = existing ?? await GetAsync<RefPayload>(
            client, $"repos/{owner}/{repo}/git/ref/heads/{baseBranch}", ct);

        var baseCommit = await GetAsync<CommitPayload>(
            client, $"repos/{owner}/{repo}/git/commits/{startRef.Object!.Sha}", ct);

        var tree = new List<object>();
        foreach (var file in files)
        {
            // Binary MUST go through a blob as base64. Never round-trip image bytes through
            // a UTF-8 string — it corrupts them silently and only shows up as a broken image
            // on the rendered site.
            var blob = await PostAsync<BlobPayload>(client, $"repos/{owner}/{repo}/git/blobs", new
            {
                content = file.IsText
                    ? System.Text.Encoding.UTF8.GetString(file.Bytes)
                    : Convert.ToBase64String(file.Bytes),
                encoding = file.IsText ? "utf-8" : "base64",
            }, ct);

            tree.Add(new { path = file.Path, mode = "100644", type = "blob", sha = blob.Sha });
        }

        // base_tree keeps every other file in the repository; without it the commit would
        // read as deleting the entire site.
        var newTree = await PostAsync<TreePayload>(client, $"repos/{owner}/{repo}/git/trees", new
        {
            base_tree = baseCommit.Tree!.Sha,
            tree,
        }, ct);

        var commit = await PostAsync<CommitPayload>(client, $"repos/{owner}/{repo}/git/commits", new
        {
            message,
            tree = newTree.Sha,
            parents = new[] { startRef.Object.Sha },
        }, ct);

        if (existing is null)
        {
            // Fully-qualified on create, unqualified on update — an asymmetry the REST API
            // has and which silently 404s or 422s if you get it backwards.
            await PostAsync<RefPayload>(client, $"repos/{owner}/{repo}/git/refs", new
            {
                @ref = $"refs/heads/{branch}",
                sha = commit.Sha,
            }, ct);
        }
        else
        {
            // force:false — fast-forward only. A non-fast-forward means somebody else moved
            // the branch, and overwriting their work silently is not ours to decide.
            await PatchAsync(client, $"repos/{owner}/{repo}/git/refs/heads/{branch}", new
            {
                sha = commit.Sha,
                force = false,
            }, ct);
        }

        return commit.Sha!;
    }

    public async Task<GitHubPullRequest?> FindOpenPullRequestAsync(
        string token, string owner, string repo, string branch, CancellationToken ct)
    {
        using var client = Client(token);
        var pulls = await GetAsync<PullPayload[]>(
            client, $"repos/{owner}/{repo}/pulls?head={owner}:{branch}&state=open", ct);

        return pulls.Length == 0 ? null : new GitHubPullRequest(pulls[0].Number, pulls[0].HtmlUrl ?? string.Empty);
    }

    public async Task<GitHubPullRequest> CreatePullRequestAsync(
        string token, string owner, string repo, string branch, string baseBranch,
        string title, string body, bool draft, CancellationToken ct)
    {
        using var client = Client(token);
        var pull = await PostAsync<PullPayload>(client, $"repos/{owner}/{repo}/pulls", new
        {
            title,
            head = branch,
            @base = baseBranch,
            body,
            draft,
        }, ct);

        return new GitHubPullRequest(pull.Number, pull.HtmlUrl ?? string.Empty);
    }

    // ---- transport --------------------------------------------------------------

    private HttpClient Client(string token)
    {
        var client = httpClients.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        // GitHub rejects requests without a User-Agent, and pins behaviour to the API version.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Castmill/1.0");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(url, body, Json, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task PatchAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        using var response = await client.SendAsync(request, ct);
        await ReadAsync<RefPayload>(response, ct);
    }

    private static async Task<RefPayload?> TryGetRefAsync(
        HttpClient client, string owner, string repo, string branch, CancellationToken ct)
    {
        using var response = await client.GetAsync($"repos/{owner}/{repo}/git/ref/heads/{branch}", ct);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await ReadAsync<RefPayload>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            // GitHub's own message is genuinely useful ("Reference already exists",
            // "Resource not accessible by personal access token") and contains no
            // credential — unlike the request, which travelled with the token.
            var message = await MessageAsync(response, ct);
            throw new GitHubApiException(
                $"GitHub returned {(int)response.StatusCode}{(message is null ? "" : $": {message}")}",
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new GitHubApiException("GitHub returned an empty response.", response.StatusCode);
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RepoPayload(string? DefaultBranch, PermissionsPayload? Permissions);

    private sealed record PermissionsPayload(bool Push);

    private sealed record RefPayload(GitObjectPayload? Object);

    private sealed record GitObjectPayload(string Sha);

    private sealed record CommitPayload(string? Sha, TreeRefPayload? Tree);

    private sealed record TreeRefPayload(string Sha);

    private sealed record TreePayload(string Sha);

    private sealed record BlobPayload(string Sha);

    private sealed record PullPayload(int Number, string? HtmlUrl);
}
