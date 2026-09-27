using Castmill.UI.Platform;

namespace Castmill.Desktop.Platform;

/// <summary>
/// On Mac Catalyst HttpClient runs on NSURLSession. The deep SEO/AEO analysis and image
/// renders send nothing for minutes, so the desktop states its transport windows here
/// rather than inheriting platform defaults: 10 minutes per request, matching the client's
/// HttpClient budget, and 15 per resource. Measured 2026-09-26 on a Catalyst harness: the
/// default handler also waited out a 330-second silent response, so this pins the limits;
/// it did not fix an observed 60-second cutoff.
/// </summary>
public sealed class DesktopHttpHandlerFactory : IPlatformHttpHandlerFactory
{
    public HttpMessageHandler Create()
    {
#if MACCATALYST
        var configuration = Foundation.NSUrlSessionConfiguration.DefaultSessionConfiguration;
        configuration.TimeoutIntervalForRequest = TimeSpan.FromMinutes(10).TotalSeconds;
        configuration.TimeoutIntervalForResource = TimeSpan.FromMinutes(15).TotalSeconds;
        return new System.Net.Http.NSUrlSessionHandler(configuration);
#else
        return new HttpClientHandler();
#endif
    }
}
