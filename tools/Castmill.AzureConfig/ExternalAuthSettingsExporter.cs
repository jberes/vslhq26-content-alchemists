using System.Text.Json;

namespace Castmill.AzureConfig;

public static class ExternalAuthSettingsExporter
{
    private static readonly string[][] AllowedPaths =
    [
        ["AttemptLifetimeMinutes"],
        ["RetentionHours"],
        ["CleanupIntervalMinutes"],
        ["Providers", "Microsoft", "Enabled"],
        ["Providers", "Microsoft", "ClientId"],
        ["Providers", "Google", "Enabled"],
        ["Providers", "Google", "ClientId"],
        ["Clients", "Web", "SignInReturnUri"],
        ["Clients", "Web", "AccountSettingsReturnUri"],
    ];

    public static void AddAllowed(
        JsonElement root,
        IDictionary<string, string> settings)
    {
        if (!root.TryGetProperty("ExternalAuth", out var externalAuth)
            || externalAuth.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var path in AllowedPaths)
        {
            var value = externalAuth;
            if (!path.All(segment =>
                value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(segment, out value)))
            {
                continue;
            }

            var serialized = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(serialized))
            {
                settings[$"ExternalAuth__{string.Join("__", path)}"] = serialized;
            }
        }
    }

    public static void AddProductionWebReturnUris(
        IDictionary<string, string> settings,
        string webBaseUrl)
    {
        if (!Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "The production Web base URL must be an HTTPS origin.",
                nameof(webBaseUrl));
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        settings["ExternalAuth__Clients__Web__SignInReturnUri"] = $"{origin}/sign-in";
        settings["ExternalAuth__Clients__Web__AccountSettingsReturnUri"] =
            $"{origin}/settings/security";
    }
}