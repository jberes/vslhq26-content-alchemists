using Castmill.UI.Platform;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace Castmill.Web.Platform;

/// <summary>Web implementation of the shell-identity seam. See <see cref="IShellInfo"/>.</summary>
internal sealed class WebShellInfo(IWebAssemblyHostEnvironment environment) : IShellInfo
{
    public string Name => "Web (Blazor WebAssembly)";

    public string HostDescription => "Browser · WebAssembly runtime";

    public bool IsDevelopment => environment.IsDevelopment();
}
