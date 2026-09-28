using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Castmill.UI.Design;

/// <summary>
/// For a <c>&lt;details open="@state" @ontoggle=…&gt;</c>: the browser raises <c>toggle</c> for
/// the component's own <c>open</c> writes as well as for clicks, so a handler that flips the flag
/// undoes every programmatic open, which re-renders, which toggles again — an endless open/close
/// loop (the whole Image Studio panel flashed after "Rewrite brief"). The handler must read what
/// the element actually is.
/// </summary>
public static class Disclosure
{
    public static async Task<bool> IsOpenAsync(IJSRuntime js, ElementReference details, bool current)
    {
        try
        {
            return await js.InvokeAsync<bool>("Reflect.get", details, "open");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            return current;
        }
    }
}
