namespace Castmill.UI.Platform;

public static class VoiceCaptureStates
{
    public const string Idle = "Idle";
    public const string RequestingPermission = "RequestingPermission";
    public const string Recording = "Recording";
    public const string Paused = "Paused";
    public const string Stopped = "Stopped";
    public const string PermissionDenied = "PermissionDenied";
    public const string Unsupported = "Unsupported";
    public const string Error = "Error";
}

public sealed record VoiceCaptureSnapshot(
    string State,
    double ElapsedSeconds = 0,
    double InputLevel = 0,
    string? PlaybackUrl = null,
    string? ContentType = null,
    long SizeBytes = 0,
    string? Message = null,
    /// <summary>The input actually recording, as the browser names it — read from the track, not assumed.</summary>
    string? InputLabel = null,
    string? InputDeviceId = null,
    /// <summary>Set when the chosen microphone could not be opened and the system default was used instead.</summary>
    string? Notice = null)
{
    public bool IsRecording => State is VoiceCaptureStates.Recording or VoiceCaptureStates.Paused;
}

public sealed record VoiceRecording(
    byte[] Bytes,
    string FileName,
    string ContentType,
    TimeSpan Duration,
    string PlaybackUrl);

/// <summary>A microphone the browser names. Browsers name inputs only after the microphone is allowed once.</summary>
public sealed record AudioInput(string DeviceId, string Label);

public interface IVoiceCaptureService
{
    VoiceCaptureSnapshot Snapshot { get; }

    /// <summary>The named microphones, kept current as devices are plugged in or removed.</summary>
    IReadOnlyList<AudioInput> Inputs { get; }

    /// <summary>The producer's saved choice for this device; null means the system default.</summary>
    AudioInput? SelectedInput { get; }

    event Action? Changed;

    /// <summary>Saves the choice for this device (null = system default); Record then opens it.</summary>
    Task SelectInputAsync(AudioInput? input, CancellationToken ct = default);

    Task InitializeAsync(CancellationToken ct = default);
    Task StartAsync(int maxSeconds, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task DiscardAsync(CancellationToken ct = default);
    Task<VoiceRecording> UseAsync(CancellationToken ct = default);
}