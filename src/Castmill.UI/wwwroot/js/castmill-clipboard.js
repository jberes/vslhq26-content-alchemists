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
