using Castmill.Core.Resources;
using Castmill.UI.Http;

namespace Castmill.UI.Scheduling;

public enum WireDeliveryStatus
{
    Draft,
    Queued,
    Staged,
    Sent,
    Error,
    Blocked,
}

public enum WirePipelineColumn
{
    Ready,
    Queued,
    Sent,
    Attention,
}

public enum WireViewMode
{
    RunOfShow,
    Pipeline,
    Agenda,
}

public sealed record WireMetrics(
    long? Reach = null,
    long? Engagement = null,
    decimal? OpenRate = null,
    decimal? CompletionRate = null);

public sealed record WireScheduleItem(
    Guid Id,
    Guid ArtifactId,
    Guid CampaignId,
    string ChannelId,
    string Channel,
    string Title,
    DateTimeOffset ScheduledAtUtc,
    string TimeZone,
    WireDeliveryStatus Status,
    string? BlockedReason = null,
    string? BrokerRef = null,
    DateTimeOffset? SentAtUtc = null,
    string? LastError = null,
    string? Permalink = null,
    WireMetrics? Metrics = null);

public sealed record WireQueueItem(
    Guid ArtifactId,
    Guid CampaignId,
    string ChannelId,
    string Channel,
    string Title,
    string Meta);

public sealed record WireDay(
    DateOnly Date,
    bool IsToday,
    bool HasPostingWindow,
    IReadOnlyList<WireScheduleItem> Items,
    DateOnly? EndDate = null);

public sealed record WireBoardData(
    DateOnly RangeStart,
    int RangeDays,
    string TimeZone,
    TimeOnly WindowStart,
    TimeOnly WindowEnd,
    bool BrokerConfigured,
    IReadOnlyList<WireQueueItem> Queue,
    IReadOnlyList<WireDay> Days)
{
    public IReadOnlyList<WireScheduleItem> Items => Days.SelectMany(day => day.Items).ToList();
}

public sealed record WireSlotRequest(
    WireQueueItem? QueueItem,
    WireScheduleItem? ScheduleItem,
    DateOnly Date,
    int Minutes);

internal static class WireBoardMapper
{
    internal static WireBoardData Create(
        DateOnly rangeStart,
        int rangeDays,
        DashboardResponse dashboard,
        IReadOnlyList<ScheduleEntryResponse> schedule,
        PublishReadinessResponse readiness,
        IReadOnlyList<PublishChannel> channels,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var scheduledArtifactIds = schedule
            .Where(entry => entry.ArtifactId.HasValue)
            .Select(entry => entry.ArtifactId!.Value)
            .ToHashSet();
        var dashboardItems = DashboardItems(dashboard)
            .GroupBy(item => item.ArtifactId)
            .ToDictionary(group => group.Key, group => group.First());
        var channelNames = channels.ToDictionary(channel => channel.Id, channel => channel.Name);

        var queue = (dashboard.ReadyToSchedule ?? [])
            .Where(item => !scheduledArtifactIds.Contains(item.ArtifactId))
            .Select(item =>
            {
                var channel = MatchChannel(item.Kind, channels);
                return new WireQueueItem(
                    item.ArtifactId,
                    item.CampaignId,
                    channel?.Id ?? item.Kind,
                    channel?.Name ?? item.Kind,
                    item.Title,
                    $"{item.CampaignName} · validators passed");
            })
            .ToList();

        var mappedSchedule = schedule.Select(entry =>
        {
            dashboardItems.TryGetValue(entry.ArtifactId ?? Guid.Empty, out var artifact);
            var status = ParseStatus(entry.Status, readiness.Ready);
            return new WireScheduleItem(
                entry.Id,
                entry.ArtifactId ?? Guid.Empty,
                entry.CampaignId,
                entry.ChannelId,
                channelNames.GetValueOrDefault(entry.ChannelId, entry.ChannelId),
                artifact?.Title ?? FirstLine(entry.Text),
                entry.ScheduledAt,
                zone.Id,
                status,
                status == WireDeliveryStatus.Blocked ? entry.Error : null,
                entry.BrokerPostId,
                entry.SentAtUtc,
                entry.Error,
                entry.Permalink,
                entry.Metrics is null
                    ? null
                    : new WireMetrics(
                        entry.Metrics.Reach,
                        entry.Metrics.Engagement,
                        entry.Metrics.OpenRate,
                        entry.Metrics.CompletionRate));
        }).ToList();

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = Enumerable.Range(0, rangeDays)
            .Select(offset => rangeStart.AddDays(offset))
            .Select(date => new WireDay(
                date,
                date == localToday,
                date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
                mappedSchedule
                    .Where(item => DateOnly.FromDateTime(WireTime.Local(item).DateTime) == date)
                    .OrderBy(item => item.ScheduledAtUtc)
                    .ToList()))
            .ToList();

        return new WireBoardData(
            rangeStart,
            rangeDays,
            zone.Id,
            new TimeOnly(6, 0),
            new TimeOnly(22, 0),
            readiness.Ready,
            queue,
            days);
    }

    internal static (DateTimeOffset From, DateTimeOffset To) UtcRange(
        DateOnly start, int days, TimeZoneInfo zone)
    {
        var fromLocal = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var toLocal = start.AddDays(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fromLocal, zone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toLocal, zone), TimeSpan.Zero));
    }

    private static IEnumerable<DashboardArtifact> DashboardItems(DashboardResponse dashboard) =>
        dashboard.ReviewQueue
            .Concat(dashboard.AgingDrafts)
            .Concat(dashboard.ReadyToSchedule ?? []);

    private static PublishChannel? MatchChannel(string kind, IReadOnlyList<PublishChannel> channels) =>
        channels.FirstOrDefault(channel =>
            channel.Id.Equals(kind, StringComparison.OrdinalIgnoreCase)
            || channel.Name.Equals(kind, StringComparison.OrdinalIgnoreCase)
            || channel.Platform.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static WireDeliveryStatus ParseStatus(string status, bool brokerReady)
    {
        if (!Enum.TryParse<WireDeliveryStatus>(status, true, out var parsed))
        {
            parsed = WireDeliveryStatus.Error;
        }

        return !brokerReady && parsed is WireDeliveryStatus.Draft or WireDeliveryStatus.Queued
            ? WireDeliveryStatus.Staged
            : parsed;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        return line.Length <= 120 ? line : string.Concat(line.AsSpan(0, 119), "…");
    }

}

internal static class WireTime
{
    internal const int SnapMinutes = 15;
    internal const int OverlapMinutes = 90;

    internal static DateTimeOffset Local(WireScheduleItem item) =>
        TimeZoneInfo.ConvertTime(item.ScheduledAtUtc, TimeZoneInfo.FindSystemTimeZoneById(item.TimeZone));

    internal static int Minutes(DateTimeOffset value) => (value.Hour * 60) + value.Minute;

    internal static int Snap(double ratio, TimeOnly start, TimeOnly end)
    {
        var startMinutes = (start.Hour * 60) + start.Minute;
        var endMinutes = (end.Hour * 60) + end.Minute;
        var raw = startMinutes + (Math.Clamp(ratio, 0d, 1d) * (endMinutes - startMinutes));
        var snapped = (int)Math.Round(raw / SnapMinutes, MidpointRounding.AwayFromZero) * SnapMinutes;
        return Math.Clamp(snapped, startMinutes, endMinutes);
    }

    internal static DateTimeOffset? ToUtc(DateOnly date, int minutes, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddMinutes(minutes);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
        {
            return null;
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    internal static bool IsSameSlot(WireScheduleItem item, DateOnly date, int minutes)
    {
        var local = Local(item);
        var itemMinutes = (int)Math.Round(
            Minutes(local) / (double)SnapMinutes,
            MidpointRounding.AwayFromZero) * SnapMinutes;
        return DateOnly.FromDateTime(local.DateTime) == date && itemMinutes == minutes;
    }

    internal static IReadOnlyDictionary<Guid, int> StackLevels(IReadOnlyList<WireScheduleItem> items)
    {
        var levelEnds = new List<int>();
        var levels = new Dictionary<Guid, int>();
        foreach (var item in items.OrderBy(MinutesFromMidnight))
        {
            var minutes = MinutesFromMidnight(item);
            var level = levelEnds.FindIndex(end => minutes - end >= OverlapMinutes);
            if (level < 0)
            {
                level = levelEnds.Count;
                levelEnds.Add(minutes);
            }
            else
            {
                levelEnds[level] = minutes;
            }
            levels[item.Id] = level;
        }
        return levels;
    }

    private static int MinutesFromMidnight(WireScheduleItem item) => Minutes(Local(item));
}