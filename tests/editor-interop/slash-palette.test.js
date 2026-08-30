// The `/` palette's matching. The original menu was a fixed list of twelve that DISMISSED
// itself on any character key — type `/i` and it vanished — which is the opposite of what
// the affordance is for. These pin the behaviour people actually expect from it.

import { describe, expect, it } from 'vitest';
import { filterItems, keepActiveOptionVisible, SLASH_GROUPS } from 'castmill-editor-interop';

const labels = query => filterItems(query).map(item => item.label);

describe('the slash palette', () => {
    it('offers everything before anything is typed', () => {
        const all = SLASH_GROUPS.flatMap(group => group.items);
        expect(filterItems('')).toHaveLength(all.length);
    });

    it('narrows as you type instead of closing', () => {
        const matches = labels('head');
        expect(matches).toContain('Heading 1');
        expect(matches).toContain('Heading 2');
        expect(matches).not.toContain('Bullet list');
    });

    it('matches aliases nobody would guess from the label', () => {
        expect(labels('img')).toContain('Image');
        expect(labels('todo')).toContain('Task list');
        expect(labels('hr')).toContain('Divider');
        expect(labels('ul')).toContain('Bullet list');
        expect(labels('ol')).toContain('Numbered list');
    });

    it('ranks a prefix match above a mere substring', () => {
        // Both contain "code"; the one that STARTS with it should be reachable first.
        const matches = labels('code');
        expect(matches[0]).toBe('Code block');
        expect(matches).toContain('Inline code');
    });

    it('is case-insensitive and ignores surrounding space', () => {
        expect(labels('  QUOTE ')).toContain('Quote');
    });

    it('returns nothing for a query that matches nothing, rather than everything', () => {
        expect(filterItems('zzzz')).toHaveLength(0);
    });

    it('routes media through a request rather than a command', () => {
        // Image and YouTube need a dialog, which lives on the .NET side — the editor only
        // ever gets the finished src back through insertImage/insertYouTube.
        const image = filterItems('image')[0];
        expect(image.request).toBe('image');
        expect(image.run).toBeUndefined();
    });

    it('offers no callout entries, because they do not round-trip', () => {
        expect(labels('')).not.toContain('Note');
        expect(labels('callout')).toHaveLength(0);
    });

    it('every non-media entry is runnable and every entry is labelled', () => {
        for (const item of filterItems('')) {
            expect(item.label).toBeTruthy();
            expect(item.icon).toBeTruthy();
            if (!item.request) {
                expect(typeof item.run).toBe('function');
            }
        }
    });

    it('keeps keyboard selection inside the scroll viewport', () => {
        let options;
        keepActiveOptionVisible({
            scrollIntoView(value) {
                options = value;
            },
        });

        expect(options).toEqual({ block: 'nearest' });
    });
});
