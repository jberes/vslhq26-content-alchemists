using Castmill.Core;

namespace Castmill.Api.Tests;

/// <summary>
/// The answer-engine rules belong to indexed surfaces only (ADR-070).
///
/// Applied to every reader-facing kind, they reached social posts: a 280-character X post was
/// being told to open with a self-contained quotable answer paragraph, use question headings
/// and close with a 3-5 question FAQ. The posts came back over their character limits and the
/// validator rejected them — reported as "Some posts failed: X post, Instagram post, Bluesky
/// post", the three tightest limits in the set.
/// </summary>
public sealed class AeoScopeTests
{
    [Theory]
    [InlineData("blog")]
    [InlineData("youtube")]
    [InlineData("show-notes")]
    [InlineData("landing-page")]
    public void Indexed_surfaces_get_the_answer_engine_rules(string kind) =>
        Assert.True(ArtifactKinds.IsSearchOptimized(kind));

    [Theory]
    [InlineData("social-x")]
    [InlineData("social-bluesky")]
    [InlineData("social-instagram")]
    [InlineData("social-linkedin")]
    [InlineData("social-threads")]
    [InlineData("social-facebook")]
    [InlineData("email-sequence")]
    [InlineData("newsletter")]
    [InlineData("clip-suggestions")]
    [InlineData("transcript")]
    public void Everything_else_does_not(string kind) =>
        Assert.False(ArtifactKinds.IsSearchOptimized(kind));

    [Fact]
    public void Every_indexed_kind_is_still_a_user_content_kind()
    {
        // The set is a narrowing of user content, not a parallel list that can drift from it.
        Assert.All(ArtifactKinds.SearchOptimized, kind => Assert.True(ArtifactKinds.IsUserContent(kind)));
    }
}
