using System.Text.RegularExpressions;
using Castmill.UI.Design;
using Castmill.UI.State;

namespace Castmill.UI.Tests;

/// <summary>
/// A store-change handler runs as fire-and-forget work on the renderer's dispatcher, not as a
/// component lifecycle method. An exception escaping one goes to the RENDERER's unhandled
/// handler — Blazor's global "An unhandled error has occurred" bar — and an
/// &lt;ErrorBoundary&gt; cannot catch it, because boundaries only catch lifecycle and
/// event-callback exceptions from components beneath them.
///
/// That combination produced the reported failure: a blank campaign, the global error bar, and
/// "Loading campaign…" frozen forever, because the render that would have cleared the loading
/// state died with the handler. These tests pin both halves of the fix.
/// </summary>
public sealed class StoreEventGuardTests
{
    [Fact]
    public async Task A_failing_handler_reports_and_still_repaints_instead_of_escaping()
    {
        var notifier = new Notifier();
        var rendered = 0;

        // Must not throw: escaping here is precisely what killed the app.
        await StoreEvents.GuardedAsync(
            () => throw new InvalidOperationException("boom"),
            notifier, () => rendered++, "Refreshing the studio");

        var error = Assert.Single(notifier.Current);
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Contains("Refreshing the studio failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);

        // The repaint happens AFTER the failure too, or the view keeps showing the half-state
        // the throw left behind — the frozen "Loading campaign…".
        Assert.Equal(1, rendered);
    }

    [Fact]
    public async Task A_cancelled_handler_is_silent()
    {
        var notifier = new Notifier();
        var rendered = 0;

        // Navigating away cancels in-flight work. That is not a fault and must not nag.
        await StoreEvents.GuardedAsync(
            () => throw new OperationCanceledException(),
            notifier, () => rendered++, "Refreshing the studio");

        Assert.Empty(notifier.Current);
        Assert.Equal(0, rendered);
    }

    [Fact]
    public async Task A_healthy_handler_repaints_and_says_nothing()
    {
        var notifier = new Notifier();
        var rendered = 0;

        await StoreEvents.GuardedAsync(
            () => Task.CompletedTask, notifier, () => rendered++, "Refreshing the studio");

        Assert.Empty(notifier.Current);
        Assert.Equal(1, rendered);
    }

    /// <summary>
    /// The real defect was six handlers written as <c>InvokeAsync(async () =&gt; …)</c> with no
    /// guard. Fixing those six fixes today; this fails the build on the seventh, which is the
    /// only way the class of bug stays fixed. bUnit cannot cover it — its renderer rethrows
    /// dispatcher faults into the test, so a passing component test proves nothing about the
    /// browser, where the same fault reaches the global error UI instead.
    /// </summary>
    [Fact]
    public void No_store_handler_dispatches_unguarded_work()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     UiRoot(), "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // The subscribers are the components that hook a store's Changed event.
            if (!text.Contains(".Changed += ", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(text, @"InvokeAsync\((?:async )?\(\) =>"))
            {
                var tail = text[(match.Index + match.Length)..];
                var head = tail[..Math.Min(120, tail.Length)];

                // Guarded when the work is routed through StoreEvents — either immediately
                // after the dispatch, or by an enclosing guard the dispatch sits inside.
                var before = text[Math.Max(0, match.Index - 400)..match.Index];
                if (head.Contains("StoreEvents.GuardedAsync", StringComparison.Ordinal)
                    || before.Contains("StoreEvents.DetachedAsync", StringComparison.Ordinal)
                    || before.Contains("StoreEvents.GuardedAsync", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {head.Split('\n')[0].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These dispatch unguarded work from a store handler, so a throw inside reaches "
            + "Blazor's global error UI and no ErrorBoundary can catch it. Route them through "
            + "StoreEvents.GuardedAsync:\n  " + string.Join("\n  ", offenders));
    }

    private static string UiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Castmill.UI")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Castmill.UI");
    }
}
