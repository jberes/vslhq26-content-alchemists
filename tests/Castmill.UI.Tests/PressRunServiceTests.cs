using System.Net;
using Castmill.UI.Http;
using Castmill.UI.State;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

public sealed class PressRunServiceTests : CastmillUiTestContext
{
    private static readonly Guid CampaignId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("c1111111-1111-1111-1111-222222222222");

    [Fact]
    public async Task Gateway_timeout_reattaches_to_the_observed_run_until_completion()
    {
        SignInTestUser();
        var generate = Http.Gate(HttpMethod.Post, $"api/v1/ai/campaigns/{CampaignId}/generate");
        var started = DateTimeOffset.UtcNow;
        var polls = 0;
        Http.OnAsync(HttpMethod.Get,
            $"api/v1/ai/campaigns/{CampaignId}/runs/latest?kind=content",
            () => Task.FromResult(StubHttpHandler.Json(++polls == 1
                ? new RunProgress(RunId, CampaignId, "Running", 1, 0, [], started, started)
                : new RunProgress(
                    RunId, CampaignId, "Completed", 1, 1,
                    [new RunItem("youtube", true, Guid.NewGuid(), null, null, 240_000)],
                    started, DateTimeOffset.UtcNow))));

        var press = Services.GetRequiredService<PressRunService>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        press.Changed += () =>
        {
            if (press.Progress is not null)
            {
                observed.TrySetResult();
            }
            if (!press.IsRunning)
            {
                finished.TrySetResult();
            }
        };

        press.Start(CampaignId, null, null, ["youtube"]);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        generate.SetResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(press.Error);
        Assert.Equal("Completed", press.Progress!.Status);
        Assert.Single(press.Progress.Items);
        Assert.True(press.Progress.Items[0].Success);
    }
}