using Castmill.Core.Ai;
using Castmill.UI.Design;

namespace Castmill.UI.Tests;

/// <summary>
/// ADR-078: the picker offers the gpt-image-2.5 pair and lands on sunburst when the
/// workspace has never chosen — but never at the cost of leaving it with no ready model.
/// </summary>
public sealed class ImageModelCatalogTests
{
    private static AiStatusResponse Status(bool openAiKeyStored) => new(
        "config", true,
        new Dictionary<string, string> { ["image"] = "eastus2:some-foundry-deployment" },
        false, null,
        [
            new ImageProviderReadiness("foundry", true, null, SupportsReferenceImages: true),
            new ImageProviderReadiness(
                "gpt-image-sunburst", openAiKeyStored,
                openAiKeyStored ? null : "No API key stored.", true, "gpt-image-2.5-sunburst"),
            new ImageProviderReadiness(
                "gpt-image-flare", openAiKeyStored,
                openAiKeyStored ? null : "No API key stored.", true, "gpt-image-2.5-flare"),
        ]);

    [Fact]
    public void Both_gpt_image_2_5_models_are_offered_with_their_model_ids()
    {
        var choices = ImageModelCatalog.Choices(Status(openAiKeyStored: true));

        Assert.Equal(
            "gpt-image-2.5-sunburst",
            choices.Single(choice => choice.Value == "gpt-image-sunburst").Label);
        Assert.Equal(
            "gpt-image-2.5-flare",
            choices.Single(choice => choice.Value == "gpt-image-flare").Label);
        Assert.DoesNotContain(choices, choice => choice.Label == "gpt-image-2");
    }

    [Fact]
    public void A_workspace_with_no_saved_choice_gets_sunburst()
    {
        var choices = ImageModelCatalog.Choices(Status(openAiKeyStored: true));

        Assert.Equal(
            ImageModelCatalog.DefaultModel,
            ImageModelCatalog.Resolve(choices, requested: null)!.Value);
        Assert.Equal("gpt-image-sunburst", ImageModelCatalog.DefaultModel);
    }

    [Fact]
    public void A_saved_choice_still_wins_over_the_default()
    {
        var choices = ImageModelCatalog.Choices(Status(openAiKeyStored: true));

        Assert.Equal(
            "gpt-image-flare",
            ImageModelCatalog.Resolve(choices, "gpt-image-flare")!.Value);
    }

    /// <summary>The default is a preference, not a trap: with no OpenAI key stored the
    /// resolution falls through to a model the workspace can actually render on.</summary>
    [Fact]
    public void Without_an_openai_key_the_default_falls_back_to_a_ready_model()
    {
        var choices = ImageModelCatalog.Choices(Status(openAiKeyStored: false));

        var resolved = ImageModelCatalog.Resolve(choices, requested: null)!;
        Assert.True(resolved.Ready);
        Assert.NotEqual(ImageModelCatalog.DefaultModel, resolved.Value);
    }
}
