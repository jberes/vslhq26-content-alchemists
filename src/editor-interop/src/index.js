// castmill-editor — the one JS-interop component that matters (Roadmap §2.5).
//
// The .NET side (RichEditor.razor) sees only this surface:
//   init(element, markdown, options) -> handle
//   handle.setMarkdown(md) / getMarkdown() / focus() / destroy()
// with change, blur and heading events reported back through callbacks. Keeping the surface
// this small is what makes the editor swappable forever (ADR-F03).

import { Editor, Extension } from '@tiptap/core';
import Placeholder from '@tiptap/extension-placeholder';
import { extensions, parse, serialize, roundTrip } from './markdown.js';
import { mountSlashMenu, filterItems, keepActiveOptionVisible, SLASH_GROUPS } from './slash.js';
import { mountGutter, blockMoveShortcuts } from './gutter.js';
import { bubbleExtension } from './bubble.js';

export { parse, serialize, roundTrip, filterItems, keepActiveOptionVisible, SLASH_GROUPS };

/**
 * Mounts an editor into `element`.
 *
 * @param {HTMLElement} element host element, emptied on init
 * @param {string} markdown initial content
 * @param {object} callbacks { onChange, onBlur, onHeadings, onRequestMedia } — each optional
 * @param {object} options { placeholder, editable }
 */
export function init(element, markdown = '', callbacks = {}, options = {}) {
    if (!(element instanceof HTMLElement)) {
        throw new TypeError('castmill-editor: element must be an HTMLElement');
    }

    const placeholder = options.placeholder
        ? Placeholder.configure({ placeholder: options.placeholder })
        : null;

    let editor = null;
    const bubble = bubbleExtension(() => editor);

    // Keyboard reorder. A drag-only affordance is unusable without a mouse, and Alt+↑/↓ is
    // faster than dragging even with one.
    const moveShortcuts = Extension.create({
        name: 'castmillBlockMove',
        addKeyboardShortcuts() {
            return blockMoveShortcuts(this.editor);
        },
    });

    editor = new Editor({
        element,
        extensions: [
            ...extensions({ placeholder }),
            bubble.extension,
            moveShortcuts,
        ],
        content: markdown,
        editable: options.editable !== false,
        editorProps: {
            attributes: { class: 'cm-editor__surface', spellcheck: 'true' },
            handlePaste: (_view, event) => handleMediaDrop(event.clipboardData, callbacks),
            handleDrop: (_view, event) => handleMediaDrop(event.dataTransfer, callbacks),
        },
        onUpdate: () => {
            callbacks.onChange?.();
            emitHeadings();
        },
        onSelectionUpdate: () => bubble.refresh(),
        // Persistence commits on blur, not per keystroke (Frontend-Architecture.md §3.3):
        // at most one keystroke-burst of work is ever at risk.
        onBlur: () => callbacks.onBlur?.(getMarkdown()),
    });

    element.appendChild(bubble.element);

    const slash = mountSlashMenu(editor, element, {
        onRequest: kind => callbacks.onRequestMedia?.(kind),
    });
    const gutter = mountGutter(editor, element, { onInsert: () => slash.openHere() });

    function getMarkdown() {
        return editor.storage.markdown.getMarkdown();
    }

    /**
     * The outline rail is a plain Blazor sibling fed by these events — it is not part of the
     * editor, so it stays .NET and testable (Roadmap §2.5).
     */
    function emitHeadings() {
        if (!callbacks.onHeadings) {
            return;
        }

        const headings = [];
        editor.state.doc.descendants((node, pos) => {
            if (node.type.name === 'heading') {
                headings.push({ level: node.attrs.level, text: node.textContent, pos });
            }
        });

        callbacks.onHeadings(headings);
    }

    emitHeadings();

    return {
        setMarkdown(md) {
            // emitUpdate false: a programmatic replacement (a regenerate, a revision
            // restore) is not a user edit and must not mark the document dirty.
            editor.commands.setContent(md, { emitUpdate: false });
            emitHeadings();
        },
        getMarkdown,
        focus() {
            editor.commands.focus();
        },
        /** Moves the caret to a heading — the outline rail's click target. */
        goTo(pos) {
            editor.commands.focus(pos);
        },
        insertImage(src, alt) {
            editor.chain().focus().setImage({ src, alt }).run();
        },
        /**
         * YouTube is inserted as a thumbnail image wrapped in a link, so it survives as
         * plain markdown rather than an embed the exporters would have to special-case.
         */
        insertYouTube(videoId, title) {
            const thumb = `https://i.ytimg.com/vi/${videoId}/hqdefault.jpg`;
            const url = `https://www.youtube.com/watch?v=${videoId}`;
            editor.chain().focus()
                .setImage({ src: thumb, alt: title || 'YouTube video' })
                .extendMarkRange('link').setLink({ href: url })
                .run();
        },
        isEmpty() {
            return editor.isEmpty;
        },
        destroy() {
            slash.destroy();
            gutter.destroy();
            bubble.element.remove();
            editor.destroy();
        },
    };
}

/**
 * Pasting or dropping an image file. The bytes cannot go into the document — base64 images
 * are disallowed (they would blow both the artifact's size cap and every export), so the
 * file is handed to .NET, uploaded, and comes back as a URL through insertImage.
 *
 * Returns true only when a file was claimed, so ordinary text paste is untouched.
 */
function handleMediaDrop(transfer, callbacks) {
    const file = [...(transfer?.files ?? [])].find(f => f.type.startsWith('image/'));
    if (!file || !callbacks.onImageFile) {
        return false;
    }

    const reader = new FileReader();
    reader.onload = () => {
        const base64 = String(reader.result).split(',')[1] ?? '';
        callbacks.onImageFile(file.name, file.type, base64);
    };
    reader.readAsDataURL(file);
    return true;
}

/**
 * Blazor-facing entry point. .NET cannot hand JS a function, so it passes a
 * DotNetObjectReference and this adapts it to the callback shape `init` expects. Keeping the
 * adapter here rather than in the component means the interop contract lives in one file.
 *
 * @param {HTMLElement} element
 * @param {string} markdown
 * @param {{invokeMethodAsync: (name: string, ...args: unknown[]) => Promise<unknown>}} dotnet
 * @param {object} options
 */
export function initFor(element, markdown, dotnet, options = {}) {
    return init(element, markdown, {
        onChange: () => dotnet.invokeMethodAsync('NotifyChangedAsync'),
        onBlur: md => dotnet.invokeMethodAsync('NotifyBlurAsync', md),
        onHeadings: headings => dotnet.invokeMethodAsync('NotifyHeadingsAsync', headings),
        onRequestMedia: kind => dotnet.invokeMethodAsync('RequestMediaAsync', kind),
        onImageFile: (name, type, base64) => dotnet.invokeMethodAsync('UploadImageAsync', name, type, base64),
    }, options);
}
