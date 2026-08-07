using Castmill.Api.Services.Scout;
using Castmill.Core.Ai;

namespace Castmill.Api.Tests;

/// <summary>
/// The Scout's contract with the model. What matters here is the shape it must produce and
/// what happens when it doesn't: a suggestion claiming something is already covered has to
/// carry the URL that proves it, and an unparseable answer must degrade to "no suggestions"
/// rather than taking the request down.
/// </summary>
public sealed class ContentScoutParsingTests
{
    private static IReadOnlyList<ScoutSuggestion> Parse(string text) => ContentScout.Parse(text);

    [Fact]
    public void A_covered_verdict_carries_the_url_that_proves_it()
    {
        var suggestions = Parse("""
            {"suggestions":[
              {"kind":"blog","title":"Reveal 2.0 connectors","angle":"a",
               "coverage":"covered","rationale":"Published in June",
               "evidence":[{"title":"Reveal 2.0","url":"https://www.revealbi.io/blog/reveal-2-0-release"}]}
            ]}
            """);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("covered", suggestion.Coverage);
        // "Skip it, we published this in June, here is the link" is a MORE useful answer
        // than "write it" — but only if the link is really there.
        Assert.Equal("https://www.revealbi.io/blog/reveal-2-0-release", Assert.Single(suggestion.Evidence).Url);
    }

    [Fact]
    public void A_fenced_response_still_parses()
    {
        var suggestions = Parse("""
            ```json
            {"suggestions":[{"kind":"blog","title":"Something new","coverage":"new"}]}
            ```
            """);

        Assert.Equal("Something new", Assert.Single(suggestions).Title);
    }

    /// <summary>
    /// A model that answers in prose must not take the request down with it — the Scout is
    /// advisory, and an empty list is a survivable outcome where a 500 is not.
    /// </summary>
    [Fact]
    public void Unparseable_output_yields_no_suggestions_rather_than_throwing()
    {
        Assert.Empty(Parse("I think you should write about widgets."));
        Assert.Empty(Parse(string.Empty));
    }

    [Fact]
    public void A_response_with_no_suggestions_array_is_simply_empty()
    {
        Assert.Empty(Parse("""{"note":"nothing worth adding"}"""));
    }

    /// <summary>All three verdicts are legitimate outcomes, including the one that says stop.</summary>
    [Fact]
    public void Every_coverage_verdict_survives_the_round_trip()
    {
        var suggestions = Parse("""
            {"suggestions":[
              {"kind":"blog","title":"A","coverage":"new"},
              {"kind":"blog","title":"B","coverage":"refresh"},
              {"kind":"blog","title":"C","coverage":"covered"}
            ]}
            """);

        Assert.Equal(["new", "refresh", "covered"], suggestions.Select(s => s.Coverage));
    }
}
