using Microsoft.JSInterop;

namespace Castmill.UI.Platform;

public sealed class BrowserVoiceCaptureService(IJSRuntime js)
    : IVoiceCaptureService, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-recorder.js";
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserVoiceCaptureService>? _self;

    public VoiceCaptureSnapshot Snapshot { get; private set; } =
        new(VoiceCaptureStates.Idle);

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct);
            Snapshot = await module.InvokeAsync<VoiceCaptureSnapshot>("capability", ct);
        }
        catch (JSException)
        {
            Snapshot = new VoiceCaptureSnapshot(
                VoiceCaptureStates.Unsupported,
                Message: "Voice recording is unavailable in this shell.");
        }
        Changed?.Invoke();
    }

    public async Task StartAsync(int maxSeconds, CancellationToken ct = default)
    {
        var module = await ModuleAsync(ct);
        _self ??= DotNetObjectReference.Create(this);
        await module.InvokeVoidAsync("start", ct, _self, maxSeconds);
    }

    public async Task PauseAsync(CancellationToken ct = default) =>
        await (await ModuleAsync(ct)).InvokeVoidAsync("pause", ct);

    public async Task ResumeAsync(CancellationToken ct = default) =>
        await (await ModuleAsync(ct)).InvokeVoidAsync("resume", ct);

    public async Task StopAsync(CancellationToken ct = default) =>
        await (await ModuleAsync(ct)).InvokeVoidAsync("stop", ct);

    public async Task DiscardAsync(CancellationToken ct = default)
    {
        await (await ModuleAsync(ct)).InvokeVoidAsync("discard", ct);
        Snapshot = new VoiceCaptureSnapshot(VoiceCaptureStates.Idle);
        Changed?.Invoke();
    }

    public async Task<VoiceRecording> UseAsync(CancellationToken ct = default)
    {
        var result = await (await ModuleAsync(ct))
            .InvokeAsync<VoiceRecordingResult>("getRecording", ct);
        return new VoiceRecording(
            result.Bytes,
            result.FileName,
            result.ContentType,
            TimeSpan.FromSeconds(result.DurationSeconds),
            result.PlaybackUrl);
    }

    [JSInvokable]
    public Task OnVoiceCaptureChanged(VoiceCaptureSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        _self?.Dispose();
    }

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken ct) =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ct, ModulePath);

    private sealed record VoiceRecordingResult(
        byte[] Bytes,
        string FileName,
        string ContentType,
        double DurationSeconds,
        string PlaybackUrl);
}