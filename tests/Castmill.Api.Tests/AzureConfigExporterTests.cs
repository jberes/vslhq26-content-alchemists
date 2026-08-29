using System.Text.Json;
using Castmill.AzureConfig;

namespace Castmill.Api.Tests;

public sealed class AzureConfigExporterTests
{
    [Fact]
    public void External_auth_export_allows_only_declared_non_secret_keys()
    {
        using var document = JsonDocument.Parse("""
            {
              "ExternalAuth": {
                "AttemptLifetimeMinutes": 10,
                "Unknown": "must-not-export",
                "FutureClientSecret": "future-secret",
                "Providers": {
                  "Microsoft": {
                    "Enabled": true,
                    "ClientId": "public-client-id",
                    "ClientSecret": "secret",
                    "ClientSecretRotationValue": "rotated-secret",
                    "Unexpected": "must-not-export"
                  },
                  "Google": {
                    "Enabled": false,
                    "ClientId": "other-public-client-id",
                    "ClientSecret": "other-secret"
                  }
                },
                "Clients": {
                  "Web": {
                    "SignInReturnUri": "https://castmill.example/sign-in",
                    "AccountSettingsReturnUri": "https://castmill.example/settings/security",
                    "Unexpected": "must-not-export"
                  }
                }
              }
            }
            """);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ExternalAuthSettingsExporter.AddAllowed(document.RootElement, settings);

        Assert.Equal("10", settings["ExternalAuth__AttemptLifetimeMinutes"]);
        Assert.Equal("true", settings["ExternalAuth__Providers__Microsoft__Enabled"]);
        Assert.Equal("public-client-id", settings["ExternalAuth__Providers__Microsoft__ClientId"]);
        Assert.Equal("https://castmill.example/sign-in", settings["ExternalAuth__Clients__Web__SignInReturnUri"]);
        Assert.DoesNotContain(settings, pair => pair.Key.Contains("ClientSecret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(settings, pair => pair.Key.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(settings, pair => pair.Key.Contains("Unknown", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(settings, pair => pair.Value.Contains("must-not-export", StringComparison.Ordinal));
        Assert.DoesNotContain(settings, pair => pair.Value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

      [Fact]
      public void Production_web_return_uris_override_local_values_from_https_origin()
      {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["ExternalAuth__Clients__Web__SignInReturnUri"] = "https://localhost:7124/sign-in",
        };

        ExternalAuthSettingsExporter.AddProductionWebReturnUris(
          settings,
          "https://castmill.example/");

        Assert.Equal(
          "https://castmill.example/sign-in",
          settings["ExternalAuth__Clients__Web__SignInReturnUri"]);
        Assert.Equal(
          "https://castmill.example/settings/security",
          settings["ExternalAuth__Clients__Web__AccountSettingsReturnUri"]);
      }

      [Theory]
      [InlineData("http://castmill.example/")]
      [InlineData("https://castmill.example/path")]
      [InlineData("https://castmill.example/?query=value")]
      public void Production_web_return_uris_reject_non_origin_values(string value)
      {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() =>
          ExternalAuthSettingsExporter.AddProductionWebReturnUris(settings, value));
      }
}