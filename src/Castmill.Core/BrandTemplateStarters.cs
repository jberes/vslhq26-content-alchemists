namespace Castmill.Core;

/// <summary>
/// The content brief a brand starts with, per kind. One copy, shared: the brand editor offers
/// it as "Reset to starter" and the API seeds it when a brand is created (ADR-069), so a brand
/// is never generating against Castmill's generic guidance alone.
/// </summary>
public static class BrandTemplateStarters
{
    public static readonly IReadOnlyDictionary<string, string> ByKind = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["youtube"] = """
            Act as an expert YouTube content strategist specializing in SEO, answer-engine
            optimization, generative-search visibility and accurate technical content.

            Priority order:
            1. Accurately represent what is actually shown or discussed in the transcript.
            2. Satisfy the search intent behind the approved primary keyword.
            3. Make the title and first 1–2 description sentences compelling to a human.
            4. Make the package easy for search engines and answer engines to understand.
            5. Cover related entities, capabilities, problems and terminology only when the
               transcript supports them.
            6. Never use keyword stuffing, unsupported claims, generic hype or clickbait.

            Before writing, determine the video's subject, problem, audience, learning outcome,
            demonstrated products/features, supported claims and questions it answers. Silently
            correct obvious transcription errors, but do not invent facts. Build a semantic topic
            cluster around the approved primary keyword: close variants, category terms,
            problem/feature/implementation searches, named technologies and natural questions.

            Produce three genuinely different A/B/C title strategies using distinct supported
            angles. Keep titles accurate and preferably 45–65 characters, front-load the important
            concept where natural, and avoid minor rewrites of one title.

            The first 200 description characters must explain exactly what the viewer will learn,
            naturally use the primary phrase or strongest semantic variation, and name a concrete
            capability or outcome. Follow with 2–4 short paragraphs covering the problem, major
            capabilities, learning outcome, technologies and differentiators actually shown.
            Where useful, add an "In this video, you'll learn" list with 4–8 concise bullets.

            Write explicit, self-contained answer sentences that can be quoted by an AI system.
            Prefer named entities and concrete nouns over vague pronouns. Answer what the subject
            is, what it does, how it works and what problem it solves only when grounded in the
            transcript.

            Generate chapters only from reliable transcript timing. Start at 00:00, use concise
            search-friendly titles, and never invent timestamps. End with one relevant CTA; use
            the supplied product URL naturally when available. Generate 12–20 directly relevant
            tags mixing the primary phrase, close variants, product/platform names, demonstrated
            features and high-intent long-tail concepts.

            The result must sound natural aloud, preserve important technical detail, avoid
            repetitive AI phrasing and prioritize useful information over keyword density.
            """,
        ["blog"] = """
            Act as the lead technical writer for this brand. The piece must read as though a
            practitioner wrote it from real work, not as though a model summarised a source.

            AUDIENCE
            Write to the one reader named in the brand block. If several personas are supplied,
            and none is selected, write to the most technical of them. Never average them.

            OPENING — the first 90 words decide everything
            - Sentence 1 states the reader's problem or task concretely, in their words.
              No throat-clearing, no restating the title, no "in today's landscape".
            - The next 2-4 sentences give the complete answer. Someone who reads only this
              paragraph must be able to act. This is what answer engines quote, so it must
              stand alone with no pronouns pointing backwards.
            - Name the environment and scope once. Do not reintroduce it a paragraph later.

            STRUCTURE
            - 1600-2400 words unless the campaign says otherwise. Long enough to answer
              completely, never padded to reach a number.
            - Every H2 is a question a reader types, or a statement that resolves one. Never
              "Overview", "Benefits", "Conclusion".
            - Each section answers its own heading in its first two sentences, then earns the
              rest with evidence: an example, a number, a tradeoff, a failure mode.
            - Show the naive or failing case before the fix wherever there is one. The contrast
              is the article; the fix alone is documentation.
            - One comparison table where the piece weighs options, with decision criteria as
              the columns rather than feature names.
            - Close with one concrete next action. Never a summary of what was just read.

            EVIDENCE AND ACCURACY
            - Every technical claim traces to the approved evidence. Where the evidence is
              silent, describe the behaviour and say what still needs confirming.
            - Never invent an API name, version, option, URL or benchmark. A fabricated symbol
              costs more trust than a missing example.
            - Prefer a specific number to an adjective, but only a number the evidence supports.
            - Distinguish what the user does, what the product does, and what the source
              actually established. Do not turn an observation into a required step.

            CODE, WHERE THE PIECE CALLS FOR IT
            - Real and runnable: imports and setup included, no "..." where the point should be.
            - Name the framework and version band the snippet is valid for.
            - State a caveat immediately under the block it applies to, not in the conclusion.

            VOICE
            - Short declarative sentences, active voice, second person.
            - Ban: "seamlessly", "robust", "leverage", "unlock", "game-changing", "delve",
              "in today's world", "it's important to note", "supercharge", "effortlessly".
            - No sentence whose only job is to introduce the next one.
            - No paragraph that could appear in an article about a different product.
            - No hedging stacks: "can help to potentially improve" is "improves", or it is cut.

            SEARCH AND ANSWER ENGINES
            - Give the central concept one quotable definition sentence, early, in plain words.
            - Each priority question from the campaign targets becomes its own section with a
              direct answer beneath it.
            - Close with a short FAQ only where those questions are not already answered in the
              body. Never invent questions to fill it.
            - Use the exact product and feature names people search for.

            REJECT YOUR OWN DRAFT IF
            - The opening paragraph does not answer the title on its own.
            - Any section could be deleted without losing information.
            - It would read the same for a competitor's product.
            - There is no tradeoff, no failure mode and no specific detail anywhere in it.
            """,
        ["newsletter"] = "One idea per issue. Lead with it in the first line.\nWrite to one person, not a list.\n250–400 words.\nOne link that matters; do not pad with a round-up.\nSign off in the brand's voice, not a template.",
        ["email-sequence"] = "Each email earns the next open — end on an unresolved thread.\nOne job per email: teach, prove, or ask.\n120–200 words each. Subject lines under 45 characters.\nNo email repeats another's argument.",
        ["landing-page"] = "Headline states the outcome, not the feature.\nSubhead names who it is for.\nThree proof blocks: what it does, what it replaces, what it costs.\nOne call to action, repeated — never two competing ones.",
        ["show-notes"] = "Two-sentence summary, then timestamped sections.\nPull three quotable lines verbatim.\nList every tool, person and link mentioned.",
        ["social-x"] = "First line is the whole argument; assume the rest is never read.\nNo hashtags. No emoji unless the brand already uses them.\nUnder 280 characters. One idea.",
        ["social-linkedin"] = "Open with a specific claim, not a question.\nShort lines, generous line breaks.\n120–200 words. End with a real question.\nNo hustle-culture register, no 'thrilled to announce'.",
        ["social-facebook"] = "Conversational, complete sentences.\n80–150 words. Lead with the human angle.\nOne link, placed last.",
        ["social-instagram"] = "Write for a caption read after the image.\nFirst line hooks; the rest can be longer.\n5–10 relevant hashtags on their own line.",
        ["social-threads"] = "Casual and direct. Under 300 characters.\nOne observation, stated plainly. No hashtags.",
        ["social-bluesky"] = "Plain, technical, no marketing register.\nUnder 300 characters. Links are fine without ceremony.",
    };

    public static string? For(string kind) => ByKind.GetValueOrDefault(kind);

    /// <summary>
    /// Seeded on brand creation. Blog is the kind whose default most changes the output, and
    /// the one a producer is least likely to write from scratch before their first run.
    /// </summary>
    public static readonly string[] SeededKinds = ["blog", "youtube"];
}
