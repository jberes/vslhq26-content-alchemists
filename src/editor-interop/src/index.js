// castmill-editor — the one JS-interop component that matters (Roadmap §2.5).
//
// The .NET side (RichEditor.razor) sees only this surface:
//   init(element, markdown, options) -> handle
//   handle.setMarkdown(md) / getMarkdown() / focus() / destroy()
// with change, blur and heading events reported back through callbacks. Keeping the surface
// this small is what makes the editor swappable forever (ADR-F03).

import { Editor } from '@tiptap/core';
import Placeholder from '@tiptap/extension-placeholder';
import { extensions, parse, serialize, roundTrip } from './markdown.js';

export { parse, serialize, roundTrip };

/** Block types offered by the slash menu, in the order they appear. */
const SLASH_ITEMS = [
    { label: 'Heading 1', run: c => c.toggleHeading({ level: 1 }) },
    { label: 'Heading 2', run: c => c.toggleHeading({ level: 2 }) },
    { label: 'Heading 3', run: c => c.toggleHeading({ level: 3 }) },
    { label: 'Paragraph', run: c => c.setParagraph() },
    { label: 'Bullet list', run: c => c.toggleBulletList() },
    { label: 'Numbered list', run: c => c.toggleOrderedList() },
    { label: 'Task list', run: c => c.toggleTaskList() },
    { label: 'Quote', run: c => c.toggleBlockquote() },
    { label: 'Code block', run: c => c.toggleCodeBlock() },
    { label: 'Divider', run: c => c.setHorizontalRule() },
    { label: 'Bold', run: c => c.toggleBold() },
    { label: 'Italic', run: c => c.toggleItalic() },
];

/**
 * Mounts an editor into `element`.
 *
 * @param {HTMLElement} element host element, emptied on init
 * @param {string} markdown initial content
 * @param {object} callbacks { onChange, onBlur, onHeadings } — each optional
 * @param {object} options { placeholder, editable }
 */
export function init(element, markdown = '', callbacks = {}, options = {}) {
    if (!(element instanceof HTMLElement)) {
        throw new TypeError('castmill-editor: element must be an HTMLElement');
    }

    const placeholder = options.placeholder
        ? Placeholder.configure({ placeholder: options.placeholder })
        : null;

    const editor = new Editor({
        element,
        extensions: extensions({ placeholder }),
        content: markdown,
        editable: options.editable !== false,
        editorProps: {
            attributes: { class: 'cm-editor__surface', spellcheck: 'true' },
        },
        onUpdate: () => {
            callbacks.onChange?.();
            emitHeadings();
        },
        // Persistence commits on blur, not per keystroke (Frontend-Architecture.md §3.3):
        // at most one keystroke-burst of work is ever at risk.
        onBlur: () => callbacks.onBlur?.(getMarkdown()),
    });

    const slash = mountSlashMenu(editor, element);

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
            editor.destroy();
        },
    };
}

/**
 * The `/` slash menu. Implemented as a small DOM list rather than through TipTap's suggestion
 * plugin + a floating-ui dependency: twelve static items do not need either, and every
 * kilobyte counts against the < 250 KB gzip budget (story 5.1).
 */
function mountSlashMenu(editor, host) {
    const menu = document.createElement('div');
    menu.className = 'cm-editor__slash';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;
    host.appendChild(menu);

    let index = 0;
    let open = false;

    for (const [i, item] of SLASH_ITEMS.entries()) {
        const option = document.createElement('button');
        option.type = 'button';
        option.className = 'cm-editor__slash-item';
        option.textContent = item.label;
        option.setAttribute('role', 'option');
        option.addEventListener('mousedown', event => {
            event.preventDefault();
            choose(i);
        });
        menu.appendChild(option);
    }

    function highlight() {
        for (const [i, child] of [...menu.children].entries()) {
            child.setAttribute('aria-selected', String(i === index));
            child.classList.toggle('cm-editor__slash-item--active', i === index);
        }
    }

    function show() {
        open = true;
        index = 0;
        menu.hidden = false;
        highlight();
    }

    function hide() {
        open = false;
        menu.hidden = true;
    }

    function choose(i) {
        // Remove the "/" the user typed before applying the block change.
        const { from } = editor.state.selection;
        editor.chain().focus().deleteRange({ from: from - 1, to: from }).run();
        SLASH_ITEMS[i].run(editor.chain().focus()).run();
        hide();
    }

    function onKeyDown(event) {
        if (!open) {
            if (event.key === '/') {
                // Only at the start of an empty block, so "and/or" mid-sentence is just text.
                if (editor.state.selection.$from.parent.textContent.length === 0) {
                    setTimeout(show, 0);
                }
            }

            return;
        }

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            index = (index + (event.key === 'ArrowDown' ? 1 : -1) + SLASH_ITEMS.length) % SLASH_ITEMS.length;
            highlight();
        } else if (event.key === 'Enter' || event.key === 'Tab') {
            event.preventDefault();
            choose(index);
        } else if (event.key === 'Escape') {
            event.preventDefault();
            hide();
        } else if (event.key.length === 1) {
            // Typing anything else means it was not a command after all.
            hide();
        }
    }

    host.addEventListener('keydown', onKeyDown, true);

    return {
        destroy() {
            host.removeEventListener('keydown', onKeyDown, true);
            menu.remove();
        },
    };
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
    }, options);
}
