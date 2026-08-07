using Castmill.UI.Pages.Campaign;

namespace Castmill.UI.Tests;

/// <summary>
/// YouTube insertion is stored as a thumbnail image wrapped in a link, so the artifact stays
/// plain markdown and every export path keeps working. That only holds if the id is right —
/// a wrong one renders a permanently broken thumbnail into the document, so this refuses to
/// guess rather than producing something that looks fine until it is published.
/// </summary>
public sealed class EditorMediaTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://www.youtube.com/watch?t=42s&v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("  dQw4w9WgXcQ  ")]
    public void Every_url_form_people_paste_yields_the_video_id(string input)
    {
        Assert.Equal("dQw4w9WgXcQ", FocusView.YouTubeId(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://vimeo.com/12345678")]
    [InlineData("https://www.youtube.com/watch?v=tooshort")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://example.test/dQw4w9WgXcQ")]
    public void Anything_else_is_refused_rather_than_guessed(string input)
    {
        Assert.Null(FocusView.YouTubeId(input));
    }
}
