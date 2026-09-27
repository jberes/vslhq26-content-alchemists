namespace Castmill.UI.Platform;

/// <summary>
/// Lets a shell supply the transport under <c>CastmillHttpHandler</c>. The default
/// <see cref="HttpClientHandler"/> is right for the browser; the macOS desktop supplies an
/// NSURLSession with explicit windows for minutes-long AI requests (see DesktopHttpHandlerFactory).
/// </summary>
public interface IPlatformHttpHandlerFactory
{
    HttpMessageHandler Create();
}
