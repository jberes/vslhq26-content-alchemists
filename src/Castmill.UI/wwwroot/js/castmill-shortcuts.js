// Global keyboard shortcuts island (G5's keyboard-first goal). Document-level key events
// cannot be heard from Blazor markup, so this is one of the sanctioned interop islands
// (G7) — it reports chords and does nothing else. What a chord *means* stays in .NET.
//
// Hand-written and hand-maintained, like castmill-ui-state.js.

/**
 * @param {{invokeMethodAsync: (name: string, chord: string) => Promise<unknown>}} dotnet
 * @returns {{dispose: () => void}}
 */
export function listen(dotnet) {
    const onKeyDown = event => {
        const meta = event.metaKey || event.ctrlKey; // ⌘ on macOS, Ctrl elsewhere

        let chord = null;
        if (meta && !event.shiftKey && event.key.toLowerCase() === 'k') {
            chord = 'omnibox';
        } else if (meta && !event.shiftKey && event.key.toLowerCase() === 'g') {
            chord = 'generate';
        } else if (meta && event.shiftKey && event.key.toLowerCase() === 'i') {
            chord = 'image-studio';
        } else if (event.key === 'Escape') {
            chord = 'escape';
        }

        if (chord) {
            // Escape is only claimed when an overlay is open — .NET decides — so it is
            // reported without preventDefault. The ⌘-chords are always ours.
            if (chord !== 'escape') {
                event.preventDefault();
            }

            dotnet.invokeMethodAsync('NotifyChordAsync', chord);
        }
    };

    document.addEventListener('keydown', onKeyDown);

    return {
        dispose() {
            document.removeEventListener('keydown', onKeyDown);
        },
    };
}

/** Focuses the omnibox input once it exists in the DOM. */
export function focus(element) {
    element?.focus();
}
