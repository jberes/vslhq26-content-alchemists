using Bunit;
using Castmill.Core.Ai;
using Castmill.UI.Http;
using Microsoft.AspNetCore.Components;

namespace Castmill.UI.Tests;

/// <summary>
/// ADR-086: the producer picks who writes every piece of text. OpenAI direct defaults to
/// gpt-6.1-sol at max effort and the full 128K output ceiling, the model is editable, and the
/// page says which route is live and whether it can run.
/// </summary>
public sealed class SettingsTextGenerationTests : CastmillUiTestContext
{
    private static readonly string[] SettingKeys =
    [
        TextGenerationSettings.ModelKey, TextGenerationSettings.EffortKey,
        TextGenerationSettings.MaxOutputTokensKey, TextGenerationSettings.ProviderKey,
    ];

    public SettingsTextGenerationTests()
    {
        SignInTestUser();
        Http.OnGet("api/v1/settings/secrets", new List<SecretStatus>
        {
            new("OpenAiImageKey", true, DateTimeOffset.UtcNow),
        });
        Http.OnGet("api/v1/settings", new List<SettingRow>());
        Http.OnGet("api/v1/ai/status", Status(new TextGenerationStatus(
            "foundry", "gpt-5.6-terra", "deployment default", 0, true, null)));
        foreach (var key in SettingKeys)
        {
            Http.OnStatus(HttpMethod.Put, $"api/v1/settings/{key}", System.Net.HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Choosing_openai_saves_sol_at_max_effort_and_the_full_output_ceiling()
    {
        var view = await OpenModelsTabAsync();
        Assert.Contains("Now: Azure AI Foundry · gpt-5.6-terra", view.Markup, StringComparison.Ordinal);

        await view.Find("select.cm-input").ChangeAsync(new ChangeEventArgs { Value = TextGenerationSettings.OpenAi });
        Assert.Equal("gpt-6.1-sol", view.Find("input.cm-mono").GetAttribute("value"));
        Assert.Equal("128000", view.Find("input[type='number']").GetAttribute("value"));

        await SaveAsync(view);

        Assert.Equal("gpt-6.1-sol", Written(TextGenerationSettings.ModelKey));
        Assert.Equal("max", Written(TextGenerationSettings.EffortKey));
        Assert.Equal("128000", Written(TextGenerationSettings.MaxOutputTokensKey));
        Assert.Equal("openai", Written(TextGenerationSettings.ProviderKey));
        // The route switches last, so no run sees "openai" with the previous model.
        var puts = Http.Bodies.Where(b => b.Method == HttpMethod.Put).Select(b => b.Path).ToList();
        Assert.EndsWith(TextGenerationSettings.ProviderKey, puts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_can_be_changed()
    {
        var view = await OpenModelsTabAsync();
        await view.Find("select.cm-input").ChangeAsync(new ChangeEventArgs { Value = TextGenerationSettings.OpenAi });

        await view.Find("input.cm-mono").ChangeAsync(new ChangeEventArgs { Value = "  gpt-6.2-sol  " });
        await SaveAsync(view);

        Assert.Equal("gpt-6.2-sol", Written(TextGenerationSettings.ModelKey));
    }

    [Fact]
    public async Task Effort_is_picked_by_name_low_to_max_and_saved_as_the_api_value()
    {
        var view = await OpenModelsTabAsync();
        await view.Find("select.cm-input").ChangeAsync(new ChangeEventArgs { Value = TextGenerationSettings.OpenAi });

        var effort = view.FindAll("label.cm-field")
            .Single(label => label.TextContent.Contains("Reasoning effort", StringComparison.Ordinal))
            .QuerySelector("select")!;
        var options = effort.QuerySelectorAll("option").Select(o => o.TextContent.Split(" — ")[0]).ToList();
        Assert.Equal(["Low", "Medium", "High", "Extra High", "Max"], options);
        Assert.Equal("max", effort.GetAttribute("value"));

        await view.FindAll("label.cm-field")
            .Single(label => label.TextContent.Contains("Reasoning effort", StringComparison.Ordinal))
            .QuerySelector("select")!.ChangeAsync(new ChangeEventArgs { Value = "xhigh" });
        await SaveAsync(view);

        Assert.Equal("xhigh", Written(TextGenerationSettings.EffortKey));
    }

    [Fact]
    public async Task A_saved_openai_route_without_a_key_says_so()
    {
        Http.OnGet("api/v1/settings", new List<SettingRow>
        {
            new(TextGenerationSettings.ProviderKey, "openai"),
            new(TextGenerationSettings.EffortKey, "xhigh"),
        });
        Http.OnGet("api/v1/ai/status", Status(new TextGenerationStatus(
            "openai", "gpt-6.1-sol", "xhigh", 128_000, false, "No OpenAI API key stored — add it under Credentials.")));

        var view = await OpenModelsTabAsync();

        Assert.Equal("openai", view.Find("select.cm-input").GetAttribute("value"));
        Assert.Contains("NOT READY", view.Markup, StringComparison.Ordinal);
        Assert.Contains("No OpenAI API key stored", view.Markup, StringComparison.Ordinal);
    }

    private async Task<IRenderedComponent<Castmill.UI.Pages.Settings>> OpenModelsTabAsync()
    {
        var view = Render<Castmill.UI.Pages.Settings>();
        await view.WaitForStateAsync(
            () => view.FindAll("button[role='tab']").Count == 6, TimeSpan.FromSeconds(5));
        await view.FindAll("button[role='tab']")
            .Single(tab => tab.TextContent.Trim() == "Models")
            .ClickAsync();
        await view.WaitForStateAsync(
            () => view.Markup.Contains("Now:", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        return view;
    }

    private static async Task SaveAsync(IRenderedComponent<Castmill.UI.Pages.Settings> view) =>
        await view.FindAll("button")
            .Single(button => button.TextContent.Contains("Save text model", StringComparison.Ordinal))
            .ClickAsync();

    private string Written(string key)
    {
        var body = Http.Bodies.Last(b => b.Method == HttpMethod.Put
            && b.Path.EndsWith("/" + key, StringComparison.Ordinal)).Body;
        return System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("value").GetString()!;
    }

    private static AiStatusResponse Status(TextGenerationStatus text) => new(
        "config", true, new Dictionary<string, string> { ["chat"] = "gpt-5.6-terra" },
        false, null, [], TextGeneration: text);
}
