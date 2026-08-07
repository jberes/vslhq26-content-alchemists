// THE G2 GATE (roadmap story 5.3). The editor's whole contract is "markdown in, markdown
// out", and every export path, the revision ring and the provenance markers depend on that
// round trip being stable. This corpus is what holds it — it runs in CI and grows with every
// bug found, per the risk table in Roadmap-Blazor.md Part 5.
//
// The contract asserted here is DOUBLE round-trip stability, not identity:
//   - the first pass may normalize (setext headings → ATX, `*` bullets → `-`)
//   - after that the document must never change again
// An editor that keeps rewriting its own output would churn the artifact on every blur and
// fill the bounded revision ring with noise.

// @vitest-environment jsdom

import { describe, expect, it } from 'vitest';
import { parse, roundTrip, serialize } from 'castmill-editor-interop/markdown';

/** Asserts a document is a fixed point of the round trip. */
function expectStable(markdown) {
    const once = roundTrip(markdown);
    const twice = roundTrip(once);
    expect(twice).toBe(once);
    return once;
}

describe('markdown contract', () => {
    it('rejects non-string input rather than coercing it', () => {
        expect(() => parse(42)).toThrow(TypeError);
        expect(() => serialize(null)).toThrow(TypeError);
    });

    it('preserves the empty document', () => {
        expect(roundTrip('')).toBe('');
    });
});

describe('the ordered-list tokenizer guard', () => {
    // The bug this whole guard exists for (Roadmap §2.5). Generated FAQ answers routinely
    // open with "Yes." or "No."; a dialect that reads that as a lettered list item would
    // silently restructure the artifact on the first save.
    it('keeps FAQ prose starting with "Yes." as a paragraph', () => {
        const prose = 'Yes. Both shells render the same page.\n';
        const out = expectStable(prose);
        expect(out.trim()).toBe('Yes. Both shells render the same page.');
        expect(parse(prose).content[0].type).toBe('paragraph');
    });

    it('keeps a lettered enumeration as prose, not a list', () => {
        const prose = 'a. This is not a list item.\n';
        expect(parse(prose).content[0].type).toBe('paragraph');
        expectStable(prose);
    });

    it('still parses a real CommonMark ordered list', () => {
        const list = '1. First\n2. Second\n3. Third\n';
        expect(parse(list).content[0].type).toBe('orderedList');
        expectStable(list);
    });

    it('does not turn a paragraph that merely contains "1." into a list', () => {
        const prose = 'We cut it by 1. That is the whole story.\n';
        expect(parse(prose).content[0].type).toBe('paragraph');
        expectStable(prose);
    });
});

describe('round-trip corpus', () => {
    it('headings', () => {
        const out = expectStable('# Title\n\n## Section\n\n### Detail\n');
        expect(out).toContain('# Title');
        expect(out).toContain('## Section');
    });

    it('numbered steps with prose between them', () => {
        expectStable([
            '## How it works',
            '',
            '1. Drop the source file in.',
            '2. Wait for the transcript.',
            '3. Press Run.',
            '',
            'That is the whole flow.',
            '',
        ].join('\n'));
    });

    it('nested bullet lists', () => {
        expectStable('- One\n  - Nested\n  - Also nested\n- Two\n');
    });

    it('task lists', () => {
        const out = expectStable('- [ ] Unchecked\n- [x] Checked\n');
        expect(out).toContain('[ ]');
        expect(out).toContain('[x]');
    });

    it('emphasis, strong, strike and inline code', () => {
        const out = expectStable('Some *italic*, **bold**, ~~struck~~ and `code`.\n');
        expect(out).toContain('**bold**');
        expect(out).toContain('`code`');
    });

    it('links', () => {
        const out = expectStable('See [the handoff](https://example.com/handoff).\n');
        expect(out).toContain('[the handoff](https://example.com/handoff)');
    });

    it('images, including the ones the image pipeline writes', () => {
        const out = expectStable('![Blog header](https://cdn.example.com/hero.webp)\n');
        expect(out).toContain('![Blog header](https://cdn.example.com/hero.webp)');
    });

    it('blockquotes', () => {
        expectStable('> The mill is standing.\n');
    });

    it('fenced code blocks keep their language', () => {
        const out = expectStable('```csharp\nvar x = 1;\n```\n');
        expect(out).toContain('csharp');
        expect(out).toContain('var x = 1;');
    });

    it('a full artifact-shaped document', () => {
        // Close to what the blog generator actually emits: headings, prose, a list, a figure,
        // a quote and a FAQ section — the combination is where bugs have historically hidden.
        const document = [
            '# Cutting deployment time in half',
            '',
            'The team shipped it in six weeks. Here is what changed.',
            '',
            '## What we built',
            '',
            '![Blog header](https://cdn.example.com/hero.webp)',
            '',
            '- A single pipeline definition',
            '- One rollback path',
            '',
            '## FAQ',
            '',
            '**Does it work on Windows?**',
            '',
            'Yes. Both agents run the same steps.',
            '',
            '**Is it faster?**',
            '',
            'No. It is more predictable, which matters more.',
            '',
            '> Predictable beats fast.',
            '',
        ].join('\n');

        const out = expectStable(document);
        expect(out).toContain('Yes. Both agents run the same steps.');
        expect(out).toContain('No. It is more predictable');
    });

    it('image-stub markers survive, because the image pipeline replaces them in place', () => {
        // The stubs are real markdown so they round-trip like any other content
        // (Roadmap §3.3.7). If they did not survive, placing an image would corrupt the blog.
        const withStub = '## Section\n\n![stub:blog-hero]()\n\nBody copy follows.\n';
        const out = expectStable(withStub);
        expect(out).toContain('stub:blog-hero');
    });
});

describe('normalization is idempotent', () => {
    // These inputs SHOULD change on the first pass. What matters is that they then settle.
    it.each([
        ['setext heading', 'Title\n=====\n'],
        ['asterisk bullets', '* One\n* Two\n'],
        ['loose spacing', '#    Title\n\n\n\nBody.\n'],
        ['underscore emphasis', 'Some _italic_ text.\n'],
    ])('%s settles after one pass', (_name, input) => {
        const once = roundTrip(input);
        expect(roundTrip(once)).toBe(once);
    });
});

describe('tables (GFM)', () => {
    // Tables are the one Notion-ish block worth adding that IS real markdown: GitHub renders
    // them natively, which matters because the same artifact gets pushed to a repo.
    const table = [
        '| Metric | Before | After |',
        '| --- | --- | --- |',
        '| Deploy time | 40 min | 21 min |',
        '| Rollbacks | 3/wk | 0/wk |',
    ].join('\n');

    it('survives the round trip with its cells intact', () => {
        const once = expectStable(table);
        expect(once).toContain('Deploy time');
        expect(once).toContain('21 min');
        expect(once).toContain('Rollbacks');
    });

    it('does not swallow the prose around it', () => {
        const document = `## Results\n\n${table}\n\nThe rollback number is the one that mattered.\n`;
        const once = expectStable(document);
        expect(once).toContain('## Results');
        expect(once).toContain('The rollback number is the one that mattered.');
    });

    // KNOWN LIMITATION, pinned so a serializer change is noticed rather than stumbled on.
    // A pipe escaped inside a cell is unescaped on the first pass and then breaks the row on
    // the second, so this one input is not a fixed point. GFM requires the escape and the
    // table extension exports escapeTableCellPipes, so this is a gap between the two rather
    // than a decision. Rare — generated copy does not put pipes in table cells — but it is
    // corruption if it happens, so it is written down rather than left to be discovered.
    it('does NOT yet keep an escaped pipe inside a cell stable', () => {
        const escaped = ['| Flag | Meaning |', '| --- | --- |', '| `a \\| b` | either |'].join('\n');
        const once = roundTrip(escaped);
        expect(roundTrip(once)).not.toBe(once);
    });
});

describe('callouts are not round-trip safe, which is why the palette has none', () => {
    // GitHub alert syntax looked like the ideal callout: a plain blockquote here, a real
    // callout once the artifact is pushed to a repo, no custom node and no HTML passthrough.
    // These two tests are why it is not offered — they pin the serializer behaviour that
    // makes it unsafe, so anyone re-adding callouts has to fix this first and will see these
    // tests go red when they do.
    it('escapes the alert marker, so it stops being an alert after one save', () => {
        const once = roundTrip('> [!WARNING]\n> Rolling this back needs a schema migration.\n');
        expect(once).toContain('\\[!WARNING\\]');
        expect(once).not.toContain('> [!WARNING]');
    });

    it('drops the hard break, so the marker and the body collapse onto one line', () => {
        const once = roundTrip('> [!NOTE]\n> First.\n');
        expect(once.trim().split('\n')).toHaveLength(1);
    });
});
