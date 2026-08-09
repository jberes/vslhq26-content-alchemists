using System.Text;
using System.Text.Json;
using Castmill.Api.Data;
using Castmill.Core;
using Microsoft.EntityFrameworkCore;

namespace Castmill.Api.Services.Ai;

/// <summary>One destination the workspace publishes to, or from.</summary>
public sealed record WorkspaceLink(string Label, string Url);

public interface IWorkspaceLinks
{
    /// <summary>The stored links, newest write wins. Empty when none are configured.</summary>
    Task<IReadOnlyList<WorkspaceLink>> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// The link block a YouTube description (or any generated piece) should carry. Empty
    /// string when nothing is configured, so a missing setup never leaves a broken heading.
    /// </summary>
    Task<string> RenderBlockAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// The workspace's own website and social URLs, kept in the plaintext per-user settings store
/// under a single key.
///
/// These are FACTS, not steering: a generator must never invent a URL, so the model is handed
/// a <c>{{LINKS}}</c> placeholder and this block is substituted in afterwards. That way a
/// hallucinated link is impossible by construction rather than by instruction.
/// </summary>
public sealed class WorkspaceLinks(CastmillDbContext db) : IWorkspaceLinks
{
    /// <summary>Plaintext by design — these are public URLs, not credentials.</summary>
    public const string SettingKey = "workspace.links";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkspaceLink>> GetAsync(Guid userId, CancellationToken ct)
    {
        var setting = await db.UserSettings
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Key == SettingKey, ct);

        if (string.IsNullOrWhiteSpace(setting?.Value))
        {
            return [];
        }

        try
        {
            var links = JsonSerializer.Deserialize<List<WorkspaceLink>>(setting.Value, Json) ?? [];
            return [.. links
                .Where(l => !string.IsNullOrWhiteSpace(l.Url) && !string.IsNullOrWhiteSpace(l.Label))
                .Take(20)];
        }
        catch (JsonException)
        {
            // Malformed stored JSON must not take a generation run down with it.
            return [];
        }
    }

    public async Task<string> RenderBlockAsync(Guid userId, CancellationToken ct)
    {
        var links = await GetAsync(userId, ct);
        if (links.Count == 0)
        {
            return string.Empty;
        }

        var block = new StringBuilder();
        foreach (var link in links)
        {
            block.Append(link.Label.Trim()).Append(": ").AppendLine(link.Url.Trim());
        }

        return block.ToString().TrimEnd();
    }
}
