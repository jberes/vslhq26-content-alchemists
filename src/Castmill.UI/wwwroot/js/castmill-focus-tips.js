// Focus outline tooltip island (ADR-F73). ONE tooltip for the whole tree, owned here and
// attached to <body> so no scrolling panel can clip it: it shows the full title for the row
// under the pointer (or a row reached by keyboard) and nothing else. Per-row tooltip
// components each kept their own open state and, on the desktop WebView, several stayed open
// at once; a single element makes that impossible.
//
// Rows declare their content with data-tip-kicker / data-tip-title; .NET decides the words.

const SHOW_DELAY_MS = 350;
const GAP_PX = 10;
const EDGE_PX = 8;

// Delegated from the document, not bound to the outline element: Focus renders its outline only
// after the campaign loads and re-creates it on navigation, and a listener on a replaced node
// silently stops working. One instance per page, shared by whoever attaches.
let shared = null;
let users = 0;

export function attach() {
    users++;
    shared ??= create();
    return {
        hide: () => shared?.hide(),
        dispose() {
            users = Math.max(0, users - 1);
            if (users === 0 && shared) {
                shared.dispose();
                shared = null;
            }
        },
    };
}

function create() {
    const root = document;
    const tip = document.createElement('div');
    tip.className = 'cm-focus__tip';
    tip.setAttribute('role', 'tooltip');
    tip.setAttribute('aria-hidden', 'true');
    const kicker = document.createElement('span');
    kicker.className = 'cm-focus__tip-kicker';
    const title = document.createElement('span');
    title.className = 'cm-focus__tip-title';
    tip.append(kicker, title);
    document.body.appendChild(tip);

    let current = null;
    let timer = 0;

    const rowOf = target => target instanceof Element ? target.closest('[data-tip-title]') : null;

    function place(row) {
        const r = row.getBoundingClientRect();
        const t = tip.getBoundingClientRect();
        let left = r.right + GAP_PX;
        let side = 'right';
        if (left + t.width > window.innerWidth - EDGE_PX) {
            left = Math.max(EDGE_PX, r.left - GAP_PX - t.width);
            side = 'left';
        }
        const top = Math.min(Math.max(EDGE_PX, r.top), window.innerHeight - EDGE_PX - t.height);
        tip.style.left = `${Math.round(left)}px`;
        tip.style.top = `${Math.round(top)}px`;
        tip.dataset.side = side;
        // The arrow points at the row's first line even when the tip was pushed down or up.
        tip.style.setProperty('--cm-focus-tip-arrow', `${Math.max(12, Math.min(t.height - 12, r.top + 18 - top))}px`);
    }

    function show(row) {
        current = row;
        kicker.textContent = row.dataset.tipKicker ?? '';
        title.textContent = row.dataset.tipTitle ?? '';
        tip.dataset.open = 'true';
        place(row);
    }

    function hide() {
        clearTimeout(timer);
        timer = 0;
        current = null;
        delete tip.dataset.open;
    }

    function request(row) {
        clearTimeout(timer);
        if (tip.dataset.open) {
            // Already showing: moving between rows swaps the content at once.
            show(row);
            return;
        }
        timer = setTimeout(() => show(row), SHOW_DELAY_MS);
    }

    function onPointerOver(e) {
        const row = rowOf(e.target);
        if (!row) {
            if (current || tip.dataset.open) hide();
            return;
        }
        if (row !== current || !tip.dataset.open) {
            current = row;
            request(row);
        }
    }

    function onPointerOut(e) {
        const row = rowOf(e.target);
        if (row && !row.contains(e.relatedTarget)) {
            hide();
        }
    }

    function onFocusIn(e) {
        const row = rowOf(e.target);
        // Keyboard only: a click also focuses the row, and a tooltip after a click is noise.
        if (row && row.matches(':focus-visible')) {
            show(row);
        }
    }

    const onFocusOut = () => hide();
    const onKey = e => { if (e.key === 'Escape') hide(); };

    root.addEventListener('pointerover', onPointerOver);
    root.addEventListener('pointerout', onPointerOut);
    document.documentElement.addEventListener('pointerleave', hide);
    root.addEventListener('pointerdown', hide, true);
    root.addEventListener('focusin', onFocusIn);
    root.addEventListener('focusout', onFocusOut);
    document.addEventListener('scroll', hide, true);
    document.addEventListener('keydown', onKey, true);
    window.addEventListener('blur', hide);
    window.addEventListener('resize', hide);

    return {
        hide,
        dispose() {
            hide();
            root.removeEventListener('pointerover', onPointerOver);
            root.removeEventListener('pointerout', onPointerOut);
            document.documentElement.removeEventListener('pointerleave', hide);
            root.removeEventListener('pointerdown', hide, true);
            root.removeEventListener('focusin', onFocusIn);
            root.removeEventListener('focusout', onFocusOut);
            document.removeEventListener('scroll', hide, true);
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('blur', hide);
            window.removeEventListener('resize', hide);
            tip.remove();
        },
    };
}
