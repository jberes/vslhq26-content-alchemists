namespace Castmill.Api.Services.Ai;

/// <summary>
/// House composition rules appended to EVERY image prompt, at the single choke point in
/// <see cref="ImageRenderer"/> — a call site cannot forget them and a user prompt cannot
/// omit them.
///
/// Why they exist: image deployments emit a fixed size set (1024×1024, 1536×1024,
/// 1024×1536), never a slot's exact aspect, so <c>ImageComposer.CentreCrop</c> always
/// scales-to-cover and crops the centre. A 1280×720 slot loses ~8% off the top and bottom
/// of the model's 1536×1024 frame; a 1600×840 header loses ~11%. Anything the model paints
/// near an edge — a headline especially — is therefore cut off. The safe margin below is
/// set above the worst case so text survives the crop intact.
/// </summary>
public static class ImagePromptRules
{
    /// <summary>Worst-case centre-crop loss per edge is ~11%; 15% leaves real headroom.</summary>
    public const int SafeMarginPercent = 15;

    private const int SafeCentrePercent = 100 - (2 * SafeMarginPercent);

    public static readonly string Composition = $"""
        COMPOSITION REQUIREMENTS (mandatory, override any conflicting instruction above):
        - This image is centre-cropped to its final aspect ratio after generation. Anything
          within {SafeMarginPercent}% of any edge WILL be cut off.
        - Keep ALL text, logos, faces, product UI and other critical content inside the
          central {SafeCentrePercent}% of the frame. Leave at least {SafeMarginPercent}% of the width and the
          height completely clear on every edge — top, bottom, left and right.
        - Never let a letter, word, or subject touch, overlap, or run past any edge.
        - Every word rendered must be complete and fully legible: no clipped glyphs, no
          truncated headlines, no text running out of frame, no text split across an edge.
        - Compose for the centre: background, gradients and atmosphere may reach the edges,
          but meaning must not.
        """;

    /// <summary>Appends the house rules to a prompt. Blank prompts are returned unchanged.</summary>
    public static string Apply(string prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? prompt : $"{prompt.TrimEnd()}\n\n{Composition}";
}
