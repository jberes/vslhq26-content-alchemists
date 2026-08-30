using System.Net;
using System.Text;
using Castmill.Api.Auth;
using Castmill.Api.Data;
using Castmill.Core.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Castmill.Api.Tests;

[Collection("api")]
public sealed class ExternalAvatarCaptureTests(CastmillApiFactory factory)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    [Fact]
    public async Task Microsoft_photo_is_captured_without_persisting_the_access_token()
    {
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Microsoft);
        var handler = new StubHandler(request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri?.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("transient-provider-token", request.Headers.Authorization?.Parameter);
            return ImageResponse();
        });

        await CaptureAsync(handler, attemptId, ExternalAuthProviders.Microsoft);

        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == attemptId);
        var image = Assert.IsType<byte[]>(attempt.CandidateAvatarImage);
        Assert.Equal(Jpeg, image);
        Assert.Equal("image/jpeg", attempt.CandidateAvatarContentType);
        Assert.DoesNotContain(
            "transient-provider-token",
            Encoding.UTF8.GetString(image),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Google_picture_is_loaded_only_from_a_trusted_https_host()
    {
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Google);
        var handler = new StubHandler(request => request.RequestUri?.Host switch
        {
            "openidconnect.googleapis.com" => JsonResponse(
                "{\"picture\":\"https://lh3.googleusercontent.com/avatar.jpg\"}"),
            "lh3.googleusercontent.com" => ImageResponse(),
            _ => throw new InvalidOperationException("Unexpected avatar request."),
        });

        await CaptureAsync(handler, attemptId, ExternalAuthProviders.Google);

        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Equal(Jpeg, attempt.CandidateAvatarImage);
        Assert.Equal("image/jpeg", attempt.CandidateAvatarContentType);
    }

    [Theory]
    [InlineData("http://lh3.googleusercontent.com/avatar.jpg")]
    [InlineData("https://googleusercontent.com.evil.example/avatar.jpg")]
    [InlineData("https://example.com/avatar.jpg")]
    public void Google_picture_rejects_untrusted_origins(string value) =>
        Assert.False(ExternalAvatarCaptureService.IsTrustedGoogleImageUri(new Uri(value)));

    [Fact]
    public void Image_signature_must_match_the_declared_content_type()
    {
        Assert.True(ExternalAvatarCaptureService.MatchesImageSignature("image/jpeg", Jpeg));
        Assert.False(ExternalAvatarCaptureService.MatchesImageSignature(
            "image/png",
            Jpeg));
        Assert.Null(ExternalAvatarCaptureService.CanonicalContentType("image/svg+xml"));
    }

    [Fact]
    public async Task Redirected_avatar_is_not_persisted()
    {
        var attemptId = await AddAttemptAsync(ExternalAuthProviders.Microsoft);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/avatar.jpg") },
        });

        await CaptureAsync(handler, attemptId, ExternalAuthProviders.Microsoft);

        await using var scope = factory.Services.CreateAsyncScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<CastmillDbContext>()
            .ExternalAuthAttempts.AsNoTracking().SingleAsync(candidate => candidate.Id == attemptId);
        Assert.Null(attempt.CandidateAvatarImage);
        Assert.Null(attempt.CandidateAvatarContentType);
    }

    private async Task CaptureAsync(StubHandler handler, Guid attemptId, string provider)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = new ExternalAvatarCaptureService(
            new HttpClient(handler),
            scope.ServiceProvider.GetRequiredService<CastmillDbContext>(),
            NullLogger<ExternalAvatarCaptureService>.Instance);
        await service.CaptureAsync(
            attemptId,
            provider,
            "transient-provider-token",
            TestContext.Current.CancellationToken);
    }

    private async Task<Guid> AddAttemptAsync(string provider)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ExternalAuthAttempt
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            ClientKind = ExternalAuthClientKinds.Web,
            ReturnRouteKey = ExternalAuthReturnRoutes.SignIn,
            CodeChallenge = new string('a', 43),
            PollSecretHash = ExternalAuthEndpoints.HashSecret($"poll-{Guid.NewGuid():N}"),
            Status = ExternalAuthStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
        };
        var db = scope.ServiceProvider.GetRequiredService<CastmillDbContext>();
        db.ExternalAuthAttempts.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return attempt.Id;
    }

    private static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Jpeg)
        {
            Headers = { ContentType = new("image/jpeg") },
        },
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
