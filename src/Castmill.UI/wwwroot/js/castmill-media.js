// Mill Floor player island (ADR-057): the media element talks to .NET through this
// module — seek on transcript click, time updates for the current-segment highlight.
// Measurement and playback live here; every decision about what to show stays in .NET.

/**
 * @param {HTMLMediaElement} media
 * @param {any} dotnet DotNetObjectReference with OnTime(seconds) and OnDuration(seconds)
 */
export function attach(media, dotnet) {
    if (!(media instanceof HTMLMediaElement)) {
        return null;
    }
    let last = -1;
    const onTime = () => {
        const t = Math.floor(media.currentTime * 2) / 2; // 500 ms resolution
        if (t !== last) {
            last = t;
            dotnet.invokeMethodAsync('OnTime', media.currentTime);
        }
    };
    const onMeta = () => dotnet.invokeMethodAsync('OnDuration', isFinite(media.duration) ? media.duration : 0);
    media.addEventListener('timeupdate', onTime);
    media.addEventListener('loadedmetadata', onMeta);
    return {
        seek(seconds) {
            media.currentTime = Math.max(0, seconds);
            if (media.paused) {
                media.play().catch(() => { /* autoplay policy: the user can press play */ });
            }
        },
        dispose() {
            media.removeEventListener('timeupdate', onTime);
            media.removeEventListener('loadedmetadata', onMeta);
        },
    };
}
