// Visible feedback for work the producer started (ADR-F74). .NET reports every API request's
// begin/end (CastmillHttpHandler → UserActivity); this module decides which control, if any,
// it belongs to: the one activated in the last second, or the one whose previous request just
// finished (a save followed by a refresh is one action). That control is marked busy — styled
// by [data-cm-busy] and announced with aria-busy — and ignores clicks until its requests
// settle. :root[data-cm-activity] drives the slim bar across the app. Background polling is
// never tied to an activation, so it never lights anything up.
//
// Hand-written and hand-maintained, like castmill-shortcuts.js.

const ACTIVATION_WINDOW_MS = 1000;
const CONTROL = 'button, [role="button"], [role="tab"], [role="menuitem"], a[href], summary, input[type="submit"], input[type="button"]';

let installed = false;
let last = null;            // { el, at } — the most recent activation; el null when no control remains
const owners = new Map();   // requestId → control element, or null for app-bar-only work

function control(target) {
    return target instanceof Element ? target.closest(CONTROL) : null;
}

function onClick(e) {
    const el = control(e.target);
    if (el) last = { el, at: performance.now() };
}

// Enter in a text field submits too (rename, add keyword): no control to mark, but the work
// is still the producer's, so it lights the app bar.
function onKey(e) {
    if (e.key === 'Enter' || e.key === ' ') last = { el: control(e.target), at: performance.now() };
}

export function install() {
    if (installed) return;
    installed = true;
    document.addEventListener('click', onClick, true);
    document.addEventListener('keydown', onKey, true);
}

function refresh() {
    if (owners.size > 0) document.documentElement.dataset.cmActivity = 'true';
    else delete document.documentElement.dataset.cmActivity;
}

export function begin(id) {
    if (!last) return;
    const busy = last.el !== null && [...owners.values()].includes(last.el);
    if (!busy && performance.now() - last.at > ACTIVATION_WINDOW_MS) return;
    // A confirm dialog's accept button is gone by the time its work starts: the request still
    // belongs to the producer, so it shows on the app bar even with no control to mark.
    const el = last.el?.isConnected ? last.el : null;
    owners.set(id, el);
    if (el) {
        el.setAttribute('data-cm-busy', 'true');
        el.setAttribute('aria-busy', 'true');
    } else {
        last.at = performance.now();
    }
    refresh();
}

export function end(id) {
    const el = owners.get(id);
    owners.delete(id);
    if (!el && last && last.el === null) last.at = performance.now();
    if (el && ![...owners.values()].includes(el)) {
        el.removeAttribute('data-cm-busy');
        el.removeAttribute('aria-busy');
        // A follow-up request (save → refresh) starting right after still belongs here.
        if (last?.el === el) last.at = performance.now();
    }
    refresh();
}
