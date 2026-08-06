// One of the four sanctioned JS-interop islands (G7): per-device UI state and the theme
// attribute swap. Everything else in the client is .NET.
//
// Hand-written and hand-maintained — this file is NOT esbuild output, unlike
// castmill-editor.js. Keep it small enough to read in one sitting.

const PREFIX = 'castmill:';

export function get(key) {
    try {
        return window.localStorage.getItem(PREFIX + key);
    } catch {
        // Private browsing and locked-down WebViews can throw on access rather than
        // returning null. A missing preference is not an error worth surfacing.
        return null;
    }
}

export function set(key, value) {
    try {
        window.localStorage.setItem(PREFIX + key, value);
    } catch {
        /* see get() */
    }
}

export function prefersDark() {
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
}

export function applyTheme(family, mode, density) {
    const root = document.documentElement;
    root.setAttribute('data-cm-family', family);
    root.setAttribute('data-cm-mode', mode);
    root.setAttribute('data-cm-density', density);
}

export function applyRail(state) {
    const root = document.documentElement;
    if (state === 'icons' || state === 'labels') {
        root.setAttribute('data-cm-rail', state);
    } else {
        root.removeAttribute('data-cm-rail');
    }
}
