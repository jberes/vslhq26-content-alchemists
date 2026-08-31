export function relativeX(elementId, clientX) {
    const lane = document.getElementById(elementId);
    if (!lane) {
        throw new Error(`Wire lane '${elementId}' was not found.`);
    }

    const bounds = lane.getBoundingClientRect();
    if (bounds.width <= 0) {
        throw new Error(`Wire lane '${elementId}' has no measurable width.`);
    }

    return Math.max(0, Math.min(1, (clientX - bounds.left) / bounds.width));
}

export function observeWidth(element, dotNetReference, threshold) {
    let narrow = null;
    const report = () => {
        const width = window.innerWidth;
        const nextNarrow = width < threshold;
        if (nextNarrow === narrow) {
            return;
        }

        narrow = nextNarrow;
        dotNetReference.invokeMethodAsync("WireWidthChanged", width);
    };
    const observer = new ResizeObserver(report);
    observer.observe(element);
    window.addEventListener("resize", report, { passive: true });
    report();

    return {
        dispose() {
            observer.disconnect();
            window.removeEventListener("resize", report);
        },
    };
}