using System.Globalization;
using Castmill.Core.Resources;

namespace Castmill.UI.Http;

/// <summary>
/// Typed client for the schedule mirror (backend B9.6 / ADR-016). The Wire renders from
/// our own rows, never from a broker round-trip, so the week draws on load.
/// </summary>
public sealed class ScheduleClient(ApiClient api)
{
    public Task<List<ScheduleEntryResponse>> ListAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        api.GetAsync<List<ScheduleEntryResponse>>(
            $"api/v1/schedule?from={Escape(from)}&to={Escape(to)}", ct);

    private static string Escape(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToString("o", CultureInfo.InvariantCulture));
}
