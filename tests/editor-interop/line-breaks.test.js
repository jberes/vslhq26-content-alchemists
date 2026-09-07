// Chapters, one per line — and byte-identical on save (ADR-F67).
//
// A YouTube description stores each chapter on its own line, which is exactly what YouTube
// needs. CommonMark treats a single newline as a soft break, so the editor rendered them as
// one run-on paragraph. The first fix appended two trailing spaces (a Markdown hard break),
// which DID render — and would have serialized back as a backslash-newline, putting literal
// "\" characters into the published description on the first save. This corpus pins both
// halves of the real contract: a newline renders as a line break, and comes back out as a
// newline and nothing else.

// @vitest-environment jsdom

import { describe, expect, it } from 'vitest';
import { parse, roundTrip } from 'castmill-editor-interop/markdown';

const chapters = '## Chapters\n00:00 React data grid accessibility and ARIA\n00:34 VoiceOver and React grid keyboard navigation\n01:06 Screen reader header and cell value\n\nExplore the guidance below.';

function paragraphs(doc) {
    return doc.content.filter((node) => node.type === 'paragraph');
}

describe('single newlines inside a paragraph', () => {
    it('become hard breaks in the document, so each chapter renders on its own line', () => {
        const doc = parse(chapters);
        const [chapterParagraph] = paragraphs(doc);
        const breaks = chapterParagraph.content.filter((node) => node.type === 'hardBreak');
        expect(breaks).toHaveLength(2);
    });

    it('serialize back to plain newlines — no backslash, no trailing spaces', () => {
        const out = roundTrip(chapters);
        // The first pass may normalize block spacing (a blank line after the heading is the
        // corpus contract); the chapter lines themselves must come back byte for byte.
        expect(out).toContain(
            '00:00 React data grid accessibility and ARIA\n00:34 VoiceOver and React grid keyboard navigation\n01:06 Screen reader header and cell value',
        );
        expect(out).not.toContain('\\');
        expect(out).not.toMatch(/ {2}\n/);
    });

    it('stay stable across a second round trip', () => {
        const once = roundTrip(chapters);
        expect(roundTrip(once)).toBe(once);
    });

    it('do not turn a blank-line paragraph break into a hard break', () => {
        const doc = parse('First paragraph.\n\nSecond paragraph.');
        expect(paragraphs(doc)).toHaveLength(2);
        expect(roundTrip('First paragraph.\n\nSecond paragraph.')).toBe('First paragraph.\n\nSecond paragraph.');
    });

    it('legacy two-space hard breaks are read, and come back out as plain newlines', () => {
        // Descriptions saved while the two-space fix was live must not gain backslashes.
        expect(roundTrip('00:00 A  \n00:34 B')).toBe('00:00 A\n00:34 B');
    });
});
