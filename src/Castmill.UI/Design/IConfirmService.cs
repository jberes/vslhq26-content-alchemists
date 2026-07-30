namespace Castmill.UI.Design;

/// <summary>Text for a confirm prompt. <paramref name="Destructive"/> styles the
/// accept action with the danger token rather than the accent.</summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string AcceptLabel = "Continue",
    string CancelLabel = "Cancel",
    bool Destructive = false);

/// <summary>
/// One confirm dialog for the whole app (roadmap E3.4) so destructive actions can never
/// each invent their own. Awaits the user's answer: true = accepted.
/// </summary>
public interface IConfirmService
{
    Task<bool> ConfirmAsync(ConfirmRequest request);
}
