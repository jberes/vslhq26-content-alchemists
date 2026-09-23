namespace Castmill.UI.Platform;

public static class LiveTranscriptionStates
{
    public const string Idle = "Idle";
    public const string RequestingPermission = "RequestingPermission";
    public const string Listening = "Listening";
    public const string Stopped = "Stopped";
    public const string PermissionDenied = "PermissionDenied";
    public const string Unsupported = "Unsupported";
    public const string Error = "Error";
}

/// <summary>
/// What the producer can see right now. <paramref name="Committed"/> is text the recogniser
/// has finalised and will not revise; <paramref name="Partial"/> is the phrase still in flight
/// and may change on the next update. Keeping them apart is what lets the UI show speech
/// arriving without the settled text flickering as the recogniser changes its mind.
/// </summary>
public sealed record LiveTranscriptionSnapshot(
    string State,
    string Committed = "",
    string Partial = "",
    double ElapsedSeconds = 0,
    string? Message = null)
{
    public bool IsListening => State == LiveTranscriptionStates.Listening;

    public bool IsAvailable =>
        State is not (LiveTranscriptionStates.Unsupported or LiveTranscriptionStates.PermissionDenied);

    /// <summary>Everything said so far, which is what lands in the campaign's source text.</summary>
    public string FullText =>
        string.IsNullOrWhiteSpace(Partial)
            ? Committed
            : string.IsNullOrWhiteSpace(Committed) ? Partial : $"{Committed} {Partial}";

    public bool HasText => FullText.Trim().Length > 0;
}

/// <summary>
/// Live speech-to-text from the machine's microphone, transcribed as the producer speaks.
///
/// Deliberately NOT part of <see cref="IVoiceCaptureService"/>: that records an audio file in
/// the WebView and transcribes it afterwards, and the desktop WebView cannot reach a
/// microphone at all. This seam is implemented with each platform's own on-device recogniser
/// — Apple's Speech framework on Mac, Windows' SpeechRecognizer on Windows — so the audio and
/// the text both stay on the machine and partial results arrive in about a second.
/// </summary>
public interface ILiveTranscriptionService
{
    LiveTranscriptionSnapshot Snapshot { get; }

    event Action? Changed;

    /// <summary>Probes support without asking for permission, so the UI can hide what cannot run.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Asks for microphone and speech permission if needed, then begins listening.</summary>
    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    /// <summary>Clears the transcript so the next recording starts from nothing.</summary>
    Task ResetAsync(CancellationToken ct = default);
}

/// <summary>
/// The fallback every shell gets unless it registers a real one. It reports Unsupported rather
/// than throwing, because "this build cannot listen" is a normal answer the source picker has
/// to render — the browser shell has no on-device recogniser to offer.
/// </summary>
public sealed class UnsupportedLiveTranscriptionService(string? reason = null) : ILiveTranscriptionService
{
    public LiveTranscriptionSnapshot Snapshot { get; } = new(
        LiveTranscriptionStates.Unsupported,
        Message: reason ?? "Live transcription needs the Castmill desktop app.");

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
}
