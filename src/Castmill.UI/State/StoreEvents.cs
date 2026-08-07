using Castmill.UI.Design;

namespace Castmill.UI.State;

/// <summary>
/// Runs a store-change handler so that a failure inside it becomes a message instead of
/// killing the app.
///
/// This exists because of a specific, non-obvious Blazor rule. A store handler is invoked as
/// <c>InvokeAsync(async () =&gt; …)</c> — fire-and-forget work on the renderer's dispatcher,
/// NOT a component lifecycle method. An exception escaping it is therefore reported to the
/// RENDERER's unhandled-exception handler, which tears everything down and shows Blazor's
/// generic "An unhandled error has occurred" bar. Critically, <c>&lt;ErrorBoundary&gt;</c>
/// does NOT catch it: a boundary only catches lifecycle and event-callback exceptions raised
/// by components beneath it, and dispatcher work is neither.
///
/// That is exactly the failure that was reported — a blank campaign, the global error bar,
/// and "Loading campaign…" frozen forever because the render that would have cleared it died
/// with the handler. Wrapping the store in try/catch could never have fixed it, because these
/// handlers return void to the store and fault only later, on their own.
///
/// The handlers this guards do real I/O (reloading galleries, brand faces, revisions), so
/// "this can't throw" is not available to us.
/// </summary>
public static class StoreEvents
{
    /// <summary>
    /// Awaits <paramref name="work"/>, reports any failure through <paramref name="notifier"/>,
    /// and re-renders either way. Cancellation is a navigation, not a fault, so it is silent.
    /// </summary>
    public static async Task GuardedAsync(
        Func<Task> work, INotifier notifier, Action render, string what)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            notifier.ShowError($"{what} failed: {Describe(ex)}");
        }

        // Outside the catch on purpose: the view must repaint after a failure too, or it keeps
        // showing whatever half-state the throw left behind.
        render();
    }

    /// <summary>
    /// For fire-and-forget work that races component teardown and has nothing worth reporting
    /// — a toast dismissing itself after a delay when the user has already navigated away.
    /// <c>InvokeAsync</c> on a disposed component throws, and unobserved that reaches the same
    /// global error UI as any other dispatcher fault.
    ///
    /// Swallowing is correct here and only here: there is no user-visible failure, and the one
    /// surface that could report it is the component being torn down.
    /// </summary>
    public static async Task DetachedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        Http.ApiException api => api.Message,
        HttpRequestException => "couldn't reach the Castmill API.",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
