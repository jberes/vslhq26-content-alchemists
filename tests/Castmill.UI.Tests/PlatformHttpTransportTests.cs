using System.Net;
using Castmill.Core.Auth;
using Castmill.UI.Http;
using Castmill.UI.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// A shell may supply the transport under the chokepoint (the desktop pins NSURLSession's
/// request windows). Whatever the shell supplies, every request still passes through
/// <see cref="CastmillHttpHandler"/> first and keeps the client's 10-minute budget.
/// </summary>
public sealed class PlatformHttpTransportTests
{
    [Fact]
    public async Task A_shell_supplied_transport_carries_every_request_under_the_chokepoint()
    {
        var transport = new RecordingTransport();
        var services = new ServiceCollection();
        services.AddCastmillUi(new Uri("https://api.test/"));
        var tokens = new TestAuthTokenProvider();
        tokens.SignIn();
        services.AddSingleton<IAuthTokenProvider>(tokens);
        services.AddSingleton<IUserActivity>(new SilentActivity());
        services.AddSingleton<IPlatformHttpHandlerFactory>(new FixedFactory(transport));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var http = scope.ServiceProvider.GetRequiredService<HttpClient>();
        using var response = await http.GetAsync("api/v1/me");

        var request = Assert.Single(transport.Requests);
        Assert.Equal("https://api.test/api/v1/me", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.True(request.Headers.Contains(CastmillHttpHandler.CorrelationHeader));
        Assert.Equal(TimeSpan.FromMinutes(10), http.Timeout);
    }

    private sealed class FixedFactory(HttpMessageHandler handler) : IPlatformHttpHandlerFactory
    {
        public HttpMessageHandler Create() => handler;
    }

    private sealed class SilentActivity : IUserActivity
    {
        public void Started(string id) { }

        public void Finished(string id) { }
    }

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
