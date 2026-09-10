using Castmill.Core.Ai;

namespace Castmill.UI.Design;

/// <summary>One image generator a workspace can select, derived from the same readiness
/// projection in Settings and Image Studio so their labels and capabilities cannot drift.</summary>
public sealed record ImageModelChoice(
    string Value, string Label, string? Detail, bool Ready, string? Reason,
    bool SupportsReferences);

public static class ImageModelCatalog
{
    /// <summary>
    /// The generator a workspace gets when it has never chosen one (ADR-078). Only a
    /// preference: <see cref="Resolve"/> falls through to any ready choice when no OpenAI
    /// key is stored, so the default cannot leave a workspace with nothing to render on.
    /// </summary>
    public const string DefaultModel = "gpt-image-sunburst";

    public static IReadOnlyList<ImageModelChoice> Choices(AiStatusResponse? status)
    {
        if (status is null)
        {
            return [];
        }

        var choices = new List<ImageModelChoice>();
        var foundry = status.ImageProviders.FirstOrDefault(provider =>
            string.Equals(provider.Name, "foundry", StringComparison.OrdinalIgnoreCase));

        foreach (var (alias, deployment) in status.Models
            .Where(model => model.Key.StartsWith("image", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model.Value))
            .OrderBy(model => model.Key, StringComparer.Ordinal))
        {
            choices.Add(new ImageModelChoice(
                alias, DeploymentLabel(deployment), $"Foundry · {alias}",
                foundry?.Ready == true, foundry?.Reason,
                foundry?.SupportsReferenceImages == true));
        }

        // A fresh workspace can have a ready Foundry provider before an alias table is shown.
        if (choices.Count == 0 && foundry is not null)
        {
            choices.Add(new ImageModelChoice(
                "foundry", "foundry", null, foundry.Ready, foundry.Reason,
                foundry.SupportsReferenceImages));
        }

        foreach (var provider in status.ImageProviders.Where(provider =>
            !string.Equals(provider.Name, "foundry", StringComparison.OrdinalIgnoreCase)))
        {
            // Label with the MODEL the provider asks for, so "own key" says which model that is.
            choices.Add(new ImageModelChoice(
                provider.Name,
                string.IsNullOrWhiteSpace(provider.Model) ? provider.Name : provider.Model,
                $"{ProviderDetail(provider.Name)} · {provider.Name}",
                provider.Ready, provider.Reason, provider.SupportsReferenceImages));
        }

        return choices;
    }

    public static ImageModelChoice? Resolve(
        IReadOnlyList<ImageModelChoice> choices, string? requested) =>
        choices.FirstOrDefault(choice => string.Equals(
            choice.Value, requested, StringComparison.OrdinalIgnoreCase))
        ?? choices.FirstOrDefault(choice => choice.Ready && string.Equals(
            choice.Value, DefaultModel, StringComparison.OrdinalIgnoreCase))
        ?? choices.FirstOrDefault(choice => choice.Ready)
        ?? (choices.Count > 0 ? choices[0] : null);

    private static string DeploymentLabel(string configured) =>
        configured.IndexOf(':', StringComparison.Ordinal) is var separator && separator > 0
            ? configured[(separator + 1)..]
            : configured;

    private static string? ProviderDetail(string name) => name switch
    {
        "nano-banana" => "Google Gemini · own key",
        "gpt-image-sunburst" => "OpenAI direct · own key · default",
        "gpt-image-flare" => "OpenAI direct · own key",
        _ => "own key",
    };
}
