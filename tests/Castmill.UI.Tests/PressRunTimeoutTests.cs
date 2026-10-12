using Castmill.Core.Ai;
using Castmill.UI.Http;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Observed 2026-10-11 on a max-effort run (ADR-086): the long generate POST passed the
/// client's 10-minute HttpClient timeout, which surfaces as an OperationCanceledException.
/// The Press Run read it as the user cancelling and ended with every piece "queued" while the
/// server went on printing them. A timeout is a lost request, not a lost run.
/// </summary>
public sealed class PressRunTimeoutTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("71111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("71111111-1111-1111-1111-222222222222");
    private static readonly string Latest = $"api/v1/ai/campaigns/{CampaignId}/runs/latest?kind=content";

    [Fact]
    public async Task A_client_timeout_on_the_long_post_follows_the_run_to_its_end()
    {
        SignInTestUser();
        var started = DateTimeOffset.UtcNow;
        Http.OnGetQuery(Latest, Run("Running", started, []));
        var timedOut = false;
        Http.OnAsync(HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            timedOut = true;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing.",
                new TimeoutException());
        });
        var press = Services.GetRequiredService<PressRunService>();

        press.Start(CampaignId, null, null, ["blog", "social-x"]);
        await WaitUntilAsync(() => press.Progress is { Status: "Running" });
        // The run is still printing when the request gives up; it finishes only afterwards.
        await WaitUntilAsync(() => timedOut);
        await Task.Delay(500);
        Http.OnGetQuery(Latest, Run("Completed", started,
        [
            new RunItem("blog", true, Guid.NewGuid(), null, [], 240_000),
            new RunItem("social-x", true, Guid.NewGuid(), null, [], 60_000),
        ]));
        await WaitUntilAsync(() => !press.IsRunning);

        Assert.Null(press.Error);
        Assert.Equal("Completed", press.Progress!.Status);
        Assert.All(press.Progress.Items, item => Assert.True(item.Success));
        Assert.Equal(2, press.Progress.Items.Count);
    }

    private static RunProgress Run(string status, DateTimeOffset started, IReadOnlyList<RunItem> items) =>
        new(RunId, CampaignId, status, 2, items.Count, items, started, DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "timed out waiting for the Press Run");
            await Task.Delay(50);
        }
    }
}
