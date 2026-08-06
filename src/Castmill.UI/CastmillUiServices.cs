using Castmill.UI.Auth;
using Castmill.UI.Design;
using Castmill.UI.Http;
using Castmill.UI.State;
using Castmill.UI.Platform;
using IgniteUI.Blazor.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Castmill.UI;

/// <summary>
/// The one place shared UI services are registered. Both shells call this and add nothing
/// of their own beyond the platform-seam implementations, which is what keeps them from
/// drifting apart (G1 / ADR-F01).
/// </summary>
public static class CastmillUiServices
{
    /// <summary>
    /// Registers every service the shared UI needs.
    /// </summary>
    /// <param name="services">The shell's service collection.</param>
    /// <param name="apiBaseAddress">
    /// Where <c>/api/v1</c> lives. The web shell points at its own origin (or the dev API);
    /// the desktop shell has no origin of its own and must be told.
    /// </param>
    public static IServiceCollection AddCastmillUi(this IServiceCollection services, Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);

        services.AddIgniteUIBlazor(
            typeof(IgbCardModule),
            typeof(IgbButtonModule),
            typeof(IgbChipModule),
            typeof(IgbBadgeModule),
            typeof(IgbInputModule),
            typeof(IgbDialogModule));

        // ---- Design system -----------------------------------------------------
        services.AddScoped<IUiStateStore, BrowserUiStateStore>();
        services.AddScoped<ThemeService>();
        services.AddScoped<Notifier>();
        services.AddScoped<INotifier>(sp => sp.GetRequiredService<Notifier>());
        services.AddScoped<ConfirmService>();
        services.AddScoped<IConfirmService>(sp => sp.GetRequiredService<ConfirmService>());

        // ---- HTTP --------------------------------------------------------------
        // Every call goes through CastmillHttpHandler — the one chokepoint for the bearer
        // token, the correlation ID, silent refresh and typed errors.
        services.AddScoped(sp => new HttpClient(
            new CastmillHttpHandler(sp.GetRequiredService<IAuthTokenProvider>())
            {
                InnerHandler = new HttpClientHandler(),
            })
        {
            BaseAddress = apiBaseAddress,
        });

        services.AddScoped<ApiClient>();
        services.AddScoped<AuthClient>();
        services.AddScoped<CampaignsClient>();
        services.AddScoped<GenerationClient>();
        services.AddScoped<SeoClient>();
        services.AddScoped<ImagesClient>();
        services.AddScoped<BrandsClient>();

        // The dependency graph is genuinely circular: the token provider refreshes through
        // AuthClient, which resolves an HttpClient whose handler needs the token provider.
        // A lazy factory is the seam that breaks it — the provider only calls back once a
        // refresh is actually needed, by which time everything is constructed.
        services.AddScoped<Func<AuthClient>>(sp => sp.GetRequiredService<AuthClient>);

        services.AddScoped<AuthState>();

        // ---- State stores (ADR-F04) -------------------------------------------
        // Workspace scope and campaign scope are separate stores on purpose: the rail must
        // not re-fetch when the active campaign changes, and the campaign views must not
        // re-fetch when the campaign list does.
        services.AddScoped<WorkspaceState>();
        services.AddScoped<CampaignState>();
        // Scoped, NOT per-page: the run must survive the navigation from the new-campaign
        // flow to the Mill Floor, and a component awaiting the POST would cancel it.
        services.AddScoped<PressRunService>();
        services.AddScoped<StudioRunService>();

        return services;
    }
}
