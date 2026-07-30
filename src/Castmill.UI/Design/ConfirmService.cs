namespace Castmill.UI.Design;

/// <summary>
/// Mediates between callers and the single <c>ConfirmHost</c> component. The host
/// subscribes to <see cref="Requested"/>, shows the dialog, and calls
/// <see cref="Complete"/> with the answer.
/// </summary>
public sealed class ConfirmService : IConfirmService
{
    private TaskCompletionSource<bool>? _pending;

    public ConfirmRequest? Active { get; private set; }

    public event Action? Requested;

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A second prompt while one is open would strand the first caller's await
        // forever, so the earlier prompt resolves as cancelled rather than vanishing.
        _pending?.TrySetResult(false);

        Active = request;
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requested?.Invoke();

        return _pending.Task;
    }

    public void Complete(bool accepted)
    {
        var pending = _pending;
        _pending = null;
        Active = null;
        pending?.TrySetResult(accepted);
        Requested?.Invoke();
    }
}
