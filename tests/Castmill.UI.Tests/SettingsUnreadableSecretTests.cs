using Bunit;
using Castmill.Core.Ai;
using Castmill.UI.Http;

namespace Castmill.UI.Tests;

/// <summary>
/// A stored key the server can no longer decrypt (encryption key rotated) is a RE-ENTER card,
/// not a 500 and not a silent NOT SET (ADR-060).
/// </summary>
public sealed class SettingsUnreadableSecretTests : CastmillUiTestContext
{
    public SettingsUnreadableSecretTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/settings/secrets", new List<SecretStatus>
        {
            new("NanoBananaKey", true, DateTimeOffset.UtcNow.AddHours(-23), Readable: false),
            new("OpenAiImageKey", true, DateTimeOffset.UtcNow.AddHours(-23)),
            new("FoundryKey", false, null),
        });
        Http.OnGet("api/v1/settings", new List<SettingRow>());
        Http.OnGet("api/v1/ai/status", new AiStatusResponse(
            "config", false, new Dictionary<string, string>(), false, null, []));
    }

    [Fact]
    public async Task An_unreadable_secret_asks_to_be_pasted_again()
    {
        var view = Render<Castmill.UI.Pages.Settings>();
        await view.WaitForStateAsync(() => view.FindAll(".cm-settings__card").Count >= 3, TimeSpan.FromSeconds(5));

        var cards = view.FindAll(".cm-settings__card");
        var nano = cards.Single(c => c.TextContent.Contains("Nano Banana", StringComparison.Ordinal));
        Assert.Equal("RE-ENTER", nano.QuerySelector(".cm-badge")!.TextContent.Trim());
        Assert.Contains("encryption key changed", nano.QuerySelector(".cm-settings__reenter")!.TextContent, StringComparison.Ordinal);

        var openAi = cards.Single(c => c.TextContent.Contains("gpt-image key", StringComparison.Ordinal));
        Assert.Equal("STORED", openAi.QuerySelector(".cm-badge")!.TextContent.Trim());
        Assert.Null(openAi.QuerySelector(".cm-settings__reenter"));

        // Older servers omit the field: it defaults to readable.
        Assert.Equal(0, cards.Count(c => c.TextContent.Contains("NOT SET", StringComparison.Ordinal) && c.QuerySelector(".cm-settings__reenter") is not null));
    }
}
