// The block gutter: a drag handle and a "+" that opens the same palette `/` does.
//
// Hand-rolled rather than @tiptap/extension-drag-handle, for the same reason the slash menu
// is hand-rolled. The official extension is MIT and works, but it imports @tiptap/y-tiptap
// for collaborative handles, which drags in yjs + lib0 + y-protocols — measured at ~104 KB
// of raw bundle for machinery a single-user editor never executes, against a hard 250 KB
// gzip gate. Native HTML5 drag plus ProseMirror's own `view.dragging` does the whole job in
// this file, with no dependency at all.

import { NodeSelection } from '@tiptap/pm/state';

/**
 * Mounts the gutter into `host` and keeps it beside whichever block the pointer is over.
 *
 * @param {import('@tiptap/core').Editor} editor
 * @param {HTMLElement} host
 * @param {{ onInsert?: () => void }} hooks
 */
export function mountGutter(editor, host, hooks = {}) {
    const gutter = document.createElement('div');
    gutter.className = 'cm-editor__gutter';
    gutter.hidden = true;

    const insert = document.createElement('button');
    insert.type = 'button';
    insert.className = 'cm-editor__gutter-button cm-editor__gutter-add';
    insert.setAttribute('aria-label', 'Insert a block');
    insert.title = 'Insert a block';
    insert.textContent = '+';
    insert.addEventListener('mousedown', event => {
        event.preventDefault();
        hooks.onInsert?.();
    });

    const grip = document.createElement('button');
    grip.type = 'button';
    grip.className = 'cm-editor__gutter-button cm-editor__gutter-grip';
    grip.draggable = true;
    grip.title = 'Drag to move this block';
    grip.setAttribute('aria-label', 'Move this block');
    grip.textContent = '⠿';

    gutter.append(insert, grip);
    host.appendChild(gutter);

    /** Document position of the block the gutter is currently pointing at. */
    let blockPos = null;

    /** Resolves the pointer to the TOP-LEVEL block containing it. */
    function blockAt(clientX, clientY) {
        const found = editor.view.posAtCoords({ left: clientX, top: clientY });
        if (!found) {
            return null;
        }
        const $pos = editor.state.doc.resolve(found.inside >= 0 ? found.inside : found.pos);
        // depth 1 is a direct child of the doc; deeper positions (list items, table cells)
        // resolve up to the block that actually moves as a unit.
        return $pos.depth === 0 ? null : $pos.before(1);
    }

    function showFor(pos) {
        blockPos = pos;
        const dom = editor.view.nodeDOM(pos);
        const element = dom instanceof HTMLElement ? dom : dom?.parentElement;
        if (!element) {
            hide();
            return;
        }

        const box = element.getBoundingClientRect();
        const base = host.getBoundingClientRect();
        gutter.hidden = false;
        gutter.style.insetBlockStart = `${box.top - base.top}px`;
        gutter.style.insetInlineStart = `${box.left - base.left}px`;
    }

    function hide() {
        gutter.hidden = true;
        blockPos = null;
    }

    function onMouseMove(event) {
        if (!editor.isEditable || gutter.contains(event.target)) {
            return;
        }
        const pos = blockAt(event.clientX, event.clientY);
        if (pos === null) {
            hide();
        } else if (pos !== blockPos) {
            showFor(pos);
        }
    }

    function onMouseLeave(event) {
        // Leaving toward the gutter itself is not leaving.
        if (!gutter.contains(event.relatedTarget)) {
            hide();
        }
    }

    /**
     * Hands the block to ProseMirror as a drag. Setting `view.dragging` is what makes the
     * editor treat the drop as a document move — without it the browser would paste the
     * dragged HTML and leave the original behind.
     */
    function onDragStart(event) {
        if (blockPos === null) {
            return;
        }

        const selection = NodeSelection.create(editor.state.doc, blockPos);
        editor.view.dispatch(editor.state.tr.setSelection(selection));

        const slice = editor.state.selection.content();
        editor.view.dragging = { slice, move: true };

        event.dataTransfer.effectAllowed = 'move';
        // Some browsers refuse to start a drag with an empty payload.
        event.dataTransfer.setData('text/plain', '');

        const dom = editor.view.nodeDOM(blockPos);
        if (dom instanceof HTMLElement) {
            event.dataTransfer.setDragImage(dom, 0, 0);
        }
    }

    function onDragEnd() {
        editor.view.dragging = null;
        hide();
    }

    grip.addEventListener('dragstart', onDragStart);
    grip.addEventListener('dragend', onDragEnd);
    host.addEventListener('mousemove', onMouseMove);
    host.addEventListener('mouseleave', onMouseLeave);

    return {
        destroy() {
            grip.removeEventListener('dragstart', onDragStart);
            grip.removeEventListener('dragend', onDragEnd);
            host.removeEventListener('mousemove', onMouseMove);
            host.removeEventListener('mouseleave', onMouseLeave);
            gutter.remove();
        },
    };
}

/**
 * Keyboard equivalent of dragging. A reorder that only works with a mouse is not an
 * accessible reorder, and Alt+↑/↓ is faster than dragging even with a mouse.
 */
export function blockMoveShortcuts(editor) {
    return {
        'Alt-ArrowUp': () => moveBlock(editor, -1),
        'Alt-ArrowDown': () => moveBlock(editor, 1),
    };
}

export function moveBlock(editor, direction) {
    return editor.commands.command(({ state, dispatch, tr }) => {
        const { $from } = state.selection;
        if ($from.depth === 0) {
            return false;
        }

        const parent = $from.node(0);
        const index = $from.index(0);
        const target = index + direction;
        if (target < 0 || target >= parent.childCount) {
            return false;
        }

        const node = parent.child(index);
        const neighbour = parent.child(target);
        const start = $from.before(1);

        const from = direction < 0 ? start - neighbour.nodeSize : start;
        const to = direction < 0 ? start + node.nodeSize : start + node.nodeSize + neighbour.nodeSize;
        const reordered = direction < 0 ? [node, neighbour] : [neighbour, node];

        if (dispatch) {
            tr.replaceWith(from, to, reordered);
            dispatch(tr);
        }
        return true;
    });
}
