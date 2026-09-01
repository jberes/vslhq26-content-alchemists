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
  /// <summary>Base inset before target-specific crop compensation is applied.</summary>
  public const int SafeMarginPercent = 20;

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
        - Do not render any new text, letters, numbers, captions, headlines, labels, badges
          or logos. Castmill composites exact authored text after generation. If an
          authoritative reference image already contains text, keep the entire referenced
          panel inside the safe area without recreating, enlarging or repositioning its text.
        - Compose for the centre: background, gradients and atmosphere may reach the edges,
          but meaning must not.
        """;

    /// <summary>Appends the house rules to a prompt. Blank prompts are returned unchanged.</summary>
    public static string Apply(string prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? prompt : $"{prompt.TrimEnd()}\n\n{Composition}";

    /// <summary>Adds crop-safe bounds calculated for the exact published slot.</summary>
    public static string Apply(
        string prompt,
        int targetWidth,
        int targetHeight,
        int generatedWidth,
        int generatedHeight)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return prompt;
        }

        var targetAspect = (double)targetWidth / targetHeight;
        var generatedAspect = (double)generatedWidth / generatedHeight;
        var horizontalCrop = targetAspect < generatedAspect
            ? (1 - (targetAspect / generatedAspect)) / 2
            : 0;
        var verticalCrop = targetAspect > generatedAspect
            ? (1 - (generatedAspect / targetAspect)) / 2
            : 0;
        var baseMargin = SafeMarginPercent / 100d;
        var horizontalMargin = Math.Max(baseMargin, horizontalCrop + (0.12 * (1 - (2 * horizontalCrop))));
        var verticalMargin = Math.Max(baseMargin, verticalCrop + (0.12 * (1 - (2 * verticalCrop))));
        var left = (int)Math.Ceiling(generatedWidth * horizontalMargin);
        var right = (int)Math.Floor(generatedWidth * (1 - horizontalMargin));
        var top = (int)Math.Ceiling(generatedHeight * verticalMargin);
        var bottom = (int)Math.Floor(generatedHeight * (1 - verticalMargin));

        return $$"""
            {{Apply(prompt)}}

            EXACT OUTPUT SAFETY (mandatory, final instruction):
            - The published slot is {{targetWidth}}×{{targetHeight}} pixels. It is produced
              from a generated {{generatedWidth}}×{{generatedHeight}} frame by centre-cropping.
            - Keep every essential visual entirely inside x={{left}} through x={{right}} and
              y={{top}} through y={{bottom}} of the generated frame. The complete outer area
              is disposable crop and must contain background only.
            - Do not render any new text. Reserve clean negative space for Castmill's
              deterministic, crop-safe text compositor.
            """;
    }
}
