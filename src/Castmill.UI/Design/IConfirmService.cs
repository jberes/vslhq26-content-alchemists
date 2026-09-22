namespace Castmill.UI.Design;

/// <summary>Text for a confirm prompt. <paramref name="Destructive"/> styles the
/// accept action with the danger token rather than the accent.</summary>
/// <param name="RequirePhrase">
/// When set, the prompt becomes a STRONG confirm: the accept action stays disabled until the
/// producer types this exact phrase (the campaign's name, say). For an action that destroys
/// work and cannot be undone, a single click — on a button the dialog has already focused —
/// is too easy to issue by reflex. Null for an ordinary confirm.
/// </param>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string AcceptLabel = "Continue",
    string CancelLabel = "Cancel",
    bool Destructive = false,
    string? RequirePhrase = null);

/// <summary>
/// One confirm dialog for the whole app (roadmap E3.4) so destructive actions can never
/// each invent their own. Awaits the user's answer: true = accepted.
/// </summary>
public interface IConfirmService
{
    Task<bool> ConfirmAsync(ConfirmRequest request);
}
