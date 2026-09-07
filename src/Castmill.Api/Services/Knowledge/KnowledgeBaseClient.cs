using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Knowledge;

public sealed class KnowledgeBaseOptions
{
    public const string SectionName = "KnowledgeBase";

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Path appended to <see cref="BaseUrl"/>. Config, because gateways differ.</summary>
    public string QueryPath { get; set; } = "/query";

    /// <summary>
    /// Name of the request field carrying the question. Config for the same reason: some
    /// gateways take <c>query</c>, some <c>question</c>, some <c>input</c>. Getting this
    /// wrong is a 400 from someone else's service, which is expensive to debug from logs.
    /// </summary>
    public string QueryField { get; set; } = "query";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>A source the gateway cited. These are real published URLs — the coverage evidence.</summary>
public sealed record KnowledgeCitation(string Url, string? Title);

/// <summary>
/// The gateway answers with synthesised prose rather than raw chunks, so this is closer to a
/// briefing than a retrieval result — which is why it is injected into the prompt as prose
/// plus sources rather than as a chunk list.
/// </summary>
public sealed record KnowledgeAnswer(
    string Output,
    string? Title,
    IReadOnlyList<KnowledgeCitation> Citations,
    int? ConfidenceLevel,
    IReadOnlyList<string> Suggestions)
{
    /// <summary>Renders the answer as the prompt block the second pass reads.</summary>
    public string ToPromptBlock()
    {
        var block = new StringBuilder();
        block.Append("Knowledge base");
        if (!string.IsNullOrWhiteSpace(Title))
        {
            block.Append(" — \"").Append(Title).Append('"');
        }
        if (ConfidenceLevel is { } confidence)
        {
            block.Append(" (confidence ").Append(confidence).Append(')');
        }
        block.AppendLine(":");
        block.AppendLine(Output);

        if (Citations.Count > 0)
        {
            block.AppendLine();
            block.AppendLine("Sources (link to these where they support a claim):");
            foreach (var citation in Citations)
            {
                block.Append("- ");
                if (!string.IsNullOrWhiteSpace(citation.Title))
                {
                    block.Append(citation.Title).Append(" — ");
                }
                block.AppendLine(citation.Url);
            }
        }

        return block.ToString();
    }
}

/// <summary>A brand's own RAG gateway (ADR-056), token already decrypted for this request.</summary>
/// <summary>
/// One brand's gateway. <paramref name="ProductType"/> is posted beside the question under
/// <paramref name="ProductField"/> when set (ADR-061) — the Infragistics gateway routes to a
/// per-product agent on "productType", so several brands share one endpoint and token.
/// </summary>
public sealed record KnowledgeEndpoint(
    string BaseUrl, string QueryPath, string QueryField, string? Token, string Name,
    string? ProductType = null, string ProductField = "productType");

public interface IKnowledgeBaseClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Asks a specific brand endpoint first, falling back to the workspace gateway when none is
    /// given. Default routes to <see cref="AskAsync(Guid, string, CancellationToken)"/> so
    /// fakes keep compiling.
    /// </summary>
    Task<KnowledgeAnswer?> AskAsync(Guid userId, string question, KnowledgeEndpoint? endpoint, CancellationToken ct) =>
        AskAsync(userId, question, ct);

    /// <summary>
    /// Asks the gateway a question. Returns null when the gateway is unconfigured, has no
    /// stored token, or answers with anything unusable — the second pass then runs without
    /// the block rather than failing, and says so in the log.
    /// </summary>
    Task<KnowledgeAnswer?> AskAsync(Guid userId, string question, CancellationToken ct);
}

public sealed class KnowledgeBaseClient(
    IHttpClientFactory httpClients,
    IUserSecretsService secrets,
    IOptions<KnowledgeBaseOptions> options,
    ILogger<KnowledgeBaseClient> log) : IKnowledgeBaseClient
{
    public const string HttpClientName = "knowledgebase";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly KnowledgeBaseOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<KnowledgeAnswer?> AskAsync(Guid userId, string question, KnowledgeEndpoint? endpoint, CancellationToken ct)
    {
        if (endpoint is null)
        {
            return await AskAsync(userId, question, ct);
        }
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }
        // A brand endpoint without its own token borrows the workspace token, so one gateway
        // configured both ways keeps working.
        var token = endpoint.Token ?? await secrets.GetAsync(userId, SecretKind.KnowledgeBaseToken, ct);
        return await QueryAsync(
            endpoint.BaseUrl, endpoint.QueryPath, endpoint.QueryField, token, question, ct,
            endpoint.ProductType, endpoint.ProductField);
    }

    public async Task<KnowledgeAnswer?> AskAsync(Guid userId, string question, CancellationToken ct)
    {
        if (!_options.IsConfigured || string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        var token = await secrets.GetAsync(userId, SecretKind.KnowledgeBaseToken, ct);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        return await QueryAsync(_options.BaseUrl, _options.QueryPath, _options.QueryField, token, question, ct);
    }

    private async Task<KnowledgeAnswer?> QueryAsync(
        string baseUrl, string queryPath, string queryField, string? token, string question, CancellationToken ct,
        string? productType = null, string productField = "productType")
    {
        var client = httpClients.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var body = new Dictionary<string, object?> { [queryField] = question };
        // The product selector rides beside the question, never inside it: a gateway that routes
        // on a field cannot be steered by prose, and prose the model wrote could not be trusted to.
        if (!string.IsNullOrWhiteSpace(productType) && !string.IsNullOrWhiteSpace(productField))
        {
            body[productField.Trim()] = productType.Trim();
        }

        try
        {
            var response = await client.PostAsJsonAsync(queryPath.TrimStart('/'), body, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Never echo the body: a gateway error can quote the request, and the request
                // travelled with a bearer token. Status only.
                log.LogWarning("Knowledge base returned {Status}.", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Knowledge base query failed: {Reason}.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Deliberately forgiving: every field but <c>output</c> is optional and unknown fields are
    /// ignored, so a gateway that grows its envelope does not break the Tech Edit.
    /// </summary>
    internal static KnowledgeAnswer? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.String
            || output.GetString() is not { Length: > 0 } text)
        {
            return null;
        }

        var citations = new List<KnowledgeCitation>();
        if (root.TryGetProperty("citations", out var cites) && cites.ValueKind == JsonValueKind.Array)
        {
            foreach (var cite in cites.EnumerateArray())
            {
                if (cite.ValueKind == JsonValueKind.Object
                    && cite.TryGetProperty("url", out var url)
                    && url.GetString() is { Length: > 0 } urlText)
                {
                    citations.Add(new KnowledgeCitation(
                        urlText,
                        cite.TryGetProperty("title", out var title) ? title.GetString() : null));
                }
            }
        }

        var suggestions = new List<string>();
        if (root.TryGetProperty("suggestions", out var suggested) && suggested.ValueKind == JsonValueKind.Array)
        {
            suggestions.AddRange(suggested.EnumerateArray()
                .Select(s => s.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!));
        }

        return new KnowledgeAnswer(
            text,
            root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            citations,
            root.TryGetProperty("confidenceLevel", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : null,
            suggestions);
    }
}
