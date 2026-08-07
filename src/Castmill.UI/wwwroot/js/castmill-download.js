// Saving a file the API returned.
//
// The export endpoints are authenticated, so a plain <a href> cannot fetch them — the Bearer
// token lives in the client, not in a cookie. The bytes therefore come back through the
// normal ApiClient and are handed here as base64 to be turned into a download.

export function save(fileName, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));

    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();

    // Revoked on the next tick rather than immediately: Safari cancels an in-flight download
    // if the object URL disappears in the same turn as the click.
    setTimeout(() => URL.revokeObjectURL(url), 0);
}
