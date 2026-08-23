using Castmill.Core.Resources;
using Castmill.UI.Http;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Regression tests for the render loop found in the browser during F3.
///
/// The stores' Changed events cause re-renders, and components call LoadAsync from
/// OnParametersSetAsync, which runs on every re-render. So a load that re-enters while it is
/// still in flight is not merely wasteful — it notifies, which re-renders, which loads again,
/// forever. The symptom is a hung tab, and the reason bUnit missed it is that the earlier
/// tests' stub transport completed before any re-render could occur.
///
/// These tests therefore use a transport that does NOT complete until told to, which is the
/// only way to observe re-entrancy at all.
/// </summary>
public sealed class StoreSingleFlightTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task Concurrent_loads_of_the_same_campaign_make_one_request()
    {
        var gate = new TaskCompletionSource();
        var transport = new GatedHandler(gate.Task, Preview());
        var api = new ApiClient(Client(transport));
        var state = new CampaignState(new CampaignsClient(api), new EvidenceClient(api));

        // Re-entrant callers, exactly as a re-render storm produces.
        var first = state.LoadAsync(CampaignId);
        var second = state.LoadAsync(CampaignId);
        var third = state.LoadAsync(CampaignId);

        Assert.Same(first, second);
        Assert.Same(first, third);

        gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, transport.Calls);
        Assert.Equal("Gated campaign", state.Campaign?.Name);
    }

    [Fact]
    public async Task A_notification_raised_mid_load_cannot_start_another_load()
    {
        // This is the loop itself, reproduced: a subscriber that calls LoadAsync from its
        // Changed handler — which is what a component's OnParametersSetAsync amounts to.
        var gate = new TaskCompletionSource();
        var transport = new GatedHandler(gate.Task, Preview());
        var api = new ApiClient(Client(transport));
        var state = new CampaignState(new CampaignsClient(api), new EvidenceClient(api));

        var reentries = 0;
        state.Changed += () =>
        {
            if (reentries++ < 50)
            {
                _ = state.LoadAsync(CampaignId);
            }
        };

        var load = state.LoadAsync(CampaignId);
        gate.SetResult();
        await load;

        // Without single-flight this is unbounded; with it, one request regardless of how
        // many times the subscriber re-enters.
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task A_forced_reload_still_goes_out_again()
    {
        var transport = new GatedHandler(Task.CompletedTask, Preview());
        var api = new ApiClient(Client(transport));
        var state = new CampaignState(new CampaignsClient(api), new EvidenceClient(api));

        await state.LoadAsync(CampaignId);
        await state.LoadAsync(CampaignId);              // cached
        await state.LoadAsync(CampaignId, force: true); // explicit refresh

        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task The_workspace_list_is_single_flight_too()
    {
        var gate = new TaskCompletionSource();
        var transport = new GatedHandler(gate.Task, new List<CampaignResponse>());
        var workspace = new WorkspaceState(new CampaignsClient(new ApiClient(Client(transport))));

        var a = workspace.LoadAsync();
        var b = workspace.LoadAsync();
        Assert.Same(a, b);

        gate.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task Campaign_preview_is_usable_before_transcript_details_finish()
    {
        var transcriptId = Guid.NewGuid();
        Http.OnGet($"api/v1/campaigns/{CampaignId}/preview", new CampaignPreview(
            Preview().Campaign,
            [new ArtifactPreviewResponse(
                transcriptId, CampaignId, "transcript", "Source transcript", "Draft", 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            [], 0, 0));
        var transcriptGate = Http.Gate(
            HttpMethod.Get, $"api/v1/campaigns/{CampaignId}/artifacts/{transcriptId}");
        var state = Services.GetRequiredService<CampaignState>();

        await state.LoadAsync(CampaignId);

        Assert.Equal("Gated campaign", state.Campaign?.Name);
        Assert.False(state.IsLoading);
        Assert.True(state.IsLoadingTranscript);
        Assert.Null(state.Transcript);

        transcriptGate.SetResult(StubHttpHandler.Json(new ArtifactResponse(
            transcriptId, CampaignId, "transcript", "Source transcript",
            """{"source":"paste","segments":[{"id":"S1","startSeconds":0,"endSeconds":2,"text":"Ready."}]}""",
            "Draft", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        await state.WhenDetailsLoadedAsync();

        Assert.False(state.IsLoadingDetails);
        Assert.Equal("Ready.", Assert.Single(state.Transcript!.Segments).Text);
    }

    // ---- helpers ---------------------------------------------------------------

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.test/") };

    private static CampaignPreview Preview() =>
        new(new CampaignResponse(CampaignId, Guid.NewGuid(), "Gated campaign", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            [], [], 0, 0);

    /// <summary>
    /// Responds only once the gate completes, so a load can be observed while still in
    /// flight. A handler that answers synchronously cannot express re-entrancy at all.
    /// </summary>
    private sealed class GatedHandler(Task gate, object body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            await gate;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(body),
            };
        }
    }
}
