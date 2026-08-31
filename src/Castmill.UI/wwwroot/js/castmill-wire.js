// Pointer-based drag for The Wire. HTML5 drag events are unreliable inside embedded
// WebViews (WebView2 and Catalyst's WKWebView both drop them), so the gesture is driven
// with pointer events and reported back to Blazor on release.
//
// Sources carry data-wire-drag (a stable id). Targets carry data-wire-lane (a day lane;
// the drop's x-position becomes a time-of-day ratio) or data-wire-col (a pipeline column).
export function armPointerDrag(root, dotNetReference) {
    const DRAG_THRESHOLD = 6;
    let pending = null;
    let drag = null;

    function targetUnder(x, y) {
        const el = document.elementFromPoint(x, y);
        if (!(el instanceof Element)) {
            return null;
        }
        return el.closest("[data-wire-lane]") ?? el.closest("[data-wire-col]");
    }

    function clearHighlight() {
        drag?.target?.classList.remove("cm-wire-drop-target");
        if (drag) {
            drag.target = null;
        }
    }

    function cleanup() {
        clearHighlight();
        drag?.ghost?.remove();
        document.body.classList.remove("cm-wire-dragging");
        pending = null;
        drag = null;
    }

    function onPointerDown(event) {
        if (event.button !== 0 || !(event.target instanceof Element)) {
            return;
        }
        const source = event.target.closest("[data-wire-drag]");
        // Buttons inside a card keep their click behavior; only the card body drags.
        if (!source || !source.dataset.wireDrag || event.target.closest("button, a, igb-button, igc-button")) {
            return;
        }
        pending = { source, x: event.clientX, y: event.clientY };
    }

    function onPointerMove(event) {
        if (pending && !drag) {
            if (Math.hypot(event.clientX - pending.x, event.clientY - pending.y) < DRAG_THRESHOLD) {
                return;
            }
            const rect = pending.source.getBoundingClientRect();
            const ghost = pending.source.cloneNode(true);
            ghost.classList.add("cm-wire-drag-ghost");
            ghost.style.inlineSize = `${rect.width}px`;
            document.body.appendChild(ghost);
            document.body.classList.add("cm-wire-dragging");
            drag = { id: pending.source.dataset.wireDrag, ghost, target: null };
            pending = null;
        }
        if (!drag) {
            return;
        }
        drag.ghost.style.transform = `translate(${event.clientX + 12}px, ${event.clientY + 8}px)`;
        const target = targetUnder(event.clientX, event.clientY);
        if (target !== drag.target) {
            clearHighlight();
            drag.target = target;
            target?.classList.add("cm-wire-drop-target");
        }
    }

    function onPointerUp(event) {
        pending = null;
        if (!drag) {
            return;
        }
        const id = drag.id;
        const target = targetUnder(event.clientX, event.clientY);
        cleanup();
        if (!target) {
            return;
        }
        if (target.dataset.wireLane) {
            const rect = target.getBoundingClientRect();
            const ratio = rect.width > 0
                ? Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width))
                : 0.5;
            dotNetReference.invokeMethodAsync("DropFromPointerAsync", id, "lane", target.dataset.wireLane, ratio);
        } else if (target.dataset.wireCol) {
            dotNetReference.invokeMethodAsync("DropFromPointerAsync", id, "column", target.dataset.wireCol, 0);
        }
    }

    function onKeyDown(event) {
        if (event.key === "Escape") {
            cleanup();
        }
    }

    root.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("pointermove", onPointerMove);
    document.addEventListener("pointerup", onPointerUp);
    document.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            cleanup();
            root.removeEventListener("pointerdown", onPointerDown);
            document.removeEventListener("pointermove", onPointerMove);
            document.removeEventListener("pointerup", onPointerUp);
            document.removeEventListener("keydown", onKeyDown);
        },
    };
}
