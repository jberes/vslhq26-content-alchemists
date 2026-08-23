using Castmill.Core.Resources;

namespace Castmill.UI.Http;

public sealed record PublishChannel(string Id, string Name, string Platform);

public sealed class PublishClient(ApiClient api)
{
    public Task<PublishReadinessResponse> GetReadinessAsync(CancellationToken ct = default) =>
        api.GetAsync<PublishReadinessResponse>("api/v1/publish/readiness", ct);

    public Task<List<PublishChannel>> ListChannelsAsync(CancellationToken ct = default) =>
        api.GetAsync<List<PublishChannel>>("api/v1/publish/channels", ct);
}