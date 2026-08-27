// Clipboard access differs between a normal browser and the MAUI embedded WebView. Prefer the
// asynchronous API, then fall back to the long-supported selection command. The fallback must
// remain synchronous with the click's user activation, which is why it lives entirely in JS.

export async function copyText(value) {
    const text = String(value ?? '');

    if (globalThis.navigator?.clipboard?.writeText) {
        try {
            await globalThis.navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Permission policy, an insecure WebView origin, or an OS clipboard restriction can
            // reject this even though the API exists. Continue to the selection fallback.
        }
    }

    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.setAttribute('readonly', '');
    textarea.setAttribute('aria-hidden', 'true');
    textarea.style.position = 'fixed';
    textarea.style.inset = '0 auto auto -10000px';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);

    try {
        textarea.focus({ preventScroll: true });
        textarea.select();
        textarea.setSelectionRange(0, textarea.value.length);
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        textarea.remove();
    }
}

export async function copyFormatted(textValue, htmlValue) {
    const text = String(textValue ?? '');
    const html = String(htmlValue ?? '');

    if (globalThis.navigator?.clipboard?.write && globalThis.ClipboardItem) {
        try {
            const item = new ClipboardItem({
                'text/plain': new Blob([text], { type: 'text/plain' }),
                'text/html': new Blob([html], { type: 'text/html' }),
            });
            await globalThis.navigator.clipboard.write([item]);
            return true;
        } catch {
            // Mac Catalyst's embedded WebView may expose ClipboardItem but reject write().
            // Continue to the synchronous selection path while the click activation is live.
        }
    }

    const container = document.createElement('div');
    container.innerHTML = html;
    container.contentEditable = 'true';
    container.setAttribute('aria-hidden', 'true');
    container.style.position = 'fixed';
    container.style.inset = '0 auto auto -10000px';
    container.style.inlineSize = '1px';
    container.style.blockSize = '1px';
    container.style.overflow = 'hidden';
    document.body.appendChild(container);

    const selection = globalThis.getSelection?.();
    const previousRanges = selection
        ? Array.from({ length: selection.rangeCount }, (_, index) => selection.getRangeAt(index).cloneRange())
        : [];
    const active = document.activeElement;

    try {
        const range = document.createRange();
        range.selectNodeContents(container);
        selection?.removeAllRanges();
        selection?.addRange(range);
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        selection?.removeAllRanges();
        previousRanges.forEach(range => selection?.addRange(range));
        if (active instanceof HTMLElement) {
            active.focus({ preventScroll: true });
        }
        container.remove();
    }
}
