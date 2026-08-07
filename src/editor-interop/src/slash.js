// The `/` command palette.
//
// Still a hand-built DOM list rather than TipTap's suggestion plugin: the list is static
// data and every kilobyte counts against the < 250 KB gzip budget (story 5.1). What it is
// NOT any more is a fixed menu that dismisses itself the moment you type — the original
// closed on any character key, which is the opposite of what people expect from Notion,
// where `/img` narrows to the image command.

/**
 * Palette entries in display order. `aliases` are matched as well as the label, so `/todo`
 * finds Task list and `/hr` finds Divider without either word appearing on screen.
 */
export const SLASH_GROUPS = [
    {
        name: 'Basic',
        items: [
            { label: 'Paragraph', icon: '¶', aliases: ['text', 'body', 'p'], run: c => c.setParagraph() },
            { label: 'Heading 1', icon: 'H1', aliases: ['title', 'h1'], run: c => c.toggleHeading({ level: 1 }) },
            { label: 'Heading 2', icon: 'H2', aliases: ['subtitle', 'h2'], run: c => c.toggleHeading({ level: 2 }) },
            { label: 'Heading 3', icon: 'H3', aliases: ['h3'], run: c => c.toggleHeading({ level: 3 }) },
            { label: 'Bullet list', icon: '•', aliases: ['ul', 'unordered', 'list'], run: c => c.toggleBulletList() },
            { label: 'Numbered list', icon: '1.', aliases: ['ol', 'ordered', 'steps'], run: c => c.toggleOrderedList() },
            { label: 'Task list', icon: '☑', aliases: ['todo', 'checkbox', 'check'], run: c => c.toggleTaskList() },
        ],
    },
    {
        name: 'Blocks',
        items: [
            { label: 'Quote', icon: '❝', aliases: ['blockquote', 'citation'], run: c => c.toggleBlockquote() },
            { label: 'Code block', icon: '</>', aliases: ['pre', 'snippet'], run: c => c.toggleCodeBlock() },
            { label: 'Divider', icon: '—', aliases: ['hr', 'rule', 'separator'], run: c => c.setHorizontalRule() },
            { label: 'Table', icon: '▦', aliases: ['grid', 'rows', 'columns'], run: c => c.insertTable({ rows: 3, cols: 3, withHeaderRow: true }) },
        ],
    },
    // NO CALLOUTS. GitHub alert syntax (`> [!NOTE]`) looked like the ideal answer — a plain
    // blockquote here, a real callout once published to a repo, no custom node and no HTML.
    // The corpus says otherwise: the serializer escapes the bracket to `> \[!NOTE\]` and
    // drops the hard break, so the marker stops being an alert on the first save. Adding
    // them back means fixing the serializer first, not adding a palette entry — see
    // "callouts are not round-trip safe" in markdown-roundtrip.test.js.
    {
        name: 'Media',
        items: [
            { label: 'Image', icon: '▣', aliases: ['img', 'picture', 'photo'], request: 'image' },
            { label: 'YouTube', icon: '▶', aliases: ['video', 'yt', 'embed'], request: 'youtube' },
        ],
    },
    {
        name: 'Formatting',
        items: [
            { label: 'Bold', icon: 'B', aliases: ['strong'], run: c => c.toggleBold() },
            { label: 'Italic', icon: 'I', aliases: ['em', 'emphasis'], run: c => c.toggleItalic() },
            { label: 'Strikethrough', icon: 'S', aliases: ['strike', 'del'], run: c => c.toggleStrike() },
            { label: 'Inline code', icon: '`', aliases: ['code', 'mono'], run: c => c.toggleCode() },
        ],
    },
];

/** Flattened once — the palette is static, so this never needs recomputing. */
const ALL_ITEMS = SLASH_GROUPS.flatMap(group => group.items.map(item => ({ ...item, group: group.name })));

/**
 * Ranks items against a query. An empty query keeps everything in declaration order; a
 * prefix match outranks a word-start match, which outranks a bare substring, so `/co` puts
 * "Code block" above "Inline code".
 */
export function filterItems(query) {
    const q = query.trim().toLowerCase();
    if (q === '') {
        return ALL_ITEMS;
    }

    const scored = [];
    for (const item of ALL_ITEMS) {
        const label = item.label.toLowerCase();
        const names = [label, ...(item.aliases ?? [])];
        let best = -1;
        for (const name of names) {
            if (name.startsWith(q)) {
                best = Math.max(best, 3);
            } else if (name.split(' ').some(word => word.startsWith(q))) {
                best = Math.max(best, 2);
            } else if (name.includes(q)) {
                best = Math.max(best, 1);
            }
        }
        if (best > 0) {
            scored.push({ item, score: best });
        }
    }

    // Stable within a score band: declaration order is the deliberate one.
    return scored.sort((a, b) => b.score - a.score).map(entry => entry.item);
}

/**
 * Mounts the palette into `host`.
 *
 * @param {import('@tiptap/core').Editor} editor
 * @param {HTMLElement} host
 * @param {{ onRequest?: (kind: string) => void }} hooks the Blazor side owns the media dialogs
 */
export function mountSlashMenu(editor, host, hooks = {}) {
    const menu = document.createElement('div');
    menu.className = 'cm-editor__slash';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;
    host.appendChild(menu);

    let index = 0;
    let open = false;
    let query = '';
    let matches = ALL_ITEMS;

    function render() {
        menu.replaceChildren();

        if (matches.length === 0) {
            const empty = document.createElement('p');
            empty.className = 'cm-editor__slash-empty';
            empty.textContent = `No block matches “${query}”`;
            menu.appendChild(empty);
            return;
        }

        let lastGroup = null;
        matches.forEach((item, i) => {
            // Group headings are suppressed while filtering: with three results left,
            // three headings above them is noise, not structure.
            if (query === '' && item.group !== lastGroup) {
                lastGroup = item.group;
                const heading = document.createElement('p');
                heading.className = 'cm-editor__slash-group';
                heading.textContent = item.group;
                menu.appendChild(heading);
            }

            const option = document.createElement('button');
            option.type = 'button';
            option.className = 'cm-editor__slash-item';
            option.setAttribute('role', 'option');
            option.setAttribute('aria-selected', String(i === index));
            if (i === index) {
                option.classList.add('cm-editor__slash-item--active');
            }

            const icon = document.createElement('span');
            icon.className = 'cm-editor__slash-icon';
            icon.setAttribute('aria-hidden', 'true');
            icon.textContent = item.icon ?? '';

            const label = document.createElement('span');
            label.className = 'cm-editor__slash-label';
            label.textContent = item.label;

            option.append(icon, label);
            option.addEventListener('mousedown', event => {
                event.preventDefault();
                choose(i);
            });
            menu.appendChild(option);
        });
    }

    function show() {
        open = true;
        index = 0;
        query = '';
        matches = ALL_ITEMS;
        menu.hidden = false;
        position();
        render();
    }

    /** Places the menu at the caret — a palette pinned to a corner reads as a bug. */
    function position() {
        try {
            const caret = editor.view.coordsAtPos(editor.state.selection.from);
            const base = host.getBoundingClientRect();
            menu.style.insetBlockStart = `${caret.bottom - base.top + 6}px`;
            menu.style.insetInlineStart = `${Math.max(0, caret.left - base.left)}px`;
        } catch {
            // Selection not measurable (empty doc edge cases): keep the default spot.
        }
    }

    function hide() {
        open = false;
        menu.hidden = true;
        query = '';
    }

    function choose(i) {
        const item = matches[i];
        if (!item) {
            return;
        }

        // Remove the whole "/query" the user typed, not just the slash.
        const { from } = editor.state.selection;
        const start = from - (query.length + 1);
        editor.chain().focus().deleteRange({ from: Math.max(0, start), to: from }).run();
        hide();

        if (item.request) {
            // Media needs a dialog, which lives on the .NET side; the editor only ever
            // receives the finished src/id back through insertImage/insertYouTube.
            hooks.onRequest?.(item.request);
            return;
        }

        item.run(editor.chain().focus()).run();
    }

    /**
     * Re-reads the query from the document after a keystroke has been applied. Tracking it
     * by hand would drift the moment anything else changed the block (paste, undo).
     */
    function syncQuery() {
        const text = editor.state.selection.$from.parent.textContent;
        const slash = text.lastIndexOf('/');
        if (slash < 0) {
            hide();
            return;
        }

        query = text.slice(slash + 1);
        matches = filterItems(query);
        index = 0;
        position();
        render();
    }

    function onKeyDown(event) {
        if (!open) {
            if (event.key === '/' && editor.state.selection.$from.parent.textContent.length === 0) {
                // Only at the start of an empty block, so "and/or" mid-sentence is just text.
                setTimeout(show, 0);
            }
            return;
        }

        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (matches.length > 0) {
                index = (index + (event.key === 'ArrowDown' ? 1 : -1) + matches.length) % matches.length;
                render();
            }
        } else if (event.key === 'Enter' || event.key === 'Tab') {
            event.preventDefault();
            choose(index);
        } else if (event.key === 'Escape') {
            event.preventDefault();
            hide();
        } else if (event.key === ' ' && matches.length === 0) {
            // A space with nothing matching means it was prose after all.
            hide();
        } else {
            // Everything else — letters, Backspace — re-filters once the edit has landed.
            setTimeout(syncQuery, 0);
        }
    }

    host.addEventListener('keydown', onKeyDown, true);

    // Click-away and focus loss both close the menu; key handling alone left it stranded
    // whenever the user clicked back into the text.
    const onDocPointerDown = event => {
        if (open && !menu.contains(event.target)) {
            hide();
        }
    };
    document.addEventListener('mousedown', onDocPointerDown, true);

    return {
        get isOpen() {
            return open;
        },
        /**
         * Opens the palette from the gutter's "+" rather than from a typed slash. The block
         * must be empty for the same reason typing `/` requires it: choosing a block type
         * replaces the current block, and doing that to a paragraph with text in it would
         * silently eat the text.
         */
        openHere() {
            editor.commands.focus();
            if (editor.state.selection.$from.parent.textContent.length > 0) {
                editor.chain().focus().createParagraphNear().run();
            }
            show();
        },
        destroy() {
            host.removeEventListener('keydown', onKeyDown, true);
            document.removeEventListener('mousedown', onDocPointerDown, true);
            menu.remove();
        },
    };
}
