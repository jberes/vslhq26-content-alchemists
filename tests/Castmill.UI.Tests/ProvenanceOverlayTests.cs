using Castmill.UI.Canvas;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Castmill.UI.Tests;

public sealed class ProvenanceOverlayTests : CastmillUiTestContext
{
    [Fact]
    public async Task Stale_module_without_auto_scroll_export_does_not_break_the_overlay()
    {
        var module = new StaleOverlayModule();

        await ProvenanceOverlay.TryScrollCitationIntoViewAsync(
            module, new ElementReference("canvas"), "s01");

        Assert.Equal(1, module.ScrollCalls);
    }

    private sealed class StaleOverlayModule : IJSObjectReference
    {
        public int ScrollCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            ScrollCalls++;
            throw new JSException("The value 'scrollCitationIntoView' is not a function.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}