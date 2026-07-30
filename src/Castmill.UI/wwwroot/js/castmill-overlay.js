// The provenance overlay's measurement island. This is the ONE component allowed to
// measure pixels at runtime (ADR-F10 / Frontend §4): everything else is fluid CSS, but a
// cubic thread from a transcript row to a card cannot be drawn without geometry.
//
// Hand-written and hand-maintained, like castmill-ui-state.js — not esbuild output.
// It measures and it watches; every decision about what to DRAW stays in .NET.

/**
 * Measures the canvas container and a set of elements inside it, in container-local
 * coordinates. Selectors that match nothing are simply absent from the result.
 *
 * @param {HTMLElement} container the canvas root (also the SVG's coordinate system)
 * @param {string[]} selectors CSS selectors, each expected to match at most one element
 * @returns {{width:number,height:number,rects:Record<string,{x:number,y:number,w:number,h:number,visible:boolean}>}}
 */
export function measure(container, selectors) {
    if (!(container instanceof HTMLElement)) {
        return { width: 0, height: 0, rects: {} };
    }

    const base = container.getBoundingClientRect();
    const rects = {};

    for (const selector of selectors) {
        const el = container.querySelector(selector);
        if (!el) {
            continue;
        }

        const r = el.getBoundingClientRect();

        // Visibility against the nearest scroll container: a transcript row scrolled out
        // of its list should not get a thread drawn to a point under other content.
        let visible = true;
        const scroller = el.closest('[data-cm-scroll]');
        if (scroller) {
            const s = scroller.getBoundingClientRect();
            visible = r.bottom > s.top + 2 && r.top < s.bottom - 2;
        }

        rects[selector] = {
            x: r.x - base.x,
            y: r.y - base.y,
            w: r.width,
            h: r.height,
            visible,
        };
    }

    return { width: base.width, height: base.height, rects };
}

/**
 * Watches everything that can move geometry — resize, any inner scroll, zoom (a CSS class
 * change re-lays-out, which ResizeObserver sees) — and calls back into .NET at most once
 * per animation frame.
 *
 * @param {HTMLElement} container
 * @param {{invokeMethodAsync: (name: string) => Promise<unknown>}} dotnet
 * @returns {{dispose: () => void}}
 */
export function observe(container, dotnet) {
    let scheduled = false;

    const notify = () => {
        if (scheduled) {
            return;
        }

        scheduled = true;
        requestAnimationFrame(() => {
            scheduled = false;
            dotnet.invokeMethodAsync('NotifyGeometryChangedAsync');
        });
    };

    const resizeObserver = new ResizeObserver(notify);
    resizeObserver.observe(container);

    // Capture phase catches scrolls of any inner region (the transcript list, the board).
    container.addEventListener('scroll', notify, { capture: true, passive: true });
    window.addEventListener('resize', notify, { passive: true });

    return {
        dispose() {
            resizeObserver.disconnect();
            container.removeEventListener('scroll', notify, { capture: true });
            window.removeEventListener('resize', notify);
        },
    };
}
