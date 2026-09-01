using Bunit;
using Castmill.Core.Ai;
using Castmill.UI.Http;

namespace Castmill.UI.Tests;

public sealed class SettingsDefaultImageModelTests : CastmillUiTestContext
{
    public SettingsDefaultImageModelTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/settings/secrets", new List<SecretStatus>());
        Http.OnGet("api/v1/settings", new List<SettingRow>
        {
            new(SettingsClient.DefaultImageModelKey, "image"),
        });
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", true,
            new Dictionary<string, string>
            {
                ["image"] = "foundry-resource:gpt-image-2",
                ["image-alt"] = "foundry-resource:mai-image-2.5-pro",
            },
            false, null,
            [new ImageProviderReadiness("foundry", true, null, SupportsReferenceImages: true)]));
        Http.OnStatus(HttpMethod.Put,
            $"api/v1/settings/{SettingsClient.DefaultImageModelKey}",
            System.Net.HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Models_tab_reads_and_persists_the_workspace_image_default()
    {
        var view = Render<Castmill.UI.Pages.Settings>();
        await view.WaitForStateAsync(
            () => view.FindAll("button[role='tab']").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("button[role='tab']")
            .Single(tab => tab.TextContent.Trim() == "Models")
            .ClickAsync();

        var select = view.Find("select[aria-label='Default image generator']");
        Assert.Equal("image", select.GetAttribute("value"));
        await select.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs
        {
            Value = "image-alt",
        });
        await view.FindAll("button")
            .Single(button => button.TextContent.Contains("Save default", StringComparison.Ordinal))
            .ClickAsync();

        var write = Http.Bodies.Single(body =>
            body.Method == HttpMethod.Put
            && body.Path.EndsWith(SettingsClient.DefaultImageModelKey, StringComparison.Ordinal));
        Assert.Contains("image-alt", write.Body, StringComparison.Ordinal);
        Assert.Contains("SAVED", view.Markup, StringComparison.Ordinal);
    }
}
