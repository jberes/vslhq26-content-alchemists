using System.Net;
using System.Net.Http.Json;
using Bunit;
using Castmill.Core.Auth;
using Castmill.UI;
using Castmill.UI.Design;
using Castmill.UI.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI.Tests;

/// <summary>
/// Shared bUnit setup: the real UI services, plus test doubles for the platform seams
/// (<see cref="IShellInfo"/>, <see cref="IUiStateStore"/>, <see cref="IAuthTokenProvider"/>)
/// and a stub HTTP handler, so no test touches JS interop, a real shell, or the network.
/// </summary>
public abstract class CastmillUiTestContext : BunitContext
{
    protected CastmillUiTestContext()
    {
        // Ignite UI components bootstrap themselves through JS. There is no browser here,
        // so unplanned interop calls are expected rather than a test failure.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddCastmillUi(new Uri("https://api.test/"));

        // Replace the browser-backed store: ThemeService would otherwise import a JS module.
        Services.AddSingleton<IUiStateStore>(UiState);
        Services.AddSingleton<IShellInfo>(Shell);
        Services.AddSingleton<IAuthTokenProvider>(Tokens);
        Services.AddSingleton<IMediaPipeline>(Media);

        // Replace the real HttpClient with one that answers from Http's route table, so
        // AuthState can load /me without a network.
        Services.AddScoped(_ => new HttpClient(Http) { BaseAddress = new Uri("https://api.test/") });
    }

    protected TestUiStateStore UiState { get; } = new();

    protected TestShellInfo Shell { get; } = new();

    protected TestAuthTokenProvider Tokens { get; } = new();

    protected StubHttpHandler Http { get; } = new();

    protected TestMediaPipeline Media { get; } = new();

    /// <summary>Signs the fake user in and stubs /me, which is what most tests want.</summary>
    protected void SignInTestUser(string email = "demo@castmill.local")
    {
        Tokens.SignIn();
        Http.OnGet("api/v1/me", new MeResponse(Guid.NewGuid(), Guid.NewGuid(), email, "Demo user"));
    }
}

public sealed class TestAuthTokenProvider : IAuthTokenProvider
{
    public string? AccessToken { get; private set; }

    public bool IsSignedIn => AccessToken is not null;

    public bool RestoreSucceeds { get; set; }

    public event Action? Changed;

    public void SignIn()
    {
        AccessToken = "test-access-token";
        RestoreSucceeds = true;
    }

    public Task<bool> TryRestoreAsync() => Task.FromResult(RestoreSucceeds && IsSignedIn);

    public Task StoreAsync(string accessToken, DateTimeOffset accessExpiresAt, string refreshToken)
    {
        AccessToken = accessToken;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task<bool> TryRefreshAsync() => Task.FromResult(IsSignedIn);

    public Task ClearAsync()
    {
        AccessToken = null;
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}

/// <summary>Tiny route table so tests state the responses they care about and nothing else.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<HttpResponseMessage>> _gates = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies snapshotted at send time — the message's own content is
    /// disposed with the request, so asserting on it afterwards needs this copy.</summary>
    public List<(HttpMethod Method, string Path, string Body)> Bodies { get; } = [];

    public void OnGet<T>(string path, T body) =>
        _routes[$"GET {path}"] = () => JsonResponse(HttpStatusCode.OK, body);

    public void OnPost<T>(string path, T body) =>
        _routes[$"POST {path}"] = () => JsonResponse(HttpStatusCode.OK, body);

    public void OnPut<T>(string path, T body) =>
        _routes[$"PUT {path}"] = () => JsonResponse(HttpStatusCode.OK, body);

    public void OnPatch<T>(string path, T body) =>
        _routes[$"PATCH {path}"] = () => JsonResponse(HttpStatusCode.OK, body);

    public void OnStatus(HttpMethod method, string path, HttpStatusCode status) =>
        _routes[$"{method} {path}"] = () => new HttpResponseMessage(status);

    /// <summary>An HTML error body — what ASP.NET's developer exception page actually returns,
    /// as opposed to the problem-details JSON the client hopes for.</summary>
    public void OnHtml(string path, HttpStatusCode status, string html) =>
        _routes[$"GET {path}"] = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };

    /// <summary>Transport-level failure: the API is not there at all.</summary>
    public void OnThrow(HttpMethod method, string path, Func<Exception> error) =>
        _routes[$"{method} {path}"] = () => throw error();

    /// <summary>A file download, complete with the Content-Disposition the client reads the
    /// name from — exports name their own files server-side.</summary>
    public void OnFile(string path, string fileName, string contentType, byte[] bytes) =>
        _routes[$"GET {path}"] = () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = fileName };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

    /// <summary>
    /// Gates a route: requests to it hang until the returned completion source is resolved.
    /// The only way to express "the model is still generating" against a stub transport.
    /// </summary>
    public TaskCompletionSource<HttpResponseMessage> Gate(HttpMethod method, string path)
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[$"{method} {path}"] = tcs;
        return tcs;
    }

    public static HttpResponseMessage Json<T>(T body) => JsonResponse(HttpStatusCode.OK, body);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            Bodies.Add((request.Method,
                request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty,
                await request.Content.ReadAsStringAsync(cancellationToken)));
        }

        var key = $"{request.Method} {request.RequestUri?.AbsolutePath.TrimStart('/')}";

        if (_gates.TryGetValue(key, out var gate))
        {
            return await gate.Task.WaitAsync(cancellationToken);
        }

        return _routes.TryGetValue(key, out var factory)
            ? factory()
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };
}

/// <summary>In-memory <see cref="IUiStateStore"/>; records the applied theme attributes so
/// tests can assert what would have been written to the document root.</summary>
public sealed class TestUiStateStore : IUiStateStore
{
    private readonly Dictionary<string, string> _values = [];

    public bool PrefersDark { get; set; }

    public string? AppliedFamily { get; private set; }

    public string? AppliedMode { get; private set; }

    public string? AppliedDensity { get; private set; }

    public string? AppliedRail { get; private set; }

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> PrefersDarkAsync() => Task.FromResult(PrefersDark);

    public Task ApplyThemeAsync(string family, string mode, string density)
    {
        AppliedFamily = family;
        AppliedMode = mode;
        AppliedDensity = density;
        return Task.CompletedTask;
    }

    public Task ApplyRailAsync(string? state)
    {
        AppliedRail = state;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Media pipeline double. Capability is OFF by default, like the web shell; a test that
/// exercises the desktop path calls <see cref="EnableLocalProcessing"/> and then reads
/// <see cref="Exports"/> to assert what was cut.
/// </summary>
public sealed class TestMediaPipeline : IMediaPipeline
{
    private bool _local;

    public bool CanProcessLocally => _local;

    public string? UnavailableReason => _local ? null : "Not available in tests.";

    public PickedMedia? LastPicked { get; private set; }

    /// <summary>Every clip export this session, in order — the batch's evidence.</summary>
    public List<ClipExportOptions> Exports { get; } = [];

    public void EnableLocalProcessing(string fileName = "webinar.mp4")
    {
        _local = true;
        LastPicked = new PickedMedia($"/tmp/{fileName}", fileName, 1024);
    }

    public Task<PickedMedia?> PickMediaAsync() => Task.FromResult(LastPicked);

    public Task<LocalTranscription> TranscribeAsync(
        PickedMedia media, IProgress<PipelineProgress> progress, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string> ExportClipAsync(
        PickedMedia source, ClipExportOptions options,
        IReadOnlyList<Castmill.Core.Ai.TranscriptSegment>? captionSegments,
        IProgress<PipelineProgress> progress, CancellationToken ct = default)
    {
        if (!_local)
        {
            throw new NotSupportedException("Not available in tests.");
        }

        Exports.Add(options);
        progress.Report(new PipelineProgress("re-encoding clip", 100));
        return Task.FromResult($"/tmp/Castmill clips/webinar/{options.OutputName ?? "clip"}.mp4");
    }
}

public sealed class TestShellInfo : IShellInfo
{
    public string Name { get; set; } = "Test shell";

    public string HostDescription { get; set; } = "Headless renderer";

    public bool IsDevelopment { get; set; } = true;
}
