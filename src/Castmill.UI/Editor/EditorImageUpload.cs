namespace Castmill.UI.Editor;

/// <summary>
/// An image pasted or dropped onto the editor surface. The bytes cross the interop boundary
/// base64-encoded and are uploaded by the host, which then calls
/// <c>RichEditor.InsertImageAsync</c> with the resulting URL — the document itself only ever
/// holds a URL, because base64 images are disallowed (they would blow the artifact's 512 KB
/// content cap and bloat every export path).
/// </summary>
/// <param name="FileName">The dropped file's name, used to derive a blob name.</param>
/// <param name="ContentType">MIME type as the browser reported it.</param>
/// <param name="Base64">The file's bytes, base64 without a data: prefix.</param>
public sealed record EditorImageUpload(string FileName, string ContentType, string Base64)
{
    public byte[] ToBytes() => Convert.FromBase64String(Base64);
}
