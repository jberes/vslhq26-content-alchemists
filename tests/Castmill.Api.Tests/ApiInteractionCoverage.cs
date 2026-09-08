using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.Api.Tests;

public sealed class ApiInteractionCoverage : IStartupFilter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _responses = new();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            await continuation(context);
            if (context.GetEndpoint() is RouteEndpoint endpoint
                && endpoint.RoutePattern.RawText is { } route)
            {
                var responses = _responses.GetOrAdd($"{context.Request.Method} {route}", _ => new());
                responses.AddOrUpdate(context.Response.StatusCode, 1, (_, count) => count + 1);
            }
        });
        next(app);
    };

    public async Task WriteReportAsync(IServiceProvider services)
    {
        var routes = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(route => new
            {
                Route = route,
                Responses = _responses.TryGetValue(route, out var responses)
                    ? responses.OrderBy(response => response.Key).ToDictionary()
                    : new Dictionary<int, int>(),
            }).ToArray();
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults", "api-interactions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Scope = "HTTP requests observed in CastmillApiFactory; route hits are not full behavior coverage.",
            TotalRoutes = routes.Length,
            ExercisedRoutes = routes.Count(route => route.Responses.Count > 0),
            SuccessfulRoutes = routes.Count(route => route.Responses.Keys.Any(code => code is >= 200 and < 300)),
            Routes = routes,
        }, Json));
    }
}