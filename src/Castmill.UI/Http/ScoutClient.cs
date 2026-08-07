using Castmill.Core.Ai;

namespace Castmill.UI.Http;

/// <summary>
/// The Content Scout (backend E4). One call; the agent loop and its tool calls happen
/// server-side and come back in the trace, so the panel can show what it actually did rather
/// than a spinner.
/// </summary>
public sealed class ScoutClient(ApiClient api)
{
    public Task<ScoutResult> RunAsync(
        Guid campaignId, string? focus, int count, CancellationToken ct = default) =>
        api.PostAsync<object, ScoutResult>(
            $"api/v1/ai/campaigns/{campaignId}/scout",
            new { focus, count },
            anonymous: false,
            ct);
}
