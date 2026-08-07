// The markdown bridge — the load-bearing half of the editor contract (G2 / ADR-F03):
// markdown string in, markdown string out. No HTML and no editor-native JSON is ever
// persisted, which is what keeps every export path trivial and the editor swappable.
//
// Parsing and serializing both run through a headless TipTap editor so the round trip uses
// exactly the same schema the user types into. A separate "just for tests" parser would
// prove nothing about what the editor does.

import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import Link from '@tiptap/extension-link';
import TaskList from '@tiptap/extension-task-list';
import TaskItem from '@tiptap/extension-task-item';
import { TableKit } from '@tiptap/extension-table';
import { Markdown } from 'tiptap-markdown';

/**
 * The extension set, shared by the live editor and the headless one used for conversion so
 * the two can never disagree about what markdown means.
 *
 * THE ORDERED-LIST TOKENIZER GUARD (Roadmap §2.5). Some markdown dialects treat a line
 * beginning "a." or "Yes." as a lettered list item. CommonMark does not, and neither may we:
 * generated FAQ prose regularly opens with "Yes." or "No.", and silently turning that into a
 * list corrupts the artifact on every save. `markdown-it` — what tiptap-markdown parses with
 * — is CommonMark by default, so the guard is to NOT enable its optional list extensions and
 * to hold that with a regression test rather than a comment. See markdown-roundtrip.test.js.
 */
export function extensions({ placeholder = null } = {}) {
    const set = [
        StarterKit.configure({
            // Provenance and history are our concern, not the editor's: undo depth beyond a
            // reasonable burst just holds memory in a WASM app.
            undoRedo: { depth: 100 },
        }),
        // inline: true is load-bearing, not a preference. As a block node, an image
        // serializes without a trailing blank line, so "![hero](…)" immediately followed by
        // a list came back out as one joined line — and the next round trip then escaped the
        // "-", corrupting the list. As an inline node the image sits inside a paragraph and
        // the paragraph serializer handles block separation, which is also how CommonMark
        // represents a standalone figure. Caught by the corpus; see the "full
        // artifact-shaped document" case in markdown-roundtrip.test.js.
        Image.configure({ inline: true, allowBase64: false }),
        Link.configure({ openOnClick: false, autolink: false }),
        TaskList,
        TaskItem.configure({ nested: true }),
        // GFM tables. Real markdown, so they survive the round trip and render natively on
        // GitHub — unlike a toggle/details block, which has no markdown representation at
        // all and would need html:false reversed to store it.
        TableKit.configure({ table: { resizable: false } }),
        Markdown.configure({
            html: false,            // no HTML passthrough: the contract is markdown only
            tightLists: true,
            bulletListMarker: '-',
            linkify: false,         // do not invent links the author did not write
            breaks: false,          // a single newline is not a <br> in CommonMark
            transformPastedText: true,
            transformCopiedText: true,
        }),
    ];

    if (placeholder) {
        set.push(placeholder);
    }

    return set;
}

let headless = null;

/** A reusable headless editor. Creating one per call is measurably slower on WASM. */
function headlessEditor() {
    if (!headless) {
        headless = new Editor({ extensions: extensions(), content: '' });
    }

    return headless;
}

/**
 * Parses markdown into the editor's document model.
 * @param {string} markdown
 * @returns {object} a ProseMirror document as JSON
 */
export function parse(markdown) {
    assertString(markdown, 'markdown');

    const editor = headlessEditor();
    editor.commands.setContent(markdown, { emitUpdate: false });
    return editor.getJSON();
}

/**
 * Serializes the editor's document model back to markdown.
 * @param {object|string} doc a ProseMirror JSON document, or markdown to normalize
 * @returns {string} markdown
 */
export function serialize(doc) {
    if (doc === null || doc === undefined) {
        throw new TypeError('castmill-editor: doc must be a document or a string, got ' + typeof doc);
    }

    const editor = headlessEditor();
    editor.commands.setContent(doc, { emitUpdate: false });
    return editor.storage.markdown.getMarkdown();
}

/**
 * parse → serialize. The Phase F4 corpus asserts this is byte-stable on the second pass:
 * the FIRST pass may legitimately normalize (setext headings become ATX, `*` bullets become
 * `-`), but a normalized document must then never change again. A round trip that keeps
 * drifting would rewrite the artifact on every save and churn the revision ring.
 * @param {string} markdown
 * @returns {string}
 */
export function roundTrip(markdown) {
    return serialize(parse(markdown));
}

function assertString(value, name) {
    if (typeof value !== 'string') {
        throw new TypeError(`castmill-editor: ${name} must be a string, got ${typeof value}`);
    }
}
