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
    string? Message = null)
{
    public bool IsRecording => State is VoiceCaptureStates.Recording or VoiceCaptureStates.Paused;
}

public sealed record VoiceRecording(
    byte[] Bytes,
    string FileName,
    string ContentType,
    TimeSpan Duration,
    string PlaybackUrl);

public interface IVoiceCaptureService
{
    VoiceCaptureSnapshot Snapshot { get; }
    event Action? Changed;
    Task InitializeAsync(CancellationToken ct = default);
    Task StartAsync(int maxSeconds, CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task DiscardAsync(CancellationToken ct = default);
    Task<VoiceRecording> UseAsync(CancellationToken ct = default);
}