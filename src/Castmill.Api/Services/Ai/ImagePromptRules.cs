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

    /// <summary>Slot kinds whose whole job is a headline read at thumbnail size (ADR-075).</summary>
    public static bool IsTextFirst(string? kind) =>
        kind is "youtube-thumbnail" or "social-card";

    /// <summary>
    /// A text-first slot with no composited headline configured, on a model that spells
    /// reliably, may render the exact words the brief quotes (ADR-075). Everything else keeps
    /// the compositor path: the model paints no text and Castmill places the authored words.
    /// </summary>
    public static bool AllowsRenderedText(string? kind, string? headlineText, bool modelRendersText) =>
        IsTextFirst(kind) && string.IsNullOrWhiteSpace(headlineText) && modelRendersText;

    // Raw string literals strip their common indentation, so the bullets below are matched
    // exactly as they appear in the emitted text: at column 0 with two-space continuations.
    private const string NoTextBulletLong =
        "- Do not render any new text, letters, numbers, captions, headlines, labels, badges\n"
        + "  or logos. Castmill composites exact authored text after generation. If an\n"
        + "  authoritative reference image already contains text, keep the entire referenced\n"
        + "  panel inside the safe area without recreating, enlarging or repositioning its text.";

    private const string ExactTextBulletLong =
        "- Render ONLY the text the brief puts in quotation marks, spelled exactly, in a bold\n"
        + "  highly legible face, fully inside the safe area. No other words, labels, badges,\n"
        + "  watermarks or logos. If a reference image already contains text, keep that panel\n"
        + "  inside the safe area without recreating or repositioning its text.";

    private const string NoTextBulletShort =
        "- Do not render any new text. Reserve clean negative space for Castmill's\n"
        + "  deterministic, crop-safe text compositor.";

    private const string ExactTextBulletShort =
        "- Render ONLY the text the brief quotes, spelled exactly and fully legible; no\n"
        + "  other words. Keep it well inside the safe area.";

    /// <summary>
    /// Swaps the no-text rules for the exact-text rules when the slot may carry rendered words.
    /// Throws if the rules text has drifted so neither bullet is found: silently leaving the
    /// no-text rule in place would make every text-first render contradict its own brief.
    /// </summary>
    public static string WithRenderedTextAllowed(string rulesText)
    {
        if (!rulesText.Contains(NoTextBulletLong, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The composition rules no longer contain the no-text bullet this swap expects.");
        }
        return rulesText
            .Replace(NoTextBulletLong, ExactTextBulletLong, StringComparison.Ordinal)
            .Replace(NoTextBulletShort, ExactTextBulletShort, StringComparison.Ordinal);
    }

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
