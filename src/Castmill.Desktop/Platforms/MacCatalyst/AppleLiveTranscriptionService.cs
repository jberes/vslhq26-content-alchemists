using AVFoundation;
using Castmill.UI.Platform;
using Foundation;
using Speech;

namespace Castmill.Desktop.Platform;

/// <summary>
/// Live dictation through Apple's own stack: <c>AVAudioEngine</c> taps the microphone and
/// <c>SFSpeechRecognizer</c> turns it into text, emitting a revised phrase roughly once a
/// second. <c>RequiresOnDeviceRecognition</c> is set wherever the installed locale supports
/// it, so the audio never leaves the Mac — the reason this path exists rather than streaming
/// to a cloud recogniser.
///
/// Two permissions, asked in order and both fatal if refused: speech recognition
/// (<c>NSSpeechRecognitionUsageDescription</c>) and the microphone
/// (<c>NSMicrophoneUsageDescription</c>).
/// </summary>
public sealed class AppleLiveTranscriptionService : ILiveTranscriptionService, IDisposable
{
    private readonly object _gate = new();
    private AVAudioEngine? _engine;
    private SFSpeechRecognizer? _recogniser;
    private SFSpeechAudioBufferRecognitionRequest? _request;
    private SFSpeechRecognitionTask? _task;
    private DateTimeOffset _startedAt;
    private string _committed = string.Empty;

    public LiveTranscriptionSnapshot Snapshot { get; private set; } =
        new(LiveTranscriptionStates.Idle);

    public event Action? Changed;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        var recogniser = new SFSpeechRecognizer();
        if (!recogniser.Available)
        {
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.Unsupported,
                Message = "No speech recogniser is available for this Mac's language.",
            });
            return Task.CompletedTask;
        }

        Publish(new LiveTranscriptionSnapshot(LiveTranscriptionStates.Idle));
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Snapshot.IsListening)
        {
            return;
        }

        Publish(Snapshot with { State = LiveTranscriptionStates.RequestingPermission, Message = null });

        if (!await RequestSpeechAsync() || !await RequestMicrophoneAsync())
        {
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.PermissionDenied,
                Message = "Castmill needs microphone and speech-recognition permission to transcribe as you speak. "
                    + "Grant them in System Settings → Privacy & Security.",
            });
            return;
        }

        try
        {
            StartEngine();
        }
        catch (Exception ex)
        {
            TeardownEngine();
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.Error,
                Message = $"Recording could not start: {ex.Message}",
            });
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!Snapshot.IsListening)
        {
            return Task.CompletedTask;
        }

        // Fold whatever was still in flight into the settled text: the producer stopped, so
        // the last phrase is theirs whether or not the recogniser had finalised it.
        var full = Snapshot.FullText;
        TeardownEngine();
        _committed = full;
        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Stopped,
            Committed = full,
            Partial = string.Empty,
        });
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        TeardownEngine();
        _committed = string.Empty;
        Publish(new LiveTranscriptionSnapshot(LiveTranscriptionStates.Idle));
        return Task.CompletedTask;
    }

    private void StartEngine()
    {
        lock (_gate)
        {
            _recogniser = new SFSpeechRecognizer();
            _engine = new AVAudioEngine();
            _request = new SFSpeechAudioBufferRecognitionRequest
            {
                ShouldReportPartialResults = true,
            };

            // On-device where the locale allows it. Where it does not, Apple falls back to its
            // own service; the UI says so rather than pretending everything stayed local.
            if (_recogniser.SupportsOnDeviceRecognition)
            {
                _request.RequiresOnDeviceRecognition = true;
            }

            // The session MUST be configured and active before the input node is asked for its
            // format. Without this the node reports 0 Hz / 0 channels, and InstallTapOnBus
            // fails its own assertion with "required condition is false:
            // IsFormatSampleRateAndChannelCountValid(format)" — an Objective-C throw, not a
            // managed error, so there is nothing to handle after the fact.
            var session = AVAudioSession.SharedInstance();
            session.SetCategory(AVAudioSessionCategory.Record);
            session.SetActive(true, out var activationError);
            if (activationError is not null)
            {
                throw new InvalidOperationException(
                    $"The audio session could not be started: {activationError.LocalizedDescription}");
            }

            var input = _engine.InputNode;
            var format = input.GetBusOutputFormat(0);
            if (format.SampleRate <= 0 || format.ChannelCount == 0)
            {
                // Still invalid with an active session: no usable input device. Say that
                // plainly rather than letting AVFoundation assert.
                throw new InvalidOperationException(
                    "No microphone is available to record from. Check the input device in "
                    + "System Settings → Sound.");
            }

            input.InstallTapOnBus(0, 4096, format, (buffer, _) => _request?.Append(buffer));

            _engine.Prepare();
            _engine.StartAndReturnError(out var engineError);
            if (engineError is not null)
            {
                throw new InvalidOperationException(engineError.LocalizedDescription);
            }

            _startedAt = DateTimeOffset.UtcNow;
            _task = _recogniser.GetRecognitionTask(_request, OnResult);

            Publish(new LiveTranscriptionSnapshot(
                LiveTranscriptionStates.Listening,
                Committed: _committed,
                Message: _recogniser.SupportsOnDeviceRecognition
                    ? null
                    : "This Mac has no on-device model for your language, so Apple transcribes in the cloud."));
        }
    }

    private void OnResult(SFSpeechRecognitionResult? result, NSError? error)
    {
        if (error is not null)
        {
            // A recogniser that stops on its own (a long silence, a session reset) is not a
            // failure the producer caused — keep what was said and let them press record again.
            TeardownEngine();
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.Stopped,
                Committed = Snapshot.FullText,
                Partial = string.Empty,
                Message = "Listening stopped — press record to continue.",
            });
            return;
        }

        if (result is null)
        {
            return;
        }

        var heard = result.BestTranscription?.FormattedString ?? string.Empty;
        var elapsed = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;

        // The recogniser reports the WHOLE utterance each time, revising earlier words. It is
        // therefore the partial until it says final, at which point it joins the settled text
        // and the next utterance starts from empty.
        if (result.Final)
        {
            _committed = string.IsNullOrWhiteSpace(_committed) ? heard : $"{_committed} {heard}";
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.Listening,
                Committed = _committed,
                Partial = string.Empty,
                ElapsedSeconds = elapsed,
            });
            return;
        }

        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Listening,
            Committed = _committed,
            Partial = heard,
            ElapsedSeconds = elapsed,
        });
    }

    private void TeardownEngine()
    {
        lock (_gate)
        {
            try
            {
                if (_engine is { Running: true })
                {
                    _engine.Stop();
                    _engine.InputNode?.RemoveTapOnBus(0);
                }
            }
            catch (Exception)
            {
                // Tearing down a session that already died must not mask the real state.
            }

            _request?.EndAudio();
            _task?.Cancel();

            try
            {
                // Hand the microphone back; leaving the session active keeps the recording
                // indicator lit and blocks other apps.
                AVAudioSession.SharedInstance().SetActive(false, out _);
            }
            catch (Exception)
            {
                // A session that never activated is already in the state we want.
            }

            _task?.Dispose();
            _request?.Dispose();
            _engine?.Dispose();
            _recogniser?.Dispose();
            _task = null;
            _request = null;
            _engine = null;
            _recogniser = null;
        }
    }

    private static Task<bool> RequestSpeechAsync()
    {
        var completion = new TaskCompletionSource<bool>();
        SFSpeechRecognizer.RequestAuthorization(status =>
            completion.TrySetResult(status == SFSpeechRecognizerAuthorizationStatus.Authorized));
        return completion.Task;
    }

    /// <summary>
    /// The project supports Catalyst 15, where the only microphone prompt is the AVAudioSession
    /// one; Catalyst 17 moved it to AVAudioApplication and deprecated the old call. Both are
    /// kept behind a runtime check rather than raising the floor of the whole desktop app.
    /// </summary>
    private static Task<bool> RequestMicrophoneAsync()
    {
        var completion = new TaskCompletionSource<bool>();
        if (OperatingSystem.IsMacCatalystVersionAtLeast(17))
        {
            AVAudioApplication.RequestRecordPermission(granted => completion.TrySetResult(granted));
        }
        else
        {
#pragma warning disable CA1422 // The deprecated call is the only one that exists before 17.
            AVAudioSession.SharedInstance().RequestRecordPermission(granted => completion.TrySetResult(granted));
#pragma warning restore CA1422
        }

        return completion.Task;
    }

    private void Publish(LiveTranscriptionSnapshot snapshot)
    {
        Snapshot = snapshot;
        // The recogniser calls back on its own queue; the UI thread owns rendering.
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    public void Dispose() => TeardownEngine();
}
