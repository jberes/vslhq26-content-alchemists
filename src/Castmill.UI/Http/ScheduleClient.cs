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

    public Task<ScheduleEntryResponse> CreateAsync(
        ScheduleEntryCreateRequest request, CancellationToken ct = default) =>
        api.PostAsync<ScheduleEntryCreateRequest, ScheduleEntryResponse>(
            "api/v1/schedule", request, anonymous: false, ct);

    public Task<ScheduleEntryResponse> MoveAsync(
        Guid id, DateTimeOffset scheduledAt, CancellationToken ct = default) =>
        api.PatchAsync<ScheduleEntryMoveRequest, ScheduleEntryResponse>(
            $"api/v1/schedule/{id}", new ScheduleEntryMoveRequest(scheduledAt), etag: null, ct);

    public Task CancelAsync(Guid id, CancellationToken ct = default) =>
        api.DeleteAsync($"api/v1/schedule/{id}", ct);

    public Task<ScheduleEntryResponse> RetryAsync(Guid id, CancellationToken ct = default) =>
        api.PostAsync<object, ScheduleEntryResponse>(
            $"api/v1/schedule/{id}/retry", new { }, anonymous: false, ct);

    public Task<ScheduleReconcileResponse> ReconcileAsync(CancellationToken ct = default) =>
        api.PostAsync<object, ScheduleReconcileResponse>(
            "api/v1/schedule/reconcile", new { }, anonymous: false, ct);

    private static string Escape(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToString("o", CultureInfo.InvariantCulture));
}

public sealed record ScheduleReconcileResponse(
    int Reconciled, int Updated, IReadOnlyList<string> UnreachableChannels);
