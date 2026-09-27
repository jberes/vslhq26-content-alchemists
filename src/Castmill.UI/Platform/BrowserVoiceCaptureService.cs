using System.Text.Json;
using Castmill.UI.Design;
using Microsoft.JSInterop;

namespace Castmill.UI.Platform;

public sealed class BrowserVoiceCaptureService(IJSRuntime js, IUiStateStore state)
    : IVoiceCaptureService, IAsyncDisposable
{
    private const string ModulePath = "./_content/Castmill.UI/js/castmill-recorder.js";

    /// <summary>Per device, like the theme (ADR-F06): each machine has its own microphones.</summary>
    internal const string InputKey = "cm.voice.input";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserVoiceCaptureService>? _self;

    public VoiceCaptureSnapshot Snapshot { get; private set; } =
        new(VoiceCaptureStates.Idle);

    public IReadOnlyList<AudioInput> Inputs { get; private set; } = [];

    public AudioInput? SelectedInput { get; private set; }

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var module = await ModuleAsync(ct);
            Snapshot = await module.InvokeAsync<VoiceCaptureSnapshot>("capability", ct);
            if (Snapshot.State == VoiceCaptureStates.Idle)
            {
                SelectedInput = await LoadChoiceAsync();
                _self ??= DotNetObjectReference.Create(this);
                Inputs = await module.InvokeAsync<AudioInput[]>("listInputs", ct);
                await module.InvokeVoidAsync("watchInputs", ct, _self);
            }
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
        await module.InvokeVoidAsync("start", ct, _self, maxSeconds, SelectedInput);
    }

    public async Task SelectInputAsync(AudioInput? input, CancellationToken ct = default)
    {
        SelectedInput = input;
        await SaveChoiceAsync();
        Changed?.Invoke();
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
    public async Task OnVoiceCaptureChanged(VoiceCaptureSnapshot snapshot)
    {
        Snapshot = snapshot;
        // Browsers may give the same microphone a new id (Safari does per session); the recorder
        // found it again by name, so remember the id it has now.
        if (SelectedInput is { } chosen && snapshot.Notice is null
            && snapshot.InputLabel == chosen.Label && snapshot.InputDeviceId is { Length: > 0 } id
            && id != chosen.DeviceId)
        {
            SelectedInput = chosen with { DeviceId = id };
            await SaveChoiceAsync();
        }
        Changed?.Invoke();
    }

    [JSInvokable]
    public Task OnInputsChanged(AudioInput[] inputs)
    {
        Inputs = inputs;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task<AudioInput?> LoadChoiceAsync()
    {
        var saved = await state.GetAsync(InputKey);
        if (string.IsNullOrWhiteSpace(saved))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<AudioInput>(saved, Json) is { DeviceId.Length: > 0 } input ? input : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SaveChoiceAsync() =>
        state.SetAsync(InputKey, SelectedInput is null ? string.Empty : JsonSerializer.Serialize(SelectedInput, Json));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("unwatchInputs");
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