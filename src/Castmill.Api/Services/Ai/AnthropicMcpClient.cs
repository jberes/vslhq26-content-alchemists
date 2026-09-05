using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Castmill.Api.Services.Secrets;
using Microsoft.Extensions.Options;

namespace Castmill.Api.Services.Ai;

/// <summary>A remote MCP server the model may call during a pass (ADR-056).</summary>
public sealed record McpServerDefinition(string Name, string Url, string? Authorization, IReadOnlyList<string>? AllowedTools);

public interface IAnthropicMcpClient
{
    /// <summary>
    /// One completion with the MCP connector attached: the model may call the listed servers'
    /// tools while answering. Returns the final assistant text (tool-use blocks stripped).
    /// </summary>
    Task<string> CompleteAsync(Guid userId, string prompt, IReadOnlyList<McpServerDefinition> servers, CancellationToken ct);
}

/// <summary>
/// The Messages API over raw HTTP, for the ONE request shape the typed SDK path does not
/// give us verified access to: <c>mcp_servers</c> + an <c>mcp_toolset</c> tool (beta
/// <c>mcp-client-2025-11-20</c>). Deliberately narrow — same key, same model default and
/// same no-sampling rules as <see cref="AnthropicChatProvider"/>; a documented exception to
/// "SDK only", recorded in ADR-056. Keys never leave the Authorization header.
/// </summary>
public sealed class AnthropicMcpClient(
    IHttpClientFactory httpClients,
    IUserSecretsService secrets,
    IOptions<AiOptions> options,
    ILogger<AnthropicMcpClient> logger) : IAnthropicMcpClient
{
    public const string HttpClientName = "anthropic-mcp";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxOutputTokens = 32_000;
    /// <summary>A pass that keeps calling tools is bounded; each continuation re-sends the transcript.</summary>
    private const int MaxTurns = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> CompleteAsync(
        Guid userId, string prompt, IReadOnlyList<McpServerDefinition> servers, CancellationToken ct)
    {
        var key = await secrets.GetAsync(userId, SecretKind.TechEditKey, ct)
            ?? throw new AiNotConfiguredException(
                "The Tech Edit's Anthropic key is not stored. Set it via Settings → Credentials (TechEditKey).");
        var model = options.Value.TextProviders.Values
            .FirstOrDefault(p => p.Enabled && p.Kind.Equals("anthropic", StringComparison.OrdinalIgnoreCase))?.Model;
        model = string.IsNullOrWhiteSpace(model) ? AnthropicChatProvider.DefaultModel : model;

        var messages = new List<object> { new { role = "user", content = prompt } };
        var text = new StringBuilder();
        var client = httpClients.CreateClient(HttpClientName);

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["max_tokens"] = MaxOutputTokens,
                ["messages"] = messages,
                ["mcp_servers"] = servers.Select(server => new Dictionary<string, object?>
                {
                    ["type"] = "url",
                    ["url"] = server.Url,
                    ["name"] = server.Name,
                    ["authorization_token"] = string.IsNullOrWhiteSpace(server.Authorization) ? null : server.Authorization,
                    ["tool_configuration"] = server.AllowedTools is { Count: > 0 }
                        ? new { enabled = true, allowed_tools = server.AllowedTools }
                        : new { enabled = true },
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(),
                ["tools"] = servers.Select(server => new { type = "mcp_toolset", mcp_server_name = server.Name }).ToList(),
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "mcp-client-2025-11-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // The provider's own message, never the body (it can quote the request).
                string detail;
                try
                {
                    using var error = JsonDocument.Parse(payload);
                    detail = error.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                        ? m.GetString() ?? "no message"
                        : "no message";
                }
                catch (JsonException)
                {
                    detail = "unreadable error body";
                }
                logger.LogError("Anthropic MCP request failed: HTTP {Status} {Detail}", (int)response.StatusCode, detail);
                throw new InvalidOperationException($"Anthropic refused the MCP-enabled Tech Edit (HTTP {(int)response.StatusCode}): {detail}");
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var stop = root.TryGetProperty("stop_reason", out var stopReason) ? stopReason.GetString() : null;
            if (stop == "refusal")
            {
                throw new InvalidOperationException("The model declined this Tech Edit (safety refusal).");
            }
            var content = root.GetProperty("content");
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var t))
                {
                    text.Append(t.GetString());
                }
            }
            if (stop != "pause_turn")
            {
                break;
            }
            // pause_turn: the server-side tool loop needs another request with the transcript so far.
            messages.Add(new { role = "assistant", content = JsonSerializer.Deserialize<object>(content.GetRawText(), Json) });
            messages.Add(new { role = "user", content = "Continue." });
        }
        return text.ToString();
    }
}
