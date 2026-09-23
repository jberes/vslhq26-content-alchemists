using Castmill.UI.Platform;
using Windows.Media.SpeechRecognition;

namespace Castmill.Desktop.Platform;

/// <summary>
/// Live dictation through Windows' own on-device recogniser. <c>SpeechRecognizer</c> in
/// continuous session mode raises <c>HypothesisGenerated</c> while a phrase is being spoken
/// and <c>ResultGenerated</c> when it settles, which maps onto the same partial/committed
/// split the Mac path uses.
///
/// Dictation runs locally when the machine has the language pack; Windows falls back to its
/// online service when "Online speech recognition" is enabled and no local model exists, so
/// this reports which it got rather than claiming local unconditionally.
/// </summary>
public sealed class WindowsLiveTranscriptionService : ILiveTranscriptionService, IDisposable
{
    private readonly object _gate = new();
    private SpeechRecognizer? _recogniser;
    private DateTimeOffset _startedAt;
    private string _committed = string.Empty;

    public LiveTranscriptionSnapshot Snapshot { get; private set; } =
        new(LiveTranscriptionStates.Idle);

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            using var probe = new SpeechRecognizer();
            await probe.CompileConstraintsAsync();
            Publish(new LiveTranscriptionSnapshot(LiveTranscriptionStates.Idle));
        }
        catch (Exception ex)
        {
            Publish(new LiveTranscriptionSnapshot(
                LiveTranscriptionStates.Unsupported,
                Message: $"Windows speech recognition is unavailable: {ex.Message}"));
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Snapshot.IsListening)
        {
            return;
        }

        Publish(Snapshot with { State = LiveTranscriptionStates.RequestingPermission, Message = null });

        try
        {
            var recogniser = new SpeechRecognizer();
            // Dictation, not a command grammar: the producer is speaking prose.
            recogniser.Constraints.Add(new SpeechRecognitionTopicConstraint(
                SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await recogniser.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success)
            {
                recogniser.Dispose();
                Publish(Snapshot with
                {
                    State = LiveTranscriptionStates.Error,
                    Message = $"Speech recognition could not start ({compiled.Status}).",
                });
                return;
            }

            recogniser.HypothesisGenerated += OnHypothesis;
            recogniser.ContinuousRecognitionSession.ResultGenerated += OnResult;
            recogniser.ContinuousRecognitionSession.Completed += OnCompleted;

            lock (_gate)
            {
                _recogniser = recogniser;
                _startedAt = DateTimeOffset.UtcNow;
            }

            await recogniser.ContinuousRecognitionSession.StartAsync();
            Publish(new LiveTranscriptionSnapshot(
                LiveTranscriptionStates.Listening, Committed: _committed));
        }
        catch (UnauthorizedAccessException)
        {
            Teardown();
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.PermissionDenied,
                Message = "Castmill needs microphone access and speech recognition turned on. "
                    + "Enable them in Settings → Privacy & security.",
            });
        }
        catch (Exception ex)
        {
            Teardown();
            Publish(Snapshot with
            {
                State = LiveTranscriptionStates.Error,
                Message = $"Recording could not start: {ex.Message}",
            });
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!Snapshot.IsListening)
        {
            return;
        }

        var full = Snapshot.FullText;
        try
        {
            var session = _recogniser?.ContinuousRecognitionSession;
            if (session is not null)
            {
                await session.StopAsync();
            }
        }
        catch (Exception)
        {
            // A session that already ended is still a successful stop from here.
        }

        Teardown();
        _committed = full;
        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Stopped,
            Committed = full,
            Partial = string.Empty,
        });
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        Teardown();
        _committed = string.Empty;
        Publish(new LiveTranscriptionSnapshot(LiveTranscriptionStates.Idle));
        return Task.CompletedTask;
    }

    private void OnHypothesis(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args) =>
        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Listening,
            Committed = _committed,
            Partial = args.Hypothesis.Text,
            ElapsedSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
        });

    private void OnResult(
        SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        // Low-confidence noise is dropped rather than written into the producer's transcript.
        if (args.Result.Confidence is SpeechRecognitionConfidence.Rejected)
        {
            return;
        }

        var heard = args.Result.Text;
        if (string.IsNullOrWhiteSpace(heard))
        {
            return;
        }

        _committed = string.IsNullOrWhiteSpace(_committed) ? heard : $"{_committed} {heard}";
        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Listening,
            Committed = _committed,
            Partial = string.Empty,
            ElapsedSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
        });
    }

    private void OnCompleted(
        SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (!Snapshot.IsListening)
        {
            return;
        }

        Teardown();
        Publish(Snapshot with
        {
            State = LiveTranscriptionStates.Stopped,
            Committed = Snapshot.FullText,
            Partial = string.Empty,
            Message = "Listening stopped — press record to continue.",
        });
    }

    private void Teardown()
    {
        lock (_gate)
        {
            if (_recogniser is null)
            {
                return;
            }

            try
            {
                _recogniser.HypothesisGenerated -= OnHypothesis;
                _recogniser.ContinuousRecognitionSession.ResultGenerated -= OnResult;
                _recogniser.ContinuousRecognitionSession.Completed -= OnCompleted;
                _recogniser.Dispose();
            }
            catch (Exception)
            {
                // Disposing a recogniser that already died must not mask the real state.
            }

            _recogniser = null;
        }
    }

    private void Publish(LiveTranscriptionSnapshot snapshot)
    {
        Snapshot = snapshot;
        MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }

    public void Dispose() => Teardown();
}
