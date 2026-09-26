// Image editor island (ADR-F72): the pointer, keyboard and drag-and-drop half of the manual
// image editor. It measures and moves; every decision — what a layer is, whether it exists,
// its z-order, undo — belongs to .NET. Layers are rendered by Razor with their geometry in
// data-* attributes (ratios of the slot), and every commit goes back to .NET as ratios so the
// server composite reproduces what the producer placed.
//
// The chrome element ([data-bench-chrome]) is owned here: selection box, handles, crop
// brackets, the ghosted full picture while cropping, snap guides, drop preview and the live
// size readout. Razor never renders into it.
//
// Hand-written and hand-maintained, like castmill-overlay-editor.js.

const DIRS = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];
const CORNERS = ['nw', 'ne', 'se', 'sw'];
const MIN_PX = 12;
const SNAP_PX = 7;
const DRAG_START_PX = 3;
const MAX_FILE_BYTES = 20 * 1024 * 1024;

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
const num = v => { const n = parseFloat(v); return Number.isFinite(n) ? n : 0; };
const isTyping = el => !!el?.closest?.('input, textarea, select, [contenteditable="true"]');

/**
 * @param {HTMLElement} root the dialog element containing stage, tray and layer list
 * @param {{invokeMethodAsync: (name: string, ...args: any[]) => Promise<any>}} dotnet
 */
export function attach(root, dotnet) {
    if (!(root instanceof HTMLElement)) {
        return null;
    }

    let state = { selectedId: null, mode: 'select', slotWidth: 1280, slotHeight: 720 };
    let drag = null;
    let guides = [];
    let readout = null;
    let hoverId = null;
    let wheelTimer = null;
    let pendingCrop = null;
    const measured = new Set();
    const grown = new Map();

    const stage = () => root.querySelector('[data-bench-stage]');
    const chrome = () => root.querySelector('[data-bench-chrome]');
    const overlay = () => root.querySelector('[data-bench-overlay]');
    const onCanvas = target => !!(stage()?.contains(target) || overlay()?.contains(target));
    const gesture = on => { if (on) root.dataset.gesture = 'true'; else delete root.dataset.gesture; };
    const clearSelection = () => { try { window.getSelection()?.removeAllRanges(); } catch { /* none */ } };
    const layerEl = id => id ? root.querySelector(`[data-layer-id="${CSS.escape(id)}"]`) : null;
    const size = () => { const s = stage(); return s ? { w: s.clientWidth, h: s.clientHeight } : { w: 1, h: 1 }; };
    const call = (name, ...args) => dotnet.invokeMethodAsync(name, ...args).catch(() => { /* circuit gone */ });

    /** Geometry of a layer in STAGE pixels, read from its data attributes. */
    function geom(el) {
        const { w, h } = size();
        return {
            x: num(el.dataset.x) * w, y: num(el.dataset.y) * h,
            w: num(el.dataset.w) * w, h: num(el.dataset.h) * h,
            kind: el.dataset.kind, shape: el.dataset.shape || 'rectangle',
            locked: el.dataset.locked === 'true',
            zoom: num(el.dataset.zoom) || 1, fx: el.dataset.fx == null ? 0.5 : num(el.dataset.fx), fy: el.dataset.fy == null ? 0.5 : num(el.dataset.fy),
        };
    }
    const fixedAspect = g => g.kind === 'image' && (g.shape === 'square' || g.shape === 'circle');

    function natural(el) {
        const img = el.querySelector('img[data-layer-img]');
        return img && img.naturalWidth > 0 ? { nw: img.naturalWidth, nh: img.naturalHeight, img } : null;
    }

    // Same maths as BenchLayers.Cover / ImageComposer: cover, zoom, source window centred on focus.
    function cover(g, n) {
        const s = Math.max(g.w / n.nw, g.h / n.nh) * clamp(g.zoom, 1, 4);
        const sw = Math.min(n.nw, g.w / s), sh = Math.min(n.nh, g.h / s);
        const sl = clamp(g.fx * n.nw - sw / 2, 0, n.nw - sw), st = clamp(g.fy * n.nh - sh / 2, 0, n.nh - sh);
        return { s, iw: n.nw * s, ih: n.nh * s, ix: -sl * s, iy: -st * s };
    }
    function extent(g, n) { const c = cover(g, n); return { x: g.x + c.ix, y: g.y + c.iy, w: c.iw, h: c.ih }; }
    function cropFromExtent(g, n, E) {
        const s0 = Math.max(g.w / n.nw, g.h / n.nh);
        const zoom = clamp(E.w / (n.nw * s0), 1, 4);
        const s = s0 * zoom;
        const sw = g.w / s, sh = g.h / s;
        const sl = (g.x - E.x) / s, st = (g.y - E.y) / s;
        g.zoom = zoom;
        g.fx = clamp((sl + sw / 2) / n.nw, 0, 1);
        g.fy = clamp((st + sh / 2) / n.nh, 0, 1);
    }

    /** Writes live geometry onto the Razor-rendered element while a gesture is in flight. */
    function paintLayer(el, g) {
        const { w, h } = size();
        el.style.left = `${(g.x / w) * 100}%`;
        el.style.top = `${(g.y / h) * 100}%`;
        el.style.width = `${(g.w / w) * 100}%`;
        el.style.height = `${(g.h / h) * 100}%`;
        const n = natural(el);
        if (n && g.kind === 'image') {
            const c = cover(g, n);
            Object.assign(n.img.style, {
                left: `${(c.ix / g.w) * 100}%`, top: `${(c.iy / g.h) * 100}%`,
                width: `${(c.iw / g.w) * 100}%`, height: `${(c.ih / g.h) * 100}%`, objectFit: '',
            });
        }
    }

    // ---- chrome -----------------------------------------------------------------------

    function el(tag, cls, attrs) {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (attrs) for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
        return e;
    }
    const hpos = (d, g) => [g.x + (d.includes('w') ? 0 : d.includes('e') ? g.w : g.w / 2), g.y + (d.includes('n') ? 0 : d.includes('s') ? g.h : g.h / 2)];
    function place(e, g) { Object.assign(e.style, { left: `${g.x}px`, top: `${g.y}px`, width: `${g.w}px`, height: `${g.h}px` }); }

    /** Keeps the instrument layer exactly over the stage, wherever layout or scrolling moved it. */
    function placeOverlay() {
        const o = overlay(), s = stage();
        if (!o || !s) return;
        const r = s.getBoundingClientRect(), rr = root.getBoundingClientRect();
        o.style.left = `${r.left - rr.left}px`;
        o.style.top = `${r.top - rr.top}px`;
        o.style.width = `${r.width}px`;
        o.style.height = `${r.height}px`;
    }

    function draw() {
        placeOverlay();
        const c = chrome();
        if (!c) return;
        c.replaceChildren();
        for (const gd of guides) {
            const line = el('div', `cm-bench__guide cm-bench__guide--${gd.axis}`);
            line.style[gd.axis === 'v' ? 'left' : 'top'] = `${gd.at}px`;
            c.appendChild(line);
        }
        if (hoverId && hoverId !== state.selectedId && !drag) {
            const h = layerEl(hoverId);
            if (h) { const hb = el('div', 'cm-bench__hover'); place(hb, geom(h)); if (h.dataset.shape === 'circle' && h.dataset.kind === 'image') hb.classList.add('cm-bench__hover--round'); c.appendChild(hb); }
        }
        const sel = layerEl(state.selectedId);
        if (!sel) return;
        const g = drag?.live ?? geom(sel);
        const round = g.kind === 'image' && g.shape === 'circle';
        if (state.mode === 'crop' && g.kind === 'image') {
            const n = natural(sel);
            if (!n) return;
            const E = drag?.E && drag.kind !== 'cropResize' ? drag.E : extent(g, n);
            const ghost = el('div', 'cm-bench__ghost', { 'data-act': 'pan' });
            place(ghost, E);
            const gi = el('img', null, { alt: '', draggable: 'false' });
            gi.src = n.img.currentSrc || n.img.src;
            ghost.appendChild(gi);
            c.appendChild(ghost);
            const box = el('div', `cm-bench__cropbox${round ? ' cm-bench__cropbox--round' : ''}`, { 'data-act': 'pan' });
            place(box, g);
            for (const t of [1 / 3, 2 / 3]) {
                const v = el('div', 'cm-bench__third cm-bench__third--v'); v.style.left = `${t * 100}%`; box.appendChild(v);
                const hz = el('div', 'cm-bench__third cm-bench__third--h'); hz.style.top = `${t * 100}%`; box.appendChild(hz);
            }
            c.appendChild(box);
            for (const d of fixedAspect(g) ? CORNERS : DIRS) {
                const hd = el('div', `cm-bench__crophandle cm-bench__crophandle--${d}`, { 'data-crop': d, 'aria-hidden': 'true' });
                const [hx, hy] = hpos(d, g); hd.style.left = `${hx}px`; hd.style.top = `${hy}px`;
                c.appendChild(hd);
            }
            return;
        }
        const box = el('div', `cm-bench__selection${round ? ' cm-bench__selection--round' : ''}`);
        place(box, g);
        c.appendChild(box);
        if (!g.locked && state.mode !== 'text') {
            for (const d of fixedAspect(g) ? CORNERS : DIRS) {
                const hd = el('div', `cm-bench__handle cm-bench__handle--${d}`, { 'data-handle': d, 'aria-hidden': 'true' });
                const [hx, hy] = hpos(d, g); hd.style.left = `${hx}px`; hd.style.top = `${hy}px`;
                c.appendChild(hd);
            }
        }
        if (readout) {
            const r = el('div', 'cm-bench__readout');
            r.textContent = readout;
            r.style.left = `${g.x + g.w / 2}px`;
            r.style.top = `${g.y + g.h}px`;
            c.appendChild(r);
        }
    }

    // ---- gestures on the stage ---------------------------------------------------------

    function stagePoint(e) {
        const r = stage().getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    function onPointerDown(e) {
        const s = stage();
        if (!s || e.button !== 0 || !onCanvas(e.target)) return;
        if (e.target.closest('[data-bench-ui], textarea')) return;
        // pointerdown's preventDefault keeps focus where it was; a half-typed inspector field
        // would then swallow Delete and the arrows. Blurring commits it (change fires) and the
        // dialog takes the keyboard back.
        const active = document.activeElement;
        if (active && active !== root && root.contains(active)) active.blur();
        root.focus({ preventScroll: true });
        const p = stagePoint(e);
        const sel = layerEl(state.selectedId);

        if (state.mode === 'crop' && sel) {
            const cd = e.target.dataset?.crop;
            const n = natural(sel);
            if (cd && n) { begin(e, { kind: 'cropResize', dir: cd, p0: p, el: sel, b0: geom(sel), n, E: extent(geom(sel), n) }); return; }
            if (e.target.dataset?.act === 'pan' && n) { const g = geom(sel); begin(e, { kind: 'pan', p0: p, el: sel, b0: g, n, E0: extent(g, n) }); return; }
            call('Command', 'cropDone');
            state.mode = 'select';
        }

        const hd = e.target.dataset?.handle;
        if (hd && sel) { begin(e, { kind: 'resize', dir: hd, p0: p, el: sel, b0: geom(sel), style0: sel.getAttribute('style'), imgStyle0: natural(sel)?.img.getAttribute('style') }); return; }

        let target = e.target.closest('[data-layer-id]');
        if (e.metaKey || e.ctrlKey) {
            // Select the layer UNDER the current one: walk the stack at the pointer.
            const stack = document.elementsFromPoint(e.clientX, e.clientY)
                .map(n => n.closest?.('[data-layer-id]'))
                .filter((n, i, a) => n && n.dataset.locked !== 'true' && a.indexOf(n) === i);
            if (stack.length) {
                const current = stack.findIndex(n => n.dataset.layerId === state.selectedId);
                target = stack[(current + 1) % stack.length];
            }
        }
        if (!target) {
            if (state.selectedId) { state.selectedId = null; call('SelectLayer', null); draw(); }
            return;
        }
        const id = target.dataset.layerId;
        if (id !== state.selectedId) {
            // Selection is immediate and local so press-and-drag is ONE gesture; .NET catches up.
            state.selectedId = id;
            if (state.mode === 'text') state.mode = 'select';
            call('SelectLayer', id);
        }
        hoverId = null;
        const g = geom(target);
        if (g.locked) { draw(); return; }
        begin(e, { kind: 'move', p0: p, el: target, b0: g, style0: target.getAttribute('style'), moved: false });
        draw();
    }

    function begin(e, d) {
        clearSelection();
        drag = d;
        stage().setPointerCapture(e.pointerId);
        e.preventDefault();
    }

    function onPointerMove(e) {
        const s = stage();
        if (!s) return;
        if (!drag) {
            const t = s.contains(e.target) ? e.target.closest?.('[data-layer-id]') : null;
            const next = t && t.dataset.locked !== 'true' ? t.dataset.layerId : null;
            if (next !== hoverId) { hoverId = next; draw(); }
            return;
        }
        const p = stagePoint(e);
        const dx = p.x - drag.p0.x, dy = p.y - drag.p0.y;
        const { w: SW, h: SH } = size();
        if (drag.kind === 'move') {
            if (!drag.moved && Math.hypot(dx, dy) < DRAG_START_PX) return;
            if (!drag.moved) { drag.moved = true; gesture(true); }
            const g = { ...drag.b0, x: drag.b0.x + dx, y: drag.b0.y + dy };
            if (!e.shiftKey) snap(g); else guides = [];
            drag.live = g;
            paintLayer(drag.el, g);
            readout = `x ${Math.round(g.x / SW * state.slotWidth)} · y ${Math.round(g.y / SH * state.slotHeight)}`;
        } else if (drag.kind === 'resize') {
            gesture(true);
            const g = resize(drag.b0, drag.dir, dx, dy, e);
            drag.live = g;
            paintLayer(drag.el, g);
            readout = `${Math.round(g.w / SW * state.slotWidth)} × ${Math.round(g.h / SH * state.slotHeight)}`;
        } else if (drag.kind === 'pan') {
            gesture(true);
            const g = { ...drag.b0 };
            const E = { ...drag.E0 };
            E.x = clamp(E.x + dx, g.x + g.w - E.w, g.x);
            E.y = clamp(E.y + dy, g.y + g.h - E.h, g.y);
            cropFromExtent(g, drag.n, E);
            drag.live = g; drag.E = E;
            paintLayer(drag.el, g);
        } else if (drag.kind === 'cropResize') {
            gesture(true);
            const g = cropResize(drag.b0, drag.E, drag.dir, dx, dy, drag.n);
            drag.live = g;
            paintLayer(drag.el, g);
            readout = `${Math.round(g.w / SW * state.slotWidth)} × ${Math.round(g.h / SH * state.slotHeight)}`;
        }
        draw();
    }

    function onPointerUp(e) {
        if (!drag) return;
        const s = stage();
        try { s?.releasePointerCapture(e.pointerId); } catch { /* already released */ }
        const d = drag;
        drag = null; guides = []; readout = null;
        gesture(false);
        const { w: SW, h: SH } = size();
        if (d.live && (d.kind !== 'move' || d.moved)) {
            const g = d.live;
            // Keep the live style: Razor's next render writes the committed numbers over it.
            if (d.kind === 'pan' || d.kind === 'cropResize') {
                call('CommitCrop', d.el.dataset.layerId, g.x / SW, g.y / SH, g.w / SW, g.h / SH, g.zoom, g.fx, g.fy);
            } else {
                call('CommitGeometry', d.el.dataset.layerId, g.x / SW, g.y / SH, g.w / SW, g.h / SH);
            }
            // Keep the chrome on the live numbers until .NET re-renders and calls sync().
            setData(d.el, g, SW, SH);
        } else if (d.style0 != null) {
            d.el.setAttribute('style', d.style0);
        }
        draw();
    }

    function setData(target, g, SW, SH) {
        target.dataset.x = String(g.x / SW); target.dataset.y = String(g.y / SH);
        target.dataset.w = String(g.w / SW); target.dataset.h = String(g.h / SH);
        if (g.kind === 'image') { target.dataset.zoom = String(g.zoom); target.dataset.fx = String(g.fx); target.dataset.fy = String(g.fy); }
    }

    function snap(g) {
        const { w: SW, h: SH } = size();
        guides = [];
        const xs = [0, SW / 2, SW], ys = [0, SH / 2, SH];
        root.querySelectorAll('[data-layer-id]').forEach(other => {
            if (other.dataset.layerId === drag.el.dataset.layerId || other.dataset.visible === 'false') return;
            const o = geom(other);
            xs.push(o.x, o.x + o.w / 2, o.x + o.w);
            ys.push(o.y, o.y + o.h / 2, o.y + o.h);
        });
        const best = (pos, len, candidates) => {
            let r = null;
            for (const off of [0, len / 2, len]) for (const c of candidates) {
                const delta = c - (pos + off);
                if (Math.abs(delta) < SNAP_PX && (!r || Math.abs(delta) < Math.abs(r.delta))) r = { delta, at: c };
            }
            return r;
        };
        const bx = best(g.x, g.w, xs), by = best(g.y, g.h, ys);
        if (bx) { g.x += bx.delta; guides.push({ axis: 'v', at: bx.at }); }
        if (by) { g.y += by.delta; guides.push({ axis: 'h', at: by.at }); }
    }

    function resize(b, dir, dx, dy, e) {
        const corner = dir.length === 2;
        // Corners keep a picture's proportions (Shift frees them); text frees by default and
        // Shift locks. Square and circle frames are always square.
        const keep = fixedAspect(b) || (corner && (b.kind === 'image' ? !e.shiftKey : e.shiftKey));
        const m = e.altKey ? 2 : 1;
        let w = b.w, h = b.h;
        if (dir.includes('e')) w = b.w + dx * m;
        if (dir.includes('w')) w = b.w - dx * m;
        if (dir.includes('s')) h = b.h + dy * m;
        if (dir.includes('n')) h = b.h - dy * m;
        if (keep) {
            const ar = b.w / b.h;
            if (!corner) { if (dir === 'e' || dir === 'w') h = w / ar; else w = h * ar; }
            else if (Math.abs(w / b.w) > Math.abs(h / b.h)) h = w / ar; else w = h * ar;
            if (w < MIN_PX) { w = MIN_PX; h = w / ar; }
            if (h < MIN_PX) { h = MIN_PX; w = h * ar; }
        }
        w = Math.max(MIN_PX, w); h = Math.max(MIN_PX, h);
        let x = b.x, y = b.y;
        if (e.altKey) { x = b.x + (b.w - w) / 2; y = b.y + (b.h - h) / 2; }
        else {
            if (dir.includes('w')) x = b.x + b.w - w; else if (!dir.includes('e')) x = b.x + (b.w - w) / 2;
            if (dir.includes('n')) y = b.y + b.h - h; else if (!dir.includes('s')) y = b.y + (b.h - h) / 2;
        }
        return { ...b, x, y, w, h };
    }

    // In crop mode the picture stays still and the frame's edges move over it.
    function cropResize(b, E, dir, dx, dy, n) {
        let x0 = b.x, y0 = b.y, x1 = b.x + b.w, y1 = b.y + b.h;
        if (dir.includes('w')) x0 = clamp(b.x + dx, E.x, x1 - MIN_PX);
        if (dir.includes('e')) x1 = clamp(b.x + b.w + dx, x0 + MIN_PX, E.x + E.w);
        if (dir.includes('n')) y0 = clamp(b.y + dy, E.y, y1 - MIN_PX);
        if (dir.includes('s')) y1 = clamp(b.y + b.h + dy, y0 + MIN_PX, E.y + E.h);
        if (fixedAspect(b)) {
            let side = Math.max(x1 - x0, y1 - y0);
            const maxW = dir.includes('w') ? x1 - E.x : E.x + E.w - x0;
            const maxH = dir.includes('n') ? y1 - E.y : E.y + E.h - y0;
            side = Math.min(side, maxW, maxH);
            if (dir.includes('w')) x0 = x1 - side; else x1 = x0 + side;
            if (dir.includes('n')) y0 = y1 - side; else y1 = y0 + side;
        }
        const g = { ...b, x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
        cropFromExtent(g, n, E);
        return g;
    }

    function zoomCrop(factor) {
        const sel = layerEl(state.selectedId);
        const n = sel && natural(sel);
        if (!n) return;
        const g = pendingCrop ?? geom(sel);
        const E0 = extent(g, n), cx = g.x + g.w / 2, cy = g.y + g.h / 2;
        const s0 = Math.max(g.w / n.nw, g.h / n.nh);
        const z = clamp(g.zoom * factor, 1, 4);
        const Ew = n.nw * s0 * z, Eh = n.nh * s0 * z, f = Ew / E0.w;
        const E = { w: Ew, h: Eh, x: cx - (cx - E0.x) * f, y: cy - (cy - E0.y) * f };
        E.x = clamp(E.x, g.x + g.w - E.w, g.x);
        E.y = clamp(E.y, g.y + g.h - E.h, g.y);
        cropFromExtent(g, n, E);
        pendingCrop = g;
        paintLayer(sel, g);
        const { w: SW, h: SH } = size();
        setData(sel, g, SW, SH);
        draw();
        clearTimeout(wheelTimer);
        wheelTimer = setTimeout(() => {
            const pc = pendingCrop; pendingCrop = null;
            if (pc) call('CommitCrop', sel.dataset.layerId, pc.x / SW, pc.y / SH, pc.w / SW, pc.h / SH, pc.zoom, pc.fx, pc.fy);
        }, 300);
    }

    function onWheel(e) {
        if (state.mode !== 'crop' || !onCanvas(e.target)) return;
        e.preventDefault();
        zoomCrop(e.deltaY < 0 ? 1.06 : 1 / 1.06);
    }

    function onDoubleClick(e) {
        if (!onCanvas(e.target) || e.target.closest('[data-bench-ui], textarea')) return;
        // Pointer capture retargets dblclick to the stage; the pointerdown already selected.
        if (state.selectedId && state.mode === 'select') call('Command', 'activate');
    }

    // ---- keyboard ----------------------------------------------------------------------

    function onKeyDown(e) {
        if (e.defaultPrevented || document.querySelector('[role="alertdialog"]')) return;
        const active = document.activeElement;
        if (active && active !== document.body && !root.contains(active)) return;
        const mod = e.metaKey || e.ctrlKey;
        if (e.target.closest?.('[data-bench-textedit]')) {
            if (e.key === 'Escape') { e.preventDefault(); call('Command', 'escape'); }
            return;
        }
        // A slider has no use for Enter: after dragging the crop zoom, Enter still means Done.
        if (e.key === 'Enter' && e.target.matches?.('input[type="range"]') && state.mode === 'crop') {
            e.preventDefault(); e.target.blur(); call('Command', 'cropDone'); return;
        }
        if (isTyping(e.target)) {
            if (e.key === 'Escape') { e.target.blur(); e.preventDefault(); }
            return;
        }
        if (e.target.closest?.('[data-bench-tile]') && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            const tile = e.target.closest('[data-bench-tile]');
            call('DropTile', tile.dataset.tileKind, tile.dataset.tileKey, 0.5, 0.5, tileAspect(tile));
            return;
        }
        if (e.key === 'Escape') { e.preventDefault(); call('Command', 'escape'); return; }
        if (mod && e.key.toLowerCase() === 'z') { e.preventDefault(); call('Command', e.shiftKey ? 'redo' : 'undo'); return; }
        if (mod && e.key.toLowerCase() === 'y') { e.preventDefault(); call('Command', 'redo'); return; }
        if (e.key === 'Tab' && !e.target.closest?.('button, a, [data-bench-tile], [tabindex]:not([tabindex="-1"])')) {
            e.preventDefault(); call('Command', e.shiftKey ? 'prev' : 'next'); return;
        }
        const sel = layerEl(state.selectedId);
        if (!sel) return;
        const onControl = e.target.closest?.('button, a, [data-bench-tile]');
        if ((e.key === 'Delete' || e.key === 'Backspace') && !onControl) { e.preventDefault(); call('Command', 'delete'); return; }
        if (e.key === 'Enter' && !onControl) { e.preventDefault(); call('Command', state.mode === 'crop' ? 'cropDone' : 'activate'); return; }
        if (mod && (e.key === ']' || e.code === 'BracketRight')) { e.preventDefault(); call('Command', e.shiftKey ? 'front' : 'forward'); return; }
        if (mod && (e.key === '[' || e.code === 'BracketLeft')) { e.preventDefault(); call('Command', e.shiftKey ? 'back' : 'backward'); return; }
        if (mod && e.key.toLowerCase() === 'd') { e.preventDefault(); call('Command', 'duplicate'); return; }
        const step = e.shiftKey ? 10 : 1;
        const move = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[e.key];
        if (move && !onControl && state.mode === 'select' && sel.dataset.locked !== 'true') {
            e.preventDefault();
            // Output pixels, not screen pixels: a 1 px nudge is one pixel of the published image.
            call('Nudge', sel.dataset.layerId, move[0] / state.slotWidth, move[1] / state.slotHeight);
        }
    }

    // ---- tray: drag a tile onto the canvas ----------------------------------------------

    function tileAspect(tile) {
        const img = tile.querySelector('img');
        return img && img.naturalWidth > 0 ? img.naturalWidth / img.naturalHeight : 0;
    }

    function overElement(target, e) {
        if (!target) return false;
        const r = target.getBoundingClientRect();
        return e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
    }

    function onTilePointerDown(e) {
        const tile = e.target.closest?.('[data-bench-tile]');
        if (!tile || e.button !== 0 || !root.contains(tile)) return;
        e.preventDefault();
        const start = { x: e.clientX, y: e.clientY };
        let ghost = null, preview = null;
        const kind = tile.dataset.tileKind;
        tile.setPointerCapture(e.pointerId);
        const aspect = tileAspect(tile);
        const bg = () => root.querySelector('[data-bench-bgdrop]');

        const move = ev => {
            if (!ghost && Math.hypot(ev.clientX - start.x, ev.clientY - start.y) < 5) return;
            if (!ghost) {
                ghost = el('div', 'cm-bench__dragghost');
                const src = tile.querySelector('img')?.currentSrc;
                if (src) ghost.style.backgroundImage = `url("${src}")`;
                else ghost.textContent = tile.dataset.tileLabel || '';
                document.body.appendChild(ghost);
                preview = el('div', 'cm-bench__droppreview');
                root.dataset.dragging = 'tile';
                clearSelection();
            }
            ghost.style.left = `${ev.clientX}px`;
            ghost.style.top = `${ev.clientY}px`;
            const s = stage();
            const onStage = overElement(s, ev);
            const b = bg();
            const onBg = kind === 'image' && b && overElement(b, ev);
            b?.toggleAttribute('data-over', !!onBg);
            ghost.toggleAttribute('data-over-stage', onStage);
            if (onStage && s.dataset.hasBg === 'true') {
                const p = stagePoint(ev), { w: SW, h: SH } = size();
                let w, h;
                if (kind === 'image') {
                    const ar = aspect || 1.5;
                    w = SW * 0.38; h = w / ar;
                    if (h > SH * 0.62) { h = SH * 0.62; w = h * ar; }
                } else {
                    w = num(tile.dataset.tileW) * SW; h = num(tile.dataset.tileH) * SH;
                }
                place(preview, { x: p.x - w / 2, y: p.y - h / 2, w, h });
                if (!preview.isConnected) chrome()?.appendChild(preview);
            } else {
                preview.remove();
            }
        };
        const up = ev => {
            tile.removeEventListener('pointermove', move);
            tile.removeEventListener('pointerup', up);
            tile.removeEventListener('pointercancel', up);
            delete root.dataset.dragging;
            if (!ghost) { if (ev.type === 'pointerup') call('TileClicked', kind); return; }
            ghost.remove(); preview?.remove();
            const b = bg();
            b?.removeAttribute('data-over');
            if (ev.type !== 'pointerup') return;
            if (kind === 'image' && b && overElement(b, ev)) { call('DropTileOnBackground', tile.dataset.tileKey); return; }
            const s = stage();
            if (!overElement(s, ev)) return;
            const p = stagePoint(ev), { w: SW, h: SH } = size();
            call('DropTile', kind, tile.dataset.tileKey, p.x / SW, p.y / SH, aspect);
        };
        tile.addEventListener('pointermove', move);
        tile.addEventListener('pointerup', up);
        tile.addEventListener('pointercancel', up);
    }

    // ---- files dropped from the desktop -------------------------------------------------

    function onDragOver(e) {
        if (!e.dataTransfer?.types?.includes('Files')) return;
        e.preventDefault();
        root.dataset.dragging = 'file';
    }
    function onDragLeave(e) {
        if (!root.contains(e.relatedTarget)) delete root.dataset.dragging;
    }
    async function onDrop(e) {
        if (!e.dataTransfer?.files?.length) return;
        e.preventDefault();
        delete root.dataset.dragging;
        const s = stage();
        const b = root.querySelector('[data-bench-bgdrop]');
        const background = !!(b && overElement(b, e)) || (s && overElement(s, e) && s.dataset.hasBg !== 'true');
        const onStage = s && overElement(s, e);
        const { w: SW, h: SH } = size();
        const p = onStage ? stagePoint(e) : { x: SW / 2, y: SH / 2 };
        let offset = 0;
        for (const file of e.dataTransfer.files) {
            if (!file.type.startsWith('image/')) continue;
            if (file.size > MAX_FILE_BYTES) { call('FileRejected', file.name); continue; }
            const bytes = new Uint8Array(await file.arrayBuffer());
            await call('DropFile', file.name, file.type, bytes, (p.x + offset) / SW, (p.y + offset) / SH, background);
            offset += 24;
            if (background) break;
        }
    }

    // ---- layers list: drag a row to change z-order ---------------------------------------

    function onRowPointerDown(e) {
        const grip = e.target.closest?.('[data-bench-grip]');
        if (!grip || e.button !== 0 || !root.contains(grip)) return;
        const row = grip.closest('[data-bench-row]');
        const list = row?.parentElement;
        if (!row || !list) return;
        e.preventDefault();
        e.stopPropagation();
        grip.setPointerCapture(e.pointerId);
        row.dataset.dragging = 'true';
        root.dataset.dragging = 'row';
        clearSelection();
        const line = el('div', 'cm-bench__insert');
        let to = null;
        const rows = () => [...list.querySelectorAll('[data-bench-row]')];
        const move = ev => {
            const rs = rows();
            let pos = rs.length;
            for (let i = 0; i < rs.length; i++) {
                const r = rs[i].getBoundingClientRect();
                if (ev.clientY < r.top + r.height / 2) { pos = i; break; }
            }
            to = pos;
            const ref = rs[Math.min(pos, rs.length - 1)];
            const lr = list.getBoundingClientRect(), rr = ref.getBoundingClientRect();
            line.style.top = `${(pos < rs.length ? rr.top : rr.bottom) - lr.top + list.scrollTop - 1}px`;
            if (!line.isConnected) list.appendChild(line);
        };
        const up = ev => {
            grip.removeEventListener('pointermove', move);
            grip.removeEventListener('pointerup', up);
            grip.removeEventListener('pointercancel', up);
            line.remove();
            delete row.dataset.dragging;
            delete root.dataset.dragging;
            if (ev.type !== 'pointerup' || to == null) return;
            const from = rows().indexOf(row);
            const target = to > from ? to - 1 : to;
            if (target !== from) call('ReorderLayer', row.dataset.benchRow, target);
        };
        grip.addEventListener('pointermove', move);
        grip.addEventListener('pointerup', up);
        grip.addEventListener('pointercancel', up);
    }

    // ---- measurement: natural image sizes, text that needs a taller box ------------------

    function reportImages() {
        root.querySelectorAll('img[data-layer-img]').forEach(img => {
            const key = `${img.dataset.asset}|${img.currentSrc || img.src}`;
            if (img.complete && img.naturalWidth > 0 && !measured.has(key)) {
                measured.add(key);
                call('ImageMeasured', img.dataset.asset, img.naturalWidth, img.naturalHeight);
            }
        });
    }
    function onLoad(e) {
        if (e.target instanceof HTMLImageElement && e.target.matches('img[data-layer-img]')) reportImages();
    }

    // Rendered text is never clipped: a box whose words need more height grows to fit them.
    function measureText() {
        if (drag) return;
        const { h: SH } = size();
        if (SH <= 1) return;
        root.querySelectorAll('[data-layer-id][data-kind="text"]').forEach(layer => {
            const box = layer.querySelector('[data-text-box]');
            if (!box) return;
            const cs = getComputedStyle(box);
            const words = layer.querySelector('[data-words]');
            const content = words ? words.getBoundingClientRect().height : box.scrollHeight - num(cs.paddingTop) - num(cs.paddingBottom);
            const need = content + num(cs.paddingTop) + num(cs.paddingBottom);
            const have = layer.clientHeight;
            if (need > have + 1) {
                const ratio = need / SH;
                const key = layer.dataset.layerId;
                if (Math.abs((grown.get(key) ?? 0) - ratio) > 0.0005) {
                    grown.set(key, ratio);
                    call('TextNeedsHeight', key, ratio);
                }
            }
        });
    }

    // Dragging across the dialog must never paint the browser's text selection over the tray,
    // canvas or layer list (WebKit starts one on press even with pointerdown's default
    // prevented). Form fields and the on-canvas text editor keep normal selection.
    function onSelectStart(e) {
        const t = e.target instanceof Element ? e.target : e.target?.parentElement;
        if (!t || !root.contains(t) || isTyping(t)) return;
        if (drag || root.dataset.dragging || root.dataset.gesture
            || t.closest('.cm-bench__tray, .cm-bench__board, .cm-bench__overlay, .cm-bench__layers, .cm-bench__stagebar')) {
            e.preventDefault();
        }
    }

    const resizeObserver = new ResizeObserver(() => { draw(); measureText(); });
    const observeStage = () => { const s = stage(); if (s) resizeObserver.observe(s); resizeObserver.observe(root); };
    // The board scrolls at 100% view; capture sees that scroll and re-seats the overlay.
    const onScroll = () => placeOverlay();

    root.addEventListener('pointerdown', onTilePointerDown);
    root.addEventListener('pointerdown', onRowPointerDown);
    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', onPointerUp);
    root.addEventListener('pointerleave', () => { if (!drag && hoverId) { hoverId = null; draw(); } });
    root.addEventListener('dblclick', onDoubleClick);
    root.addEventListener('wheel', onWheel, { passive: false });
    root.addEventListener('dragover', onDragOver);
    root.addEventListener('dragleave', onDragLeave);
    root.addEventListener('drop', onDrop);
    root.addEventListener('load', onLoad, true);
    root.addEventListener('scroll', onScroll, true);
    document.addEventListener('selectstart', onSelectStart);
    document.addEventListener('keydown', onKeyDown);
    document.fonts?.ready?.then(() => measureText());
    observeStage();

    return {
        /** Called by .NET after every render: the committed state is now in the DOM. */
        sync(next) {
            state = { ...state, ...next };
            if (!drag) { pendingCrop = pendingCrop && layerEl(state.selectedId) ? pendingCrop : null; }
            observeStage();
            reportImages();
            draw();
            requestAnimationFrame(measureText);
        },
        /** Crop zoom from the toolbar slider, applied about the frame centre. */
        zoomTo(zoom) {
            const sel = layerEl(state.selectedId);
            if (!sel) return;
            const g = pendingCrop ?? geom(sel);
            zoomCrop(zoom / (g.zoom || 1));
        },
        focusTextEditor() {
            const ta = root.querySelector('[data-bench-textedit]');
            if (ta) { ta.focus(); ta.select(); }
        },
        focusRoot() { root.focus({ preventScroll: true }); },
        dispose() {
            resizeObserver.disconnect();
            clearTimeout(wheelTimer);
            root.removeEventListener('pointerdown', onTilePointerDown);
            root.removeEventListener('pointerdown', onRowPointerDown);
            root.removeEventListener('pointerdown', onPointerDown);
            root.removeEventListener('pointermove', onPointerMove);
            root.removeEventListener('pointerup', onPointerUp);
            root.removeEventListener('pointercancel', onPointerUp);
            root.removeEventListener('dblclick', onDoubleClick);
            root.removeEventListener('wheel', onWheel);
            root.removeEventListener('dragover', onDragOver);
            root.removeEventListener('dragleave', onDragLeave);
            root.removeEventListener('drop', onDrop);
            root.removeEventListener('load', onLoad, true);
            root.removeEventListener('scroll', onScroll, true);
            document.removeEventListener('selectstart', onSelectStart);
            document.removeEventListener('keydown', onKeyDown);
            chrome()?.replaceChildren();
        },
    };
}
