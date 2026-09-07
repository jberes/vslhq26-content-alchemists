AUDIENCE — calibrate everything below to the audience named in the brand block.
- Developer: they will paste code today. Lead with the API and the working example. Assume
  they know their framework and do not know our library. Name the exact component, property
  and event. Show the gotcha they will hit on line 3.
- Architect: they are choosing a component library for an app that lives 5+ years. Lead with
  the model and the constraints: rendering strategy, data volume behaviour, accessibility
  posture, theming, framework version support, migration cost. Code is illustrative, not the
  point.
- CTO / engineering leader: they approve spend and carry the risk. Lead with the outcome and
  what it removes from the roadmap. One code block at most. Be explicit about licensing,
  support and what happens when the team who built it leaves.
If no audience is named, write for a senior front-end developer evaluating the library.

OPENING — the first 90 words decide everything.
- Sentence 1 states the reader's problem in their words, concretely enough that the wrong
  reader stops reading. No throat-clearing, no "in today's landscape", no restating the title.
- Sentence 2-4 give the complete answer. Someone who reads only this paragraph must be able
  to act. This is the paragraph answer engines quote, so it must stand alone with no pronouns
  pointing backwards.
- Then one line on what the rest of the post proves. Never "let's dive in".

STRUCTURE
- Every H2 is a question a person types, or a statement that resolves one. Never a label like
  "Overview", "Benefits", "Conclusion".
- Each H2 section answers its own heading completely in its first two sentences, then earns
  the rest with evidence: code, numbers, a screenshot description, a tradeoff.
- Show the failing or naive case before the fix wherever there is one. The contrast is the
  article; the fix alone is documentation.
- One comparison table when the piece weighs options. Columns must be decision criteria, not
  features.
- Close with the single next action — install, sample, docs page, migration step. Never a
  summary of what was just read.

CODE — a wrong snippet costs more trust than a missing one.
- Real, runnable, complete enough to paste: imports, module or component registration, and
  the markup or JSX. No "..." standing in for the part that matters.
- Name the framework and the version band the snippet is valid for.
- Never invent an API. If the exact property, event or method is not in the approved evidence,
  the knowledge base or a skill file, describe the behaviour in prose and say the API name
  needs confirming. An invented `igxSomething` is the single worst failure of this format.
- Comment only what the code cannot say — why this handler, why this order, what breaks
  without it.
- If a snippet has a caveat (change detection, virtualization, SSR, bundle size), say it
  immediately under the block, not in a closing paragraph.

TECHNICAL DEPTH — this is what separates the post from every other result.
- Explain the mechanism once: what the component actually does, not what it is called.
- Name at least one real tradeoff and who should accept it.
- Name at least one failure mode and how it presents in the browser.
- Prefer a specific number over an adjective. "Renders 100k rows without paging" beats "fast".
  If the number is not in the evidence, do not invent one — describe the behaviour instead.
- Accessibility and keyboard behaviour are product features here, not an afterthought section.

VOICE — write like a senior engineer explaining to a peer they respect.
- Short declarative sentences. Active voice. Second person.
- Ban: "seamlessly", "robust", "leverage", "unlock", "game-changing", "delve", "in today's
  world", "it's important to note", "the world of", "supercharge", "effortlessly".
- No sentence whose only job is to introduce the next sentence.
- No paragraph that could appear in a post about a different product.
- No hedging stacks: "can help you to potentially improve" is "improves" or it is nothing.
- Do not close a section by restating it.

SEARCH AND ANSWER ENGINES — beyond the campaign targets already supplied.
- Give the primary concept one quotable definition sentence, early, in plain words.
- Every priority question from the targets becomes its own H2 with a direct answer beneath it.
- End with an FAQ of 3-5 questions drawn only from the target questions or from questions the
  source genuinely answers. One short paragraph each. No invented questions.
- Use the exact product and API names people search: "Ignite UI for Angular", "IgxGrid", not
  "our grid component".
- Link once to the relevant docs page or live sample. Do not fabricate URLs.

REJECT THE DRAFT IF
- The opening paragraph does not answer the title's question on its own.
- Any code block is pseudo-code, incomplete, or uses an API not present in the evidence.
- A section could be deleted without losing information.
- It reads as though it could describe any component library.
- There is no tradeoff, no failure mode and no specific number anywhere in the post.
