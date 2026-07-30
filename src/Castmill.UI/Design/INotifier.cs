namespace Castmill.UI.Design;

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A queued message. Auto-dismiss is 2600 ms, per the handoff's toast host.</summary>
public sealed record Notification(string Message, NotificationSeverity Severity, TimeSpan Duration)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Toasts behind an interface (roadmap E3.4) so features never construct an IgbToast
/// themselves and every message looks the same in both families. Rendered by
/// <c>NotificationHost</c>, mounted once in the shell layout.
/// </summary>
/// Methods are verb-first (<c>ShowError</c>, not <c>Error</c>) because a bare <c>Error</c>
/// member collides with a reserved word in other .NET languages (CA1716).
public interface INotifier
{
    void ShowInfo(string message);

    void ShowSuccess(string message);

    void ShowWarning(string message);

    /// <summary>Errors stay until dismissed — an error the user missed is an error unhandled.</summary>
    void ShowError(string message);
}
