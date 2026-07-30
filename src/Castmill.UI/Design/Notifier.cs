namespace Castmill.UI.Design;

/// <summary>
/// Holds the live notification queue. Scoped, so each app instance has its own; the host
/// component subscribes to <see cref="Changed"/> and renders whatever is in
/// <see cref="Current"/>.
/// </summary>
public sealed class Notifier : INotifier
{
    private static readonly TimeSpan AutoDismiss = TimeSpan.FromMilliseconds(2600);

    private readonly List<Notification> _current = [];

    public IReadOnlyList<Notification> Current => _current;

    public event Action? Changed;

    public void ShowInfo(string message) => Add(message, NotificationSeverity.Info, AutoDismiss);

    public void ShowSuccess(string message) => Add(message, NotificationSeverity.Success, AutoDismiss);

    public void ShowWarning(string message) => Add(message, NotificationSeverity.Warning, AutoDismiss);

    // Timeout.InfiniteTimeSpan means "until dismissed".
    public void ShowError(string message) => Add(message, NotificationSeverity.Error, Timeout.InfiniteTimeSpan);

    public void Dismiss(Guid id)
    {
        if (_current.RemoveAll(n => n.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private void Add(string message, NotificationSeverity severity, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _current.Add(new Notification(message, severity, duration));
        Changed?.Invoke();
    }
}
