using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration.Json;

namespace Castmill.Api.Tests;

/// <summary>
/// Isolates tests from the developer's local appsettings.Development.json. Real
/// Foundry/SEO/SQL credentials must never leak into — or be spent by — a test run.
///
/// Blanking individual keys is not enough: a model alias can route to an Ai:Resources
/// entry carrying its own endpoint and key, so the whole file is dropped as a
/// configuration source and only explicit test values remain. Every factory that boots
/// the API in the Development environment MUST call this.
/// </summary>
internal static class HermeticConfiguration
{
    public static IWebHostBuilder DropDeveloperConfig(this IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
        {
            var devFiles = config.Sources
                .OfType<JsonConfigurationSource>()
                .Where(s => s.Path?.Contains("appsettings.Development.json", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();

            foreach (var source in devFiles)
            {
                config.Sources.Remove(source);
            }
        });

        return builder;
    }
}
