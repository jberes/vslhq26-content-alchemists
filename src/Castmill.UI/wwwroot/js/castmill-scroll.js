export function observeOverflow(element, dotNet) {
    const edgeTolerance = 2;

    const report = () => {
        const maxScroll = Math.max(0, element.scrollWidth - element.clientWidth);
        dotNet.invokeMethodAsync(
            'UpdateOverflow',
            element.scrollLeft > edgeTolerance,
            element.scrollLeft < maxScroll - edgeTolerance);
    };

    element.addEventListener('scroll', report, { passive: true });
    const resizeObserver = new ResizeObserver(report);
    resizeObserver.observe(element);
    report();

    return {
        scroll(direction) {
            element.scrollBy({
                left: direction * Math.max(160, element.clientWidth * 0.8),
                behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
            });
        },
        dispose() {
            element.removeEventListener('scroll', report);
            resizeObserver.disconnect();
        }
    };
}