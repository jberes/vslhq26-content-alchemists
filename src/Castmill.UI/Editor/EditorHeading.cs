namespace Castmill.UI.Editor;

/// <summary>
/// One heading in the document, as reported by the editor bundle. Feeds the outline rail,
/// which is a plain Blazor sibling rather than part of the editor (Roadmap §2.5) — so the
/// outline stays testable in .NET and the bundle stays small.
/// </summary>
/// <param name="Level">1–6.</param>
/// <param name="Text">The heading's plain text.</param>
/// <param name="Pos">Document position, for moving the caret there.</param>
public sealed record EditorHeading(int Level, string Text, int Pos);
