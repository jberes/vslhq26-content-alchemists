// Image Studio editor island (ADR-055): drag/resize of overlay text boxes over the take,
// and a rectangle mask tool for region edits. Geometry is reported to .NET as RATIOS of
// the image so the same numbers drive the server's full-size composite. The module
// measures and moves; what a box says, looks like, or whether it exists is decided in .NET.

/**
 * @param {HTMLElement} stage the positioned element wrapping the <img> and the boxes
 * @param {any} dotnet DotNetObjectReference with BoxMoved(id, x, y, w, h) and MaskDrawn(x, y, w, h)
 */
export function attach(stage, dotnet) {
    if (!(stage instanceof HTMLElement)) {
        return null;
    }
    let mode = 'boxes'; // boxes | mask
    let drag = null;
    let maskRect = null;
    const maskLayer = stage.querySelector('[data-mask-layer]');

    const rect = () => {
        const img = stage.querySelector('img');
        return (img || stage).getBoundingClientRect();
    };
    const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

    function onDown(e) {
        if (e.button !== 0) { return; }
        const r = rect();
        if (mode === 'mask') {
            const x = clamp((e.clientX - r.left) / r.width, 0, 1);
            const y = clamp((e.clientY - r.top) / r.height, 0, 1);
            maskRect = { x0: x, y0: y, x1: x, y1: y };
            drag = { kind: 'mask' };
            stage.setPointerCapture(e.pointerId);
            e.preventDefault();
            return;
        }
        const handle = e.target.closest('[data-handle]');
        const box = e.target.closest('[data-box]');
        if (!box) { return; }
        const b = box.getBoundingClientRect();
        drag = {
            kind: handle ? 'resize' : 'move',
            id: box.dataset.box,
            el: box,
            startX: e.clientX, startY: e.clientY,
            x: (b.left - r.left) / r.width, y: (b.top - r.top) / r.height,
            w: b.width / r.width, h: b.height / r.height,
        };
        stage.setPointerCapture(e.pointerId);
        e.preventDefault();
    }

    function onMove(e) {
        if (!drag) { return; }
        const r = rect();
        if (drag.kind === 'mask') {
            maskRect.x1 = clamp((e.clientX - r.left) / r.width, 0, 1);
            maskRect.y1 = clamp((e.clientY - r.top) / r.height, 0, 1);
            paintMask();
            return;
        }
        const dx = (e.clientX - drag.startX) / r.width;
        const dy = (e.clientY - drag.startY) / r.height;
        let { x, y, w, h } = drag;
        if (drag.kind === 'move') {
            x = clamp(x + dx, 0, 1 - w);
            y = clamp(y + dy, 0, 1 - h);
        } else {
            w = clamp(w + dx, 0.04, 1 - x);
            h = clamp(h + dy, 0.04, 1 - y);
        }
        drag.live = { x, y, w, h };
        drag.el.style.left = `${x * 100}%`;
        drag.el.style.top = `${y * 100}%`;
        drag.el.style.width = `${w * 100}%`;
        drag.el.style.height = `${h * 100}%`;
    }

    function onUp(e) {
        if (!drag) { return; }
        try { stage.releasePointerCapture(e.pointerId); } catch { /* already released */ }
        if (drag.kind === 'mask') {
            const m = normalized();
            if (m && m.w > 0.01 && m.h > 0.01) {
                dotnet.invokeMethodAsync('MaskDrawn', m.x, m.y, m.w, m.h);
            }
        } else if (drag.live) {
            const { x, y, w, h } = drag.live;
            dotnet.invokeMethodAsync('BoxMoved', drag.id, x, y, w, h);
        }
        drag = null;
    }

    function normalized() {
        if (!maskRect) { return null; }
        const x = Math.min(maskRect.x0, maskRect.x1), y = Math.min(maskRect.y0, maskRect.y1);
        return { x, y, w: Math.abs(maskRect.x1 - maskRect.x0), h: Math.abs(maskRect.y1 - maskRect.y0) };
    }

    function paintMask() {
        if (!maskLayer) { return; }
        const m = normalized();
        if (!m) { maskLayer.hidden = true; return; }
        maskLayer.hidden = false;
        maskLayer.style.left = `${m.x * 100}%`;
        maskLayer.style.top = `${m.y * 100}%`;
        maskLayer.style.width = `${m.w * 100}%`;
        maskLayer.style.height = `${m.h * 100}%`;
    }

    stage.addEventListener('pointerdown', onDown);
    stage.addEventListener('pointermove', onMove);
    stage.addEventListener('pointerup', onUp);
    stage.addEventListener('pointercancel', onUp);

    return {
        setMode(next) {
            mode = next;
            stage.dataset.editorMode = next;
            if (next !== 'mask') { maskRect = null; paintMask(); }
        },
        clearMask() { maskRect = null; paintMask(); },
        /**
         * The mask as a PNG data URL at the image's natural size: white where the edit
         * applies, transparent elsewhere — the convention the API expects.
         */
        exportMask(naturalWidth, naturalHeight) {
            const m = normalized();
            if (!m) { return null; }
            const canvas = document.createElement('canvas');
            canvas.width = naturalWidth; canvas.height = naturalHeight;
            const ctx = canvas.getContext('2d');
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            ctx.fillStyle = '#ffffff';
            ctx.fillRect(Math.round(m.x * naturalWidth), Math.round(m.y * naturalHeight),
                Math.round(m.w * naturalWidth), Math.round(m.h * naturalHeight));
            return canvas.toDataURL('image/png');
        },
        naturalSize() {
            const img = stage.querySelector('img');
            return img ? [img.naturalWidth || 0, img.naturalHeight || 0] : [0, 0];
        },
        dispose() {
            stage.removeEventListener('pointerdown', onDown);
            stage.removeEventListener('pointermove', onMove);
            stage.removeEventListener('pointerup', onUp);
            stage.removeEventListener('pointercancel', onUp);
        },
    };
}
